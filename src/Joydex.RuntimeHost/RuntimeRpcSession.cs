using Joydex.Contracts;
using Joydex.Core.Input;

namespace Joydex.RuntimeHost;

/// <summary>Implements the RPC surface for one transport-authenticated live connection.</summary>
internal sealed class RuntimeRpcSession : IRuntimeRpcServer, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly RuntimeEngine _engine;
    private readonly RuntimeClientKind _authorizedClientKind;
    private readonly RuntimeConnectionDispatcher _dispatcher;
    private readonly IRuntimeSettingsProcessLauncher _settingsProcessLauncher;
    private readonly Action<Exception?> _abortConnection;
    private readonly CancellationTokenRegistration _connectionCancellationRegistration;
    private readonly Dictionary<Guid, RuntimeCaptureUpdate> _captures = [];
    private readonly Dictionary<Guid, string> _captureObservations = [];
    private readonly HashSet<Guid> _commandNotifications = [];
    private readonly List<RuntimeEvent> _pendingRuntimeEvents = [];
    private int _negotiatedProtocolMinor = -1;
    private RuntimeIdentitySnapshot? _compatibilityIdentity;
    private bool _attachCompleted;
    private bool _settingsLifecycleAttached;
    private bool _disposed;

    internal RuntimeRpcSession(
        RuntimeEngine engine,
        string connectionId,
        RuntimeClientKind authorizedClientKind,
        IRuntimeRpcClient client,
        IRuntimeSettingsProcessLauncher settingsProcessLauncher,
        CancellationToken connectionCancellationToken,
        Action<Exception?> abortConnection)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        ConnectionId = string.IsNullOrWhiteSpace(connectionId)
            ? throw new ArgumentException("A runtime connection ID is required.", nameof(connectionId))
            : connectionId.Trim();
        _authorizedClientKind = authorizedClientKind;
        _settingsProcessLauncher = settingsProcessLauncher
            ?? throw new ArgumentNullException(nameof(settingsProcessLauncher));
        _abortConnection = abortConnection ?? throw new ArgumentNullException(nameof(abortConnection));
        _dispatcher = new RuntimeConnectionDispatcher(client, engine.EngineEpoch);
        _engine.InputHost.CaptureChanged += OnCaptureChanged;
        _engine.InputHost.InputObserved += OnInputObserved;
        _connectionCancellationRegistration = connectionCancellationToken.Register(
            static state => ((RuntimeRpcSession)state!).BeginConnectionDisposal(),
            this);
        _ = ObserveDispatcherTerminationAsync();
    }

    internal string ConnectionId { get; }

    public async Task<RuntimeAttachResult> AttachAsync(
        RuntimeAttachRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (_attachCompleted)
            {
                throw new InvalidOperationException("The runtime connection is already attached.");
            }
        }

        if (request.ProtocolMajor != RuntimeProtocol.MajorVersion)
        {
            throw new InvalidOperationException(
                $"Runtime protocol major {request.ProtocolMajor} is incompatible with {RuntimeProtocol.MajorVersion}.");
        }
        if (request.ProtocolMinor < 0)
        {
            throw new InvalidOperationException("The runtime protocol minor version cannot be negative.");
        }
        if (request.ClientKind != _authorizedClientKind)
        {
            throw new UnauthorizedAccessException(
                "The requested runtime client kind does not match the consumed launch ticket.");
        }
        if (_engine.InstanceKind == RuntimeInstanceKind.Production
            && request.ClientKind == RuntimeClientKind.HeadlessTest)
        {
            throw new UnauthorizedAccessException("Headless test clients cannot attach to production.");
        }
        if (request.InstanceKind != _engine.InstanceKind
            || !string.Equals(request.DataRootId, _engine.DataRootId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "The requested runtime instance or data root does not match the host endpoint.");
        }

        var negotiatedMinor = Math.Min(request.ProtocolMinor, RuntimeProtocol.MinorVersion);
        var compatibilityIdentity = _engine.GetIdentitySnapshot();
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            _negotiatedProtocolMinor = negotiatedMinor;
            _compatibilityIdentity = compatibilityIdentity;
        }

        var attached = await _engine.AttachSettingsAsync(
                ConnectionId,
                request.PreviousEngineEpoch,
                request.AfterEventSequence,
                OnRuntimeEvent,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                foreach (var replay in attached.Replay)
                {
                    _dispatcher.EnqueueRuntimeEvent(ForNegotiatedProtocol(replay));
                }
                foreach (var pending in _pendingRuntimeEvents.OrderBy(item => item.Sequence))
                {
                    _dispatcher.EnqueueRuntimeEvent(pending);
                }
                _pendingRuntimeEvents.Clear();
                _attachCompleted = true;
            }

            var identity = _engine.GetIdentitySnapshot();
            var inputEventCursor = _dispatcher.InputCursor;
            var snapshot = new RuntimeSnapshot(
                _engine.EngineEpoch,
                attached.EventCursor,
                identity,
                attached.Snapshot,
                _engine.GetInputSnapshot(ConnectionId),
                negotiatedMinor >= 1 ? _engine.GetUiSnapshot() : null,
                inputEventCursor);
            lock (_gate)
            {
                _compatibilityIdentity = identity;
                if (_authorizedClientKind == RuntimeClientKind.Settings)
                {
                    _settingsProcessLauncher.OnSettingsAttached(ConnectionId);
                    _settingsLifecycleAttached = true;
                }
            }
            return new RuntimeAttachResult(
                ConnectionId,
                RuntimeProtocol.MajorVersion,
                negotiatedMinor,
                RuntimeProtocol.CapabilitiesForMinor(negotiatedMinor),
                RuntimeProtocol.MaximumMessageBytes,
                attached.ResynchronizationRequired,
                snapshot);
        }
        catch
        {
            await _engine.DisconnectAsync(this).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<RuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        EnsureAttached();
        // Both cursors are sampled before the state reads. A concurrent mutation may therefore be
        // represented twice (in the snapshot and its later callback), but it can never be hidden
        // behind a cursor newer than the returned state.
        var eventCursor = _engine.EventCursor;
        var inputEventCursor = _dispatcher.InputCursor;
        var settings = await _engine
            .GetSettingsSnapshotAsync(ConnectionId, cancellationToken)
            .ConfigureAwait(false);
        return new RuntimeSnapshot(
            _engine.EngineEpoch,
            eventCursor,
            _engine.GetIdentitySnapshot(),
            settings,
            _engine.GetInputSnapshot(ConnectionId),
            _negotiatedProtocolMinor >= 1 ? _engine.GetUiSnapshot() : null,
            inputEventCursor);
    }

    public Task<PrepareSettingsResult> PrepareSettingsAsync(
        PrepareSettingsRequest request,
        CancellationToken cancellationToken)
    {
        EnsureAttached();
        return _engine.PrepareSettingsAsync(ConnectionId, request, cancellationToken);
    }

    public Task<ApplySettingsResult> ApplySettingsAsync(
        ApplySettingsRequest request,
        CancellationToken cancellationToken)
    {
        EnsureAttached();
        return _engine.ApplySettingsAsync(ConnectionId, request, cancellationToken);
    }

    public Task<SettingsOperationResult> GetSettingsOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        EnsureAttached();
        return _engine.GetSettingsOperationAsync(ConnectionId, operationId, cancellationToken);
    }

    public Task<RuntimeInputSnapshot> RefreshInputSourcesAsync(CancellationToken cancellationToken)
    {
        EnsureAttached();
        return _engine.RefreshInputSourcesAsync(ConnectionId, cancellationToken);
    }

    public Task<RuntimeCaptureStartResult> BeginInputCaptureAsync(
        RuntimeCaptureRequest request,
        CancellationToken cancellationToken)
    {
        EnsureAttached();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_engine.BeginCapture(this, request));
    }

    public Task<RuntimeCaptureCommandResult> RenewInputCaptureAsync(
        Guid captureId,
        CancellationToken cancellationToken)
    {
        EnsureAttached();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_engine.RenewCapture(this, captureId));
    }

    public Task<RuntimeCaptureCommandResult> CancelInputCaptureAsync(
        Guid captureId,
        CancellationToken cancellationToken)
    {
        EnsureAttached();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_engine.CancelCapture(this, captureId));
    }

    public Task<RuntimeCaptureLookupResult> GetInputCaptureAsync(
        Guid captureId,
        CancellationToken cancellationToken)
    {
        EnsureAttached();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_engine.GetCapture(this, captureId));
    }

    public async Task<RuntimeCommandResult> ExecuteCommandAsync(
        RuntimeCommandRequest request,
        CancellationToken cancellationToken)
    {
        EnsureAttached();
        ValidateCommandAuthorization(request);
        var operation = _engine.ExecuteCommandAsync(ConnectionId, request, CancellationToken.None);
        bool notify;
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            notify = _commandNotifications.Add(request.OperationId);
        }
        if (notify)
        {
            _ = NotifyCommandCompletionAsync(operation);
        }
        return await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<RuntimeCommandOperationResult> GetCommandOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        EnsureAttached();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_engine.GetCommandOperation(ConnectionId, operationId));
    }

    internal RuntimeCaptureLease[] CaptureSnapshot()
    {
        lock (_gate)
        {
            return _captures.Values
                .Where(update => !RuntimeEngine.IsTerminal(update.Lease.Status))
                .Select(update => update.Lease)
                .ToArray();
        }
    }

    internal bool CanBeginCapture()
    {
        lock (_gate)
        {
            return !_disposed && _attachCompleted;
        }
    }

    internal void RecordCapture(
        InputCaptureLease lease,
        JoystickEvent? capturedInput,
        string? detail)
    {
        if (!string.Equals(lease.ConnectionId, ConnectionId, StringComparison.Ordinal))
        {
            return;
        }
        var update = new RuntimeCaptureUpdate(RuntimeEngine.Sanitize(lease), capturedInput, detail);
        var releaseObservation = false;
        lock (_gate)
        {
            if (_disposed
                || (_captures.TryGetValue(lease.CaptureId, out var current)
                    && current.Lease.Revision >= lease.Revision))
            {
                return;
            }
            _captures[lease.CaptureId] = update;
            _dispatcher.EnqueueCapture(update);
            if (RuntimeEngine.IsTerminal(lease.Status))
            {
                releaseObservation = _captureObservations.Remove(lease.CaptureId);
            }
        }
        if (releaseObservation)
        {
            _engine.ReleaseCaptureObservation(lease.CaptureId);
        }
    }

    internal void RecordCaptureObservation(Guid captureId, string sourceId)
    {
        var releaseImmediately = false;
        lock (_gate)
        {
            if (_disposed
                || (_captures.TryGetValue(captureId, out var current)
                    && RuntimeEngine.IsTerminal(current.Lease.Status)))
            {
                releaseImmediately = true;
            }
            else
            {
                _captureObservations.Add(captureId, sourceId);
            }
        }

        if (releaseImmediately)
        {
            _engine.ReleaseCaptureObservation(captureId);
        }
    }

    internal bool TryGetCapture(Guid captureId, out RuntimeCaptureUpdate update)
    {
        lock (_gate)
        {
            return _captures.TryGetValue(captureId, out update!);
        }
    }

    public ValueTask DisposeAsync() => DisposeCoreAsync(disconnectEngine: true);

    internal ValueTask DisposeFromEngineAsync() => DisposeCoreAsync(disconnectEngine: false);

    private async ValueTask DisposeCoreAsync(bool disconnectEngine)
    {
        Guid[] captureObservations;
        bool settingsLifecycleAttached;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            settingsLifecycleAttached = _settingsLifecycleAttached;
            _settingsLifecycleAttached = false;
            captureObservations = [.. _captureObservations.Keys];
            _captureObservations.Clear();
        }

        _connectionCancellationRegistration.Dispose();
        _engine.InputHost.CaptureChanged -= OnCaptureChanged;
        _engine.InputHost.InputObserved -= OnInputObserved;
        if (disconnectEngine)
        {
            await _engine.DisconnectAsync(this).ConfigureAwait(false);
        }
        foreach (var captureId in captureObservations)
        {
            _engine.ReleaseCaptureObservation(captureId);
        }
        await _dispatcher.DisposeAsync().ConfigureAwait(false);
        if (disconnectEngine && settingsLifecycleAttached)
        {
            _settingsProcessLauncher.OnSettingsDisconnected(ConnectionId);
        }
    }

    private void OnRuntimeEvent(RuntimeEvent runtimeEvent)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            if (runtimeEvent.Identity is not null)
            {
                _compatibilityIdentity = runtimeEvent.Identity;
            }
            if (!_attachCompleted)
            {
                _pendingRuntimeEvents.Add(ForNegotiatedProtocol(runtimeEvent));
                return;
            }
            _dispatcher.EnqueueRuntimeEvent(ForNegotiatedProtocol(runtimeEvent));
        }
    }

    private RuntimeEvent ForNegotiatedProtocol(RuntimeEvent runtimeEvent) =>
        _negotiatedProtocolMinor >= 1 || !IsUiEvent(runtimeEvent.Kind)
            ? runtimeEvent
            : new RuntimeEvent(
                runtimeEvent.EngineEpoch,
                runtimeEvent.Sequence,
                RuntimeEventKind.RuntimeIdentityChanged,
                Identity: _compatibilityIdentity);

    private static bool IsUiEvent(RuntimeEventKind kind) => kind is
        RuntimeEventKind.PromptPickerChanged or
        RuntimeEventKind.ButtonMapVisibilityChanged or
        RuntimeEventKind.ControllerStatusChanged or
        RuntimeEventKind.ActionActivityAdded or
        RuntimeEventKind.TaskAlertsChanged or
        RuntimeEventKind.VoiceChanged or
        RuntimeEventKind.PebbleIndexChanged or
        RuntimeEventKind.UiResynchronizationRequired;

    private void OnCaptureChanged(object? sender, InputCaptureChangedEventArgs eventArgs) =>
        RecordCapture(eventArgs.Lease, eventArgs.CapturedInput, eventArgs.Detail);

    private void OnInputObserved(object? sender, InputObservationEventArgs eventArgs)
    {
        lock (_gate)
        {
            if (_disposed
                || !_captures.Values.Any(update =>
                    !RuntimeEngine.IsTerminal(update.Lease.Status)
                    && string.Equals(
                        update.Lease.SourceId,
                        eventArgs.Observation.Source.Descriptor.SourceId,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
            var observation = eventArgs.Observation;
            _dispatcher.EnqueueObservation(new RuntimeInputObservation(
                observation.Sequence,
                observation.Source.Descriptor.SourceId,
                observation.Source.Generation,
                observation.Snapshot,
                observation.Events.ToArray()));
        }
    }

    private async Task NotifyCommandCompletionAsync(Task<RuntimeCommandResult> operation)
    {
        try
        {
            var result = await operation.ConfigureAwait(false);
            lock (_gate)
            {
                if (!_disposed)
                {
                    _dispatcher.EnqueueCommandResult(result);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // The direct RPC or operation lookup reports command failures. Callback transport
            // failure is handled by the dispatcher and must interrupt no runtime owner.
        }
    }

    private void ValidateCommandAuthorization(RuntimeCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind is RuntimeCommandKind.OpenSettings or RuntimeCommandKind.ShutdownRuntime
            && _authorizedClientKind != RuntimeClientKind.Tray)
        {
            throw new UnauthorizedAccessException(
                request.Kind == RuntimeCommandKind.OpenSettings
                    ? "Only the tray client may open settings."
                    : "Only the tray client may stop the runtime.");
        }
    }

    private void EnsureAttached()
    {
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (!_attachCompleted)
            {
                throw new InvalidOperationException("The runtime connection has not attached.");
            }
            _dispatcher.ThrowIfUnavailable();
        }
    }

    private void ThrowIfDisposedLocked() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void BeginConnectionDisposal()
    {
        var disposal = DisposeAsync().AsTask();
        _ = disposal.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ObserveDispatcherTerminationAsync()
    {
        var failure = await _dispatcher.Termination.ConfigureAwait(false);
        if (failure is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        try
        {
            _abortConnection(failure);
        }
        catch
        {
            // Connection cleanup below still releases runtime-owned state if transport abort fails.
        }
        BeginConnectionDisposal();
    }
}
