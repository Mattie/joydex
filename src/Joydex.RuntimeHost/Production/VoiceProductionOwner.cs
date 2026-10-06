using Joydex.Contracts;
using Joydex.Core.Config;
using Joydex.Core.Mapping;
using Joydex.Core.Voice;
using Joydex.Ipc;
using Joydex.RuntimeHost.Plugins;
using Joydex.RuntimeHost.Plugins.Voice;
using Joydex.Windows.Actions;
using Joydex.Windows.Voice;

namespace Joydex.RuntimeHost.Production;

internal interface IVoicePluginHostServices
{
    VoiceHostCommandPolicy CreateVoiceHostPolicy(SettingsBundle activeSettings);

    void PublishVoice(ProductionVoiceState state, bool reset = false);

    void PublishVoiceBecameIdle();

    void PublishVoiceHealth(VoiceProductionOwner owner, BundledPluginHealth health);

    void ClearVoiceOwner(VoiceProductionOwner owner);

    void WriteLog(string message);
}

internal sealed record VoiceHostCommandPolicy(
    VoicePePreferences Preferences,
    SafetyOptions Safety,
    IPinnedVoiceTargetNavigator Navigator,
    Func<ActionRequest, CancellationToken, Task<ActionExecutionResult>> ExecuteActionAsync);

internal sealed class VoiceProductionOwner : IProductionVoiceOwner, IVoiceWorkerHostCallbacks
{
    private static readonly TimeSpan StableGenerationDuration = TimeSpan.FromMinutes(1);
    private readonly IVoicePluginHostServices _factory;
    private readonly ProductionRuntimePaths _paths;
    private readonly IVoiceWorkerGenerationFactory _generations;
    private readonly ProductionDesktopBrokerLease? _desktopBrokerLease;
    private readonly VoicePePreferences _preferences;
    private readonly CancellationTokenSource _lifetime;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _stateGate = new();
    private readonly object _disposeGate = new();
    private readonly Func<int, CancellationToken, Task> _retryDelay;
    private readonly TimeProvider _timeProvider;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IVoiceWorkerGeneration? _instance;
    private Task? _monitor;
    private Task? _disposeTask;
    private VoiceHostCommandPolicy _policy;
    private ProductionVoiceState _state;
    private BundledPluginHealth _health;
    private long _generation;
    private long _acceptedSequence;
    private long _activeGeneration;
    private long _lastIdleSequence;
    private int _restartAttempt;
    private bool _wasSessionActive;
    private bool _committed;
    private bool _disposed;
    private Exception? _terminalCleanupFailure;

    private VoiceProductionOwner(
        IVoicePluginHostServices factory,
        ProductionRuntimePaths paths,
        IVoiceWorkerGenerationFactory generations,
        ProductionDesktopBrokerLease? desktopBrokerLease,
        SettingsBundle activeSettings,
        Func<int, CancellationToken, Task> retryDelay,
        TimeProvider timeProvider,
        CancellationToken runtimeCancellationToken)
    {
        _factory = factory;
        _paths = paths;
        _generations = generations;
        _desktopBrokerLease = desktopBrokerLease;
        _preferences = activeSettings.Voice.Normalize();
        _policy = CreatePolicy(activeSettings);
        _retryDelay = retryDelay;
        _timeProvider = timeProvider;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(runtimeCancellationToken);
        _state = UnavailableState("Room Voice worker is starting.");
        _health = Health(BundledPluginLifecycleState.Starting, 0, "Room Voice worker is starting.");
    }

    public SettingsAggregateId Aggregate => SettingsAggregateId.Voice;

    public Task Completion => _completion.Task;

    public bool IsSessionActive
    {
        get { lock (_stateGate) { return _state.Snapshot.SessionActive; } }
    }

    internal BundledPluginHealth HealthSnapshot
    {
        get { lock (_stateGate) { return _health; } }
    }

    internal bool MatchesPreferences(VoicePePreferences preferences) =>
        _preferences == preferences.Normalize();

    internal void Commit()
    {
        _lifecycle.Wait();
        try
        {
            ThrowIfDisposed();
            if (_committed)
            {
                return;
            }
            _committed = true;
            var instance = _instance
                ?? throw new InvalidOperationException("Room Voice has no initialized worker generation.");
            AcceptSnapshot(instance.ActivateCallbacks());
            PublishHealth(Health(BundledPluginLifecycleState.Ready, instance.Generation,
                "Room Voice worker is initialized."));
            _monitor = ObserveGenerationAsync(instance, _timeProvider.GetUtcNow());
        }
        finally { _lifecycle.Release(); }
    }

    public static Task<VoiceProductionOwner> StartAsync(
        WindowsProductionRuntimeOwnerFactory factory,
        ProductionRuntimePaths paths,
        ProductionDesktopBrokerManager desktopBroker,
        SettingsBundle activeSettings,
        CancellationToken runtimeCancellationToken,
        CancellationToken cancellationToken) => StartAsync(
            factory,
            paths,
            desktopBroker,
            new VoiceWorkerProcessGenerationFactory(
                paths.VoiceWorker,
                WindowsRuntimeSettingsProcessFactory.Instance),
            activeSettings,
            static (attempt, token) => Task.Delay(RetryDelay(attempt), token),
            TimeProvider.System,
            runtimeCancellationToken,
            cancellationToken);

    internal static async Task<VoiceProductionOwner> StartAsync(
        IVoicePluginHostServices factory,
        ProductionRuntimePaths paths,
        ProductionDesktopBrokerManager desktopBroker,
        IVoiceWorkerGenerationFactory generations,
        SettingsBundle activeSettings,
        Func<int, CancellationToken, Task> retryDelay,
        TimeProvider timeProvider,
        CancellationToken runtimeCancellationToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(desktopBroker);
        ArgumentNullException.ThrowIfNull(generations);
        ArgumentNullException.ThrowIfNull(activeSettings);
        ArgumentNullException.ThrowIfNull(retryDelay);
        ArgumentNullException.ThrowIfNull(timeProvider);
        var preferences = activeSettings.Voice.Normalize();
        ProductionDesktopBrokerLease? brokerLease = null;
        VoiceProductionOwner? owner = null;
        try
        {
            // Ownership handoff needs the bridge even when outbound messaging is disabled.
            // Keep the lease in dry run too: safety policy can change without replacing this owner.
            if (preferences.SessionMode == VoicePeSessionMode.JoydexOwner
                || preferences.DesktopTaskMessagingEnabled)
            {
                brokerLease = await desktopBroker.AcquireAsync(cancellationToken).ConfigureAwait(false);
            }
            owner = new VoiceProductionOwner(
                factory,
                paths,
                generations,
                brokerLease,
                activeSettings,
                retryDelay,
                timeProvider,
                runtimeCancellationToken);
            brokerLease = null;
            await owner.StartCandidateAsync(cancellationToken).ConfigureAwait(false);
            return owner;
        }
        catch (Exception startupFailure)
        {
            var cleanupFailures = new List<Exception>();
            if (owner is not null)
            {
                try { await owner.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            if (brokerLease is not null)
            {
                try { await brokerLease.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            if (cleanupFailures.Count > 0)
            {
                throw new VoiceOwnershipCleanupException(
                    "Room Voice startup failed and worker cleanup was incomplete.",
                    [startupFailure, .. cleanupFailures]);
            }
            throw;
        }
    }

    internal static async Task<VoiceProductionOwner> StartForTestAsync(
        IVoicePluginHostServices host,
        ProductionRuntimePaths paths,
        IVoiceWorkerGenerationFactory generations,
        SettingsBundle activeSettings,
        Func<int, CancellationToken, Task> retryDelay,
        TimeProvider timeProvider,
        CancellationToken runtimeCancellationToken,
        CancellationToken cancellationToken)
    {
        var owner = new VoiceProductionOwner(
            host,
            paths,
            generations,
            desktopBrokerLease: null,
            activeSettings,
            retryDelay,
            timeProvider,
            runtimeCancellationToken);
        try
        {
            await owner.StartCandidateAsync(cancellationToken).ConfigureAwait(false);
            return owner;
        }
        catch (Exception startupFailure)
        {
            try { await owner.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupFailure)
            {
                throw new VoiceOwnershipCleanupException(
                    "Room Voice startup failed and worker cleanup was incomplete.",
                    [startupFailure, cleanupFailure]);
            }
            throw;
        }
    }

    public async Task EndSessionAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_instance is { } instance && IsSessionActive)
            {
                await instance.EndSessionAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally { _lifecycle.Release(); }
    }

    public async Task RefreshConversationAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_instance is not { } instance)
            {
                throw new InvalidOperationException("Room Voice worker is unavailable.");
            }
            await instance.RefreshConversationAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    public ProductionVoiceState GetState()
    {
        lock (_stateGate) { return _state; }
    }

    public async Task<RuntimeVoiceConversationPage> GetConversationPageAsync(
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return _instance is { } instance
                ? await instance.GetConversationPageAsync(continuationToken, cancellationToken).ConfigureAwait(false)
                : throw new InvalidOperationException("Room Voice worker is unavailable.");
        }
        finally { _lifecycle.Release(); }
    }

    internal void RefreshPolicy(SettingsBundle activeSettings)
    {
        ArgumentNullException.ThrowIfNull(activeSettings);
        Volatile.Write(ref _policy, CreatePolicy(activeSettings));
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) { return new ValueTask(_disposeTask ??= DisposeCoreAsync()); }
    }

    void IVoiceWorkerHostCallbacks.PublishSnapshot(VoiceWorkerSnapshot snapshot) => AcceptSnapshot(snapshot);

    void IVoiceWorkerHostCallbacks.VoiceBecameIdle(long generation, long sequence)
    {
        var hiddenIdleEdge = false;
        lock (_stateGate)
        {
            hiddenIdleEdge = !_disposed
                && _activeGeneration == generation
                && sequence == _acceptedSequence
                && sequence > _lastIdleSequence
                && _state.Snapshot.SessionActive;
            if (hiddenIdleEdge) { _lastIdleSequence = sequence; }
        }
        if (hiddenIdleEdge) { _factory.PublishVoiceBecameIdle(); }
    }

    async Task<bool> IVoiceWorkerHostCallbacks.NavigateAsync(
        long generation,
        string taskId,
        CancellationToken cancellationToken)
    {
        var policy = Volatile.Read(ref _policy);
        if (!IsCurrentGeneration(generation)
            || !string.Equals(taskId, policy.Preferences.PinnedTaskId, StringComparison.Ordinal))
        {
            return false;
        }
        var navigated = await policy.Navigator.NavigateAsync(taskId, cancellationToken).ConfigureAwait(false);
        return IsCurrentGeneration(generation) && navigated;
    }

    async Task<ActionExecutionResult> IVoiceWorkerHostCallbacks.ExecuteActionAsync(
        long generation,
        ActionRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsCurrentGeneration(generation))
        {
            return ActionExecutionResult.Blocked("The Voice worker generation is stale.");
        }
        var policy = Volatile.Read(ref _policy);
        var fixedRequest = new ActionRequest(
            "Voice PE wake",
            CompanionConfig.AlwaysBank,
            0,
            "wake",
            CodexAction.StartVoiceChat,
            DateTimeOffset.UtcNow,
            DeviceId: "voice-pe");
        var result = await policy.ExecuteActionAsync(fixedRequest, cancellationToken).ConfigureAwait(false);
        return IsCurrentGeneration(generation)
            ? result
            : ActionExecutionResult.Blocked("The Voice worker generation is stale.");
    }

    void IVoiceWorkerHostCallbacks.WriteLog(long generation, string message)
    {
        if (IsCurrentGeneration(generation)) { _factory.WriteLog(message); }
    }

    internal static int DecodeContinuationToken(string? token, long conversationVersion)
    {
        if (string.IsNullOrWhiteSpace(token)) { return 0; }
        try
        {
            var pieces = System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(token)).Split(':', 2);
            if (pieces.Length != 2
                || !long.TryParse(pieces[0], out var version)
                || version != conversationVersion
                || !int.TryParse(pieces[1], out var offset)
                || offset < 0)
            {
                throw new InvalidDataException(
                    "The Voice conversation changed while it was being read; restart from the first page.");
            }
            return offset;
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The Voice conversation continuation token is invalid.", exception);
        }
    }

    internal static string EncodeContinuationToken(long conversationVersion, int offset) =>
        Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{conversationVersion}:{offset}"));

    private async Task StartCandidateAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var instance = await StartGenerationAsync(cancellationToken).ConfigureAwait(false);
            InstallGeneration(instance);
        }
        catch (Exception exception)
        {
            PublishHealth(Health(
                exception is VoiceOwnershipCleanupException
                    ? BundledPluginLifecycleState.Blocked
                    : BundledPluginLifecycleState.Faulted,
                _generation,
                exception is VoiceOwnershipCleanupException
                    ? "Room Voice worker cleanup was not confirmed."
                    : "Room Voice worker could not initialize."));
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    private async Task<IVoiceWorkerGeneration> StartGenerationAsync(CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _generation);
        PublishHealth(Health(BundledPluginLifecycleState.Starting, generation,
            "Room Voice worker is starting."));
        var policy = Volatile.Read(ref _policy);
        return await _generations.StartAsync(
                new VoiceWorkerGenerationConfiguration(
                    generation,
                    _preferences,
                    policy.Safety,
                    _paths.VoiceWebViewData,
                    _paths.VoiceActivePreferences,
                    _paths.DesktopBridgeHost,
                    _desktopBrokerLease?.PipeName ?? "Joydex.DesktopBridge.unavailable"),
                this,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ObserveGenerationAsync(IVoiceWorkerGeneration instance, DateTimeOffset startedAt)
    {
        try
        {
            try { await instance.Completion.WaitAsync(_lifetime.Token).ConfigureAwait(false); } catch { }
            await RecoverAsync(instance, startedAt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _factory.WriteLog("Room Voice worker recovery failed (" + exception.GetType().Name + ").");
        }
    }

    private async Task RecoverAsync(IVoiceWorkerGeneration failed, DateTimeOffset startedAt)
    {
        await _lifecycle.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (_disposed || !ReferenceEquals(_instance, failed)) { return; }
            failed.DeactivateCallbacks();
            lock (_stateGate) { _activeGeneration = 0; }
            PublishHealth(Health(BundledPluginLifecycleState.Retrying, failed.Generation,
                "Room Voice worker is stopping before restart."));
            _instance = null;
            PublishUnavailableAfterLoss(failed.Generation);
            try { await failed.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception)
            {
                _terminalCleanupFailure = exception;
                PublishTerminalUnavailable("Room Voice worker cleanup was not confirmed.");
                PublishHealth(Health(BundledPluginLifecycleState.Blocked, failed.Generation,
                    "Room Voice worker cleanup was not confirmed."));
                return;
            }

            if (_timeProvider.GetUtcNow() - startedAt >= StableGenerationDuration)
            {
                _restartAttempt = 0;
            }
            _restartAttempt++;
            while (!_disposed)
            {
                PublishHealth(Health(BundledPluginLifecycleState.Retrying, failed.Generation,
                    "Room Voice worker is waiting to restart."));
                await _retryDelay(_restartAttempt, _lifetime.Token).ConfigureAwait(false);
                IVoiceWorkerGeneration replacement;
                try { replacement = await StartGenerationAsync(_lifetime.Token).ConfigureAwait(false); }
                catch (VoiceOwnershipCleanupException exception)
                {
                    _terminalCleanupFailure = exception;
                    PublishTerminalUnavailable("Room Voice worker cleanup was not confirmed.");
                    PublishHealth(Health(BundledPluginLifecycleState.Blocked, _generation,
                        "Room Voice worker cleanup was not confirmed."));
                    return;
                }
                catch (Exception exception) when (IsTransientStartupFailure(exception))
                {
                    _restartAttempt++;
                    PublishHealth(Health(BundledPluginLifecycleState.Retrying, _generation,
                        "Room Voice worker restart failed; another attempt is scheduled."));
                    continue;
                }
                catch
                {
                    PublishTerminalUnavailable("Room Voice worker could not restart.");
                    PublishHealth(Health(BundledPluginLifecycleState.Faulted, _generation,
                        "Room Voice worker could not restart."));
                    return;
                }
                InstallGeneration(replacement);
                AcceptSnapshot(replacement.ActivateCallbacks());
                PublishHealth(Health(BundledPluginLifecycleState.Ready, replacement.Generation,
                    "Room Voice worker is initialized."));
                _monitor = ObserveGenerationAsync(replacement, _timeProvider.GetUtcNow());
                return;
            }
        }
        finally { _lifecycle.Release(); }
    }

    private async Task DisposeCoreAsync()
    {
        var preserveStartupFailure = !_committed && HealthSnapshot.State is
            BundledPluginLifecycleState.Faulted or BundledPluginLifecycleState.Blocked;
        lock (_stateGate) { _disposed = true; }
        await _lifetime.CancelAsync().ConfigureAwait(false);
        List<Exception>? failures = _terminalCleanupFailure is null
            ? null
            : [_terminalCleanupFailure];
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!preserveStartupFailure)
            {
                PublishHealth(Health(BundledPluginLifecycleState.Stopping, _generation,
                    "Room Voice worker is stopping."));
            }
            var instance = _instance;
            instance?.DeactivateCallbacks();
            _instance = null;
            lock (_stateGate) { _activeGeneration = 0; }
            if (instance is not null)
            {
                try { await instance.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
            }
        }
        finally { _lifecycle.Release(); }
        var monitor = _monitor;
        if (monitor is not null)
        {
            try { await monitor.ConfigureAwait(false); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        if (_desktopBrokerLease is not null)
        {
            try { await _desktopBrokerLease.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        _lifetime.Dispose();
        _completion.TrySetResult();
        _factory.ClearVoiceOwner(this);
        if (!preserveStartupFailure)
        {
            PublishHealth(Health(
                failures is null ? BundledPluginLifecycleState.Stopped : BundledPluginLifecycleState.Blocked,
                _generation,
                failures is null ? "Room Voice worker is stopped." : "Room Voice worker cleanup was not confirmed."));
        }
        if (failures is not null)
        {
            throw new VoiceOwnershipCleanupException("Room Voice worker cleanup was not confirmed.", failures);
        }
    }

    private void AcceptSnapshot(VoiceWorkerSnapshot snapshot)
    {
        VoiceWorkerSnapshotValidator.Validate(snapshot, snapshot.Generation);
        var becameIdle = false;
        lock (_stateGate)
        {
            if (_disposed || snapshot.Generation != _activeGeneration || snapshot.Sequence <= _acceptedSequence) { return; }
            _acceptedSequence = snapshot.Sequence;
            becameIdle = _wasSessionActive && !snapshot.Voice.SessionActive;
            if (becameIdle) { _lastIdleSequence = snapshot.Sequence; }
            _wasSessionActive = snapshot.Voice.SessionActive;
            _state = new ProductionVoiceState(snapshot.Voice, snapshot.Timeline);
        }
        _factory.PublishVoice(GetState());
        if (becameIdle) { _factory.PublishVoiceBecameIdle(); }
    }

    private void PublishUnavailableAfterLoss(long generation)
    {
        var becameIdle = false;
        lock (_stateGate)
        {
            if (_disposed || generation != _generation) { return; }
            becameIdle = _wasSessionActive;
            _wasSessionActive = false;
            _acceptedSequence = 0;
            _state = new ProductionVoiceState(
                _state.Snapshot with
                {
                    OwnerReady = false,
                    SessionActive = false,
                    Stale = true,
                    Status = "Room Voice worker is restarting.",
                    Error = null,
                },
                _state.Timeline);
        }
        _factory.PublishVoice(GetState());
        if (becameIdle) { _factory.PublishVoiceBecameIdle(); }
    }

    private void PublishTerminalUnavailable(string status)
    {
        lock (_stateGate)
        {
            _state = new ProductionVoiceState(
                _state.Snapshot with
                {
                    OwnerReady = false,
                    SessionActive = false,
                    Stale = true,
                    Status = status,
                    Error = status,
                },
                _state.Timeline);
        }
        _factory.PublishVoice(GetState());
    }

    private bool IsCurrentGeneration(long generation)
    {
        lock (_stateGate) { return !_disposed && _committed && _activeGeneration == generation; }
    }

    private void PublishHealth(BundledPluginHealth health)
    {
        lock (_stateGate) { _health = health; }
        _factory.PublishVoiceHealth(this, health);
    }

    private void InstallGeneration(IVoiceWorkerGeneration instance)
    {
        _instance = instance;
        lock (_stateGate)
        {
            _activeGeneration = instance.Generation;
            _acceptedSequence = 0;
            _lastIdleSequence = 0;
        }
    }

    private VoiceHostCommandPolicy CreatePolicy(SettingsBundle activeSettings) =>
        _factory.CreateVoiceHostPolicy(activeSettings);

    private static BundledPluginHealth Health(BundledPluginLifecycleState state, long generation, string detail) => new(
        BundledPluginCatalog.VoiceId, state, generation, detail, CanRestart: false, CanReload: false);

    private static ProductionVoiceState UnavailableState(string status) => new(
        new RuntimeVoiceSnapshot(
            RuntimeVoiceSessionState.Armed, false, false, false, true, status, null, 0), []);

    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromSeconds(
        Math.Min(30, 2 * (1 << Math.Min(Math.Max(0, attempt - 1), 4))));

    private static bool IsTransientStartupFailure(Exception exception) => exception switch
    {
        VoiceWorkerStartupException worker => worker.Kind == VoiceWorkerStartFailureKind.Transient,
        InvalidDataException or FileNotFoundException or DirectoryNotFoundException
            or UnauthorizedAccessException or ArgumentException => false,
        StreamJsonRpc.RemoteInvocationException => false,
        _ => true,
    };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

}
