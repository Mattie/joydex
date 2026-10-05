using Joydex.Core.Config;
using Joydex.Core.Mapping;
using Joydex.Core.TaskAlerts;
using Joydex.WirelessPanel;
using Joydex.Windows.Actions;
using Joydex.Windows.TaskAlerts;
using Joydex.Windows.WirelessPanel;

namespace Joydex.RuntimeHost.Plugins;

/// <summary>Supplies the runtime-owned capabilities used by the bundled PAD plugin.</summary>
internal interface IPadPluginHostServices
{
    event EventHandler<TaskAlertSnapshot>? TaskAlertsChanged;

    TaskAlertSnapshot GetTaskAlertSnapshot();

    bool AcknowledgeTerminalTaskAlert(int slot, string sessionId);

    PadCommandPolicy CreatePadCommandPolicy(CompanionConfig config);

    void WritePadLog(string message);
}

/// <summary>One coherent generation of the shared policy used for PAD commands.</summary>
internal sealed record PadCommandPolicy(
    ITaskAlertNavigator Navigator,
    Func<ActionRequest, CancellationToken, Task<ActionExecutionResult>> ExecuteAction);

/// <summary>The running PAD adapter boundary used by deterministic lifecycle tests.</summary>
internal interface IPadPluginInstance : IAsyncDisposable
{
    Task Completion { get; }

    void Apply(TaskAlertSnapshot snapshot);
}

internal interface IPadPluginInstanceFactory
{
    Task<IPadPluginInstance> StartAsync(
        WirelessPanelConfiguration configuration,
        TaskAlertSnapshot initialSnapshot,
        Func<TaskAlertSnapshot> getSnapshot,
        ITaskAlertNavigator navigator,
        Func<int, string, bool> acknowledgeTerminal,
        Func<ActionRequest, CancellationToken, Task<ActionExecutionResult>> executeAction,
        Action<string> log,
        CancellationToken cancellationToken);
}

/// <summary>
/// Owns the bundled PAD independently from controller generations. Only an unexpected end of one
/// confirmed adapter generation enters bounded automatic restart.
/// </summary>
internal sealed class PadPlugin : IAsyncDisposable
{
    private const int MaximumAutomaticRestartAttempts = 3;
    private readonly IPadPluginHostServices _host;
    private readonly Func<WirelessPanelConfiguration?> _loadConfiguration;
    private readonly IPadPluginInstanceFactory _instances;
    private readonly Func<int, CancellationToken, Task> _restartDelay;
    private readonly CancellationTokenSource _shutdown;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _healthGate = new();
    private readonly object _disposeGate = new();
    private readonly object _backgroundGate = new();
    private readonly List<Task> _backgroundTasks = [];
    private readonly PadCommandPolicyProxy _policy = new();
    private IPadPluginInstance? _instance;
    private WirelessPanelConfiguration? _configuration;
    private BundledPluginHealth _health = NewHealth(
        BundledPluginLifecycleState.Stopped,
        generation: 0,
        "PAD has not started.",
        canRestart: false,
        canReload: false);
    private Task? _disposeTask;
    private PadPluginCleanupException? _terminalFailure;
    private long _generation;
    private long _controlVersion;
    private bool _initialized;
    private bool _configurationLoaded;
    private bool _policyValid;
    private bool _policyBlocked;
    private bool _terminal;
    private bool _disposed;

    internal PadPlugin(
        IPadPluginHostServices host,
        Func<WirelessPanelConfiguration?> loadConfiguration,
        IPadPluginInstanceFactory instances,
        Func<int, CancellationToken, Task> restartDelay,
        CancellationToken runtimeCancellationToken)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _loadConfiguration = loadConfiguration ?? throw new ArgumentNullException(nameof(loadConfiguration));
        _instances = instances ?? throw new ArgumentNullException(nameof(instances));
        _restartDelay = restartDelay ?? throw new ArgumentNullException(nameof(restartDelay));
        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(runtimeCancellationToken);
        _host.TaskAlertsChanged += OnTaskAlertsChanged;
    }

    public BundledPluginRegistration Registration =>
        BundledPluginCatalog.GetRequired(BundledPluginCatalog.PadId);

    public BundledPluginHealth Health
    {
        get
        {
            lock (_healthGate)
            {
                return _health;
            }
        }
    }

    /// <summary>
    /// Installs the current active Companion policy atomically. The panel document is loaded only
    /// on the first call; later Companion changes do not adopt external panel-file edits.
    /// </summary>
    public async Task RefreshSharedConfigurationAsync(
        CompanionConfig companion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(companion);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdown.Token,
            cancellationToken);
        await _lifecycle.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            PadCommandPolicy policy;
            try
            {
                policy = _host.CreatePadCommandPolicy(CompanionConfigNormalizer.Normalize(companion));
            }
            catch (Exception exception)
            {
                await StopForPolicyFailureLockedAsync(exception).ConfigureAwait(false);
                return;
            }

            _policy.Update(policy);
            _policyValid = true;
            var resumeAfterPolicyFailure = _policyBlocked;
            _policyBlocked = false;
            if (_initialized)
            {
                if (resumeAfterPolicyFailure && !_terminal && _instance is null)
                {
                    await StartConfiguredLockedAsync().ConfigureAwait(false);
                }
                return;
            }

            _initialized = true;
            try
            {
                _configuration = _loadConfiguration();
                _configurationLoaded = true;
            }
            catch (Exception exception)
            {
                PublishHealth(NewHealth(
                    BundledPluginLifecycleState.Blocked,
                    _generation,
                    "PAD configuration could not be loaded.",
                    canRestart: false,
                    canReload: true));
                LogFailure("configuration load was rejected", exception);
                return;
            }

            await StartConfiguredLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Stops and starts the current effective PAD configuration without reading disk.</summary>
    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdown.Token,
            cancellationToken);
        await _lifecycle.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfTerminal();
            EnsureInitialized();
            ThrowIfPolicyUnavailable();
            if (_configuration is not { Enabled: true })
            {
                throw new InvalidOperationException(
                    "PAD restart requires an enabled effective configuration; reload PAD configuration instead.");
            }
            checked { _controlVersion++; }
            if (!await StopCurrentLockedAsync("PAD is restarting.").ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "PAD cleanup was not confirmed; restart is blocked for this runtime process.");
            }
            if (!await StartConfiguredLockedAsync().ConfigureAwait(false))
            {
                throw new InvalidOperationException("PAD could not restart.");
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Reads the existing panel document explicitly. A rejected document leaves a running prior
    /// generation and its decrypted credentials unchanged.
    /// </summary>
    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdown.Token,
            cancellationToken);
        await _lifecycle.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfTerminal();
            EnsureInitialized();
            ThrowIfPolicyUnavailable();
            WirelessPanelConfiguration? candidate;
            try
            {
                candidate = _loadConfiguration();
            }
            catch (Exception exception)
            {
                LogFailure("configuration reload was rejected; the prior effective configuration remains active", exception);
                throw new InvalidOperationException(
                    "PAD configuration reload was rejected; the prior effective configuration remains active.",
                    exception);
            }

            if (_configurationLoaded && ConfigurationEquals(_configuration, candidate))
            {
                return;
            }

            var priorConfiguration = _configuration;
            var priorConfigurationLoaded = _configurationLoaded;
            checked { _controlVersion++; }
            if (!await StopCurrentLockedAsync("PAD is applying reloaded configuration.").ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "PAD cleanup was not confirmed; configuration reload is blocked for this runtime process.");
            }

            _configuration = candidate;
            _configurationLoaded = true;
            if (!await StartConfiguredLockedAsync().ConfigureAwait(false))
            {
                if (_terminal)
                {
                    throw new InvalidOperationException(
                        "The reloaded PAD configuration could not start and cleanup is unconfirmed.");
                }

                _configuration = priorConfiguration;
                _configurationLoaded = priorConfigurationLoaded;
                if (priorConfigurationLoaded
                    && await StartConfiguredLockedAsync().ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        "The reloaded PAD configuration could not start; the prior effective configuration was restored.");
                }

                throw new InvalidOperationException(
                    "The reloaded PAD configuration could not start and the prior effective configuration could not be restored.");
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task<bool> StartConfiguredLockedAsync()
    {
        if (_configuration is null)
        {
            PublishHealth(NewHealth(
                BundledPluginLifecycleState.Disabled,
                _generation,
                "PAD is not configured.",
                canRestart: false,
                canReload: true));
            return true;
        }
        if (!_configuration.Enabled)
        {
            PublishHealth(NewHealth(
                BundledPluginLifecycleState.Disabled,
                _generation,
                "PAD is disabled.",
                canRestart: false,
                canReload: true));
            return true;
        }

        var generation = checked(++_generation);
        PublishHealth(NewHealth(
            BundledPluginLifecycleState.Starting,
            generation,
            "PAD is starting.",
            canRestart: false,
            canReload: true));
        IPadPluginInstance? instance = null;
        try
        {
            instance = await _instances.StartAsync(
                    _configuration,
                    _host.GetTaskAlertSnapshot(),
                    _host.GetTaskAlertSnapshot,
                    _policy,
                    _host.AcknowledgeTerminalTaskAlert,
                    _policy.ExecuteAsync,
                    _host.WritePadLog,
                    _shutdown.Token)
                .ConfigureAwait(false);
            if (instance is null)
            {
                throw new InvalidOperationException(
                    "The PAD instance factory returned no running instance.");
            }
            if (_shutdown.IsCancellationRequested)
            {
                try
                {
                    await instance.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupFailure)
                {
                    throw new PadPluginCleanupException(
                        "PAD startup was canceled and cleanup was not confirmed.",
                        cleanupFailure);
                }
                _shutdown.Token.ThrowIfCancellationRequested();
            }
            _instance = instance;
            PublishHealth(NewHealth(
                BundledPluginLifecycleState.Ready,
                generation,
                "PAD plugin generation is running.",
                canRestart: true,
                canReload: true));
            TrackBackground(ObserveInstanceAsync(instance, generation, _controlVersion));
            return true;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            throw;
        }
        catch (PadPluginCleanupException exception)
        {
            RecordTerminal(exception);
            return false;
        }
        catch (Exception exception)
        {
            PublishHealth(NewHealth(
                BundledPluginLifecycleState.Faulted,
                generation,
                "PAD could not start.",
                canRestart: true,
                canReload: true));
            LogFailure("startup failed", exception);
            return false;
        }
    }

    private async Task ObserveInstanceAsync(
        IPadPluginInstance instance,
        long generation,
        long controlVersion)
    {
        Exception? failure = null;
        try
        {
            await instance.Completion.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed
                || !ReferenceEquals(_instance, instance)
                || _controlVersion != controlVersion)
            {
                return;
            }

            _instance = null;
            PublishHealth(NewHealth(
                BundledPluginLifecycleState.Retrying,
                generation,
                "PAD stopped unexpectedly; cleanup is being confirmed.",
                canRestart: false,
                canReload: true));
            try
            {
                await instance.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                RecordTerminal(new PadPluginCleanupException(
                    "The stopped PAD generation did not confirm cleanup.",
                    cleanupFailure));
                return;
            }
            LogFailure("generation stopped unexpectedly", failure);
        }
        finally
        {
            _lifecycle.Release();
        }

        try
        {
            await RetryUnexpectedTerminationAsync(controlVersion).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            PublishHealth(NewHealth(
                BundledPluginLifecycleState.Faulted,
                _generation,
                "PAD automatic restart failed.",
                canRestart: true,
                canReload: true));
            LogFailure("automatic restart failed", exception);
        }
    }

    private async Task RetryUnexpectedTerminationAsync(long controlVersion)
    {
        for (var attempt = 1; attempt <= MaximumAutomaticRestartAttempts; attempt++)
        {
            try
            {
                await _restartDelay(attempt, _shutdown.Token).ConfigureAwait(false);
                await _lifecycle.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }

            try
            {
                if (_disposed
                    || _terminal
                    || _controlVersion != controlVersion
                    || _instance is not null)
                {
                    return;
                }

                if (await StartConfiguredLockedAsync().ConfigureAwait(false))
                {
                    return;
                }
                if (_terminal)
                {
                    return;
                }
                if (attempt < MaximumAutomaticRestartAttempts)
                {
                    PublishHealth(NewHealth(
                        BundledPluginLifecycleState.Retrying,
                        _generation,
                        "PAD restart is waiting for another bounded attempt.",
                        canRestart: false,
                        canReload: true));
                }
            }
            finally
            {
                _lifecycle.Release();
            }
        }

        await _lifecycle.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        try
        {
            if (!_disposed
                && !_terminal
                && _controlVersion == controlVersion
                && _instance is null)
            {
                PublishHealth(NewHealth(
                    BundledPluginLifecycleState.Faulted,
                    _generation,
                    "PAD exhausted its automatic restart attempts.",
                    canRestart: true,
                    canReload: true));
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task<bool> StopCurrentLockedAsync(string detail)
    {
        var instance = _instance;
        if (instance is null)
        {
            return true;
        }

        _instance = null;
        PublishHealth(NewHealth(
            BundledPluginLifecycleState.Stopping,
            _generation,
            detail,
            canRestart: false,
            canReload: false));
        try
        {
            await instance.DisposeAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            RecordTerminal(new PadPluginCleanupException(
                "The prior PAD generation did not confirm cleanup.",
                exception));
            return false;
        }
    }

    private async Task StopForPolicyFailureLockedAsync(Exception failure)
    {
        _policyValid = false;
        _policyBlocked = true;
        checked { _controlVersion++; }
        if (!await StopCurrentLockedAsync("PAD is stopping after a shared-policy failure.")
                .ConfigureAwait(false))
        {
            return;
        }
        PublishHealth(NewHealth(
            BundledPluginLifecycleState.Blocked,
            _generation,
            "PAD could not apply the active command policy.",
            canRestart: false,
            canReload: false));
        LogFailure("shared command policy was rejected", failure);
    }

    private async Task DisposeCoreAsync()
    {
        _host.TaskAlertsChanged -= OnTaskAlertsChanged;
        _shutdown.Cancel();
        Exception? cleanupFailure = null;
        Task[] background;
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            checked { _controlVersion++; }
            var instance = _instance;
            _instance = null;
            if (_terminalFailure is null)
            {
                PublishHealth(NewHealth(
                    BundledPluginLifecycleState.Stopping,
                    _generation,
                    "PAD is stopping.",
                    canRestart: false,
                    canReload: false));
            }
            if (instance is not null)
            {
                try
                {
                    await instance.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                }
            }
            lock (_backgroundGate)
            {
                background = _backgroundTasks.ToArray();
            }
        }
        finally
        {
            _lifecycle.Release();
        }

        foreach (var task in background)
        {
            await task.ConfigureAwait(false);
        }
        if (cleanupFailure is not null)
        {
            RecordTerminal(new PadPluginCleanupException(
                "PAD cleanup was not confirmed during runtime shutdown.",
                cleanupFailure));
        }

        var terminalFailure = _terminalFailure;
        if (terminalFailure is null)
        {
            PublishHealth(NewHealth(
                BundledPluginLifecycleState.Stopped,
                _generation,
                "PAD stopped.",
                canRestart: false,
                canReload: false));
        }
        _shutdown.Dispose();
        _lifecycle.Dispose();
        if (terminalFailure is not null)
        {
            throw terminalFailure;
        }
    }

    private void OnTaskAlertsChanged(object? sender, TaskAlertSnapshot snapshot)
    {
        try
        {
            Volatile.Read(ref _instance)?.Apply(snapshot);
        }
        catch (Exception exception)
        {
            LogFailure("task projection was rejected", exception);
        }
    }

    private void TrackBackground(Task task)
    {
        lock (_backgroundGate)
        {
            _backgroundTasks.Add(task);
        }
    }

    private void PublishHealth(BundledPluginHealth health)
    {
        lock (_healthGate)
        {
            _health = health;
        }
    }

    private void RecordTerminal(PadPluginCleanupException exception)
    {
        if (_terminalFailure is { } priorFailure)
        {
            exception = new PadPluginCleanupException(
                "More than one PAD generation failed to confirm cleanup.",
                new AggregateException(priorFailure, exception));
        }
        _terminalFailure = exception;
        _terminal = true;
        PublishHealth(NewHealth(
            BundledPluginLifecycleState.Blocked,
            _generation,
            "PAD cleanup is unconfirmed; replacement is blocked for this runtime process.",
            canRestart: false,
            canReload: false));
        LogFailure("cleanup was not confirmed; replacement is blocked", exception);
    }

    private void LogFailure(string category, Exception? failure)
    {
        try
        {
            _host.WritePadLog(
                failure is null
                    ? $"{BundledPluginCatalog.PadId}: {category}."
                    : $"{BundledPluginCatalog.PadId}: {category} ({failure.GetType().Name}).");
        }
        catch
        {
            // Runtime ownership and health remain authoritative when diagnostics are unavailable.
        }
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("PAD plugins have not received active runtime settings.");
        }
    }

    private void ThrowIfTerminal()
    {
        if (_terminal)
        {
            throw new InvalidOperationException(
                "PAD cleanup is unconfirmed; replacement is blocked for this runtime process.");
        }
    }

    private void ThrowIfPolicyUnavailable()
    {
        if (!_policyValid)
        {
            throw new InvalidOperationException(
                "PAD command policy is unavailable until active Companion settings can be applied.");
        }
    }

    private static bool ConfigurationEquals(
        WirelessPanelConfiguration? left,
        WirelessPanelConfiguration? right) =>
        ReferenceEquals(left, right)
        || left is not null
        && right is not null
        && left.Enabled == right.Enabled
        && left.Endpoint == right.Endpoint
        && string.Equals(left.Username, right.Username, StringComparison.Ordinal)
        && string.Equals(left.Password, right.Password, StringComparison.Ordinal);

    private static BundledPluginHealth NewHealth(
        BundledPluginLifecycleState state,
        long generation,
        string detail,
        bool canRestart,
        bool canReload) => new(
        BundledPluginCatalog.PadId,
        state,
        generation,
        detail,
        canRestart,
        canReload);

    private sealed class PadCommandPolicyProxy : ITaskAlertNavigator
    {
        private PadCommandPolicy? _current;

        public void Update(PadCommandPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(policy);
            Volatile.Write(ref _current, policy);
        }

        public Task<bool> NavigateAsync(
            TaskAlertNavigationRequest request,
            CancellationToken cancellationToken) =>
            Current.Navigator.NavigateAsync(request, cancellationToken);

        public Task<ActionExecutionResult> ExecuteAsync(
            ActionRequest request,
            CancellationToken cancellationToken) =>
            Current.ExecuteAction(request, cancellationToken);

        private PadCommandPolicy Current => Volatile.Read(ref _current)
            ?? throw new InvalidOperationException("The active PAD command policy is unavailable.");
    }
}

internal sealed class PadPluginCleanupException(string message, Exception innerException)
    : Exception(message, innerException);

internal sealed class EspHomePadPluginInstanceFactory : IPadPluginInstanceFactory
{
    public static EspHomePadPluginInstanceFactory Instance { get; } = new();

    private EspHomePadPluginInstanceFactory()
    {
    }

    public async Task<IPadPluginInstance> StartAsync(
        WirelessPanelConfiguration configuration,
        TaskAlertSnapshot initialSnapshot,
        Func<TaskAlertSnapshot> getSnapshot,
        ITaskAlertNavigator navigator,
        Func<int, string, bool> acknowledgeTerminal,
        Func<ActionRequest, CancellationToken, Task<ActionExecutionResult>> executeAction,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EspHomePanelAdapter? adapter = null;
        try
        {
            adapter = new EspHomePanelAdapter(
                new EspHomePanelTransport(
                    configuration.Endpoint,
                    configuration.Username,
                    configuration.Password,
                    log),
                initialSnapshot,
                getSnapshot,
                navigator,
                acknowledgeTerminal,
                executeAction,
                log);
            adapter.Start();
            return new EspHomePadPluginInstance(adapter);
        }
        catch (Exception startupFailure)
        {
            if (adapter is not null)
            {
                try
                {
                    await adapter.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupFailure)
                {
                    throw new PadPluginCleanupException(
                        "PAD startup failed and cleanup was not confirmed.",
                        new AggregateException(startupFailure, cleanupFailure));
                }
            }
            throw;
        }
    }
}

internal sealed class EspHomePadPluginInstance(EspHomePanelAdapter adapter) : IPadPluginInstance
{
    private readonly EspHomePanelAdapter _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));

    public Task Completion => _adapter.Completion;

    public void Apply(TaskAlertSnapshot snapshot) => _adapter.Apply(snapshot);

    public ValueTask DisposeAsync() => _adapter.DisposeAsync();
}
