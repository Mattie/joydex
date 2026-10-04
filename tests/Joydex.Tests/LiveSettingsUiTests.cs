using System.Runtime.ExceptionServices;
using Joydex.App;
using Joydex.Contracts;
using Joydex.Core.Config;
using Joydex.Core.Input;
using Joydex.Core.TaskAlerts;
using Joydex.Core.Voice;
using Joydex.Windows.Runtime;

namespace Joydex.Tests;

public sealed class LiveSettingsUiTests
{
    [Fact]
    public void DemoConfigurationIsClearlyLabeledAndLocksDryRun()
    {
        var directory = CreateDirectory();
        try
        {
            RunSta(() =>
            {
                using var form = new ConfigurationForm(
                    Path.Combine(directory, "config.json"),
                    Path.Combine(directory, "window.json"),
                    IntPtr.Zero,
                    initialConfig: Config(Guid.NewGuid(), "controller"),
                    demoMode: true);

                Assert.Contains("Demo", form.Text, StringComparison.OrdinalIgnoreCase);
                var dryRun = Assert.IsType<CheckBox>(Assert.Single(
                    form.Controls.Find("ConfigurationDryRun", searchAllChildren: true)));
                Assert.True(dryRun.Checked);
                Assert.False(dryRun.Enabled);
                Assert.Contains("locked", dryRun.Text, StringComparison.OrdinalIgnoreCase);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PromptPickerAdvancesItsBaseAfterAConflict()
    {
        var settings = new SettingsBundle(
            CompanionConfig.CreateSafeDefault(),
            VoicePePreferences.Default,
            PebbleIndexPreferences.Default,
            TaskAlertPreferences.Default);
        var result = new RuntimeSettingsWriteResult(
            RuntimeSettingsWriteOutcome.Conflict,
            Guid.NewGuid(),
            new SettingsSnapshot(11, settings, settings, [], []),
            ApplyResult: null,
            "test result");

        Assert.Equal(
            11,
            RuntimeTrayApplicationContext.PromptPickerBaseRevisionAfter(4, result));
    }

    [Theory]
    [InlineData((int)RuntimeSettingsWriteOutcome.Uncertain)]
    [InlineData((int)RuntimeSettingsWriteOutcome.Running)]
    public void PromptPickerKeepsItsBaseWhileAWriteIsUncertain(int outcomeValue)
    {
        var outcome = (RuntimeSettingsWriteOutcome)outcomeValue;
        var settings = new SettingsBundle(
            CompanionConfig.CreateSafeDefault(),
            VoicePePreferences.Default,
            PebbleIndexPreferences.Default,
            TaskAlertPreferences.Default);
        var result = new RuntimeSettingsWriteResult(
            outcome,
            Guid.NewGuid(),
            new SettingsSnapshot(11, settings, settings, [], []),
            ApplyResult: null,
            "test result");

        Assert.Equal(
            4,
            RuntimeTrayApplicationContext.PromptPickerBaseRevisionAfter(4, result));
        var candidate = CompanionConfig.CreateSafeDefault();
        Assert.Same(candidate, RuntimeTrayApplicationContext.PromptPickerCandidateAfter(candidate, result));
    }

    [Theory]
    [InlineData((int)RuntimeSettingsWriteOutcome.Conflict)]
    [InlineData((int)RuntimeSettingsWriteOutcome.Failed)]
    [InlineData((int)RuntimeSettingsWriteOutcome.Rejected)]
    [InlineData((int)RuntimeSettingsWriteOutcome.Applied)]
    [InlineData((int)RuntimeSettingsWriteOutcome.PendingIdle)]
    [InlineData((int)RuntimeSettingsWriteOutcome.NoChanges)]
    public void TerminalPromptPickerResultRebasesBeforeAdvancingRevision(int outcomeValue)
    {
        var outcome = (RuntimeSettingsWriteOutcome)outcomeValue;
        var candidateDevice = new DeviceSelector { ProductNameContains = "editor device" };
        var candidateDevices = new List<DeviceProfile>();
        var candidateBanks = new Dictionary<string, int> { ["editor"] = 4 };
        var candidatePickers = new List<PromptPickerConfig>();
        var candidate = new CompanionConfig
        {
            Device = candidateDevice,
            Devices = candidateDevices,
            Polling = new PollingOptions { PollIntervalMs = 5 },
            Safety = new SafetyOptions { DryRun = true },
            OpenWorkingDirectory = new OpenWorkingDirectoryOptions { Target = "editor" },
            BankSelectors = candidateBanks,
            Bindings = [],
            PromptPickers = candidatePickers,
        };
        var authoritativePolling = new PollingOptions { PollIntervalMs = 29 };
        var authoritativeSafety = new SafetyOptions { DryRun = false };
        var authoritativeDirectory = new OpenWorkingDirectoryOptions { Target = "runtime" };
        var authoritativeBindings = new List<ButtonBinding>();
        var authoritative = new CompanionConfig
        {
            Polling = authoritativePolling,
            Safety = authoritativeSafety,
            OpenWorkingDirectory = authoritativeDirectory,
            Bindings = authoritativeBindings,
        };

        var bundle = new SettingsBundle(authoritative, VoicePePreferences.Default,
            PebbleIndexPreferences.Default, TaskAlertPreferences.Default);
        var result = new RuntimeSettingsWriteResult(outcome, Guid.NewGuid(),
            new SettingsSnapshot(11, bundle, bundle, [], []), null, "recovered result");
        var rebased = RuntimeTrayApplicationContext.PromptPickerCandidateAfter(candidate, result);

        Assert.Equal(11, RuntimeTrayApplicationContext.PromptPickerBaseRevisionAfter(4, result));
        Assert.Same(candidateDevice, rebased.Device);
        Assert.Same(candidateDevices, rebased.Devices);
        Assert.Same(candidateBanks, rebased.BankSelectors);
        Assert.Same(candidatePickers, rebased.PromptPickers);
        Assert.Same(authoritativePolling, rebased.Polling);
        Assert.Same(authoritativeSafety, rebased.Safety);
        Assert.Same(authoritativeDirectory, rebased.OpenWorkingDirectory);
        Assert.Same(authoritativeBindings, rebased.Bindings);
    }

    [Fact]
    public void SettingsStateChangeCanRefreshAStateMarkedForResynchronization()
    {
        var state = new RuntimeClientConnectionState(
            Snapshot: null,
            InputEventCursor: 0,
            IsInitialized: true,
            ResynchronizationRequired: true,
            IsDisconnected: false,
            DisconnectFailure: null);

        Assert.True(RuntimeSettingsApplicationContext.ShouldProcessStateChange(
            RuntimeClientChangeKind.ResynchronizationRequired,
            state));
        Assert.False(RuntimeSettingsApplicationContext.ShouldProcessStateChange(
            RuntimeClientChangeKind.RuntimeEvent,
            state));
    }

    [Fact]
    public void SeparatelyLoadedUnchangedPreferencesDoNotRequestRuntimeRestarts()
    {
        var directory = CreateDirectory();
        try
        {
            var configPath = Path.Combine(directory, "config.json");
            var voicePath = Path.Combine(directory, "voice.json");
            var pebblePath = Path.Combine(directory, "pebble.json");
            var config = Config(Guid.NewGuid(), "controller");
            ConfigStore.Save(configPath, config);
            VoicePePreferencesStore.Save(voicePath, VoicePePreferences.Default);
            PebbleIndexPreferencesStore.Save(pebblePath, PebbleIndexPreferences.Default);

            var changes = ConfigurationChangeDetector.Detect(
                ConfigStore.LoadOrCreate(configPath),
                ConfigStore.LoadOrCreate(configPath),
                VoicePePreferencesStore.LoadOrCreate(voicePath),
                VoicePePreferencesStore.LoadOrCreate(voicePath),
                PebbleIndexPreferencesStore.LoadOrCreate(pebblePath),
                PebbleIndexPreferencesStore.LoadOrCreate(pebblePath));

            Assert.False(changes.CompanionChanged);
            Assert.False(changes.VoicePreferencesChanged);
            Assert.False(changes.PebbleIndexPreferencesChanged);
            Assert.False(changes.VoiceRuntimeChanged);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ObservationCoalescerFiltersOtherSourcesAndRepeatedIdlePolls()
    {
        var coalescer = new InputObservationCoalescer();
        coalescer.SelectSource("selected");
        var selected = Observation("selected", sequence: 1, buttonPressed: false, events: []);

        Assert.False(coalescer.TryQueue(Observation("other", sequence: 2, buttonPressed: true, events: [])));
        Assert.True(coalescer.TryQueue(selected));
        Assert.False(coalescer.TryQueue(Observation("selected", sequence: 3, buttonPressed: false, events: [])));
        Assert.Equal(selected, coalescer.TakePending());
        Assert.False(coalescer.TryQueue(Observation("selected", sequence: 4, buttonPressed: false, events: [])));
    }

    [Fact]
    public void ObservationCoalescerKeepsEdgesWhileReplacingThePendingSnapshot()
    {
        var coalescer = new InputObservationCoalescer();
        coalescer.SelectSource("selected");
        var pressed = new JoystickEvent(JoystickEventKind.ButtonPressed, 0, 1);
        var released = new JoystickEvent(JoystickEventKind.ButtonReleased, 0, 0);

        Assert.True(coalescer.TryQueue(Observation("selected", 1, buttonPressed: true, [pressed])));
        Assert.False(coalescer.TryQueue(Observation("selected", 2, buttonPressed: false, [released])));

        var pending = Assert.IsType<InputObservation>(coalescer.TakePending());
        Assert.False(pending.Snapshot.Buttons[0]);
        Assert.Equal([pressed, released], pending.Events);
        Assert.Equal(2, pending.Sequence);
    }

    [Fact]
    public void ObservationCoalescerReschedulesPendingObservationAfterPostingFails()
    {
        var coalescer = new InputObservationCoalescer();
        coalescer.SelectSource("selected");
        var selected = Observation("selected", sequence: 1, buttonPressed: false, events: []);

        Assert.True(coalescer.TryQueue(selected));
        coalescer.CancelScheduledDispatch();

        Assert.True(coalescer.TryQueue(Observation("selected", sequence: 2, buttonPressed: false, events: [])));
        Assert.Equal(selected, coalescer.TakePending());
    }

    [Fact]
    public void SettingsSaveAddsOnlyUnconfiguredDevicesUsedByPromptPickers()
    {
        var configured = new DeviceProfile
        {
            Id = "configured",
            DisplayName = "Configured controller",
            Selector = new DeviceSelector { ProductNameContains = "Configured" },
            ButtonMapTemplate = "edited-map",
        };
        var editorDevices = new[]
        {
            new DeviceProfile
            {
                Id = "configured",
                DisplayName = "Configured controller",
                Selector = configured.Selector,
                ButtonMapTemplate = "stale-map",
            },
            new DeviceProfile
            {
                Id = "device-2",
                DisplayName = "Picker controller",
                Selector = new DeviceSelector { ProductNameContains = "Picker" },
            },
            new DeviceProfile
            {
                Id = "device-3",
                DisplayName = "Unused controller",
                Selector = new DeviceSelector { ProductNameContains = "Unused" },
            },
        };
        var picker = new PromptPickerConfig
        {
            Id = "picker",
            Name = "Picker",
            Prompts = ["Prompt"],
            Controls = new PromptPickerControls
            {
                Up = CompanionConfigNormalizer.Control("device-2", 1),
                Down = CompanionConfigNormalizer.Control("configured", 2),
                Insert = CompanionConfigNormalizer.Control("device-2", 3),
            },
        };

        var merged = ConfigurationForm.MergePromptPickerDevices(
            [configured],
            editorDevices,
            [picker]);

        Assert.Equal(["configured", "device-2"], merged.Select(device => device.Id));
        Assert.Equal("edited-map", merged[0].ButtonMapTemplate);
        Assert.Equal("Picker controller", merged[1].DisplayName);
    }

    [Fact]
    public void PromptPickerEditorDoesNotDuplicateRuntimeSourceMappedByProductName()
    {
        var productGuid = Guid.NewGuid();
        var instanceGuid = Guid.NewGuid();
        var selector = new DeviceSelector
        {
            ProductNameContains = "VPC Throttle MT-50CM3",
            ProductGuid = productGuid.ToString("D"),
        };
        var config = new CompanionConfig
        {
            Device = selector,
            Devices =
            [
                new DeviceProfile
                {
                    Id = "configured",
                    DisplayName = "Configured throttle",
                    Selector = selector,
                },
            ],
            Safety = new SafetyOptions { DryRun = true },
        };
        var runtimeSource = new RuntimeInputSourceCatalogEntry(
            new InputSourceDescriptor("configured", "VPC Throttle MT-50CM3", instanceGuid.ToString("D")),
            new DeviceSelector
            {
                ProductNameContains = "VPC Throttle MT-50CM3",
                InstanceGuid = instanceGuid.ToString("D"),
                ProductGuid = productGuid.ToString("D"),
            },
            instanceGuid,
            productGuid,
            ConfiguredDeviceId: "configured");
        using var client = new StaticConfigurationInputClient([runtimeSource]);

        RunSta(() =>
        {
            using var form = new PromptPickerEditorForm(
                Path.Combine(Path.GetTempPath(), "unused-joydex-config.json"),
                IntPtr.Zero,
                pickerOnly: true,
                initialConfig: config,
                inputClient: client);

            var devices = form.GetDeviceProfiles();
            var device = Assert.Single(devices, profile => string.Equals(
                profile.Id,
                "configured",
                StringComparison.OrdinalIgnoreCase));
            Assert.Equal("configured", device.Id);
            Assert.Null(device.Selector.InstanceGuid);
            Assert.DoesNotContain(devices, profile =>
                Guid.TryParse(profile.Selector.InstanceGuid, out var configuredInstance)
                && configuredInstance == instanceGuid);
        });
    }

    private static InputObservation Observation(
        string sourceId,
        long sequence,
        bool buttonPressed,
        IReadOnlyList<JoystickEvent> events) => new(
            sequence,
            new InputSourceState(
                new InputSourceDescriptor(sourceId, sourceId),
                Generation: 1,
                Connected: true),
            new JoystickSnapshot(
                DateTimeOffset.UtcNow,
                [buttonPressed],
                [-1],
                [0]),
            events);

    private static CompanionConfig Config(Guid instanceGuid, string deviceId) => new()
    {
        Device = new DeviceSelector
        {
            ProductNameContains = "Synthetic controller",
            InstanceGuid = instanceGuid.ToString("D"),
            ProductGuid = Guid.NewGuid().ToString("D"),
        },
        Devices =
        [
            new DeviceProfile
            {
                Id = deviceId,
                DisplayName = "Synthetic controller",
                Selector = new DeviceSelector
                {
                    ProductNameContains = "Synthetic controller",
                    InstanceGuid = instanceGuid.ToString("D"),
                    ProductGuid = Guid.NewGuid().ToString("D"),
                },
                ButtonMapTemplate = "cm3",
            },
        ],
        Polling = new PollingOptions
        {
            ConnectWarmupMs = 0,
            PollIntervalMs = 5,
            ReconnectIntervalMs = 250,
        },
        Safety = new SafetyOptions { DryRun = true },
    };

    private static void RunSta(Action action, TimeSpan? timeout = null)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // Cold WinForms initialization can exceed five seconds on the shared CI runner.
        // Joining also avoids disposing a completion signal that a late worker still needs.
        Assert.True(
            thread.Join(timeout ?? TimeSpan.FromSeconds(15)),
            "The settings form did not close.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
    private static string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "joydex-live-settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class StaticConfigurationInputClient(
        IReadOnlyList<RuntimeInputSourceCatalogEntry> sources) : IConfigurationInputClient
    {
        public string ConnectionId => "static-test-client";

        public event EventHandler<InputObservationEventArgs>? InputObserved
        {
            add { }
            remove { }
        }

        public event EventHandler<InputCaptureChangedEventArgs>? CaptureChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<RuntimeInputSourceCatalogEntry> RefreshSources() => sources;

        public InputSourceState? GetSourceState(string sourceId) => null;

        public InputCaptureStartResult BeginCapture(
            string sourceId,
            string purpose,
            long? expectedGeneration = null) => new(false, null, "Capture is not available in this test.");

        public bool ObserveForCapture(string sourceId) => false;

        public bool CancelCapture(Guid captureId) => false;

        public void ReleaseCaptureObservation(string sourceId)
        {
        }

        public void Dispose()
        {
        }
    }
}
