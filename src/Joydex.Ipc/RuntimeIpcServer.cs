using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using Joydex.Contracts;

namespace Joydex.Ipc;

/// <summary>Hosts bounded, authenticated runtime RPC connections on a local named pipe.</summary>
public sealed class RuntimeIpcServer : IAsyncDisposable
{
    private readonly RuntimeIpcServerOptions _options;
    private readonly RuntimeRpcServerFactory _factory;
    private readonly RuntimeIpcTicketStore _tickets;
    private readonly RuntimeIpcProductionLease? _productionLease;
    private readonly RuntimeListenerLifecycleTestHooks? _testHooks;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _connectionSlots;
    private readonly ConcurrentDictionary<long, ConnectionWork> _connections = new();
    private Exception? _connectionCleanupFailure;
    private readonly Task _acceptTask;
    private long _nextConnectionWorkId;
    private int _disposed;

    private RuntimeIpcServer(
        RuntimeIpcEndpoint endpoint,
        RuntimeRpcServerFactory factory,
        RuntimeIpcServerOptions options,
        RuntimeIpcProductionLease? productionLease,
        NamedPipeServerStream firstPipe,
        RuntimeListenerLifecycleTestHooks? testHooks)
    {
        Endpoint = endpoint;
        _factory = factory;
        _options = options;
        _productionLease = productionLease;
        _testHooks = testHooks;
        _tickets = new RuntimeIpcTicketStore(options.TimeProvider, options.DefaultTicketLifetime);
        _connectionSlots = new SemaphoreSlim(options.MaximumConnections, options.MaximumConnections);
        _acceptTask = AcceptLoopAsync(firstPipe, _lifetime.Token);
    }

    public RuntimeIpcEndpoint Endpoint { get; }

    /// <summary>Completes when the listener stops, or faults if accepting connections fails.</summary>
    public Task Completion => _acceptTask;

    /// <summary>
    /// Starts listening immediately. Production endpoints also acquire the current-user/session
    /// endpoint lease before this method returns.
    /// </summary>
    public static RuntimeIpcServer Start(
        RuntimeIpcEndpoint endpoint,
        RuntimeRpcServerFactory factory,
        RuntimeIpcServerOptions? options = null) =>
        StartCore(endpoint, factory, options, testHooks: null);

    internal static RuntimeIpcServer StartForTest(
        RuntimeIpcEndpoint endpoint,
        RuntimeRpcServerFactory factory,
        RuntimeIpcServerOptions? options,
        RuntimeListenerLifecycleTestHooks testHooks) =>
        StartCore(endpoint, factory, options, testHooks);

    private static RuntimeIpcServer StartCore(
        RuntimeIpcEndpoint endpoint,
        RuntimeRpcServerFactory factory,
        RuntimeIpcServerOptions? options,
        RuntimeListenerLifecycleTestHooks? testHooks)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(factory);
        options ??= new RuntimeIpcServerOptions();
        options.Validate();
        if (endpoint.SessionId != Process.GetCurrentProcess().SessionId)
        {
            throw new InvalidOperationException("The runtime endpoint belongs to another Windows session.");
        }

        RuntimeIpcProductionLease? productionLease = null;
        NamedPipeServerStream? firstPipe = null;
        try
        {
            if (endpoint.InstanceKind == RuntimeInstanceKind.Production)
            {
                productionLease = RuntimeIpcProductionLease.Acquire(endpoint);
            }
            firstPipe = CreatePipe(endpoint, options, firstPipeInstance: true);
            var server = new RuntimeIpcServer(
                endpoint,
                factory,
                options,
                productionLease,
                firstPipe,
                testHooks);
            productionLease = null;
            firstPipe = null;
            return server;
        }
        finally
        {
            firstPipe?.Dispose();
            productionLease?.Dispose();
        }
    }

    public RuntimeIpcLaunchTicket IssueLaunchTicket(
        RuntimeClientKind clientKind,
        TimeSpan? lifetime = null)
    {
        ThrowIfDisposed();
        if (!Enum.IsDefined(clientKind))
        {
            throw new ArgumentOutOfRangeException(nameof(clientKind));
        }
        return _tickets.Issue(clientKind, lifetime, expectedPeer: null);
    }

    public RuntimeIpcLaunchTicket IssueLaunchTicket(
        RuntimeClientKind clientKind,
        Process expectedPeer,
        TimeSpan? lifetime = null)
    {
        ThrowIfDisposed();
        if (!Enum.IsDefined(clientKind))
        {
            throw new ArgumentOutOfRangeException(nameof(clientKind));
        }
        return _tickets.Issue(
            clientKind,
            lifetime,
            ExpectedRuntimeIpcPeer.FromProcess(expectedPeer));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        List<Exception>? failures = null;
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AddFailure(ref failures, exception);
        }

        DisposeConnectionPipes(ref failures);
        try
        {
            await _acceptTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AddFailure(ref failures, exception);
        }

        // The accept loop can publish a connection while cancellation races its completed accept.
        DisposeConnectionPipes(ref failures);
        var connectionTasks = _connections
            .ToArray()
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value.Task)
            .ToArray();
        if (connectionTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(connectionTasks).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                AddFailure(ref failures, exception);
            }
        }
        var connectionCleanupFailure = Interlocked.Exchange(
            ref _connectionCleanupFailure,
            null);
        if (connectionCleanupFailure is not null)
        {
            AddFailure(ref failures, connectionCleanupFailure);
        }

        TryCleanup(_connectionSlots.Dispose, ref failures);
        TryCleanup(_lifetime.Dispose, ref failures);
        if (_productionLease is not null)
        {
            TryCleanup(_productionLease.Dispose, ref failures);
        }
        ThrowFailures(failures);
    }

    private async Task AcceptLoopAsync(
        NamedPipeServerStream firstPipe,
        CancellationToken cancellationToken)
    {
        NamedPipeServerStream? pendingPipe = firstPipe;
        var isFirst = true;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_testHooks?.BeforeAcceptAsync is { } beforeAccept)
                {
                    await beforeAccept().ConfigureAwait(false);
                }
                await _connectionSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    pendingPipe ??= CreatePipe(Endpoint, _options, firstPipeInstance: isFirst);
                    isFirst = false;
                    await pendingPipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                    var pipe = pendingPipe;
                    pendingPipe = null;
                    var id = Interlocked.Increment(ref _nextConnectionWorkId);
                    var registrationReady = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    var handlerTask = HandleConnectionAsync(pipe, cancellationToken);
                    var trackedTask = TrackConnectionAsync(
                        id,
                        handlerTask,
                        registrationReady.Task);
                    if (!_connections.TryAdd(id, new ConnectionWork(pipe, trackedTask)))
                    {
                        registrationReady.TrySetCanceled();
                        pipe.Dispose();
                        throw new InvalidOperationException("The runtime connection could not be tracked.");
                    }
                    registrationReady.TrySetResult();
                }
                catch
                {
                    _connectionSlots.Release();
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            pendingPipe?.Dispose();
        }
    }

    private async Task TrackConnectionAsync(
        long id,
        Task handlerTask,
        Task registrationReady)
    {
        await registrationReady.ConfigureAwait(false);
        try
        {
            await handlerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // A handler failure belongs to this client. Awaiting it observes the exception;
            // listener health depends on the accept loop and connection cleanup.
        }

        List<Exception>? cleanupFailures = null;
        try
        {
            if (_testHooks?.BeforeConnectionCleanupAsync is { } beforeCleanup)
            {
                await beforeCleanup().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            AddFailure(ref cleanupFailures, exception);
        }
        try
        {
            _connectionSlots.Release();
        }
        catch (Exception exception)
        {
            AddFailure(ref cleanupFailures, exception);
        }
        try
        {
            if (cleanupFailures is { Count: > 0 })
            {
                var cleanupFailure = cleanupFailures.Count == 1
                    ? cleanupFailures[0]
                    : new AggregateException(
                        "Runtime IPC connection cleanup failed.",
                        cleanupFailures);
                // Retain one cleanup failure for disposal so a long-lived listener cannot
                // become an unbounded exception store.
                Interlocked.CompareExchange(
                    ref _connectionCleanupFailure,
                    cleanupFailure,
                    null);
            }
        }
        finally
        {
            _connections.TryRemove(id, out _);
        }
    }

    private async Task HandleConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            var peer = WindowsPipePeerVerifier.VerifyClient(pipe, Endpoint.SessionId);
            var connectionId = Guid.NewGuid().ToString("N");
            var context = new RuntimeIpcConnectionContext(connectionId, Endpoint, peer);
            var (rpc, boundedStream) = RuntimeJsonRpc.Create(pipe);
            await using (boundedStream.ConfigureAwait(false))
            using (rpc)
            {
                using var disconnected = new CancellationTokenSource();
                var abortState = 0;
                void AbortConnection(Exception? _)
                {
                    if (Interlocked.Exchange(ref abortState, 1) != 0)
                    {
                        return;
                    }

                    try
                    {
                        disconnected.Cancel();
                    }
                    catch (Exception)
                    {
                        // A failing cancellation callback cannot keep a failed connection open.
                    }
                    try
                    {
                        rpc.Dispose();
                    }
                    catch (Exception)
                    {
                        // The pipe close below remains the authoritative physical abort.
                    }
                    try
                    {
                        pipe.Dispose();
                    }
                    catch (Exception)
                    {
                        // Abort is best effort once the connection is already being torn down.
                    }
                }
                rpc.Disconnected += (_, _) => disconnected.Cancel();
                var remoteClient = new RemoteRuntimeRpcClient(rpc);
                await using var target = new RuntimeRpcServerTarget(
                    context,
                    _factory,
                    _tickets,
                    remoteClient,
                    AbortConnection,
                    _options.MaximumConcurrentRequestsPerConnection,
                    cancellationToken,
                    disconnected.Token);
                rpc.AddLocalRpcTarget(target);
                rpc.StartListening();

                var attachDeadline = Task.Delay(_options.AttachTimeout, cancellationToken);
                var firstCompletion = await Task.WhenAny(
                        target.Attached,
                        rpc.Completion,
                        attachDeadline)
                    .ConfigureAwait(false);
                if (firstCompletion == attachDeadline || cancellationToken.IsCancellationRequested)
                {
                    rpc.Dispose();
                }
                else if (firstCompletion == target.Attached)
                {
                    await rpc.Completion.ConfigureAwait(false);
                }
                await rpc.DispatchCompletion.ConfigureAwait(false);
            }
        }
    }

    private static NamedPipeServerStream CreatePipe(
        RuntimeIpcEndpoint endpoint,
        RuntimeIpcServerOptions options,
        bool firstPipeInstance)
    {
        var pipeOptions = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
        if (firstPipeInstance)
        {
            pipeOptions |= PipeOptions.FirstPipeInstance;
        }
        return new NamedPipeServerStream(
            endpoint.PipeName,
            PipeDirection.InOut,
            options.MaximumConnections,
            PipeTransmissionMode.Byte,
            pipeOptions,
            inBufferSize: 4096,
            outBufferSize: 4096);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);

    private void DisposeConnectionPipes(ref List<Exception>? failures)
    {
        foreach (var pair in _connections.ToArray())
        {
            try
            {
                pair.Value.Pipe.Dispose();
            }
            catch (Exception exception)
            {
                AddFailure(ref failures, exception);
            }
        }
    }

    private static void TryCleanup(Action cleanup, ref List<Exception>? failures)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            AddFailure(ref failures, exception);
        }
    }

    private static void AddFailure(ref List<Exception>? failures, Exception exception)
    {
        failures ??= [];
        if (exception is AggregateException aggregate)
        {
            failures.AddRange(aggregate.Flatten().InnerExceptions);
        }
        else
        {
            failures.Add(exception);
        }
    }

    private static void ThrowFailures(List<Exception>? failures)
    {
        if (failures is null or { Count: 0 })
        {
            return;
        }
        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        throw new AggregateException("Runtime IPC listener cleanup failed.", failures);
    }

    private sealed record ConnectionWork(NamedPipeServerStream Pipe, Task Task);
}

internal sealed class RuntimeListenerLifecycleTestHooks
{
    public Func<ValueTask>? BeforeAcceptAsync { get; init; }

    public Func<ValueTask>? BeforeConnectionCleanupAsync { get; init; }
}
