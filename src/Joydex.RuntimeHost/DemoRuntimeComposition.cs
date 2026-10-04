using Joydex.App;
using Joydex.Contracts;
using Joydex.Core.Config;
using Joydex.Core.Input;
using Joydex.Core.Mapping;
using Joydex.Core.Runtime;
using Joydex.Core.Voice;
using Joydex.RuntimeHost.Production;
using Joydex.RuntimeHost.Settings;
using Joydex.Windows.Actions;
using Joydex.Windows.Input;
using Joydex.Windows.Runtime;

namespace Joydex.RuntimeHost;

/// <summary>
/// Runs the real controller and binding pipeline against deterministic simulated controllers.
/// Every external action remains a recorded dry-run result.
/// </summary>
internal sealed class DemoRuntimeComposition : IRuntimeComposition
{
    private readonly ProductionRuntimeComposition _inner;

    private DemoRuntimeComposition(ProductionRuntimeComposition inner) =>
        _inner = inner;

    public bool VoiceSessionActive => false;

    internal Task Completion => _inner.Completion;

    public event EventHandler? ActivationBoundaryAvailable
    {
        add => _inner.ActivationBoundaryAvailable += value;
        remove => _inner.ActivationBoundaryAvailable -= value;
    }

    public event EventHandler<RuntimeUiEvent>? UiChanged
    {
        add => _inner.UiChanged += value;
        remove => _inner.UiChanged -= value;
    }

    public static IRuntimeComposition Create(
        RuntimeInputHost inputHost,
        TimeProvider timeProvider,
        CancellationToken runtimeCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputHost);
        ArgumentNullException.ThrowIfNull(timeProvider);
        var startedAt = timeProvider.GetTimestamp();
        var inputSources = new DemoJoystickSourceFactory(
            () => timeProvider.GetElapsedTime(startedAt));
        return new DemoRuntimeComposition(new ProductionRuntimeComposition(
            new DemoRuntimeOwnerFactory(inputHost, inputSources),
            runtimeCancellationToken));
    }

    public RuntimeUiSnapshot GetUiSnapshot() => _inner.GetUiSnapshot();

    public RuntimeInputSource[] Refresh(SettingsBundle activeSettings) =>
        _inner.Refresh(activeSettings);

    public bool ObserveForCapture(Guid captureId, string sourceId) =>
        _inner.ObserveForCapture(captureId, sourceId);

    public void ReleaseCaptureObservation(Guid captureId) =>
        _inner.ReleaseCaptureObservation(captureId);

    public Task<SettingsActivationResult> ActivateAsync(
        SettingsAggregateId aggregate,
        SettingsBundle activationCandidate,
        long desiredRevision,
        CancellationToken runtimeCancellationToken) =>
        _inner.ActivateAsync(
            aggregate,
            activationCandidate,
            desiredRevision,
            runtimeCancellationToken);

    public Task<RuntimeCommandResult> ExecuteAsync(
        RuntimeCommandRequest request,
        CancellationToken runtimeCancellationToken) =>
        _inner.ExecuteAsync(request, runtimeCancellationToken);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private sealed class DemoRuntimeOwnerFactory(
        RuntimeInputHost inputHost,
        DemoJoystickSourceFactory inputSources) : IProductionRuntimeOwnerFactory
    {
        private readonly ProductionRuntimeUiProjector _ui = new();
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SyntheticRuntimeCommandHandler _commands = new();
        private int _disposed;

        public event Action? VoiceBecameIdle
        {
            add { }
            remove { }
        }

        public event EventHandler<RuntimeUiEvent>? UiChanged
        {
            add => _ui.Changed += value;
            remove => _ui.Changed -= value;
        }

        public Task Completion => _completion.Task;

        public RuntimeUiSnapshot GetUiSnapshot() => _ui.GetSnapshot();

        public void RefreshVoiceMessaging(VoicePePreferences preferences) =>
            _ui.RefreshVoiceMessaging(preferences);

        public async Task<IProductionRuntimeOwner?> CreateAsync(
            SettingsAggregateId aggregate,
            SettingsBundle activeSettings,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(activeSettings);
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            cancellationToken.ThrowIfCancellationRequested();
            return aggregate == SettingsAggregateId.Companion
                ? await DemoCompanionOwner.StartAsync(
                        inputHost,
                        inputSources,
                        _ui,
                        activeSettings,
                        cancellationToken)
                    .ConfigureAwait(false)
                : null;
        }

        public Task<RuntimeCommandResult> ExecuteAsync(
            RuntimeCommandRequest request,
            SettingsBundle activeSettings,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(activeSettings);
            return _commands.ExecuteAsync(request, cancellationToken);
        }

        public void ReportFailure(SettingsAggregateId aggregate, Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            _ui.PublishActivity($"Demo {aggregate} stopped: {exception.Message}");
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _completion.TrySetResult();
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DemoCompanionOwner : IProductionInputOwner
    {
        private readonly RuntimeInputHost _inputHost;
        private readonly DemoJoystickSourceFactory _inputSources;
        private readonly RuntimeInputSourceProvider _catalog;
        private readonly List<CompanionWorker> _workers;
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IReadOnlyDictionary<string, RuntimeInputSourceCatalogEntry> _catalogEntries;
        private int _disposed;

        private DemoCompanionOwner(
            RuntimeInputHost inputHost,
            DemoJoystickSourceFactory inputSources,
            RuntimeInputSourceProvider catalog,
            List<CompanionWorker> workers,
            IReadOnlyList<RuntimeInputSourceCatalogEntry> catalogEntries)
        {
            _inputHost = inputHost;
            _inputSources = inputSources;
            _catalog = catalog;
            _workers = workers;
            _catalogEntries = catalogEntries.ToDictionary(
                entry => entry.Source.SourceId,
                StringComparer.OrdinalIgnoreCase);
        }

        public SettingsAggregateId Aggregate => SettingsAggregateId.Companion;

        public Task Completion => _completion.Task;

        public static async Task<DemoCompanionOwner> StartAsync(
            RuntimeInputHost inputHost,
            DemoJoystickSourceFactory inputSources,
            ProductionRuntimeUiProjector ui,
            SettingsBundle activeSettings,
            CancellationToken cancellationToken)
        {
            var config = ProjectConfig(activeSettings.Companion, inputSources);
            var catalog = new RuntimeInputSourceProvider(
                inputHost,
                inputSources,
                config.Polling,
                ui.PublishActivity);
            var workers = new List<CompanionWorker>();
            try
            {
                var catalogEntries = catalog.Refresh(config);
                foreach (var device in config.Devices)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var worker = new CompanionWorker(
                        config,
                        inputSources.Create(),
                        CreateExecutor(config, ui),
                        ui.PublishActivity,
                        keyStateLifecycle: DemoKeyStateLifecycle.Instance,
                        deviceId: device.Id,
                        buttonMapHandler: request => ui.PublishButtonMap(
                            request.DeviceId,
                            request.Visible),
                        inputHost: inputHost);
                    worker.StatusChanged += (_, status) => ui.PublishController(
                        device.Id,
                        device.DisplayName,
                        status,
                        device.ButtonMapTemplate is not null);
                    workers.Add(worker);
                    worker.Start();
                }
                cancellationToken.ThrowIfCancellationRequested();

                return new DemoCompanionOwner(
                    inputHost,
                    inputSources,
                    catalog,
                    workers,
                    catalogEntries);
            }
            catch (Exception startupFailure)
            {
                var cleanupFailures = new List<Exception>();
                foreach (var worker in workers.AsEnumerable().Reverse())
                {
                    try
                    {
                        await worker.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        cleanupFailures.Add(exception);
                    }
                }
                try
                {
                    await catalog.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception);
                }
                if (cleanupFailures.Count > 0)
                {
                    cleanupFailures.Insert(0, startupFailure);
                    throw new AggregateException(
                        "Demo controller startup failed and cleanup did not complete.",
                        cleanupFailures);
                }
                throw;
            }
        }

        public RuntimeInputSource[] Refresh(SettingsBundle activeSettings)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            var config = ProjectConfig(activeSettings.Companion, _inputSources);
            var entries = _catalog.Refresh(config);
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

        public bool ObserveForCapture(string sourceId) =>
            _catalog.ObserveForCapture(sourceId);

        public void ReleaseCaptureObservation(string sourceId) =>
            _catalog.ReleaseCaptureObservation(sourceId);

        public void DismissPromptPicker()
        {
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var failures = new List<Exception>();
            foreach (var worker in _workers.AsEnumerable().Reverse())
            {
                try
                {
                    await worker.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }
            try
            {
                await _catalog.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            _completion.TrySetResult();
            if (failures.Count > 0)
            {
                throw new AggregateException(
                    "Demo controller cleanup did not complete.",
                    failures);
            }
        }

        private static CompanionConfig ProjectConfig(
            CompanionConfig active,
            DemoJoystickSourceFactory inputSources)
        {
            var normalized = CompanionConfigNormalizer.Normalize(active);
            var availableProfiles = normalized.Devices.ToList();
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var profiles = new List<DeviceProfile>();
            foreach (var device in inputSources.EnumerateDevices())
            {
                var template = FindProfile(device, availableProfiles);
                if (template is not null)
                {
                    availableProfiles.Remove(template);
                }

                var id = UniqueId(
                    template?.Id ?? (profiles.Count == 0 ? "cm3" : "warbrd"),
                    usedIds);
                var selector = new DeviceSelector
                {
                    ProductNameContains = device.ProductName,
                    InstanceGuid = device.InstanceGuid.ToString("D"),
                    ProductGuid = device.ProductGuid.ToString("D"),
                };
                profiles.Add(new DeviceProfile
                {
                    Id = id,
                    DisplayName = device.ProductName,
                    Selector = selector,
                    BankSelectors = template is null
                        ? []
                        : new Dictionary<string, int>(
                            template.BankSelectors,
                            StringComparer.OrdinalIgnoreCase),
                    ButtonMapTemplate = template?.ButtonMapTemplate
                        ?? CompanionConfigNormalizer.InferTemplate(selector),
                    ButtonMapHoldControl = CloneControl(template?.ButtonMapHoldControl),
                });
            }

            var simulatedIds = profiles
                .Select(profile => profile.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new CompanionConfig
            {
                Device = profiles[0].Selector,
                Devices = profiles,
                Polling = new PollingOptions
                {
                    ConnectWarmupMs = 0,
                    PollIntervalMs = 5,
                    ReconnectIntervalMs = 25,
                    ActionCooldownMs = 0,
                    AxisTraceThreshold = normalized.Polling.AxisTraceThreshold,
                },
                Safety = new SafetyOptions
                {
                    DryRun = true,
                    RequireCodexForeground = false,
                    CodexProcessNames = [],
                    SimulatorProcessNames = [],
                },
                OpenWorkingDirectory = new OpenWorkingDirectoryOptions
                {
                    Target = normalized.OpenWorkingDirectory.Target,
                },
                BankSelectors = new Dictionary<string, int>(
                    profiles[0].BankSelectors,
                    StringComparer.OrdinalIgnoreCase),
                Bindings = normalized.Bindings
                    .Where(binding => simulatedIds.Contains(binding.DeviceId ?? string.Empty))
                    .Select(CloneBinding)
                    .ToList(),
                PromptPickers = normalized.PromptPickers
                    .Where(picker => PickerTargets(picker, simulatedIds))
                    .Select(ClonePicker)
                    .ToList(),
            };
        }

        private static DeviceProfile? FindProfile(
            DirectInputDeviceInfo device,
            IReadOnlyList<DeviceProfile> candidates)
        {
            var template = CompanionConfigNormalizer.InferTemplate(new DeviceSelector
            {
                ProductNameContains = device.ProductName,
            });
            return candidates.FirstOrDefault(candidate => string.Equals(
                       candidate.ButtonMapTemplate,
                       template,
                       StringComparison.OrdinalIgnoreCase))
                   ?? candidates.FirstOrDefault(candidate =>
                       candidate.DisplayName.Contains(
                           template ?? device.ProductName,
                           StringComparison.OrdinalIgnoreCase)
                       || candidate.Id.Contains(
                           template ?? device.ProductName,
                           StringComparison.OrdinalIgnoreCase))
                   ?? candidates.FirstOrDefault();
        }

        private static string UniqueId(string preferred, ISet<string> used)
        {
            var candidate = preferred;
            var suffix = 2;
            while (!used.Add(candidate))
            {
                candidate = $"{preferred}-{suffix++}";
            }
            return candidate;
        }

        private static ButtonBinding CloneBinding(ButtonBinding binding) => new()
        {
            Name = binding.Name,
            DeviceId = binding.DeviceId,
            Bank = binding.Bank,
            Button = binding.Button,
            Trigger = binding.Trigger,
            Action = binding.Action,
            WheelNotches = binding.WheelNotches,
        };

        private static PromptPickerConfig ClonePicker(PromptPickerConfig picker) => new()
        {
            Id = picker.Id,
            Name = picker.Name,
            Prompts = [.. picker.Prompts],
            SubmitAfterInsert = [.. picker.SubmitAfterInsert],
            IncludeExitOption = picker.IncludeExitOption,
            DefaultPromptIndex = picker.DefaultPromptIndex,
            Controls = new PromptPickerControls
            {
                Up = CloneControl(picker.Controls.Up)!,
                Down = CloneControl(picker.Controls.Down)!,
                Insert = CloneControl(picker.Controls.Insert)!,
            },
        };

        private static bool PickerTargets(
            PromptPickerConfig picker,
            IReadOnlySet<string> deviceIds) =>
            deviceIds.Contains(picker.Controls.Up.DeviceId)
            && deviceIds.Contains(picker.Controls.Down.DeviceId)
            && deviceIds.Contains(picker.Controls.Insert.DeviceId);

        private static DeviceControlReference? CloneControl(DeviceControlReference? control) =>
            control is null
                ? null
                : new DeviceControlReference
                {
                    DeviceId = control.DeviceId,
                    Bank = control.Bank,
                    Button = control.Button,
                };

        private static CodexActionExecutor CreateExecutor(
            CompanionConfig config,
            ProductionRuntimeUiProjector ui)
        {
            var input = new DemoInputSender();
            return new CodexActionExecutor(
                config.Safety,
                ui.PublishActivity,
                DemoKeybindingResolver.Instance,
                config.OpenWorkingDirectory,
                DemoForegroundProcessGuard.Instance,
                input,
                DemoClipboard.Instance,
                internalAction: request => ui.PublishActivity(
                    $"DEMO {CodexActionCatalog.GetId(request.Action)} {request.Trigger} "
                    + $"from {request.DeviceId}/button {request.Button}"),
                injectedKeyStateOwner: new InjectedKeyStateOwner(input));
        }
    }

    private sealed class DemoKeybindingResolver : ICodexKeybindingResolver
    {
        public static DemoKeybindingResolver Instance { get; } = new();

        public Task<CodexBindingResolution> ResolveAsync(
            CodexAction action,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!KeySequenceParser.TryParse(
                    "Ctrl+Alt+F24",
                    allowBareModifiers: true,
                    out var sequence,
                    out var error))
            {
                throw new InvalidOperationException(
                    "The demo action binding could not be prepared: " + error);
            }
            return Task.FromResult(new CodexBindingResolution(
                action,
                "demo." + CodexActionCatalog.GetId(action),
                sequence,
                CodexBindingSource.Provisioned,
                CodexBindingSnapshotState.Current,
                null));
        }
    }

    private sealed class DemoForegroundProcessGuard : IForegroundProcessGuard
    {
        public static DemoForegroundProcessGuard Instance { get; } = new();

        public ForegroundCheck Check(
            SafetyOptions safety,
            bool actionMayBringCodexForward) =>
            new(true, "JoydexDemo", "The isolated demo records this action without sending input.");
    }

    private sealed class DemoInputSender : IInputSender
    {
        public Task SendSequenceAsync(
            KeySequence sequence,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SendTextAsync(
            string text,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public void HoldChord(KeyChord chord)
        {
        }

        public void ReleaseChord(KeyChord chord)
        {
        }

        public void SendMouseWheel(int delta)
        {
        }
    }

    private sealed class DemoClipboard : IWorkingDirectoryClipboard
    {
        public static DemoClipboard Instance { get; } = new();

        public uint GetSequenceNumber() => 0;

        public Task<ClipboardDirectoryResult> WaitForNewDirectoryAsync(
            uint previousSequenceNumber,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromResult(ClipboardDirectoryResult.Failure(
                "The isolated demo does not read the clipboard."));
    }

    private sealed class DemoKeyStateLifecycle : IInjectedKeyStateLifecycle
    {
        public static DemoKeyStateLifecycle Instance { get; } = new();

        public void ClearInjectedKeyState()
        {
        }

        public void ReleaseHeldKeys(InputSourceSession source)
        {
        }

        public void ReleaseAllHeldKeys()
        {
        }
    }
}
