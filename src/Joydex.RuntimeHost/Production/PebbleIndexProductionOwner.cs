using Joydex.Contracts;
using Joydex.Core.Voice;
using Joydex.Ipc;
using Joydex.RuntimeHost.Plugins;
using Joydex.RuntimeHost.Plugins.Pebble;

namespace Joydex.RuntimeHost.Production;

internal interface IPebblePluginHostServices
{
    void PublishPebble(PebbleWorkerStatus status);

    void PublishPebbleHealth(PebbleIndexProductionOwner owner, BundledPluginHealth health);

    void ClearPebbleOwner(PebbleIndexProductionOwner owner);

    void WriteLog(string message);
}

internal sealed class PebbleIndexProductionOwner : IProductionRuntimeOwner, IPebbleWorkerHostCallbacks
{
    private static readonly TimeSpan StableGenerationDuration = TimeSpan.FromMinutes(1);
    private readonly IPebblePluginHostServices _host;
    private readonly ProductionRuntimePaths _paths;
    private readonly IPebbleWorkerGenerationFactory _generations;
    private readonly ProductionDesktopBrokerLease? _desktopBrokerLease;
    private readonly PebbleIndexPreferences _preferences;
    private readonly CancellationTokenSource _lifetime;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _stateGate = new();
    private readonly object _disposeGate = new();
    private readonly Func<int, CancellationToken, Task> _retryDelay;
    private readonly TimeProvider _timeProvider;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IPebbleWorkerGeneration? _instance;
    private Task? _monitor;
    private Task? _disposeTask;
    private PebbleWorkerStatus? _status;
    private BundledPluginHealth _health;
    private long _generation;
    private long _acceptedSequence;
    private long _activeGeneration;
    private int _restartAttempt;
    private bool _committed;
    private bool _disposed;
    private Exception? _terminalCleanupFailure;

    private PebbleIndexProductionOwner(
        IPebblePluginHostServices host,
        ProductionRuntimePaths paths,
        IPebbleWorkerGenerationFactory generations,
        ProductionDesktopBrokerLease? desktopBrokerLease,
        PebbleIndexPreferences preferences,
        Func<int, CancellationToken, Task> retryDelay,
        TimeProvider timeProvider,
        CancellationToken runtimeCancellationToken)
    {
        _host = host;
        _paths = paths;
        _generations = generations;
        _desktopBrokerLease = desktopBrokerLease;
        _preferences = preferences.Normalize();
        _retryDelay = retryDelay;
        _timeProvider = timeProvider;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(runtimeCancellationToken);
        _health = Health(BundledPluginLifecycleState.Starting, 0, "Pebble Index worker is starting.");
    }

    public SettingsAggregateId Aggregate => SettingsAggregateId.PebbleIndex;

    public Task Completion => _completion.Task;

    internal BundledPluginHealth HealthSnapshot
    {
        get { lock (_stateGate) { return _health; } }
    }

    internal bool MatchesPreferences(PebbleIndexPreferences preferences) =>
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
            var instance = _instance
                ?? throw new InvalidOperationException("Pebble Index has no initialized worker generation.");
            _committed = true;
            AcceptStatus(instance.ActivateCallbacks());
            PublishHealth(Health(BundledPluginLifecycleState.Ready, instance.Generation,
                "Pebble Index worker is initialized."));
            _monitor = ObserveGenerationAsync(instance, _timeProvider.GetUtcNow());
        }
        finally { _lifecycle.Release(); }
    }

    public static Task<PebbleIndexProductionOwner> StartAsync(
        WindowsProductionRuntimeOwnerFactory factory,
        ProductionRuntimePaths paths,
        ProductionDesktopBrokerManager desktopBroker,
        PebbleIndexPreferences preferences,
        CancellationToken runtimeCancellationToken,
        CancellationToken cancellationToken) => StartAsync(
            factory,
            paths,
            desktopBroker,
            new PebbleWorkerProcessGenerationFactory(
                paths.PebbleWorker,
                WindowsRuntimeSettingsProcessFactory.Instance),
            preferences,
            static (attempt, token) => Task.Delay(RetryDelay(attempt), token),
            TimeProvider.System,
            runtimeCancellationToken,
            cancellationToken);

    internal static async Task<PebbleIndexProductionOwner> StartAsync(
        WindowsProductionRuntimeOwnerFactory factory,
        ProductionRuntimePaths paths,
        ProductionDesktopBrokerManager desktopBroker,
        IPebbleWorkerGenerationFactory generations,
        PebbleIndexPreferences preferences,
        Func<int, CancellationToken, Task> retryDelay,
        TimeProvider timeProvider,
        CancellationToken runtimeCancellationToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(desktopBroker);
        ProductionDesktopBrokerLease? brokerLease = null;
        PebbleIndexProductionOwner? owner = null;
        try
        {
            brokerLease = await desktopBroker.AcquireAsync(cancellationToken).ConfigureAwait(false);
            owner = new PebbleIndexProductionOwner(
                factory, paths, generations, brokerLease, preferences,
                retryDelay, timeProvider, runtimeCancellationToken);
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
                throw new ProductionOwnershipCleanupException(
                    "Pebble Index startup failed and cleanup was incomplete.",
                    [startupFailure, .. cleanupFailures]);
            }
            throw;
        }
    }

    internal static async Task<PebbleIndexProductionOwner> StartForTestAsync(
        IPebblePluginHostServices host,
        ProductionRuntimePaths paths,
        IPebbleWorkerGenerationFactory generations,
        PebbleIndexPreferences preferences,
        Func<int, CancellationToken, Task> retryDelay,
        TimeProvider timeProvider,
        CancellationToken runtimeCancellationToken,
        CancellationToken cancellationToken)
    {
        var owner = new PebbleIndexProductionOwner(
            host, paths, generations, desktopBrokerLease: null, preferences,
            retryDelay, timeProvider, runtimeCancellationToken);
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
                throw new ProductionOwnershipCleanupException(
                    "Pebble Index startup failed and cleanup was incomplete.",
                    [startupFailure, cleanupFailure]);
            }
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) { return new ValueTask(_disposeTask ??= DisposeCoreAsync()); }
    }

    void IPebbleWorkerHostCallbacks.PublishStatus(PebbleWorkerStatus status) => AcceptStatus(status);

    void IPebbleWorkerHostCallbacks.WriteLog(long generation, string message)
    {
        lock (_stateGate)
        {
            if (!_disposed && _committed && _activeGeneration == generation)
            {
                _host.WriteLog(message);
            }
        }
    }

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
                exception is ProductionOwnershipCleanupException
                    ? BundledPluginLifecycleState.Blocked
                    : BundledPluginLifecycleState.Faulted,
                _generation,
                exception is ProductionOwnershipCleanupException
                    ? "Pebble Index worker cleanup was not confirmed."
                    : "Pebble Index worker could not initialize."));
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    private async Task<IPebbleWorkerGeneration> StartGenerationAsync(CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _generation);
        PublishHealth(Health(BundledPluginLifecycleState.Starting, generation,
            "Pebble Index worker is starting."));
        return await _generations.StartAsync(
                new PebbleWorkerGenerationConfiguration(
                    generation,
                    _preferences,
                    _paths.PebbleIndexSecret,
                    _paths.PebbleIndexInbox,
                    _desktopBrokerLease?.PipeName ?? "Joydex.DesktopBridge.unavailable"),
                this,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ObserveGenerationAsync(
        IPebbleWorkerGeneration instance,
        DateTimeOffset startedAt)
    {
        try
        {
            try { await instance.Completion.WaitAsync(_lifetime.Token).ConfigureAwait(false); }
            catch { }
            await RecoverAsync(instance, startedAt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _host.WriteLog("Pebble Index worker recovery failed (" + exception.GetType().Name + ").");
        }
    }

    private async Task RecoverAsync(IPebbleWorkerGeneration failed, DateTimeOffset startedAt)
    {
        await _lifecycle.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (_disposed || !ReferenceEquals(_instance, failed)) { return; }
            failed.DeactivateCallbacks();
            lock (_stateGate) { _activeGeneration = 0; }
            PublishHealth(Health(BundledPluginLifecycleState.Retrying, failed.Generation,
                "Pebble Index worker is stopping before restart."));
            _instance = null;
            PublishUnavailable("Pebble Index worker is restarting.");
            try { await failed.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception)
            {
                _terminalCleanupFailure = exception;
                PublishUnavailable("Pebble Index worker cleanup was not confirmed.");
                PublishHealth(Health(BundledPluginLifecycleState.Blocked, failed.Generation,
                    "Pebble Index worker cleanup was not confirmed."));
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
                    "Pebble Index worker is waiting to restart."));
                await _retryDelay(_restartAttempt, _lifetime.Token).ConfigureAwait(false);
                IPebbleWorkerGeneration replacement;
                try { replacement = await StartGenerationAsync(_lifetime.Token).ConfigureAwait(false); }
                catch (ProductionOwnershipCleanupException exception)
                {
                    _terminalCleanupFailure = exception;
                    PublishUnavailable("Pebble Index worker cleanup was not confirmed.");
                    PublishHealth(Health(BundledPluginLifecycleState.Blocked, _generation,
                        "Pebble Index worker cleanup was not confirmed."));
                    return;
                }
                catch (Exception exception) when (IsTransientStartupFailure(exception))
                {
                    _restartAttempt++;
                    PublishHealth(Health(BundledPluginLifecycleState.Retrying, _generation,
                        "Pebble Index worker restart failed; another attempt is scheduled."));
                    continue;
                }
                catch
                {
                    PublishUnavailable("Pebble Index worker could not restart.");
                    PublishHealth(Health(BundledPluginLifecycleState.Faulted, _generation,
                        "Pebble Index worker could not restart."));
                    return;
                }

                InstallGeneration(replacement);
                AcceptStatus(replacement.ActivateCallbacks());
                PublishHealth(Health(BundledPluginLifecycleState.Ready, replacement.Generation,
                    "Pebble Index worker is initialized."));
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
        List<Exception>? failures = _terminalCleanupFailure is null ? null : [_terminalCleanupFailure];
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!preserveStartupFailure)
            {
                PublishHealth(Health(BundledPluginLifecycleState.Stopping, _generation,
                    "Pebble Index worker is stopping."));
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
        if (_monitor is not null)
        {
            try { await _monitor.ConfigureAwait(false); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        if (_desktopBrokerLease is not null)
        {
            try { await _desktopBrokerLease.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        _lifetime.Dispose();
        _completion.TrySetResult();
        if (_committed)
        {
            PublishUnavailable(
                failures is null
                    ? "Pebble Index worker is stopped."
                    : "Pebble Index worker cleanup was not confirmed.");
        }
        _host.ClearPebbleOwner(this);
        if (!preserveStartupFailure)
        {
            PublishHealth(Health(
                failures is null ? BundledPluginLifecycleState.Stopped : BundledPluginLifecycleState.Blocked,
                _generation,
                failures is null
                    ? "Pebble Index worker is stopped."
                    : "Pebble Index worker cleanup was not confirmed."));
        }
        if (failures is not null)
        {
            throw new ProductionOwnershipCleanupException(
                "Pebble Index worker cleanup was not confirmed.", failures);
        }
    }

    private void AcceptStatus(PebbleWorkerStatus status)
    {
        PebbleWorkerStatusValidator.Validate(status, status.Generation, requireRunning: false);
        lock (_stateGate)
        {
            if (_disposed || !_committed || status.Generation != _activeGeneration
                || status.Sequence <= _acceptedSequence)
            {
                return;
            }
            _acceptedSequence = status.Sequence;
            _status = status;
            _host.PublishPebble(status);
        }
    }

    private void PublishUnavailable(string message)
    {
        lock (_stateGate)
        {
            var status = new PebbleWorkerStatus(
                _generation,
                Math.Max(1, _acceptedSequence + 1),
                Running: false,
                message,
                _status?.OutstandingCount ?? 0,
                _status?.Latest);
            _status = status;
            _host.PublishPebble(status);
        }
    }

    private void PublishHealth(BundledPluginHealth health)
    {
        lock (_stateGate) { _health = health; }
        _host.PublishPebbleHealth(this, health);
    }

    private void InstallGeneration(IPebbleWorkerGeneration instance)
    {
        _instance = instance;
        lock (_stateGate)
        {
            _activeGeneration = instance.Generation;
            _acceptedSequence = 0;
        }
    }

    private static BundledPluginHealth Health(
        BundledPluginLifecycleState state,
        long generation,
        string detail) => new(
            BundledPluginCatalog.PebbleId,
            state,
            generation,
            detail,
            CanRestart: false,
            CanReload: false);

    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromSeconds(
        Math.Min(30, 2 * (1 << Math.Min(Math.Max(0, attempt - 1), 4))));

    private static bool IsTransientStartupFailure(Exception exception) => exception switch
    {
        PebbleWorkerStartupException worker => worker.Kind == PebbleWorkerStartFailureKind.Transient,
        InvalidDataException or FileNotFoundException or DirectoryNotFoundException
            or UnauthorizedAccessException or ArgumentException => false,
        StreamJsonRpc.RemoteInvocationException => false,
        _ => true,
    };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
