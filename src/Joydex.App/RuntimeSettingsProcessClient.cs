using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Joydex.Contracts;
using Joydex.Ipc;

namespace Joydex.App;

/// <summary>Reports one ordered Settings-process connection generation to the UI context.</summary>
internal sealed class RuntimeSettingsProcessConnectionChangedEventArgs(
    long generation,
    RuntimeClientConnection? connection,
    RuntimeClientState? state,
    IRuntimeRpcServer? rpc,
    Exception? failure,
    bool isConnected) : EventArgs
{
    public long Generation { get; } = generation;

    public RuntimeClientConnection? Connection { get; } = connection;

    public RuntimeClientState? State { get; } = state;

    public IRuntimeRpcServer? Rpc { get; } = rpc;

    public Exception? Failure { get; } = failure;

    public bool IsConnected { get; } = isConnected;
}

/// <summary>
/// Creates one Settings-role connection. The returned handle lets deterministic tests replace the
/// transport without opening a real pipe.
/// </summary>
internal interface IRuntimeSettingsConnector
{
    Task<IRuntimeSettingsProcessConnection> ConnectAsync(
        RuntimeIpcEndpoint endpoint,
        string launchTicket,
        SynchronizationContext uiContext,
        RuntimeClientResumeCursor? resumeCursor,
        CancellationToken cancellationToken);
}

/// <summary>Provides the state and lifetime owned for one Settings runtime attachment.</summary>
internal interface IRuntimeSettingsProcessConnection : IAsyncDisposable
{
    RuntimeClientConnection? Connection { get; }

    RuntimeClientState State { get; }

    IRuntimeRpcServer Rpc { get; }

    Task Completion { get; }
}

/// <summary>
/// Owns the Settings process's inherited control stream and its current runtime attachment. The
/// constructor starts the bootstrap attachment and control reader immediately.
/// </summary>
internal sealed class RuntimeSettingsProcessClient : IAsyncDisposable
{
    private const int MaximumPendingControlMessages = 32;

    private readonly object _stateGate = new();
    private readonly object _disposeGate = new();
    private readonly object _shutdownGate = new();
    private readonly RuntimeIpcEndpoint _endpoint;
    private readonly Stream _channelStream;
    private readonly SynchronizationContext _uiContext;
    private readonly IRuntimeSettingsConnector _connector;
    private readonly bool _requireConcreteConnection;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<RuntimeSettingsChannelMessage> _controlMessages;
    private readonly HashSet<string> _usedTicketFingerprints = new(StringComparer.Ordinal);
    private readonly Task _reader;
    private IRuntimeSettingsProcessConnection? _current;
    private RuntimeClientResumeCursor? _resumeCursor;
    private Task _retirement = Task.CompletedTask;
    private Exception? _readerFailure;
    private Task? _disposal;
    private Task? _cancellation;
    private Task? _channelDisposal;
    private long _generation;
    private int _disposed;

    internal RuntimeSettingsProcessClient(
        RuntimeSettingsChannelMessage bootstrap,
        Stream channel,
        SynchronizationContext uiContext,
        IRuntimeSettingsConnector? connector = null)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        _endpoint = RuntimeSettingsChannel.GetBootstrapEndpoint(bootstrap);
        _channelStream = channel ?? throw new ArgumentNullException(nameof(channel));
        if (!channel.CanRead)
        {
            throw new ArgumentException("The inherited Settings channel must be readable.", nameof(channel));
        }
        _uiContext = uiContext ?? throw new ArgumentNullException(nameof(uiContext));
        _requireConcreteConnection = connector is null;
        _connector = connector ?? DefaultRuntimeSettingsConnector.Instance;
        _controlMessages = Channel.CreateBounded<RuntimeSettingsChannelMessage>(
            new BoundedChannelOptions(MaximumPendingControlMessages)
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait,
            });

        var bootstrapTicket = bootstrap.LaunchTicket!.Trim();
        _usedTicketFingerprints.Add(Fingerprint(bootstrapTicket));
        var lifetimeToken = _lifetime.Token;
        // Windows console input may block synchronously inside ReadAsync and remain blocked after
        // stream disposal. Keep the process-lifetime reader off the coordinator and UI threads.
        _reader = Task.Run(() => ReadControlMessagesAsync(lifetimeToken));
        Completion = RunAsync(bootstrapTicket);
    }

    public event EventHandler<RuntimeSettingsProcessConnectionChangedEventArgs>? ConnectionChanged;

    public event EventHandler? ActivateRequested;

    public RuntimeClientConnection? Current
    {
        get
        {
            lock (_stateGate)
            {
                return _current?.Connection;
            }
        }
    }

    public Task Completion { get; }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_disposal is null)
            {
                Interlocked.Exchange(ref _disposed, 1);
                _disposal = DisposeCoreAsync();
            }
            return new ValueTask(_disposal);
        }
    }

    private async Task RunAsync(string bootstrapTicket)
    {
        Exception? failure = null;
        List<Exception> cleanupFailures = [];
        try
        {
            await ConnectAsync(bootstrapTicket, initial: true).ConfigureAwait(false);
            await foreach (var message in _controlMessages.Reader.ReadAllAsync(_lifetime.Token))
            {
                switch (message.Kind)
                {
                    case RuntimeSettingsChannelMessageKind.Activate:
                        PostActivateRequested();
                        break;
                    case RuntimeSettingsChannelMessageKind.Reconnect:
                        var ticket = message.LaunchTicket!.Trim();
                        if (!_usedTicketFingerprints.Add(Fingerprint(ticket)))
                        {
                            throw new InvalidDataException(
                                "The Settings channel repeated a runtime launch ticket.");
                        }
                        await ConnectAsync(ticket, initial: false).ConfigureAwait(false);
                        break;
                    default:
                        throw new InvalidDataException(
                            "The Settings channel sent Bootstrap more than once.");
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // EOF and explicit disposal cancel any connect that is still awaiting runtime attach.
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            await CancelAsync(cleanupFailures).ConfigureAwait(false);
            await DisposeChannelAsync(cleanupFailures).ConfigureAwait(false);
            failure ??= Volatile.Read(ref _readerFailure);
            await RetireCurrentAsync(failure, notify: true, cleanupFailures).ConfigureAwait(false);
            failure ??= Volatile.Read(ref _readerFailure);
        }

        ThrowFailures(failure, cleanupFailures);
    }

    private async Task ReadControlMessagesAsync(CancellationToken lifetimeToken)
    {
        Exception? failure = null;
        try
        {
            while (true)
            {
                var message = await RuntimeSettingsChannel
                    .ReadAsync(_channelStream, lifetimeToken)
                    .ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }
                if (message.Kind == RuntimeSettingsChannelMessageKind.Bootstrap)
                {
                    throw new InvalidDataException(
                        "The Settings channel sent Bootstrap more than once.");
                }
                if (!_controlMessages.Writer.TryWrite(message))
                {
                    throw new InvalidDataException(
                        $"The Settings channel exceeded its {MaximumPendingControlMessages}-message control queue.");
                }
            }
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (lifetimeToken.IsCancellationRequested)
        {
            // Disposing the inherited stream is the fallback for streams that ignore read
            // cancellation. The coordinator's earlier failure, if any, remains authoritative.
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            if (failure is not null)
            {
                Interlocked.CompareExchange(ref _readerFailure, failure, null);
            }
            try
            {
                await GetOrStartCancellation().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref _readerFailure, exception, null);
            }
            _controlMessages.Writer.TryComplete();
        }
    }

    private async Task ConnectAsync(string launchTicket, bool initial)
    {
        await RetireCurrentAsync(failure: null, notify: false, failures: null)
            .ConfigureAwait(false);

        long generation;
        RuntimeClientResumeCursor? resumeCursor;
        lock (_stateGate)
        {
            generation = checked(++_generation);
            resumeCursor = _resumeCursor;
        }

        IRuntimeSettingsProcessConnection? connection = null;
        try
        {
            connection = await _connector.ConnectAsync(
                    _endpoint,
                    launchTicket,
                    _uiContext,
                    resumeCursor,
                    _lifetime.Token)
                .ConfigureAwait(false);
            if (connection is null
                || connection.State is null
                || connection.Rpc is null
                || connection.Completion is null)
            {
                throw new InvalidOperationException(
                    "The Settings connector returned an incomplete runtime connection.");
            }
            if (_requireConcreteConnection && connection.Connection is null)
            {
                throw new InvalidOperationException(
                    "The production Settings connector returned no RuntimeClientConnection.");
            }
            _lifetime.Token.ThrowIfCancellationRequested();

            lock (_stateGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    throw new OperationCanceledException(
                        "The Settings runtime client is stopping.",
                        _lifetime.Token);
                }
                _lifetime.Token.ThrowIfCancellationRequested();
                _current = connection;
                _resumeCursor = TryCreateResumeCursor(connection.State);
            }
            PostConnectionChanged(new RuntimeSettingsProcessConnectionChangedEventArgs(
                generation,
                connection.Connection,
                connection.State,
                connection.Rpc,
                failure: null,
                isConnected: true));
            _ = ObserveConnectionCompletionAsync(connection, generation);
            connection = null;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
        catch (Exception exception)
        {
            if (connection is not null)
            {
                try
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupFailure)
                {
                    exception = new AggregateException(
                        "The failed Settings connection did not dispose cleanly.",
                        exception,
                        cleanupFailure);
                }
            }
            if (initial)
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
            }
            PostConnectionChanged(new RuntimeSettingsProcessConnectionChangedEventArgs(
                generation,
                connection: null,
                state: null,
                rpc: null,
                exception,
                isConnected: false));
        }
    }

    private async Task ObserveConnectionCompletionAsync(
        IRuntimeSettingsProcessConnection connection,
        long connectionGeneration)
    {
        Exception? failure = null;
        try
        {
            await connection.Completion.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        failure ??= connection.State.Current.DisconnectFailure;

        long notificationGeneration;
        lock (_stateGate)
        {
            if (!ReferenceEquals(_current, connection)
                || _generation != connectionGeneration)
            {
                return;
            }
            _resumeCursor = TryCreateResumeCursor(connection.State);
            _current = null;
            notificationGeneration = checked(++_generation);
            _retirement = RetireAsync(_retirement, connection);
        }

        PostConnectionChanged(new RuntimeSettingsProcessConnectionChangedEventArgs(
            notificationGeneration,
            connection: null,
            connection.State,
            rpc: null,
            failure,
            isConnected: false));
        try
        {
            await CurrentRetirement().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The connection event already carries its transport failure. A later replacement or
            // final disposal joins and reports any disposal failure.
        }
    }

    private async Task RetireCurrentAsync(
        Exception? failure,
        bool notify,
        List<Exception>? failures)
    {
        IRuntimeSettingsProcessConnection? retiring = null;
        RuntimeClientState? retiringState = null;
        long generation = 0;
        Task retirement;
        lock (_stateGate)
        {
            if (_current is { } current)
            {
                retiring = current;
                retiringState = current.State;
                _resumeCursor = TryCreateResumeCursor(current.State);
                _current = null;
                generation = checked(++_generation);
                _retirement = RetireAsync(_retirement, current);
            }
            retirement = _retirement;
        }

        if (notify && (retiring is not null || failure is not null))
        {
            if (generation == 0)
            {
                lock (_stateGate)
                {
                    generation = checked(++_generation);
                }
            }
            PostConnectionChanged(new RuntimeSettingsProcessConnectionChangedEventArgs(
                generation,
                connection: null,
                retiringState,
                rpc: null,
                failure,
                isConnected: false));
        }

        try
        {
            await retirement.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (failures is null)
            {
                throw;
            }
            failures.Add(exception);
        }
    }

    private static async Task RetireAsync(
        Task previousRetirement,
        IRuntimeSettingsProcessConnection connection)
    {
        await previousRetirement.ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private Task CurrentRetirement()
    {
        lock (_stateGate)
        {
            return _retirement;
        }
    }

    private static RuntimeClientResumeCursor? TryCreateResumeCursor(RuntimeClientState state)
    {
        var current = state.Current;
        var snapshot = current.Snapshot;
        return current.IsInitialized
               && !current.ResynchronizationRequired
               && snapshot is not null
               && snapshot.EngineEpoch != Guid.Empty
               && snapshot.EventCursor >= 0
            ? new RuntimeClientResumeCursor(snapshot.EngineEpoch, snapshot.EventCursor)
            : null;
    }

    private void PostConnectionChanged(RuntimeSettingsProcessConnectionChangedEventArgs eventArgs) =>
        _uiContext.Post(
            static state =>
            {
                var notification = (ConnectionNotification)state!;
                notification.Owner.ConnectionChanged?.Invoke(
                    notification.Owner,
                    notification.EventArgs);
            },
            new ConnectionNotification(this, eventArgs));

    private void PostActivateRequested() =>
        _uiContext.Post(
            static state =>
            {
                var owner = (RuntimeSettingsProcessClient)state!;
                owner.ActivateRequested?.Invoke(owner, EventArgs.Empty);
            },
            this);

    private async Task DisposeCoreAsync()
    {
        List<Exception> failures = [];
        await AwaitWithoutReportingAsync(GetOrStartCancellation()).ConfigureAwait(false);
        await AwaitWithoutReportingAsync(GetOrStartChannelDisposal()).ConfigureAwait(false);
        try
        {
            await Completion.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        if (_reader.IsCompleted)
        {
            _lifetime.Dispose();
        }
        // A blocked Windows console read owns the canceled token until process exit.
        ThrowFailures(failure: null, failures);
    }

    private async Task CancelAsync(List<Exception> failures)
    {
        try
        {
            await GetOrStartCancellation().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private async Task DisposeChannelAsync(List<Exception> failures)
    {
        try
        {
            await GetOrStartChannelDisposal().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private Task GetOrStartCancellation()
    {
        lock (_shutdownGate)
        {
            if (_cancellation is null)
            {
                try
                {
                    _cancellation = _lifetime.CancelAsync();
                }
                catch (Exception exception)
                {
                    _cancellation = Task.FromException(exception);
                }
            }
            return _cancellation;
        }
    }

    private Task GetOrStartChannelDisposal()
    {
        lock (_shutdownGate)
        {
            if (_channelDisposal is null)
            {
                try
                {
                    _channelDisposal = _channelStream.DisposeAsync().AsTask();
                }
                catch (Exception exception)
                {
                    _channelDisposal = Task.FromException(exception);
                }
            }
            return _channelDisposal;
        }
    }

    private static async Task AwaitWithoutReportingAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // RunAsync reports the shared shutdown failure through Completion.
        }
    }

    private static void ThrowFailures(Exception? failure, List<Exception> failures)
    {
        if (failure is null && failures.Count == 0)
        {
            return;
        }
        if (failure is not null && failures.Count == 0)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        if (failure is null && failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        throw new AggregateException(
            "The Settings runtime client did not stop cleanly.",
            failure is null ? failures : new[] { failure }.Concat(failures));
    }

    private static string Fingerprint(string ticket) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)));

    private sealed record ConnectionNotification(
        RuntimeSettingsProcessClient Owner,
        RuntimeSettingsProcessConnectionChangedEventArgs EventArgs);
}

internal sealed class DefaultRuntimeSettingsConnector : IRuntimeSettingsConnector
{
    public static DefaultRuntimeSettingsConnector Instance { get; } = new();

    private DefaultRuntimeSettingsConnector()
    {
    }

    public async Task<IRuntimeSettingsProcessConnection> ConnectAsync(
        RuntimeIpcEndpoint endpoint,
        string launchTicket,
        SynchronizationContext uiContext,
        RuntimeClientResumeCursor? resumeCursor,
        CancellationToken cancellationToken)
    {
        var connection = await RuntimeClientConnection.ConnectAsync(
                endpoint,
                RuntimeClientKind.Settings,
                launchTicket,
                uiContext,
                resumeCursor,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new ConnectionHandle(connection);
    }

    private sealed class ConnectionHandle(RuntimeClientConnection connection) :
        IRuntimeSettingsProcessConnection
    {
        public RuntimeClientConnection? Connection { get; } =
            connection ?? throw new ArgumentNullException(nameof(connection));

        public RuntimeClientState State => Connection!.State;

        public IRuntimeRpcServer Rpc => Connection!.Rpc;

        public Task Completion => Connection!.Completion;

        public ValueTask DisposeAsync() => Connection!.DisposeAsync();
    }
}
