using Joydex.Contracts;
using Joydex.Core.Input;
using Joydex.Core.Runtime;
using Joydex.RuntimeHost.Settings;

namespace Joydex.RuntimeHost;

/// <summary>
/// Owns one independent runtime generation. UI connections borrow snapshots, commands, and capture
/// leases without owning controller acquisition or settings/resource lifetime.
/// </summary>
internal sealed class RuntimeEngine : IAsyncDisposable
{
    private static long _nextRuntimeGeneration = DateTimeOffset.UtcNow.UtcTicks;
    private readonly object _gate = new();
    private readonly RuntimeEngineOptions _options;
    private readonly IRuntimeOwnershipLease _ownershipLease;
    private readonly CancellationTokenSource _runtimeCancellation;
    private readonly RuntimeEventHub _events;
    private readonly RuntimeInputHost _inputHost;
    private readonly IRuntimeComposition _composition;
    private readonly RuntimeSettingsCoordinator _settings;
    private readonly RuntimeCommandExecutor _commands;
    private readonly SemaphoreSlim _activationBoundaryGate = new(1, 1);
    private readonly TaskCompletionSource _shutdownRequested = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, RuntimeRpcSession> _sessions = new(StringComparer.Ordinal);
    private RuntimeInputSource[] _sources;
    private int _shutdownSignalScheduled;
    private bool _disposed;

    private RuntimeEngine(
        RuntimeEngineOptions options,
        IRuntimeOwnershipLease ownershipLease,
        CancellationTokenSource runtimeCancellation,
        RuntimeEventHub events,
        RuntimeInputHost inputHost,
        IRuntimeComposition composition,
        RuntimeSettingsCoordinator settings,
        RuntimeCommandExecutor commands,
        RuntimeInputSource[] sources)
    {
        _options = options;
        _ownershipLease = ownershipLease;
        _runtimeCancellation = runtimeCancellation;
        _events = events;
        _inputHost = inputHost;
        _composition = composition;
        _settings = settings;
        _commands = commands;
        _sources = sources;
        EngineEpoch = events.EngineEpoch;
        RuntimeGeneration = Interlocked.Increment(ref _nextRuntimeGeneration);
        OwnerId = $"runtime-{EngineEpoch:N}";
        _composition.UiChanged += OnUiChanged;
        _composition.ActivationBoundaryAvailable += OnActivationBoundaryAvailable;
    }

    public Guid EngineEpoch { get; }
    public long RuntimeGeneration { get; }
    public string OwnerId { get; }
    public RuntimeInstanceKind InstanceKind => _options.InstanceKind;
    public string DataRootId => _options.DataRootId;
    internal long EventCursor => _events.Cursor;
    internal RuntimeInputHost InputHost => _inputHost;
    internal Task ShutdownRequested => _shutdownRequested.Task;

    internal static Task<RuntimeEngine> StartSyntheticAsync(
        string dataRoot,
        string instanceName,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var normalizedRoot = Path.GetFullPath(dataRoot.Trim());
        return StartSyntheticForConfigurationAsync(
            Path.Combine(normalizedRoot, "config.json"),
            normalizedRoot,
            instanceName,
            timeProvider);
    }

    internal static Task<RuntimeEngine> StartSyntheticForConfigurationAsync(
        string configurationPath,
        string dataRootId,
        string instanceName,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRootId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        var normalizedConfigurationPath = Path.GetFullPath(configurationPath.Trim());
        var normalizedRoot = Path.GetDirectoryName(normalizedConfigurationPath)
            ?? throw new ArgumentException(
                "The companion configuration path has no parent directory.",
                nameof(configurationPath));
        var options = new RuntimeEngineOptions(
            RuntimeInstanceKind.Synthetic,
            normalizedRoot,
            dataRootId.Trim(),
            RuntimeSettingsPaths.ForConfiguration(normalizedConfigurationPath),
            new SyntheticRuntimeOwnershipLeaseFactory(),
            (host, _) => new SyntheticRuntimeComposition(host, instanceName),
            new DefaultSettingsImpactPlanner(),
            timeProvider ?? TimeProvider.System);
        return StartAsync(options);
    }

    internal static async Task<RuntimeEngine> StartAsync(
        RuntimeEngineOptions options,
        CancellationToken startupCancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DataRootId);
        startupCancellationToken.ThrowIfCancellationRequested();

        IRuntimeOwnershipLease? ownershipLease = null;
        CancellationTokenSource? runtimeCancellation = null;
        RuntimeInputHost? inputHost = null;
        IRuntimeComposition? composition = null;
        RuntimeSettingsCoordinator? settings = null;
        try
        {
            // Production takes the legacy guard before touching configuration or hardware. This
            // ordering lets an already-running legacy Joydex process block the new engine safely.
            ownershipLease = options.OwnershipLeaseFactory.Acquire();
            Directory.CreateDirectory(Path.GetFullPath(options.DataRoot));
            runtimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                startupCancellationToken);
            var events = new RuntimeEventHub(Guid.NewGuid());
            inputHost = new RuntimeInputHost(options.TimeProvider);
            composition = options.CompositionFactory(inputHost, runtimeCancellation.Token);
            settings = RuntimeSettingsCoordinator.Create(
                options.SettingsPaths,
                options.SettingsImpactPlanner,
                composition,
                options.TimeProvider,
                runtimeCancellation.Token,
                new SettingsDocumentIo(),
                SettingsTransactionObserver.Instance,
                events);

            var active = await settings
                .ReconcileStartupAsync(runtimeCancellation.Token)
                .ConfigureAwait(false);
            var sources = composition.Refresh(active.Active);
            events.Publish(
                RuntimeEventKind.InputSourcesChanged,
                inputSources: new RuntimeInputSourceSnapshot(sources));

            var commands = new RuntimeCommandExecutor(
                new RuntimeAuthorityCommandHandler(
                    settings,
                    composition,
                    options.SettingsProcessLauncher ?? UnsupportedRuntimeSettingsProcessLauncher.Instance),
                runtimeCancellation.Token);
            var engine = new RuntimeEngine(
                options,
                ownershipLease,
                runtimeCancellation,
                events,
                inputHost,
                composition,
                settings,
                commands,
                sources);
            ownershipLease = null;
            runtimeCancellation = null;
            inputHost = null;
            composition = null;
            settings = null;
            events.Publish(RuntimeEventKind.RuntimeIdentityChanged, identity: engine.GetIdentitySnapshot());
            return engine;
        }
        catch
        {
            if (settings is not null)
            {
                await settings.DisposeAsync().ConfigureAwait(false);
            }
            if (composition is not null)
            {
                await composition.DisposeAsync().ConfigureAwait(false);
            }
            inputHost?.Dispose();
            runtimeCancellation?.Cancel();
            runtimeCancellation?.Dispose();
            ownershipLease?.Dispose();
            throw;
        }
    }

    internal RuntimeRpcSession CreateSession(
        string connectionId,
        RuntimeClientKind authorizedClientKind,
        IRuntimeRpcClient client,
        CancellationToken connectionCancellationToken,
        Action<Exception?>? abortConnection = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(client);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sessions.ContainsKey(connectionId))
            {
                throw new InvalidOperationException($"Connection '{connectionId}' already exists.");
            }
            var session = new RuntimeRpcSession(
                this,
                connectionId,
                authorizedClientKind,
                client,
                _options.SettingsProcessLauncher ?? UnsupportedRuntimeSettingsProcessLauncher.Instance,
                connectionCancellationToken,
                abortConnection ?? (static _ => { }));
            _sessions.Add(connectionId, session);
            return session;
        }
    }

    internal RuntimeIdentitySnapshot GetIdentitySnapshot()
    {
        lock (_gate)
        {
            var resources = new List<RuntimeResourceIdentity>
            {
                new("engine", OwnerId, RuntimeGeneration, "Active"),
                new(
                    "voice",
                    OwnerId,
                    RuntimeGeneration,
                    _options.InstanceKind == RuntimeInstanceKind.Synthetic
                        ? "SyntheticArmed"
                        : "HostOwned"),
            };
            resources.AddRange(_sources.Select(source => new RuntimeResourceIdentity(
                $"input:{source.SourceId}",
                OwnerId,
                source.Generation ?? 0,
                source.Connected ? "Connected" : "Available")));
            return new RuntimeIdentitySnapshot(
                Environment.ProcessId,
                _options.InstanceKind,
                _options.DataRootId,
                RuntimeGeneration,
                resources.ToArray());
        }
    }

    internal RuntimeInputSnapshot GetInputSnapshot(string connectionId) => new(
        GetSources(),
        GetSession(connectionId).CaptureSnapshot());

    internal RuntimeUiSnapshot GetUiSnapshot() => _composition.GetUiSnapshot();

    internal async Task<RuntimeInputSnapshot> RefreshInputSourcesAsync(
        string connectionId,
        CancellationToken cancellationToken)
    {
        var active = await _settings.GetSnapshotAsync(connectionId, cancellationToken).ConfigureAwait(false);
        var sources = _composition.Refresh(active.Active);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _sources = sources;
        }
        _events.Publish(
            RuntimeEventKind.InputSourcesChanged,
            inputSources: new RuntimeInputSourceSnapshot(sources));
        _events.Publish(RuntimeEventKind.RuntimeIdentityChanged, identity: GetIdentitySnapshot());
        return GetInputSnapshot(connectionId);
    }

    internal RuntimeCaptureStartResult BeginCapture(
        RuntimeRpcSession session,
        RuntimeCaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (_disposed
                || !_sessions.TryGetValue(session.ConnectionId, out var registered)
                || !ReferenceEquals(registered, session)
                || !session.CanBeginCapture())
            {
                throw new ObjectDisposedException(nameof(RuntimeRpcSession));
            }

            var started = _inputHost.BeginCapture(new InputCaptureRequest(
                session.ConnectionId,
                request.SourceId,
                request.Purpose,
                request.ExpectedGeneration,
                request.Timeout));
            if (!started.Accepted || started.Lease is null)
            {
                return new RuntimeCaptureStartResult(false, null, started.Error);
            }

            session.RecordCapture(started.Lease, capturedInput: null, detail: null);
            if (!_composition.ObserveForCapture(started.Lease.CaptureId, started.Lease.SourceId))
            {
                _inputHost.CancelCapture(started.Lease.CaptureId, session.ConnectionId);
                return new RuntimeCaptureStartResult(
                    false,
                    null,
                    "The runtime could not observe the selected input source.");
            }
            session.RecordCaptureObservation(started.Lease.CaptureId, started.Lease.SourceId);
            return new RuntimeCaptureStartResult(true, Sanitize(started.Lease), null);
        }
    }

    internal RuntimeCaptureCommandResult RenewCapture(RuntimeRpcSession session, Guid captureId)
    {
        var renewed = _inputHost.RenewCapture(captureId, session.ConnectionId);
        return renewed && session.TryGetCapture(captureId, out var update)
            ? new RuntimeCaptureCommandResult(true, update.Lease, null)
            : new RuntimeCaptureCommandResult(false, null, "The capture is no longer active.");
    }

    internal RuntimeCaptureCommandResult CancelCapture(RuntimeRpcSession session, Guid captureId)
    {
        var cancelled = _inputHost.CancelCapture(captureId, session.ConnectionId);
        return cancelled && session.TryGetCapture(captureId, out var update)
            ? new RuntimeCaptureCommandResult(true, update.Lease, null)
            : new RuntimeCaptureCommandResult(false, null, "The capture is no longer active.");
    }

    internal RuntimeCaptureLookupResult GetCapture(RuntimeRpcSession session, Guid captureId) =>
        session.TryGetCapture(captureId, out var update)
            ? new RuntimeCaptureLookupResult(
                IsTerminal(update.Lease.Status)
                    ? RuntimeCaptureLookupStatus.Completed
                    : RuntimeCaptureLookupStatus.Active,
                update)
            : new RuntimeCaptureLookupResult(RuntimeCaptureLookupStatus.NotFound);

    internal void ReleaseCaptureObservation(Guid captureId) =>
        _composition.ReleaseCaptureObservation(captureId);

    internal Task<SettingsAttachState> AttachSettingsAsync(
        string connectionId,
        Guid? previousEpoch,
        long? afterSequence,
        Action<RuntimeEvent> eventSink,
        CancellationToken cancellationToken) => _settings.AttachAsync(
            connectionId,
            previousEpoch,
            afterSequence,
            eventSink,
            cancellationToken);

    internal Task<SettingsSnapshot> GetSettingsSnapshotAsync(
        string connectionId,
        CancellationToken cancellationToken) => _settings.GetSnapshotAsync(connectionId, cancellationToken);

    internal Task<PrepareSettingsResult> PrepareSettingsAsync(
        string connectionId,
        PrepareSettingsRequest request,
        CancellationToken cancellationToken) => _settings.PrepareAsync(connectionId, request, cancellationToken);

    internal Task<ApplySettingsResult> ApplySettingsAsync(
        string connectionId,
        ApplySettingsRequest request,
        CancellationToken cancellationToken) => _settings.ApplyAsync(connectionId, request, cancellationToken);

    internal Task<SettingsOperationResult> GetSettingsOperationAsync(
        string connectionId,
        Guid operationId,
        CancellationToken cancellationToken) => _settings.GetOperationAsync(
            connectionId,
            operationId,
            cancellationToken);

    internal Task<RuntimeCommandResult> ExecuteCommandAsync(
        string connectionId,
        RuntimeCommandRequest request,
        CancellationToken cancellationToken)
    {
        var operation = _commands.ExecuteAsync(connectionId, request, CancellationToken.None);
        if (request.Kind == RuntimeCommandKind.ShutdownRuntime)
        {
            var observer = ObserveShutdownAcceptanceAsync(operation);
            _ = observer.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        return operation.WaitAsync(cancellationToken);
    }

    internal RuntimeCommandOperationResult GetCommandOperation(string connectionId, Guid operationId) =>
        _commands.GetOperation(connectionId, operationId);

    internal void ScheduleShutdownAfterReply()
    {
        if (Interlocked.Exchange(ref _shutdownSignalScheduled, 1) != 0)
        {
            return;
        }

        var delayed = SignalShutdownAfterReplyGraceAsync();
        _ = delayed.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal async Task DisconnectAsync(RuntimeRpcSession session)
    {
        lock (_gate)
        {
            _sessions.Remove(session.ConnectionId);
        }
        _inputHost.DisconnectClient(session.ConnectionId);
        _commands.Disconnect(session.ConnectionId);
        await _settings.DisconnectAsync(session.ConnectionId).ConfigureAwait(false);
    }

    internal async Task PublishSyntheticInputAsync(
        JoystickSnapshot snapshot,
        IReadOnlyList<JoystickEvent> events)
    {
        if (_composition is not SyntheticRuntimeComposition synthetic)
        {
            throw new InvalidOperationException("Synthetic input is unavailable in production.");
        }
        await synthetic.Inputs.PublishAsync(snapshot, events).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        RuntimeRpcSession[] sessions;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
        }

        var failures = new List<Exception>();
        void Cleanup(Action action)
        {
            try { action(); }
            catch (Exception exception) { failures.Add(exception); }
        }
        async ValueTask CleanupAsync(Func<ValueTask> action)
        {
            try { await action().ConfigureAwait(false); }
            catch (Exception exception) { failures.Add(exception); }
        }

        Cleanup(_runtimeCancellation.Cancel);
        Cleanup(() => _composition.UiChanged -= OnUiChanged);
        Cleanup(() => _composition.ActivationBoundaryAvailable -= OnActivationBoundaryAvailable);
        foreach (var session in sessions)
        {
            await CleanupAsync(session.DisposeFromEngineAsync).ConfigureAwait(false);
        }
        await _activationBoundaryGate.WaitAsync().ConfigureAwait(false);
        _activationBoundaryGate.Release();
        await CleanupAsync(_settings.DisposeAsync).ConfigureAwait(false);
        await CleanupAsync(_composition.DisposeAsync).ConfigureAwait(false);
        Cleanup(_inputHost.Dispose);
        Cleanup(_runtimeCancellation.Dispose);
        Cleanup(_ownershipLease.Dispose);
        if (failures.Count > 0)
        {
            throw new AggregateException("Runtime cleanup failed.", failures);
        }
    }

    private void OnUiChanged(object? sender, RuntimeUiEvent runtimeUiEvent)
    {
        _events.Publish(
            runtimeUiEvent.Kind,
            promptPicker: runtimeUiEvent.PromptPicker,
            buttonMapVisibility: runtimeUiEvent.ButtonMapVisibility,
            controllerStatus: runtimeUiEvent.ControllerStatus,
            actionActivity: runtimeUiEvent.ActionActivity,
            taskAlerts: runtimeUiEvent.TaskAlerts,
            voice: runtimeUiEvent.Voice,
            pebbleIndex: runtimeUiEvent.PebbleIndex);
    }

    private void OnActivationBoundaryAvailable(object? sender, EventArgs eventArgs)
    {
        var activation = ActivateAtAvailableBoundaryAsync();
        _ = activation.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ActivateAtAvailableBoundaryAsync()
    {
        try
        {
            await _activationBoundaryGate.WaitAsync(_runtimeCancellation.Token).ConfigureAwait(false);
            try
            {
                _ = await _settings.ActivateDesiredAsync(
                    SettingsAggregateId.Voice,
                    _runtimeCancellation.Token).ConfigureAwait(false);
                _ = await _settings.ActivateDesiredAsync(
                    SettingsAggregateId.Companion,
                    _runtimeCancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                _activationBoundaryGate.Release();
            }
        }
        catch (OperationCanceledException) when (_runtimeCancellation.IsCancellationRequested)
        {
        }
    }

    private async Task SignalShutdownAfterReplyGraceAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), _runtimeCancellation.Token)
                .ConfigureAwait(false);
            _shutdownRequested.TrySetResult();
        }
        catch (OperationCanceledException) when (_runtimeCancellation.IsCancellationRequested)
        {
        }
    }

    private async Task ObserveShutdownAcceptanceAsync(Task<RuntimeCommandResult> operation)
    {
        var result = await operation.ConfigureAwait(false);
        if (result.Status == RuntimeCommandStatus.Completed)
        {
            ScheduleShutdownAfterReply();
        }
    }

    private RuntimeRpcSession GetSession(string connectionId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _sessions.TryGetValue(connectionId, out var session)
                ? session
                : throw new InvalidOperationException("The runtime connection is not active.");
        }
    }

    private RuntimeInputSource[] GetSources()
    {
        lock (_gate)
        {
            return [.. _sources];
        }
    }

    internal static RuntimeCaptureLease Sanitize(InputCaptureLease lease) => new(
        lease.CaptureId,
        lease.SourceId,
        lease.Purpose,
        lease.SourceGeneration,
        lease.ExpiresAt,
        lease.Status,
        lease.Revision);

    internal static bool IsTerminal(InputCaptureStatus status) => status is not
        (InputCaptureStatus.Pending or InputCaptureStatus.Active);
}
