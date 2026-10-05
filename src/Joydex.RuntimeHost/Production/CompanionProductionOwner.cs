using Joydex.App;
using Joydex.Contracts;
using Joydex.Core.Config;
using Joydex.Core.Mapping;
using Joydex.Core.Runtime;
using Joydex.Core.TaskAlerts;
using Joydex.Windows.Input;
using Joydex.Windows.Runtime;
using Joydex.Windows.TaskAlerts;

namespace Joydex.RuntimeHost.Production;

internal sealed class CompanionProductionOwner : IProductionInputOwner
{
    private readonly WindowsProductionRuntimeOwnerFactory _factory;
    private readonly ProductionWindowsStaHost _windowsSta;
    private readonly RuntimeInputHost _inputHost;
    private readonly RuntimeInputSourceProvider _catalog;
    private readonly List<CompanionWorker> _workers;
    private readonly PromptPickerCoordinator _promptPicker;
    private readonly TaskCompletionSource _ownedCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _completion;
    private IReadOnlyDictionary<string, RuntimeInputSourceCatalogEntry> _catalogEntries =
        new Dictionary<string, RuntimeInputSourceCatalogEntry>(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    private CompanionProductionOwner(
        WindowsProductionRuntimeOwnerFactory factory,
        ProductionWindowsStaHost windowsSta,
        RuntimeInputHost inputHost,
        RuntimeInputSourceProvider catalog,
        List<CompanionWorker> workers,
        PromptPickerCoordinator promptPicker)
    {
        _factory = factory;
        _windowsSta = windowsSta;
        _inputHost = inputHost;
        _catalog = catalog;
        _workers = workers;
        _promptPicker = promptPicker;
        _completion = Task.WhenAny(_ownedCompletion.Task, windowsSta.Completion).Unwrap();
    }

    public SettingsAggregateId Aggregate => SettingsAggregateId.Companion;

    public Task Completion => _completion;

    public static async Task<CompanionProductionOwner> StartAsync(
        WindowsProductionRuntimeOwnerFactory factory,
        ProductionWindowsStaHost windowsSta,
        RuntimeInputHost inputHost,
        IJoystickSourceFactory inputSources,
        SettingsBundle activeSettings)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(windowsSta);
        ArgumentNullException.ThrowIfNull(inputHost);
        ArgumentNullException.ThrowIfNull(inputSources);
        ArgumentNullException.ThrowIfNull(activeSettings);
        var config = CompanionConfigNormalizer.Normalize(activeSettings.Companion);
        var catalog = new RuntimeInputSourceProvider(
            inputHost,
            inputSources,
            config.Polling,
            factory.WriteLog);
        var workers = new List<CompanionWorker>();
        PromptPickerCoordinator? promptPicker = null;
        try
        {
            var entries = catalog.Refresh(config);
            var promptSubmitExecutor = factory.CreateActionExecutor(config);
            promptPicker = new PromptPickerCoordinator(
                config,
                factory.WriteActivity,
                windowsSta.SynchronizationContext,
                submit: (request, token) => promptSubmitExecutor.ExecuteAsync(
                    new ActionRequest(
                        "Prompt picker submit",
                        CompanionConfig.AlwaysBank,
                        request.Button,
                        "press",
                        CodexAction.Submit,
                        DateTimeOffset.UtcNow,
                        DeviceId: request.DeviceId),
                    token));
            promptPicker.Changed += factory.OnPromptPickerChanged;
            var navigator = new TaskDeepLinkNavigator(
                config.Safety,
                factory.WriteActivity);
            foreach (var device in config.Devices)
            {
                var executor = factory.CreateActionExecutor(
                    config,
                    request => factory.PublishButtonMap(
                        request.DeviceId,
                        !string.Equals(request.Trigger, "release", StringComparison.OrdinalIgnoreCase)));
                var isCm3 = string.Equals(
                    device.ButtonMapTemplate,
                    "cm3",
                    StringComparison.OrdinalIgnoreCase);
                var worker = new CompanionWorker(
                    config,
                    inputSources.Create(),
                    executor,
                    factory.WriteActivity,
                    taskAlertInputInterceptor: isCm3
                        ? new TaskAlertInputInterceptor(
                            () => factory.GetTaskAlertSnapshot().Assignments)
                        : null,
                    taskAlertNavigator: isCm3 ? navigator : null,
                    acknowledgeTerminalTaskAlert: isCm3
                        ? factory.AcknowledgeTerminalTaskAlert
                        : null,
                    deviceId: device.Id,
                    promptPickerHandler: promptPicker.HandleAsync,
                    buttonMapHandler: request => factory.PublishButtonMap(request.DeviceId, request.Visible),
                    inputHost: inputHost);
                worker.StatusChanged += (_, status) => factory.PublishController(
                    device.Id,
                    device.DisplayName,
                    status,
                    device.ButtonMapTemplate is not null);
                workers.Add(worker);
                worker.Start();
            }

            var owner = new CompanionProductionOwner(
                factory,
                windowsSta,
                inputHost,
                catalog,
                workers,
                promptPicker)
            {
                _catalogEntries = entries.ToDictionary(
                    entry => entry.Source.SourceId,
                    StringComparer.OrdinalIgnoreCase),
            };
            return owner;
        }
        catch (Exception startupFailure)
        {
            var cleanupFailures = new List<Exception>();
            try
            {
                if (promptPicker is not null)
                {
                    promptPicker.Changed -= factory.OnPromptPickerChanged;
                }
            }
            catch (Exception exception) { cleanupFailures.Add(exception); }
            foreach (var worker in workers.AsEnumerable().Reverse())
            {
                await TryDisposeAsync(worker, cleanupFailures).ConfigureAwait(true);
            }
            await TryDisposeAsync(catalog, cleanupFailures).ConfigureAwait(true);
            if (cleanupFailures.Count > 0)
            {
                cleanupFailures.Insert(0, startupFailure);
                throw new ProductionOwnershipCleanupException(
                    "Controller or PAD startup failed and cleanup was incomplete.",
                    cleanupFailures);
            }
            throw;
        }
    }

    public RuntimeInputSource[] Refresh(SettingsBundle activeSettings)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var entries = _catalog.Refresh(activeSettings.Companion);
        _catalogEntries = entries.ToDictionary(
            entry => entry.Source.SourceId,
            StringComparer.OrdinalIgnoreCase);
        return _inputHost.Sources.Select(source =>
        {
            _catalogEntries.TryGetValue(source.Descriptor.SourceId, out var entry);
            return new RuntimeInputSource(
                source.Descriptor.SourceId,
                source.Descriptor.DisplayName,
                source.Descriptor.HardwareId,
                entry?.ProductGuid.ToString("D"),
                entry?.ConfiguredDeviceId,
                source.Generation,
                source.Connected);
        }).ToArray();
    }

    public bool ObserveForCapture(string sourceId) => _catalog.ObserveForCapture(sourceId);

    public void ReleaseCaptureObservation(string sourceId) =>
        _catalog.ReleaseCaptureObservation(sourceId);

    public void DismissPromptPicker() => _windowsSta.Invoke(_promptPicker.Dismiss);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        return new ValueTask(_windowsSta.InvokeAsync(DisposeOnStaAsync));
    }

    private async Task DisposeOnStaAsync()
    {
        _promptPicker.Changed -= _factory.OnPromptPickerChanged;
        var failures = new List<Exception>();
        foreach (var worker in _workers.AsEnumerable().Reverse())
        {
            await TryDisposeAsync(worker, failures);
        }
        await TryDisposeAsync(_catalog, failures);
        _ownedCompletion.TrySetResult();
        if (failures.Count > 0)
        {
            throw new AggregateException("Controller cleanup did not complete.", failures);
        }
    }

    private static async Task TryDisposeAsync(IAsyncDisposable disposable, List<Exception> failures)
    {
        try
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }
}
