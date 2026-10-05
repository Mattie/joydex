using System.Threading.Channels;
using Joydex.App;
using Joydex.Contracts;
using Joydex.Core.Voice;
using Joydex.Ipc;
using StreamJsonRpc;

namespace Joydex.PebbleWorker;

internal sealed record PebbleWorkerRuntimeConfiguration(
    PebbleIndexPreferences Preferences,
    string SecretPath,
    string InboxDirectory,
    string DesktopBridgePipeName);

internal interface IPebbleWorkerRuntime : IAsyncDisposable
{
    Task Completion { get; }
}

internal interface IPebbleWorkerRuntimeFactory
{
    Task<IPebbleWorkerRuntime> StartAsync(
        PebbleWorkerRuntimeConfiguration configuration,
        Action<PebbleIndexReceiverStatus> status,
        Action<string> log,
        CancellationToken cancellationToken);
}

internal sealed class ProductionPebbleWorkerRuntimeFactory : IPebbleWorkerRuntimeFactory
{
    public static ProductionPebbleWorkerRuntimeFactory Instance { get; } = new();

    private ProductionPebbleWorkerRuntimeFactory()
    {
    }

    public async Task<IPebbleWorkerRuntime> StartAsync(
        PebbleWorkerRuntimeConfiguration configuration,
        Action<PebbleIndexReceiverStatus> status,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var runtime = await PebbleIndexReceiverRuntime.StartAsync(
                configuration.Preferences,
                configuration.SecretPath,
                configuration.InboxDirectory,
                configuration.DesktopBridgePipeName,
                status,
                log,
                cancellationToken)
            .ConfigureAwait(false);
        return new ProductionPebbleWorkerRuntime(runtime);
    }

    private sealed class ProductionPebbleWorkerRuntime(PebbleIndexReceiverRuntime runtime) :
        IPebbleWorkerRuntime
    {
        private readonly PebbleIndexReceiverRuntime _runtime = runtime;

        public Task Completion => _runtime.Completion;

        public ValueTask DisposeAsync() => _runtime.DisposeAsync();
    }
}

internal sealed class PebbleWorkerService : IAsyncDisposable
{
    private readonly PebbleWorkerLaunchTicket _ticket;
    private readonly JsonRpc _rpc;
    private readonly IPebbleWorkerRuntimeFactory _runtimeFactory;
    private readonly object _statusGate = new();
    private readonly object _disposeGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<bool> _statusSignals = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly Channel<string> _logs = Channel.CreateBounded<string>(
        new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _statusPublisher;
    private readonly Task _logPublisher;
    private IPebbleWorkerRuntime? _runtime;
    private PebbleWorkerStatus? _latestStatus;
    private Task? _disposeTask;
    private long _sequence;
    private int _started;
    private int _stopRequested;
    private int _disposed;

    public PebbleWorkerService(
        PebbleWorkerLaunchTicket ticket,
        JsonRpc rpc,
        IPebbleWorkerRuntimeFactory? runtimeFactory = null)
    {
        _ticket = ticket ?? throw new ArgumentNullException(nameof(ticket));
        _rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
        _runtimeFactory = runtimeFactory ?? ProductionPebbleWorkerRuntimeFactory.Instance;
        _statusPublisher = RunStatusPublisherAsync();
        _logPublisher = RunLogPublisherAsync();
    }

    public Task Completion => _completion.Task;

    public bool StopRequested => Volatile.Read(ref _stopRequested) != 0;

    [JsonRpcMethod(PebbleWorkerProtocol.Start)]
    public async Task<PebbleWorkerStartResponse> StartAsync(
        PebbleWorkerStartRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            throw new InvalidOperationException("The Pebble worker has already received Start.");
        }
        ValidateStart(request);

        IPebbleWorkerRuntime? runtime = null;
        try
        {
            var preferences = new PebbleIndexPreferences(
                request.Preferences.SchemaVersion,
                request.Preferences.Enabled,
                request.Preferences.Port,
                request.Preferences.TargetTaskId,
                request.Preferences.TargetHostId,
                request.Preferences.TargetTaskLabel).Normalize();
            runtime = await _runtimeFactory.StartAsync(
                    new PebbleWorkerRuntimeConfiguration(
                        preferences,
                        request.Paths.Secret,
                        request.Paths.Inbox,
                        request.Paths.DesktopBridgePipeName),
                    PublishStatus,
                    WriteLog,
                    cancellationToken)
                .ConfigureAwait(false);
            var status = GetLatestStatus()
                ?? throw new InvalidDataException(
                    "The Pebble receiver did not publish its initialized status.");
            if (!status.Running || runtime.Completion.IsCompleted)
            {
                throw new InvalidOperationException(
                    "The Pebble receiver did not remain running after initialization.");
            }
            _runtime = runtime;
            _ = ObserveRuntimeCompletionAsync(runtime);
            return new PebbleWorkerStartResponse(
                PebbleWorkerProtocol.MajorVersion,
                Math.Min(request.ProtocolMinor, PebbleWorkerProtocol.MinorVersion),
                status);
        }
        catch (Exception startupFailure)
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().ConfigureAwait(false);
            }
            return new PebbleWorkerStartResponse(
                PebbleWorkerProtocol.MajorVersion,
                Math.Min(request.ProtocolMinor, PebbleWorkerProtocol.MinorVersion),
                Status: null,
                Failure: ClassifyStartFailure(startupFailure));
        }
    }

    [JsonRpcMethod(PebbleWorkerProtocol.Stop)]
    public async Task StopAsync(
        PebbleWorkerGenerationMessage request,
        CancellationToken cancellationToken)
    {
        ValidateGeneration(request.Generation);
        Interlocked.Exchange(ref _stopRequested, 1);
        await DisposeAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        List<Exception>? failures = null;
        _lifetime.Cancel();
        _statusSignals.Writer.TryComplete();
        _logs.Writer.TryComplete();
        if (_runtime is not null)
        {
            try { await _runtime.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        try { await _statusPublisher.ConfigureAwait(false); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        try { await _logPublisher.ConfigureAwait(false); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        _lifetime.Dispose();
        if (failures is { Count: > 0 })
        {
            throw new AggregateException("Pebble worker cleanup did not complete.", failures);
        }
    }

    private async Task ObserveRuntimeCompletionAsync(IPebbleWorkerRuntime runtime)
    {
        Exception? failure = null;
        try
        {
            await runtime.Completion.ConfigureAwait(false);
            if (Volatile.Read(ref _disposed) == 0)
            {
                failure = new InvalidOperationException(
                    "The Pebble receiver stopped unexpectedly.");
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        if (failure is not null)
        {
            _completion.TrySetException(failure);
        }
    }

    private void PublishStatus(PebbleIndexReceiverStatus status)
    {
        lock (_statusGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }
            var sequence = ++_sequence;
            _latestStatus = ProjectStatus(status, sequence);
        }
        _statusSignals.Writer.TryWrite(true);
    }

    private PebbleWorkerStatus? GetLatestStatus()
    {
        lock (_statusGate) { return _latestStatus; }
    }

    private async Task RunStatusPublisherAsync()
    {
        try
        {
            await foreach (var signal in _statusSignals.Reader.ReadAllAsync(_lifetime.Token)
                .ConfigureAwait(false))
            {
                _ = signal;
                while (_statusSignals.Reader.TryRead(out _))
                {
                }
                var status = GetLatestStatus();
                if (status is not null && Volatile.Read(ref _disposed) == 0)
                {
                    await _rpc.InvokeWithCancellationAsync(
                            PebbleWorkerProtocol.PublishStatus,
                            [status],
                            _lifetime.Token)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            _completion.TrySetException(
                new IOException("The Pebble worker could not publish status to the host."));
        }
    }

    private async Task RunLogPublisherAsync()
    {
        try
        {
            await foreach (var message in _logs.Reader.ReadAllAsync(_lifetime.Token)
                .ConfigureAwait(false))
            {
                await _rpc.InvokeWithCancellationAsync(
                        PebbleWorkerProtocol.Log,
                        [new PebbleWorkerLogMessage(_ticket.Generation, message)],
                        _lifetime.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            _completion.TrySetException(
                new IOException("The Pebble worker could not publish diagnostics to the host."));
        }
    }

    private PebbleWorkerStatus ProjectStatus(PebbleIndexReceiverStatus status, long sequence)
    {
        var latest = status.Latest is null || !IsCanonicalStoredId(status.Latest.Id)
            ? null
            : new PebbleWorkerDeliveryStatus(
                status.Latest.Id,
                status.Latest.State,
                status.Latest.State switch
                {
                    PebbleIndexDeliveryState.Sent => "Desktop confirmed delivery.",
                    PebbleIndexDeliveryState.Received =>
                        "Received; delivery is not confirmed.",
                    _ => "Desktop delivery is uncertain and needs manual review.",
                });
        return new PebbleWorkerStatus(
            _ticket.Generation,
            sequence,
            status.Running,
            status.Running
                ? "Pebble Index receiver is listening on loopback."
                : "Pebble Index receiver is unavailable.",
            Math.Clamp(status.OutstandingCount, 0, 100_000),
            latest);
    }

    private void WriteLog(string message) => _logs.Writer.TryWrite(SanitizeLog(message));

    private static string SanitizeLog(string message)
    {
        if (message.Contains("confirmed by Desktop", StringComparison.OrdinalIgnoreCase))
        {
            return "Pebble Index delivery was confirmed by Desktop.";
        }
        if (message.Contains("uncertain", StringComparison.OrdinalIgnoreCase))
        {
            return "Pebble Index delivery is uncertain and needs manual review.";
        }
        if (message.Contains("held", StringComparison.OrdinalIgnoreCase)
            || message.Contains("queue", StringComparison.OrdinalIgnoreCase))
        {
            return "Pebble Index delivery is held before send.";
        }
        if (message.Contains("started on loopback", StringComparison.OrdinalIgnoreCase))
        {
            return "Pebble Index receiver started on loopback.";
        }
        if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "Pebble Index request timed out.";
        }
        return "Pebble Index worker reported a diagnostic.";
    }

    private void ValidateStart(PebbleWorkerStartRequest request)
    {
        if (!string.Equals(request.Capability, _ticket.Capability, StringComparison.Ordinal)
            || request.Generation != _ticket.Generation)
        {
            throw new InvalidDataException("The Pebble worker capability is invalid.");
        }
        if (request.ProtocolMajor != PebbleWorkerProtocol.MajorVersion)
        {
            throw new InvalidDataException("The Pebble worker protocol major version is unsupported.");
        }
        ArgumentNullException.ThrowIfNull(request.Preferences);
        ArgumentNullException.ThrowIfNull(request.Paths);
        if (request.ProtocolMinor < 0
            || !request.Preferences.Enabled
            || request.Preferences.TargetTaskId is null
            || request.Preferences.TargetHostId is null
            || request.Preferences.TargetTaskLabel is null
            || InvalidPath(request.Paths.Secret)
            || InvalidPath(request.Paths.Inbox)
            || string.IsNullOrWhiteSpace(request.Paths.DesktopBridgePipeName)
            || request.Paths.DesktopBridgePipeName.Length > 256)
        {
            throw new InvalidDataException("The Pebble worker Start payload is invalid.");
        }
    }

    private void ValidateGeneration(long generation)
    {
        if (generation != _ticket.Generation)
        {
            throw new InvalidOperationException("The Pebble worker generation is stale.");
        }
    }

    private static bool IsCanonicalStoredId(string? id)
    {
        if (id is null)
        {
            return false;
        }
        var hash = id.StartsWith("provided-", StringComparison.Ordinal)
            ? id.AsSpan("provided-".Length)
            : id.AsSpan();
        if (hash.Length != 64)
        {
            return false;
        }
        foreach (var character in hash)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }
        return true;
    }

    private static bool InvalidPath(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 32_767;

    private static PebbleWorkerStartFailure ClassifyStartFailure(Exception exception) => exception switch
    {
        InvalidDataException or ArgumentException or UnauthorizedAccessException
            or FileNotFoundException or DirectoryNotFoundException => new(
                PebbleWorkerStartFailureKind.Configuration,
                "Pebble Index configuration or a required state path is invalid."),
        _ => new(
            PebbleWorkerStartFailureKind.Transient,
            "Pebble Index receiver initialization failed."),
    };
}
