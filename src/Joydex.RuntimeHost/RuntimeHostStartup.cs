using System.Runtime.ExceptionServices;
using Joydex.Contracts;
using Joydex.Core.Runtime;
using Joydex.Ipc;
using Joydex.RuntimeHost.Production;
using Joydex.RuntimeHost.Settings;
using Joydex.Windows.Actions;

namespace Joydex.RuntimeHost;

internal enum RuntimeHostLaunchMode
{
    Production,
    Demo,
    Synthetic,
}

/// <summary>
/// Freezes every path and endpoint used by one live RuntimeHost before any listener or hardware
/// owner starts.
/// </summary>
internal sealed record RuntimeHostLiveLaunchPolicy(
    RuntimeHostLaunchMode Mode,
    string DataRoot,
    string ConfigurationPath,
    RuntimeIpcEndpoint Endpoint,
    string InstanceName,
    string JoydexAppPath,
    string RuntimeHostPath,
    bool ExistingCompanionInstall)
{
    public static RuntimeHostLiveLaunchPolicy Create(
        RuntimeHostLaunchMode mode,
        string configurationPath,
        string? pipeName,
        string? instanceName,
        string? deploymentDirectory = null,
        string? defaultConfigurationPath = null,
        string? provisioningStatePath = null)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (mode == RuntimeHostLaunchMode.Synthetic)
        {
            throw new ArgumentException(
                "The inherited-stdio synthetic host does not use the live launch policy.",
                nameof(mode));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        if (!Path.IsPathFullyQualified(configurationPath))
        {
            throw new InvalidDataException("The runtime configuration path must be absolute.");
        }

        string normalizedConfigurationPath;
        string dataRoot;
        RuntimeIpcEndpoint endpoint;
        string normalizedInstanceName;
        if (mode == RuntimeHostLaunchMode.Demo)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
            var synthetic = SyntheticRuntimeLaunchPolicy.Validate(configurationPath);
            normalizedConfigurationPath = synthetic.ConfigurationPath;
            dataRoot = synthetic.DataRoot;
            var normalizedPipeName = pipeName.Trim();
            endpoint = RuntimeIpcEndpoint.CreateSynthetic(
                dataRoot,
                normalizedConfigurationPath,
                normalizedPipeName);
            normalizedInstanceName = string.IsNullOrWhiteSpace(instanceName)
                ? normalizedPipeName
                : instanceName.Trim();
        }
        else
        {
            if (pipeName is not null || instanceName is not null)
            {
                throw new ArgumentException(
                    "Production uses its stable endpoint and instance identity.",
                    nameof(pipeName));
            }
            normalizedConfigurationPath = Path.GetFullPath(configurationPath.Trim());
            dataRoot = Path.GetDirectoryName(normalizedConfigurationPath)
                ?? throw new InvalidDataException(
                    "The runtime configuration path has no parent directory.");
            endpoint = RuntimeIpcEndpoint.CreateProduction(dataRoot, normalizedConfigurationPath);
            normalizedInstanceName = "production";
        }

        var deploymentRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            deploymentDirectory ?? AppContext.BaseDirectory));
        var existingCompanionInstall = mode == RuntimeHostLaunchMode.Production
            && HasExistingCompanionInstallation(
                normalizedConfigurationPath,
                defaultConfigurationPath ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Joydex",
                    "config.json"),
                provisioningStatePath ?? CodexKeybindingService.DefaultProvisioningStatePath);
        return new RuntimeHostLiveLaunchPolicy(
            mode,
            Path.TrimEndingDirectorySeparator(dataRoot),
            normalizedConfigurationPath,
            endpoint,
            normalizedInstanceName,
            Path.Combine(deploymentRoot, "Joydex.App.exe"),
            Path.Combine(deploymentRoot, "Joydex.RuntimeHost.exe"),
            existingCompanionInstall);
    }

    private static bool HasExistingCompanionInstallation(
        string selectedConfigurationPath,
        string defaultConfigurationPath,
        string provisioningStatePath) =>
        File.Exists(selectedConfigurationPath)
        || File.Exists(defaultConfigurationPath)
        || File.Exists(provisioningStatePath);
}

internal interface IRuntimeHostLiveRunner
{
    Task RunAsync(RuntimeHostLiveLaunchPolicy policy, CancellationToken stoppingToken);
}

internal interface IRuntimeHostRuntimeListener : IAsyncDisposable
{
    Task Completion { get; }
}

internal interface IRuntimeHostRendezvous : IAsyncDisposable
{
    Task Completion { get; }

    IDisposable ClaimTrayConnection(
        RuntimeIpcConnectionContext context,
        RuntimeClientKind authorizedKind,
        CancellationToken physicalConnectionLifetime);
}

internal interface IRuntimeHostEngine : IAsyncDisposable
{
    Task ShutdownRequested { get; }

    Task CompositionCompletion { get; }

    IRuntimeRpcServer CreateSession(
        string connectionId,
        RuntimeClientKind authorizedClientKind,
        IRuntimeRpcClient client,
        CancellationToken connectionCancellationToken,
        Action<Exception?> abortConnection);
}

internal sealed class RuntimeHostSettingsProcessOwner(
    IRuntimeSettingsProcessLauncher launcher,
    IAsyncDisposable lifetime) : IAsyncDisposable
{
    public IRuntimeSettingsProcessLauncher Launcher { get; } =
        launcher ?? throw new ArgumentNullException(nameof(launcher));

    private readonly IAsyncDisposable _lifetime =
        lifetime ?? throw new ArgumentNullException(nameof(lifetime));

    public ValueTask DisposeAsync() => _lifetime.DisposeAsync();
}

internal interface IRuntimeHostComponentFactory
{
    IRuntimeOwnershipLeaseFactory PrepareOwnership(RuntimeHostLiveLaunchPolicy policy);

    IRuntimeHostRuntimeListener StartRuntimeListener(
        RuntimeHostLiveLaunchPolicy policy,
        RuntimeRpcServerFactory connectionFactory);

    IRuntimeHostRendezvous StartRendezvous(
        RuntimeHostLiveLaunchPolicy policy,
        IRuntimeHostRuntimeListener runtimeListener);

    RuntimeHostSettingsProcessOwner CreateSettingsProcessOwner(
        RuntimeHostLiveLaunchPolicy policy,
        IRuntimeHostRuntimeListener runtimeListener);

    Task<IRuntimeHostEngine> StartEngineAsync(
        RuntimeHostLiveLaunchPolicy policy,
        IRuntimeOwnershipLeaseFactory ownershipLeaseFactory,
        IRuntimeSettingsProcessLauncher settingsProcessLauncher,
        CancellationToken startupCancellationToken);
}

/// <summary>
/// Starts the persistent RuntimeHost in guard-listener-owner order and treats unexpected listener
/// or composition completion as terminal.
/// </summary>
internal sealed class RuntimeHostLiveRunner(IRuntimeHostComponentFactory components) :
    IRuntimeHostLiveRunner
{
    private readonly IRuntimeHostComponentFactory _components =
        components ?? throw new ArgumentNullException(nameof(components));

    public static RuntimeHostLiveRunner CreateDefault() =>
        new(new WindowsRuntimeHostComponentFactory());

    public async Task RunAsync(
        RuntimeHostLiveLaunchPolicy policy,
        CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.Mode is not (RuntimeHostLaunchMode.Production or RuntimeHostLaunchMode.Demo))
        {
            throw new ArgumentException(
                "The live runner accepts only production or demo launch policies.",
                nameof(policy));
        }

        IRuntimeOwnershipLeaseFactory? ownership = null;
        IRuntimeHostRuntimeListener? runtimeListener = null;
        IRuntimeHostRendezvous? rendezvous = null;
        RuntimeHostSettingsProcessOwner? settings = null;
        IRuntimeHostEngine? engine = null;
        Exception? failure = null;
        List<Exception> cleanupFailures = [];
        var engineReady = NewSource<IRuntimeHostEngine>();
        var rendezvousReady = NewSource<IRuntimeHostRendezvous>();
        using var hostLifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        ValueTask<IRuntimeRpcServer> CreateConnectionAsync(
            RuntimeIpcConnectionContext context,
            RuntimeClientKind authorizedClientKind,
            IRuntimeRpcClient client,
            Action<Exception?> abortConnection,
            CancellationToken physicalConnectionLifetime) =>
            CreateConnectionCoreAsync(
                context,
                authorizedClientKind,
                client,
                abortConnection,
                physicalConnectionLifetime,
                rendezvousReady.Task,
                engineReady.Task);

        try
        {
            stoppingToken.ThrowIfCancellationRequested();
            ownership = _components.PrepareOwnership(policy);
            runtimeListener = _components.StartRuntimeListener(policy, CreateConnectionAsync);
            try
            {
                rendezvous = _components.StartRendezvous(policy, runtimeListener);
                rendezvousReady.SetResult(rendezvous);
            }
            catch (Exception exception)
            {
                rendezvousReady.TrySetException(exception);
                throw;
            }

            settings = _components.CreateSettingsProcessOwner(policy, runtimeListener);
            if (runtimeListener.Completion.IsCompleted)
            {
                throw await UnexpectedCompletionAsync(
                        runtimeListener.Completion,
                        "The runtime RPC listener")
                    .ConfigureAwait(false);
            }
            if (rendezvous.Completion.IsCompleted)
            {
                throw await UnexpectedCompletionAsync(
                        rendezvous.Completion,
                        "The tray bootstrap rendezvous")
                    .ConfigureAwait(false);
            }
            var engineTask = _components.StartEngineAsync(
                policy,
                ownership,
                settings.Launcher,
                hostLifetime.Token);
            var stopping = WaitForCancellationAsync(stoppingToken);
            var startupSignal = await Task.WhenAny(
                    engineTask,
                    runtimeListener.Completion,
                    rendezvous.Completion,
                    stopping)
                .ConfigureAwait(false);
            if (!ReferenceEquals(startupSignal, engineTask))
            {
                failure = ReferenceEquals(startupSignal, stopping)
                    ? new OperationCanceledException(stoppingToken)
                    : await UnexpectedCompletionAsync(
                            startupSignal,
                            ReferenceEquals(startupSignal, runtimeListener.Completion)
                                ? "The runtime RPC listener"
                                : "The tray bootstrap rendezvous")
                        .ConfigureAwait(false);
                if (failure is OperationCanceledException && stoppingToken.IsCancellationRequested)
                {
                    engineReady.TrySetCanceled(stoppingToken);
                }
                else
                {
                    engineReady.TrySetException(failure);
                }
                await DisposeAsync(rendezvous, cleanupFailures).ConfigureAwait(false);
                rendezvous = null;
                await DisposeAsync(runtimeListener, cleanupFailures).ConfigureAwait(false);
                runtimeListener = null;
                await CancelAsync(hostLifetime, cleanupFailures).ConfigureAwait(false);
                try
                {
                    engine = await engineTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (hostLifetime.IsCancellationRequested)
                {
                    // Cancellation is the expected engine response to this terminal host signal.
                }
                catch (Exception startupFailure)
                {
                    cleanupFailures.Add(new InvalidOperationException(
                        "Runtime engine startup did not unwind cleanly.",
                        startupFailure));
                }
            }
            else
            {
                engine = await engineTask.ConfigureAwait(false);
                engineReady.SetResult(engine);
                if (policy.Mode == RuntimeHostLaunchMode.Production
                    && !policy.ExistingCompanionInstall)
                {
                    var openSettings = await settings.Launcher.OpenAsync(
                            new RuntimeCommandRequest(
                                Guid.NewGuid(),
                                RuntimeCommandKind.OpenSettings),
                            hostLifetime.Token)
                        .ConfigureAwait(false);
                    if (openSettings.Status != RuntimeCommandStatus.Completed)
                    {
                        throw new InvalidOperationException(
                            openSettings.Detail
                            ?? "Joydex could not open Settings for the new installation.");
                    }
                }
                await WaitForTerminalSignalAsync(
                        engine,
                        runtimeListener,
                        rendezvous,
                        stoppingToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failure ??= exception;
            if (!engineReady.Task.IsCompleted)
            {
                if (exception is OperationCanceledException && stoppingToken.IsCancellationRequested)
                {
                    engineReady.TrySetCanceled(stoppingToken);
                }
                else
                {
                    engineReady.TrySetException(exception);
                }
            }
            if (engine is null)
            {
                await CancelAsync(hostLifetime, cleanupFailures).ConfigureAwait(false);
            }
        }

        await DisposeAsync(rendezvous, cleanupFailures).ConfigureAwait(false);
        await DisposeAsync(runtimeListener, cleanupFailures).ConfigureAwait(false);
        await DisposeAsync(engine, cleanupFailures).ConfigureAwait(false);
        await DisposeAsync(settings, cleanupFailures).ConfigureAwait(false);
        if (ownership is IDisposable ownershipLifetime)
        {
            try
            {
                ownershipLifetime.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
        }

        ObserveFailure(engineReady.Task);
        ObserveFailure(rendezvousReady.Task);
        ThrowFailures(failure, cleanupFailures);
    }

    private static async ValueTask<IRuntimeRpcServer> CreateConnectionCoreAsync(
        RuntimeIpcConnectionContext context,
        RuntimeClientKind authorizedClientKind,
        IRuntimeRpcClient client,
        Action<Exception?> abortConnection,
        CancellationToken physicalConnectionLifetime,
        Task<IRuntimeHostRendezvous> rendezvousReady,
        Task<IRuntimeHostEngine> engineReady)
    {
        IDisposable? trayLease = null;
        try
        {
            if (authorizedClientKind == RuntimeClientKind.Tray)
            {
                var rendezvous = await rendezvousReady
                    .WaitAsync(physicalConnectionLifetime)
                    .ConfigureAwait(false);
                trayLease = rendezvous.ClaimTrayConnection(
                    context,
                    authorizedClientKind,
                    physicalConnectionLifetime);
            }

            var engine = await engineReady
                .WaitAsync(physicalConnectionLifetime)
                .ConfigureAwait(false);
            physicalConnectionLifetime.ThrowIfCancellationRequested();
            var session = engine.CreateSession(
                context.ConnectionId,
                authorizedClientKind,
                client,
                physicalConnectionLifetime,
                abortConnection);
            if (trayLease is null)
            {
                return session;
            }

            var ownedLease = trayLease;
            trayLease = null;
            return new RuntimeHostTrayLeaseRpcServer(session, ownedLease);
        }
        catch
        {
            trayLease?.Dispose();
            throw;
        }
    }

    private static async Task WaitForTerminalSignalAsync(
        IRuntimeHostEngine engine,
        IRuntimeHostRuntimeListener runtimeListener,
        IRuntimeHostRendezvous rendezvous,
        CancellationToken stoppingToken)
    {
        var stopping = WaitForCancellationAsync(stoppingToken);
        await Task.WhenAny(
                runtimeListener.Completion,
                rendezvous.Completion,
                engine.CompositionCompletion,
                engine.ShutdownRequested,
                stopping)
            .ConfigureAwait(false);

        if (runtimeListener.Completion.IsCompleted)
        {
            throw await UnexpectedCompletionAsync(
                    runtimeListener.Completion,
                    "The runtime RPC listener")
                .ConfigureAwait(false);
        }
        if (rendezvous.Completion.IsCompleted)
        {
            throw await UnexpectedCompletionAsync(
                    rendezvous.Completion,
                    "The tray bootstrap rendezvous")
                .ConfigureAwait(false);
        }
        if (engine.CompositionCompletion.IsCompleted)
        {
            throw await UnexpectedCompletionAsync(
                    engine.CompositionCompletion,
                    "The runtime composition")
                .ConfigureAwait(false);
        }
        if (engine.ShutdownRequested.IsCompleted)
        {
            await engine.ShutdownRequested.ConfigureAwait(false);
            return;
        }

        stoppingToken.ThrowIfCancellationRequested();
    }

    private static async Task<Exception> UnexpectedCompletionAsync(Task task, string owner)
    {
        try
        {
            await task.ConfigureAwait(false);
            return new InvalidOperationException($"{owner} stopped unexpectedly.");
        }
        catch (Exception exception)
        {
            return new InvalidOperationException($"{owner} failed unexpectedly.", exception);
        }
    }

    private static Task WaitForCancellationAsync(CancellationToken cancellationToken) =>
        Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

    private static async Task DisposeAsync(
        IAsyncDisposable? disposable,
        List<Exception> failures)
    {
        if (disposable is null)
        {
            return;
        }
        try
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static async Task CancelAsync(
        CancellationTokenSource cancellation,
        List<Exception> failures)
    {
        try
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static void ThrowFailures(Exception? failure, List<Exception> cleanupFailures)
    {
        if (failure is null && cleanupFailures.Count == 0)
        {
            return;
        }
        if (failure is not null && cleanupFailures.Count == 0)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        var failures = failure is null
            ? cleanupFailures
            : new[] { failure }.Concat(cleanupFailures).ToList();
        throw new AggregateException("RuntimeHost did not stop cleanly.", failures);
    }

    private static void ObserveFailure(Task task)
    {
        if (task.IsFaulted)
        {
            _ = task.Exception;
        }
    }

    private static TaskCompletionSource<T> NewSource<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class WindowsRuntimeHostComponentFactory : IRuntimeHostComponentFactory
{
    public IRuntimeOwnershipLeaseFactory PrepareOwnership(RuntimeHostLiveLaunchPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.Mode == RuntimeHostLaunchMode.Production
            ? new TransferredRuntimeOwnershipLeaseFactory(
                new LegacyJoydexOwnershipLeaseFactory().Acquire())
            : new SyntheticRuntimeOwnershipLeaseFactory();
    }

    public IRuntimeHostRuntimeListener StartRuntimeListener(
        RuntimeHostLiveLaunchPolicy policy,
        RuntimeRpcServerFactory connectionFactory) =>
        new RuntimeListener(RuntimeIpcServer.Start(policy.Endpoint, connectionFactory));

    public IRuntimeHostRendezvous StartRendezvous(
        RuntimeHostLiveLaunchPolicy policy,
        IRuntimeHostRuntimeListener runtimeListener)
    {
        var runtime = RequireRuntimeListener(runtimeListener).Server;
        var server = policy.Mode == RuntimeHostLaunchMode.Production
            ? RuntimeBootstrapRendezvousServer.Start(
                runtime,
                policy.Endpoint,
                policy.JoydexAppPath,
                policy.RuntimeHostPath)
            : RuntimeBootstrapRendezvousServer.StartDemo(
                runtime,
                policy.Endpoint,
                policy.JoydexAppPath,
                policy.RuntimeHostPath);
        return new Rendezvous(server);
    }

    public RuntimeHostSettingsProcessOwner CreateSettingsProcessOwner(
        RuntimeHostLiveLaunchPolicy policy,
        IRuntimeHostRuntimeListener runtimeListener)
    {
        var runtime = RequireRuntimeListener(runtimeListener).Server;
        var launcher = new RuntimeSettingsProcessLauncher(
            policy.Endpoint,
            policy.DataRoot,
            policy.ConfigurationPath,
            policy.JoydexAppPath,
            new RuntimeIpcSettingsTicketIssuer(runtime));
        return new RuntimeHostSettingsProcessOwner(launcher, launcher);
    }

    public async Task<IRuntimeHostEngine> StartEngineAsync(
        RuntimeHostLiveLaunchPolicy policy,
        IRuntimeOwnershipLeaseFactory ownershipLeaseFactory,
        IRuntimeSettingsProcessLauncher settingsProcessLauncher,
        CancellationToken startupCancellationToken)
    {
        Task? compositionCompletion = null;
        IRuntimeComposition CreateComposition(
            RuntimeInputHost inputHost,
            CancellationToken runtimeCancellationToken)
        {
            if (policy.Mode == RuntimeHostLaunchMode.Production)
            {
                var composition = ProductionRuntimeComposition.Create(
                    inputHost,
                    policy.ConfigurationPath,
                    policy.ExistingCompanionInstall,
                    runtimeCancellationToken);
                compositionCompletion = composition.Completion;
                return composition;
            }

            var demo = (DemoRuntimeComposition)DemoRuntimeComposition.Create(
                inputHost,
                TimeProvider.System,
                runtimeCancellationToken);
            compositionCompletion = demo.Completion;
            return demo;
        }

        var options = new RuntimeEngineOptions(
            policy.Endpoint.InstanceKind,
            policy.DataRoot,
            policy.Endpoint.DataRootId,
            RuntimeSettingsPaths.ForConfiguration(policy.ConfigurationPath),
            ownershipLeaseFactory,
            CreateComposition,
            new DefaultSettingsImpactPlanner(),
            TimeProvider.System,
            settingsProcessLauncher);
        var engine = await RuntimeEngine
            .StartAsync(options, startupCancellationToken)
            .ConfigureAwait(false);
        return new Engine(
            engine,
            compositionCompletion
                ?? throw new InvalidOperationException("The runtime composition was not created."));
    }

    private static RuntimeListener RequireRuntimeListener(IRuntimeHostRuntimeListener listener) =>
        listener as RuntimeListener
        ?? throw new ArgumentException("The runtime listener was created by another factory.", nameof(listener));

    private sealed class RuntimeListener(RuntimeIpcServer server) : IRuntimeHostRuntimeListener
    {
        public RuntimeIpcServer Server { get; } =
            server ?? throw new ArgumentNullException(nameof(server));

        public Task Completion => Server.Completion;

        public ValueTask DisposeAsync() => Server.DisposeAsync();
    }

    private sealed class Rendezvous(RuntimeBootstrapRendezvousServer server) : IRuntimeHostRendezvous
    {
        private readonly RuntimeBootstrapRendezvousServer _server =
            server ?? throw new ArgumentNullException(nameof(server));

        public Task Completion => _server.Completion;

        public IDisposable ClaimTrayConnection(
            RuntimeIpcConnectionContext context,
            RuntimeClientKind authorizedKind,
            CancellationToken physicalConnectionLifetime) =>
            _server.ClaimTrayConnection(context, authorizedKind, physicalConnectionLifetime);

        public ValueTask DisposeAsync() => _server.DisposeAsync();
    }

    private sealed class Engine(RuntimeEngine engine, Task compositionCompletion) :
        IRuntimeHostEngine
    {
        private readonly RuntimeEngine _engine =
            engine ?? throw new ArgumentNullException(nameof(engine));

        public Task ShutdownRequested => _engine.ShutdownRequested;

        public Task CompositionCompletion { get; } =
            compositionCompletion ?? throw new ArgumentNullException(nameof(compositionCompletion));

        public IRuntimeRpcServer CreateSession(
            string connectionId,
            RuntimeClientKind authorizedClientKind,
            IRuntimeRpcClient client,
            CancellationToken connectionCancellationToken,
            Action<Exception?> abortConnection) =>
            _engine.CreateSession(
                connectionId,
                authorizedClientKind,
                client,
                connectionCancellationToken,
                abortConnection);

        public ValueTask DisposeAsync() => _engine.DisposeAsync();
    }
}

/// <summary>
/// Keeps an authenticated tray admission claimed through application attach and the physical
/// connection lifetime.
/// </summary>
internal sealed class RuntimeHostTrayLeaseRpcServer : IRuntimeRpcServer, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IRuntimeRpcServer _inner;
    private IDisposable? _lease;
    private Task? _disposal;

    public RuntimeHostTrayLeaseRpcServer(IRuntimeRpcServer inner, IDisposable lease)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
    }

    public async Task<RuntimeAttachResult> AttachAsync(
        RuntimeAttachRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _inner.AttachAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<RuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        _inner.GetSnapshotAsync(cancellationToken);

    public Task<PrepareSettingsResult> PrepareSettingsAsync(
        PrepareSettingsRequest request,
        CancellationToken cancellationToken) =>
        _inner.PrepareSettingsAsync(request, cancellationToken);

    public Task<ApplySettingsResult> ApplySettingsAsync(
        ApplySettingsRequest request,
        CancellationToken cancellationToken) =>
        _inner.ApplySettingsAsync(request, cancellationToken);

    public Task<SettingsOperationResult> GetSettingsOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken) =>
        _inner.GetSettingsOperationAsync(operationId, cancellationToken);

    public Task<RuntimeInputSnapshot> RefreshInputSourcesAsync(CancellationToken cancellationToken) =>
        _inner.RefreshInputSourcesAsync(cancellationToken);

    public Task<RuntimeCaptureStartResult> BeginInputCaptureAsync(
        RuntimeCaptureRequest request,
        CancellationToken cancellationToken) =>
        _inner.BeginInputCaptureAsync(request, cancellationToken);

    public Task<RuntimeCaptureCommandResult> RenewInputCaptureAsync(
        Guid captureId,
        CancellationToken cancellationToken) =>
        _inner.RenewInputCaptureAsync(captureId, cancellationToken);

    public Task<RuntimeCaptureCommandResult> CancelInputCaptureAsync(
        Guid captureId,
        CancellationToken cancellationToken) =>
        _inner.CancelInputCaptureAsync(captureId, cancellationToken);

    public Task<RuntimeCaptureLookupResult> GetInputCaptureAsync(
        Guid captureId,
        CancellationToken cancellationToken) =>
        _inner.GetInputCaptureAsync(captureId, cancellationToken);

    public Task<RuntimeCommandResult> ExecuteCommandAsync(
        RuntimeCommandRequest request,
        CancellationToken cancellationToken) =>
        _inner.ExecuteCommandAsync(request, cancellationToken);

    public Task<RuntimeCommandOperationResult> GetCommandOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken) =>
        _inner.GetCommandOperationAsync(operationId, cancellationToken);

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposal ??= DisposeCoreAsync();
            return new ValueTask(_disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            switch (_inner)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _lease, null)?.Dispose();
        }
    }
}
