using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Joydex.Contracts;

namespace Joydex.Ipc;

/// <summary>
/// Authenticates a production Joydex.App process and grants the sole tray launch ticket.
/// </summary>
public sealed class RuntimeBootstrapRendezvousServer : IAsyncDisposable
{
    private readonly RuntimeIpcServer _runtimeServer;
    private readonly RuntimeIpcEndpoint _endpoint;
    private readonly RuntimeBootstrapRendezvousOptions _options;
    private readonly IRuntimeBootstrapImageVerifier _imageVerifier;
    private readonly RuntimeBootstrapImageIdentity _expectedAppImage;
    private readonly RuntimeListenerLifecycleTestHooks? _testHooks;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _requestSlots;
    private readonly ConcurrentDictionary<long, ConnectionWork> _connections = new();
    private Exception? _connectionCleanupFailure;
    private readonly object _reservationGate = new();
    private readonly Task _acceptTask;
    private TrayAdmission? _reservation;
    private long _nextConnectionWorkId;
    private int _disposed;

    private RuntimeBootstrapRendezvousServer(
        RuntimeIpcServer runtimeServer,
        RuntimeIpcEndpoint endpoint,
        RuntimeBootstrapRendezvousOptions options,
        IRuntimeBootstrapImageVerifier imageVerifier,
        RuntimeBootstrapImageIdentity expectedAppImage,
        NamedPipeServerStream firstPipe,
        RuntimeListenerLifecycleTestHooks? testHooks)
    {
        _runtimeServer = runtimeServer;
        _endpoint = endpoint;
        _options = options;
        _imageVerifier = imageVerifier;
        _expectedAppImage = expectedAppImage;
        _testHooks = testHooks;
        _requestSlots = new SemaphoreSlim(
            options.MaximumConcurrentRequests,
            options.MaximumConcurrentRequests);
        PipeName = RuntimeBootstrapRendezvousProtocol.GetPipeName(endpoint);
        _acceptTask = AcceptLoopAsync(firstPipe, _lifetime.Token);
    }

    public string PipeName { get; }

    /// <summary>Completes when the listener stops, or faults if accepting requests fails.</summary>
    public Task Completion => _acceptTask;

    public static RuntimeBootstrapRendezvousServer Start(
        RuntimeIpcServer runtimeServer,
        RuntimeIpcEndpoint endpoint,
        string frozenJoydexAppPath,
        string frozenRuntimeHostPath,
        RuntimeBootstrapRendezvousOptions? options = null) =>
        StartCore(
            runtimeServer,
            endpoint,
            frozenJoydexAppPath,
            frozenRuntimeHostPath,
            options,
            WindowsRuntimeBootstrapImageVerifier.Instance,
            requiredInstanceKind: RuntimeInstanceKind.Production,
            validateExecutableNames: true,
            testHooks: null);

    /// <summary>
    /// Starts the authenticated tray rendezvous for one explicitly synthetic demo runtime.
    /// </summary>
    public static RuntimeBootstrapRendezvousServer StartDemo(
        RuntimeIpcServer runtimeServer,
        RuntimeIpcEndpoint endpoint,
        string frozenJoydexAppPath,
        string frozenRuntimeHostPath,
        RuntimeBootstrapRendezvousOptions? options = null) =>
        StartCore(
            runtimeServer,
            endpoint,
            frozenJoydexAppPath,
            frozenRuntimeHostPath,
            options,
            WindowsRuntimeBootstrapImageVerifier.Instance,
            requiredInstanceKind: RuntimeInstanceKind.Synthetic,
            validateExecutableNames: true,
            testHooks: null);

    internal static RuntimeBootstrapRendezvousServer StartForTest(
        RuntimeIpcServer runtimeServer,
        RuntimeIpcEndpoint endpoint,
        string frozenJoydexAppPath,
        string frozenRuntimeHostPath,
        RuntimeBootstrapRendezvousOptions? options,
        IRuntimeBootstrapImageVerifier imageVerifier,
        RuntimeListenerLifecycleTestHooks? testHooks = null) =>
        StartCore(
            runtimeServer,
            endpoint,
            frozenJoydexAppPath,
            frozenRuntimeHostPath,
            options,
            imageVerifier,
            requiredInstanceKind: RuntimeInstanceKind.Synthetic,
            validateExecutableNames: false,
            testHooks);

    /// <summary>
    /// Claims the exact tray admission consumed by this authenticated runtime connection.
    /// Call this as the first factory operation and dispose the returned lease if factory or
    /// application attach fails. The lease also releases itself at physical IPC disconnect.
    /// </summary>
    public RuntimeBootstrapTrayLease ClaimTrayConnection(
        RuntimeIpcConnectionContext context,
        RuntimeClientKind authorizedKind,
        CancellationToken physicalConnectionLifetime)
    {
        ArgumentNullException.ThrowIfNull(context);
        ThrowIfDisposed();
        physicalConnectionLifetime.ThrowIfCancellationRequested();
        if (authorizedKind != RuntimeClientKind.Tray)
        {
            throw new RuntimeIpcAuthenticationException(
                "Only an authenticated Tray connection can claim the tray admission.");
        }
        if (!EndpointMatches(_endpoint, context.Endpoint))
        {
            throw new RuntimeIpcAuthenticationException(
                "The tray connection does not belong to this runtime endpoint and data root.");
        }
        if (string.IsNullOrWhiteSpace(context.AuthenticatedLaunchTicketFingerprint))
        {
            throw new RuntimeIpcAuthenticationException(
                "The tray connection has no authenticated launch-ticket identity.");
        }

        TrayAdmission admission;
        lock (_reservationGate)
        {
            ExpirePendingLocked(_options.TimeProvider.GetUtcNow());
            admission = _reservation
                ?? throw new RuntimeIpcAuthenticationException("No tray admission is pending.");
            if (admission.State != TrayAdmissionState.Pending
                || !admission.DeliveryAcknowledged
                || !PeerMatches(admission.Peer, context.Peer)
                || !string.Equals(
                    admission.TicketFingerprint,
                    context.AuthenticatedLaunchTicketFingerprint,
                    StringComparison.Ordinal))
            {
                throw new RuntimeIpcAuthenticationException(
                    "The tray connection does not match the pending admission.");
            }

            admission.State = TrayAdmissionState.Attached;
            admission.ExpirationTimer?.Dispose();
            admission.ExpirationTimer = null;
        }

        return new RuntimeBootstrapTrayLease(this, admission.Id, physicalConnectionLifetime);
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

        DisposeConnectionPipes(ref failures);
        var work = _connections
            .ToArray()
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value.Task)
            .ToArray();
        if (work.Length > 0)
        {
            try
            {
                await Task.WhenAll(work).ConfigureAwait(false);
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

        try
        {
            lock (_reservationGate)
            {
                ClearReservationLocked();
            }
        }
        catch (Exception exception)
        {
            AddFailure(ref failures, exception);
        }
        TryCleanup(_requestSlots.Dispose, ref failures);
        TryCleanup(_lifetime.Dispose, ref failures);
        ThrowFailures(failures);
    }

    internal bool HasTrayReservation
    {
        get
        {
            lock (_reservationGate)
            {
                return _reservation is not null;
            }
        }
    }

    private static RuntimeBootstrapRendezvousServer StartCore(
        RuntimeIpcServer runtimeServer,
        RuntimeIpcEndpoint endpoint,
        string frozenJoydexAppPath,
        string frozenRuntimeHostPath,
        RuntimeBootstrapRendezvousOptions? options,
        IRuntimeBootstrapImageVerifier imageVerifier,
        RuntimeInstanceKind requiredInstanceKind,
        bool validateExecutableNames,
        RuntimeListenerLifecycleTestHooks? testHooks)
    {
        ArgumentNullException.ThrowIfNull(runtimeServer);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(frozenJoydexAppPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(frozenRuntimeHostPath);
        ArgumentNullException.ThrowIfNull(imageVerifier);
        options ??= new RuntimeBootstrapRendezvousOptions();
        options.Validate();

        if (!EndpointMatches(runtimeServer.Endpoint, endpoint))
        {
            throw new ArgumentException(
                "The bootstrap endpoint must be the endpoint owned by the runtime server.",
                nameof(endpoint));
        }
        if (endpoint.InstanceKind != requiredInstanceKind)
        {
            throw new InvalidOperationException(
                $"{requiredInstanceKind} bootstrap rendezvous requires a {requiredInstanceKind} endpoint.");
        }
        if (endpoint.SessionId != Process.GetCurrentProcess().SessionId)
        {
            throw new InvalidOperationException("The bootstrap endpoint belongs to another Windows session.");
        }

        var expectedAppImage = imageVerifier.CaptureFrozenImage(frozenJoydexAppPath);
        var expectedHostImage = imageVerifier.CaptureFrozenImage(frozenRuntimeHostPath);
        if (!string.Equals(
                expectedAppImage.CanonicalDirectory,
                expectedHostImage.CanonicalDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Joydex.App and RuntimeHost must come from the same frozen deployment directory.");
        }
        if (validateExecutableNames
            && (!string.Equals(
                    Path.GetFileName(expectedAppImage.CanonicalPath),
                    "Joydex.App.exe",
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    Path.GetFileName(expectedHostImage.CanonicalPath),
                    "Joydex.RuntimeHost.exe",
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "The frozen deployment must contain Joydex.App.exe and Joydex.RuntimeHost.exe.");
        }
        imageVerifier.VerifyCurrentProcess(expectedHostImage);

        NamedPipeServerStream? firstPipe = null;
        try
        {
            firstPipe = CreatePipe(
                RuntimeBootstrapRendezvousProtocol.GetPipeName(endpoint),
                options.MaximumConcurrentRequests,
                firstPipeInstance: true);
            var server = new RuntimeBootstrapRendezvousServer(
                runtimeServer,
                endpoint,
                options,
                imageVerifier,
                expectedAppImage,
                firstPipe,
                testHooks);
            firstPipe = null;
            return server;
        }
        finally
        {
            firstPipe?.Dispose();
        }
    }

    private async Task AcceptLoopAsync(
        NamedPipeServerStream firstPipe,
        CancellationToken cancellationToken)
    {
        NamedPipeServerStream? pendingPipe = firstPipe;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_testHooks?.BeforeAcceptAsync is { } beforeAccept)
                {
                    await beforeAccept().ConfigureAwait(false);
                }
                await _requestSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    pendingPipe ??= CreatePipe(
                        PipeName,
                        _options.MaximumConcurrentRequests,
                        firstPipeInstance: false);
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
                        throw new InvalidOperationException("The bootstrap request could not be tracked.");
                    }
                    registrationReady.TrySetResult();
                }
                catch
                {
                    _requestSlots.Release();
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
            // A handler failure belongs to this request. Awaiting it observes the exception;
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
            _requestSlots.Release();
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
                        "Bootstrap request cleanup failed.",
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
        CancellationToken serverLifetime)
    {
        await using (pipe.ConfigureAwait(false))
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(serverLifetime))
        {
            deadline.CancelAfter(_options.RequestTimeout);
            TrayAdmission? issuedAdmission = null;
            try
            {
                var peer = WindowsPipePeerVerifier.VerifyClient(pipe, _endpoint.SessionId);
                using var process = Process.GetProcessById(peer.ProcessId);
                var request = await RuntimeBootstrapWire.ReadAsync<RuntimeBootstrapWireRequest>(
                        pipe,
                        _options.MaximumRequestBytes,
                        deadline.Token)
                    .ConfigureAwait(false);
                ValidateRequest(request);
                _imageVerifier.VerifyPeerProcess(process, peer, _expectedAppImage, "Joydex.App");

                RuntimeBootstrapWireResponse response;
                lock (_reservationGate)
                {
                    ExpirePendingLocked(_options.TimeProvider.GetUtcNow());
                    if (_reservation is not null)
                    {
                        response = new RuntimeBootstrapWireResponse(
                            RuntimeBootstrapWireStatus.TrayAlreadyReserved,
                            LaunchTicket: null,
                            TicketExpiresAtUnixMilliseconds: null,
                            AcknowledgementNonce: null,
                            Detail: "A tray is already starting or attached.");
                    }
                    else
                    {
                        var ticket = _runtimeServer.IssueLaunchTicket(
                            RuntimeClientKind.Tray,
                            process,
                            _options.TicketLifetime);
                        issuedAdmission = new TrayAdmission(
                            Guid.NewGuid(),
                            peer,
                            RuntimeBootstrapTicketFingerprint.Compute(ticket.Value),
                            ticket.ExpiresAtUtc,
                            ticket.ExpiresAtUtc.Add(_options.ClaimRaceGrace));
                        _reservation = issuedAdmission;
                        response = new RuntimeBootstrapWireResponse(
                            RuntimeBootstrapWireStatus.Admitted,
                            ticket.Value,
                            ticket.ExpiresAtUtc.ToUnixTimeMilliseconds(),
                            CreateNonce(),
                            Detail: null);
                        issuedAdmission.AcknowledgementNonce = response.AcknowledgementNonce;
                    }
                }

                if (issuedAdmission is not null)
                {
                    ScheduleExpiration(issuedAdmission);
                }

                await RuntimeBootstrapWire.WriteAsync(
                        pipe,
                        response,
                        _options.MaximumRequestBytes,
                        deadline.Token)
                    .ConfigureAwait(false);
                if (issuedAdmission is null)
                {
                    return;
                }

                var acknowledgement =
                    await RuntimeBootstrapWire.ReadAsync<RuntimeBootstrapWireAcknowledgement>(
                            pipe,
                            _options.MaximumRequestBytes,
                            deadline.Token)
                        .ConfigureAwait(false);
                if (!string.Equals(
                        acknowledgement.Nonce,
                        issuedAdmission.AcknowledgementNonce,
                        StringComparison.Ordinal))
                {
                    throw new RuntimeIpcAuthenticationException(
                        "The bootstrap admission acknowledgement is invalid.");
                }

                var acknowledgementAccepted = false;
                lock (_reservationGate)
                {
                    if (ReferenceEquals(_reservation, issuedAdmission)
                        && issuedAdmission.State == TrayAdmissionState.Pending
                        && issuedAdmission.TicketExpiresAtUtc > _options.TimeProvider.GetUtcNow())
                    {
                        issuedAdmission.DeliveryAcknowledged = true;
                        acknowledgementAccepted = true;
                    }
                    else
                    {
                        if (ReferenceEquals(_reservation, issuedAdmission)
                            && issuedAdmission.State == TrayAdmissionState.Pending)
                        {
                            ClearReservationLocked();
                        }
                    }
                }
                await RuntimeBootstrapWire.WriteAsync(
                        pipe,
                        new RuntimeBootstrapWireCommit(acknowledgementAccepted),
                        _options.MaximumRequestBytes,
                        deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException
                                              || !serverLifetime.IsCancellationRequested)
            {
                if (issuedAdmission is null)
                {
                    await TryWriteRejectionAsync(pipe, deadline.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                if (issuedAdmission is not null && !issuedAdmission.DeliveryAcknowledged)
                {
                    ReleasePending(issuedAdmission.Id);
                }
            }
        }
    }

    private void ValidateRequest(RuntimeBootstrapWireRequest request)
    {
        if (request.ProtocolVersion != RuntimeBootstrapRendezvousProtocol.Version)
        {
            throw new RuntimeIpcAuthenticationException("The bootstrap protocol version is unsupported.");
        }
        if (!string.Equals(request.ClientKind, RuntimeClientKind.Tray.ToString(), StringComparison.Ordinal))
        {
            throw new RuntimeIpcAuthenticationException("Production bootstrap can admit only the Tray role.");
        }
        if (!string.Equals(request.DataRootId, _endpoint.DataRootId, StringComparison.Ordinal))
        {
            throw new RuntimeIpcAuthenticationException("The bootstrap request targets another data root.");
        }
    }

    private async Task TryWriteRejectionAsync(Stream pipe, CancellationToken cancellationToken)
    {
        try
        {
            await RuntimeBootstrapWire.WriteAsync(
                    pipe,
                    new RuntimeBootstrapWireResponse(
                        RuntimeBootstrapWireStatus.Rejected,
                        LaunchTicket: null,
                        TicketExpiresAtUnixMilliseconds: null,
                        AcknowledgementNonce: null,
                        Detail: "The bootstrap request was rejected."),
                    _options.MaximumRequestBytes,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Authentication failure and disconnect both end this one-shot request.
        }
    }

    private void ScheduleExpiration(TrayAdmission admission)
    {
        var dueTime = admission.PendingUntilUtc - _options.TimeProvider.GetUtcNow();
        if (dueTime < TimeSpan.Zero)
        {
            dueTime = TimeSpan.Zero;
        }
        var timer = _options.TimeProvider.CreateTimer(
            _ => ReleasePending(admission.Id),
            state: null,
            dueTime,
            Timeout.InfiniteTimeSpan);
        lock (_reservationGate)
        {
            if (ReferenceEquals(_reservation, admission)
                && admission.State == TrayAdmissionState.Pending)
            {
                admission.ExpirationTimer = timer;
            }
            else
            {
                timer.Dispose();
            }
        }
    }

    private void ExpirePendingLocked(DateTimeOffset now)
    {
        if (_reservation is { State: TrayAdmissionState.Pending } admission
            && admission.PendingUntilUtc <= now)
        {
            ClearReservationLocked();
        }
    }

    private void ReleasePending(Guid admissionId)
    {
        lock (_reservationGate)
        {
            if (_reservation is { State: TrayAdmissionState.Pending } admission
                && admission.Id == admissionId)
            {
                ClearReservationLocked();
            }
        }
    }

    internal void ReleaseAttached(Guid admissionId)
    {
        lock (_reservationGate)
        {
            if (_reservation is { State: TrayAdmissionState.Attached } admission
                && admission.Id == admissionId)
            {
                ClearReservationLocked();
            }
        }
    }

    private void ClearReservationLocked()
    {
        var reservation = _reservation;
        _reservation = null;
        reservation?.ExpirationTimer?.Dispose();
    }

    private static string CreateNonce() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

    private static bool EndpointMatches(RuntimeIpcEndpoint expected, RuntimeIpcEndpoint actual) =>
        string.Equals(expected.PipeName, actual.PipeName, StringComparison.Ordinal)
        && string.Equals(expected.DataRootId, actual.DataRootId, StringComparison.Ordinal)
        && expected.InstanceKind == actual.InstanceKind
        && expected.SessionId == actual.SessionId;

    private static bool PeerMatches(RuntimeIpcPeer expected, RuntimeIpcPeer actual) =>
        expected.ProcessId == actual.ProcessId
        && expected.ProcessStartTimeUtc.UtcTicks == actual.ProcessStartTimeUtc.UtcTicks
        && expected.SessionId == actual.SessionId
        && string.Equals(expected.UserSid, actual.UserSid, StringComparison.Ordinal)
        && expected.IsElevated == actual.IsElevated;

    private static NamedPipeServerStream CreatePipe(
        string pipeName,
        int maximumInstances,
        bool firstPipeInstance)
    {
        var options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
        if (firstPipeInstance)
        {
            options |= PipeOptions.FirstPipeInstance;
        }
        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maximumInstances,
            PipeTransmissionMode.Byte,
            options,
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
        throw new AggregateException("Bootstrap rendezvous listener cleanup failed.", failures);
    }

    private sealed record ConnectionWork(NamedPipeServerStream Pipe, Task Task);

    private sealed class TrayAdmission(
        Guid id,
        RuntimeIpcPeer peer,
        string ticketFingerprint,
        DateTimeOffset ticketExpiresAtUtc,
        DateTimeOffset pendingUntilUtc)
    {
        public Guid Id { get; } = id;

        public RuntimeIpcPeer Peer { get; } = peer;

        public string TicketFingerprint { get; } = ticketFingerprint;

        public DateTimeOffset TicketExpiresAtUtc { get; } = ticketExpiresAtUtc;

        public DateTimeOffset PendingUntilUtc { get; } = pendingUntilUtc;

        public string? AcknowledgementNonce { get; set; }

        public bool DeliveryAcknowledged { get; set; }

        public TrayAdmissionState State { get; set; }

        public ITimer? ExpirationTimer { get; set; }
    }

    private enum TrayAdmissionState
    {
        Pending,
        Attached,
    }
}

/// <summary>Owns the sole tray reservation for one authenticated physical runtime connection.</summary>
public sealed class RuntimeBootstrapTrayLease : IDisposable
{
    private readonly RuntimeBootstrapRendezvousServer _owner;
    private readonly Guid _admissionId;
    private readonly CancellationTokenRegistration _disconnectRegistration;
    private int _disposed;

    internal RuntimeBootstrapTrayLease(
        RuntimeBootstrapRendezvousServer owner,
        Guid admissionId,
        CancellationToken physicalConnectionLifetime)
    {
        _owner = owner;
        _admissionId = admissionId;
        _disconnectRegistration = physicalConnectionLifetime.Register(
            static state => ((RuntimeBootstrapTrayLease)state!).ReleaseFromDisconnect(),
            this);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _disconnectRegistration.Dispose();
        _owner.ReleaseAttached(_admissionId);
    }

    private void ReleaseFromDisconnect()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _owner.ReleaseAttached(_admissionId);
        }
    }
}
