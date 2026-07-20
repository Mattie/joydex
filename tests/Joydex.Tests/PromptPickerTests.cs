using Joydex.App;
using Joydex.Core.Config;
using Joydex.Core.Input;
using Joydex.Core.Runtime;
using Joydex.Windows.Actions;

namespace Joydex.Tests;

public sealed class PromptPickerTests
{
    [Fact]
    public void LegacyConfigurationMigratesToPrimaryDeviceAndDefaultPicker()
    {
        var config = CompanionConfigNormalizer.Normalize(new CompanionConfig
        {
            Device = new DeviceSelector { ProductNameContains = "LEFT VPC MongoosT-50CM3" },
            Bindings =
            [
                Binding("Reasoning clockwise", 52, "reasoning-up"),
                Binding("Keep me", 9, "plan-mode"),
            ],
        });

        Assert.Equal("cm3", Assert.Single(config.Devices).Id);
        var picker = Assert.Single(config.PromptPickers);
        Assert.Equal(3, picker.Controls.Up.Button);
        Assert.Equal(2, picker.Controls.Down.Button);
        Assert.Equal(1, picker.Controls.Insert.Button);
        Assert.Contains(config.Bindings, binding => binding.Action == "reasoning-up");
        Assert.Contains(config.Bindings, binding => binding.Name == "Keep me");
    }

    [Fact]
    public void DefaultPickerMigrationPreservesExistingEncoderBindingsAcrossDevices()
    {
        var config = CreateConfig();
        config.PromptPickers.Clear();
        config.Bindings.Add(Binding("CM3 reasoning", 52, "reasoning-up", "cm3"));
        config.Bindings.Add(Binding("Alpha reasoning", 52, "reasoning-up", "alpha"));

        var migrated = CompanionConfigNormalizer.Normalize(config);

        Assert.Contains(migrated.Bindings, binding => binding.Name == "CM3 reasoning");
        Assert.Contains(migrated.Bindings, binding => binding.Name == "Alpha reasoning");
    }

    [Fact]
    public void DefaultPickerMigrationPreservesAConflictingGripBindingAndUsesAFreeControl()
    {
        var config = CreateConfig();
        config.PromptPickers.Clear();
        config.Bindings.Add(Binding("Existing grip action", 3, "new-task", "cm3"));

        var migrated = CompanionConfigNormalizer.Normalize(config);
        var picker = Assert.Single(migrated.PromptPickers);

        Assert.Contains(migrated.Bindings, binding => binding.Name == "Existing grip action");
        Assert.Equal(4, picker.Controls.Up.Button);
        Assert.Equal(2, picker.Controls.Down.Button);
        Assert.Equal(1, picker.Controls.Insert.Button);
        Assert.Empty(ConfigValidator.Validate(migrated));
    }

    [Fact]
    public void EngineRoutesPickerAndMapControlsPerDeviceAndDismissesForOtherButtons()
    {
        var config = CreateConfig();
        var engine = new CompanionEngine(config, deviceId: "alpha");
        var now = DateTimeOffset.UtcNow;
        engine.Process(Snapshot(now));

        var up = engine.Process(Snapshot(now.AddMilliseconds(16)), [Pressed(24)]);
        var other = engine.Process(Snapshot(now.AddMilliseconds(32)), [Pressed(7)]);
        var mapDown = engine.Process(Snapshot(now.AddMilliseconds(48)), [Pressed(30)]);
        var mapUp = engine.Process(Snapshot(now.AddMilliseconds(64)), [Released(30)]);

        var request = Assert.Single(up.PromptPickerRequests);
        Assert.Equal("picker-2", request.PickerId);
        Assert.Equal(PromptPickerGesture.Up, request.Gesture);
        Assert.Equal(PromptPickerGesture.Dismiss, Assert.Single(other.PromptPickerRequests).Gesture);
        Assert.True(Assert.Single(mapDown.ButtonMapVisibilityRequests).Visible);
        Assert.False(Assert.Single(mapUp.ButtonMapVisibilityRequests).Visible);

        var cm3Engine = new CompanionEngine(config, deviceId: "cm3");
        cm3Engine.Process(Snapshot(now));
        Assert.DoesNotContain(
            cm3Engine.Process(Snapshot(now.AddMilliseconds(16)), [Pressed(24)]).PromptPickerRequests,
            candidate => candidate.PickerId == "picker-2");
        Assert.DoesNotContain(
            engine.Process(Snapshot(now.AddMilliseconds(80)), [Pressed(51)]).PromptPickerRequests,
            candidate => candidate.PickerId == "picker-1");
    }

    [Fact]
    public void Cm3HoldControlCanShowAndHideTheAlphaMap()
    {
        var config = CreateConfig();
        var alpha = config.Devices[1];
        config.Devices[1] = new DeviceProfile
        {
            Id = alpha.Id,
            DisplayName = alpha.DisplayName,
            Selector = alpha.Selector,
            BankSelectors = alpha.BankSelectors,
            ButtonMapTemplate = alpha.ButtonMapTemplate,
            ButtonMapHoldControl = CompanionConfigNormalizer.Control("cm3", 34),
        };
        var now = DateTimeOffset.UtcNow;
        var cm3Engine = new CompanionEngine(config, deviceId: "cm3");
        cm3Engine.Process(Snapshot(now));

        var shown = Assert.Single(cm3Engine.Process(
            Snapshot(now.AddMilliseconds(16)),
            [Pressed(34)]).ButtonMapVisibilityRequests);
        var hidden = Assert.Single(cm3Engine.Process(
            Snapshot(now.AddMilliseconds(32)),
            [Released(34)]).ButtonMapVisibilityRequests);

        Assert.Equal("alpha", shown.DeviceId);
        Assert.True(shown.Visible);
        Assert.Equal("alpha", hidden.DeviceId);
        Assert.False(hidden.Visible);
        Assert.Empty(ConfigValidator.Validate(config));

        var alphaEngine = new CompanionEngine(config, deviceId: "alpha");
        alphaEngine.Process(Snapshot(now));
        Assert.Empty(alphaEngine.Process(
            Snapshot(now.AddMilliseconds(16)),
            [Pressed(34)]).ButtonMapVisibilityRequests);
    }

    [Fact]
    public void LegacySameDeviceMapButtonMigratesToAQualifiedControl()
    {
        var normalized = CompanionConfigNormalizer.Normalize(CreateConfig());

        var cm3 = normalized.Devices[0];
        var alpha = normalized.Devices[1];
        Assert.Equal("cm3", cm3.ButtonMapHoldControl!.DeviceId);
        Assert.Equal(36, cm3.ButtonMapHoldControl.Button);
        Assert.Equal("alpha", alpha.ButtonMapHoldControl!.DeviceId);
        Assert.Equal(30, alpha.ButtonMapHoldControl.Button);
    }

    [Fact]
    public void NonPickerPressWinsWhenItSharesAPollWithPickerInput()
    {
        var engine = new CompanionEngine(CreateConfig(), deviceId: "alpha");
        var now = DateTimeOffset.UtcNow;
        engine.Process(Snapshot(now));

        var requests = engine.Process(
            Snapshot(now.AddMilliseconds(16)),
            [Pressed(24), Pressed(7)]).PromptPickerRequests;

        Assert.Equal(PromptPickerGesture.Up, requests[0].Gesture);
        Assert.Equal(PromptPickerGesture.Dismiss, requests[^1].Gesture);
    }

    [Fact]
    public async Task FirstDetentOpensOnDefaultThenMovesAndInsertCloses()
    {
        var input = new RecordingInputSender();
        var coordinator = new PromptPickerCoordinator(
            CreateConfig(),
            _ => { },
            new ImmediateSynchronizationContext(),
            new AllowedGuard(),
            input);
        var snapshots = new List<PromptPickerSnapshot>();
        coordinator.Changed += (_, snapshot) => snapshots.Add(snapshot);

        await coordinator.HandleAsync(Request("picker-1", PromptPickerGesture.Down), CancellationToken.None);
        await coordinator.HandleAsync(Request("picker-1", PromptPickerGesture.Down), CancellationToken.None);
        await coordinator.HandleAsync(Request("picker-1", PromptPickerGesture.Insert), CancellationToken.None);

        Assert.Equal(1, snapshots[0].SelectedIndex);
        Assert.Equal(2, snapshots[1].SelectedIndex);
        Assert.False(snapshots[2].Visible);
        Assert.Equal("How much work will this take?", Assert.Single(input.Texts));
    }

    [Fact]
    public async Task ClosedInsertUsesDefaultAndLogsOnlyMetadata()
    {
        var input = new RecordingInputSender();
        var log = new List<string>();
        var coordinator = new PromptPickerCoordinator(
            CreateConfig(),
            log.Add,
            new ImmediateSynchronizationContext(),
            new AllowedGuard(),
            input);

        await coordinator.HandleAsync(Request("picker-1", PromptPickerGesture.Insert), CancellationToken.None);

        Assert.Equal("Make it so.", Assert.Single(input.Texts));
        Assert.DoesNotContain(log, message => message.Contains("Make it so", StringComparison.Ordinal));
        Assert.Contains(log, message => message.Contains("length=11", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PromptCanRunTheCodexSubmitActionAfterItsTextIsInserted()
    {
        var config = CreateConfig();
        config.PromptPickers[0].SubmitAfterInsert[1] = true;
        var input = new RecordingInputSender();
        var submitted = false;
        var coordinator = new PromptPickerCoordinator(
            config,
            _ => { },
            new ImmediateSynchronizationContext(),
            new AllowedGuard(),
            input,
            (_, _) =>
            {
                Assert.Single(input.Texts);
                submitted = true;
                return Task.CompletedTask;
            });

        await coordinator.HandleAsync(Request("picker-1", PromptPickerGesture.Insert), CancellationToken.None);

        Assert.Equal("Make it so.", Assert.Single(input.Texts));
        Assert.True(submitted);
    }

    [Fact]
    public async Task PromptDoesNotSubmitWhenTextInsertionFails()
    {
        var config = CreateConfig();
        config.PromptPickers[0].SubmitAfterInsert[1] = true;
        var input = new RecordingInputSender { FailTextInsertion = true };
        var submitted = false;
        var coordinator = new PromptPickerCoordinator(
            config,
            _ => { },
            new ImmediateSynchronizationContext(),
            new AllowedGuard(),
            input,
            (_, _) =>
            {
                submitted = true;
                return Task.CompletedTask;
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.HandleAsync(
            Request("picker-1", PromptPickerGesture.Insert),
            CancellationToken.None));

        Assert.False(submitted);
    }

    [Fact]
    public async Task ExitOptionIsLastAndInsertDismissesWithoutTypingOrSubmitting()
    {
        var config = CreateConfig();
        config.PromptPickers[0] = WithExitOption(config.PromptPickers[0]);
        var input = new RecordingInputSender();
        var submitted = false;
        var snapshots = new List<PromptPickerSnapshot>();
        var coordinator = new PromptPickerCoordinator(
            config,
            _ => { },
            new ImmediateSynchronizationContext(),
            new AllowedGuard(),
            input,
            (_, _) =>
            {
                submitted = true;
                return Task.CompletedTask;
            });
        coordinator.Changed += (_, snapshot) => snapshots.Add(snapshot);

        await coordinator.HandleAsync(Request("picker-1", PromptPickerGesture.Down), CancellationToken.None);
        await coordinator.HandleAsync(Request("picker-1", PromptPickerGesture.Down), CancellationToken.None);
        await coordinator.HandleAsync(Request("picker-1", PromptPickerGesture.Down), CancellationToken.None);

        Assert.Equal(PromptPickerCoordinator.ExitOptionLabel, snapshots[^1].Prompts[^1]);
        Assert.Equal(3, snapshots[^1].SelectedIndex);

        await coordinator.HandleAsync(Request("picker-1", PromptPickerGesture.Insert), CancellationToken.None);

        Assert.False(snapshots[^1].Visible);
        Assert.Empty(input.Texts);
        Assert.False(submitted);
    }

    [Fact]
    public async Task DryRunReportsSubmitIntentWithoutTypingOrSubmitting()
    {
        var current = CreateConfig();
        current.PromptPickers[0].SubmitAfterInsert[1] = true;
        var config = new CompanionConfig
        {
            Device = current.Device,
            Devices = current.Devices,
            Safety = new SafetyOptions { DryRun = true },
            PromptPickers = current.PromptPickers,
        };
        var input = new RecordingInputSender();
        var log = new List<string>();
        var submitted = false;
        var coordinator = new PromptPickerCoordinator(
            config,
            log.Add,
            new ImmediateSynchronizationContext(),
            new AllowedGuard(),
            input,
            (_, _) =>
            {
                submitted = true;
                return Task.CompletedTask;
            });

        await coordinator.HandleAsync(Request("picker-1", PromptPickerGesture.Insert), CancellationToken.None);

        Assert.Empty(input.Texts);
        Assert.False(submitted);
        Assert.Contains(log, message => message.Contains("submit=True", StringComparison.Ordinal));
    }

    [Fact]
    public void LegacyPromptSubmitFlagsDefaultToOffAndRoundTripByPrompt()
    {
        var legacy = CompanionConfigNormalizer.CreateDefaultPicker("cm3");
        var withoutFlags = new PromptPickerConfig
        {
            Id = legacy.Id,
            Name = legacy.Name,
            Prompts = [.. legacy.Prompts],
            DefaultPromptIndex = legacy.DefaultPromptIndex,
            Controls = legacy.Controls,
        };
        var normalized = CompanionConfigNormalizer.Normalize(new CompanionConfig
        {
            Device = new DeviceSelector { ProductNameContains = "CM3" },
            PromptPickers = [withoutFlags],
        });

        Assert.Equal([false, false, false], Assert.Single(normalized.PromptPickers).SubmitAfterInsert);
    }

    [Fact]
    public async Task SwitchingPickersOpensTheNewDefaultAndSelectionsWrap()
    {
        var input = new RecordingInputSender();
        var coordinator = new PromptPickerCoordinator(
            CreateConfig(),
            _ => { },
            new ImmediateSynchronizationContext(),
            new AllowedGuard(),
            input);
        var snapshots = new List<PromptPickerSnapshot>();
        coordinator.Changed += (_, snapshot) => snapshots.Add(snapshot);

        await coordinator.HandleAsync(Request("picker-1", PromptPickerGesture.Down), CancellationToken.None);
        await coordinator.HandleAsync(Request("picker-1", PromptPickerGesture.Down), CancellationToken.None);
        await coordinator.HandleAsync(Request("picker-1", PromptPickerGesture.Down), CancellationToken.None);
        await coordinator.HandleAsync(Request("picker-2", PromptPickerGesture.Up), CancellationToken.None);
        await coordinator.HandleAsync(Request("picker-2", PromptPickerGesture.Up), CancellationToken.None);

        Assert.Equal([1, 2, 0], snapshots.Take(3).Select(snapshot => snapshot.SelectedIndex));
        Assert.Equal("picker-2", snapshots[3].PickerId);
        Assert.Equal(0, snapshots[3].SelectedIndex);
        Assert.Equal(1, snapshots[4].SelectedIndex);
    }

    [Fact]
    public async Task DismissAndForegroundBlockResetSelectionWithoutTyping()
    {
        var input = new RecordingInputSender();
        var log = new List<string>();
        var foreground = new MutableGuard();
        var coordinator = new PromptPickerCoordinator(
            CreateConfig(),
            log.Add,
            new ImmediateSynchronizationContext(),
            foreground,
            input);
        var snapshots = new List<PromptPickerSnapshot>();
        coordinator.Changed += (_, snapshot) => snapshots.Add(snapshot);

        await coordinator.HandleAsync(Request("picker-1", PromptPickerGesture.Down), CancellationToken.None);
        await coordinator.HandleAsync(Request("picker-1", PromptPickerGesture.Down), CancellationToken.None);
        await coordinator.HandleAsync(Request("picker-1", PromptPickerGesture.Dismiss), CancellationToken.None);
        await coordinator.HandleAsync(Request("picker-1", PromptPickerGesture.Down), CancellationToken.None);
        foreground.Allowed = false;
        await coordinator.HandleAsync(Request("picker-1", PromptPickerGesture.Insert), CancellationToken.None);

        Assert.Equal(1, snapshots[^2].SelectedIndex);
        Assert.False(snapshots[^1].Visible);
        Assert.Empty(input.Texts);
        Assert.Contains(log, message => message.StartsWith("BLOCKED prompt-picker", StringComparison.Ordinal));
    }

    [Fact]
    public void MultiDeviceConfigurationRoundTripsPickerControls()
    {
        var directory = Path.Combine(Path.GetTempPath(), "JoydexTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "config.json");
        try
        {
            var expected = CreateConfig();
            expected.PromptPickers[1].SubmitAfterInsert[0] = true;
            expected.PromptPickers[1] = WithExitOption(expected.PromptPickers[1]);
            var alphaProfile = expected.Devices[1];
            expected.Devices[1] = new DeviceProfile
            {
                Id = alphaProfile.Id,
                DisplayName = alphaProfile.DisplayName,
                Selector = alphaProfile.Selector,
                BankSelectors = alphaProfile.BankSelectors,
                ButtonMapTemplate = alphaProfile.ButtonMapTemplate,
                ButtonMapHoldControl = CompanionConfigNormalizer.Control("cm3", 34),
            };
            ConfigStore.Save(path, expected);

            var loaded = ConfigStore.LoadOrCreate(path);

            Assert.Equal(["cm3", "alpha"], loaded.Devices.Select(device => device.Id));
            var alphaPicker = Assert.Single(loaded.PromptPickers, picker => picker.Id == "picker-2");
            Assert.Equal("alpha", alphaPicker.Controls.Up.DeviceId);
            Assert.Equal(24, alphaPicker.Controls.Up.Button);
            Assert.Equal([true, false], alphaPicker.SubmitAfterInsert);
            Assert.True(alphaPicker.IncludeExitOption);
            var alphaMap = Assert.Single(loaded.Devices, device => device.Id == "alpha");
            Assert.Equal("cm3", alphaMap.ButtonMapHoldControl!.DeviceId);
            Assert.Equal(34, alphaMap.ButtonMapHoldControl.Button);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ValidatorRejectsFourthPickerAndCrossDeviceControlConflict()
    {
        var config = CreateConfig();
        var first = config.PromptPickers[0];
        var invalid = new CompanionConfig
        {
            Device = config.Device,
            Devices = config.Devices,
            Bindings = config.Bindings,
            PromptPickers =
            [
                first,
                WithId(first, "picker-x"),
                WithId(first, "picker-y"),
                WithId(first, "picker-z"),
            ],
        };

        var errors = ConfigValidator.Validate(invalid);

        Assert.Contains(errors, error => error.Contains("between one and three", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("conflicts", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorRejectsBlankPromptsInvalidDefaultsAndUnknownControlReferences()
    {
        var config = CreateConfig();
        config.PromptPickers.Clear();
        config.PromptPickers.Add(
            new PromptPickerConfig
            {
                Id = "broken",
                Name = "Broken",
                Prompts = ["   "],
                DefaultPromptIndex = 3,
                Controls = new PromptPickerControls
                {
                    Up = CompanionConfigNormalizer.Control("missing-device", 1),
                    Down = new DeviceControlReference
                    {
                        DeviceId = "cm3",
                        Bank = "missing-bank",
                        Button = 2,
                    },
                    Insert = CompanionConfigNormalizer.Control("cm3", 0),
                },
            });

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, error => error.Contains("nonblank prompt", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("defaultPromptIndex", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("unknown device", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("unknown bank", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("one-based button number", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorRejectsDuplicateDeviceSelectorsAndOverlappingAlwaysControls()
    {
        var config = CreateConfig();
        config.Devices.Add(new DeviceProfile
        {
            Id = "duplicate-cm3",
            DisplayName = "Duplicate CM3",
            Selector = config.Devices[0].Selector,
        });
        config.Devices[0].BankSelectors["M2"] = 80;
        config.Bindings.Add(new ButtonBinding
        {
            Name = "Always button ten",
            DeviceId = "cm3",
            Bank = CompanionConfig.AlwaysBank,
            Button = 10,
            Action = "new-task",
        });
        var original = config.PromptPickers[0];
        config.PromptPickers[0] = new PromptPickerConfig
        {
            Id = original.Id,
            Name = original.Name,
            Prompts = original.Prompts,
            IncludeExitOption = original.IncludeExitOption,
            DefaultPromptIndex = original.DefaultPromptIndex,
            Controls = new PromptPickerControls
            {
                Up = new DeviceControlReference { DeviceId = "cm3", Bank = "M2", Button = 10 },
                Down = original.Controls.Down,
                Insert = original.Controls.Insert,
            },
        };

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, error => error.Contains("same physical controller", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("conflicts", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorAllowsAlwaysAndBankedOrdinaryBindingsOnTheSameButton()
    {
        var config = CreateConfig();
        config.Devices[0].BankSelectors["M2"] = 80;
        config.Bindings.Add(new ButtonBinding
        {
            Name = "Always ten",
            DeviceId = "cm3",
            Bank = CompanionConfig.AlwaysBank,
            Button = 10,
            Action = "new-task",
        });
        config.Bindings.Add(new ButtonBinding
        {
            Name = "M2 ten",
            DeviceId = "cm3",
            Bank = "M2",
            Button = 10,
            Action = "plan-mode",
        });

        Assert.Empty(ConfigValidator.Validate(config));
    }

    [Fact]
    public void ValidatorRejectsDeviceIdsThatAreUnsafeForStateFileNames()
    {
        var config = CreateConfig();
        config.Devices.Add(new DeviceProfile
        {
            Id = "../outside",
            DisplayName = "Unsafe",
            Selector = new DeviceSelector { ProductNameContains = "Unsafe controller" },
        });

        Assert.Contains(
            ConfigValidator.Validate(config),
            error => error.Contains("may contain only", StringComparison.Ordinal));
    }

    private static CompanionConfig CreateConfig()
    {
        var cm3 = new DeviceProfile
        {
            Id = "cm3",
            DisplayName = "CM3",
            Selector = new DeviceSelector { ProductNameContains = "CM3" },
            ButtonMapTemplate = "cm3",
            ButtonMapHoldButton = 36,
        };
        var alpha = new DeviceProfile
        {
            Id = "alpha",
            DisplayName = "Alpha",
            Selector = new DeviceSelector { ProductNameContains = "WarBRD" },
            ButtonMapTemplate = "alpha-warbrd",
            ButtonMapHoldButton = 30,
        };
        return new CompanionConfig
        {
            Device = cm3.Selector,
            Devices = [cm3, alpha],
            Safety = new SafetyOptions { DryRun = false },
            PromptPickers =
            [
                CompanionConfigNormalizer.CreateDefaultPicker("cm3"),
                new PromptPickerConfig
                {
                    Id = "picker-2",
                    Name = "Alpha prompts",
                    Prompts = ["One", "Two"],
                    SubmitAfterInsert = [false, false],
                    DefaultPromptIndex = 0,
                    Controls = new PromptPickerControls
                    {
                        Up = CompanionConfigNormalizer.Control("alpha", 24),
                        Down = CompanionConfigNormalizer.Control("alpha", 23),
                        Insert = CompanionConfigNormalizer.Control("alpha", 21),
                    },
                },
            ],
        };
    }

    private static PromptPickerConfig WithId(PromptPickerConfig picker, string id) => new()
    {
        Id = id,
        Name = id,
        Prompts = [.. picker.Prompts],
        SubmitAfterInsert = [.. picker.SubmitAfterInsert],
        IncludeExitOption = picker.IncludeExitOption,
        DefaultPromptIndex = picker.DefaultPromptIndex,
        Controls = picker.Controls,
    };

    private static PromptPickerConfig WithExitOption(PromptPickerConfig picker) => new()
    {
        Id = picker.Id,
        Name = picker.Name,
        Prompts = [.. picker.Prompts],
        SubmitAfterInsert = [.. picker.SubmitAfterInsert],
        IncludeExitOption = true,
        DefaultPromptIndex = picker.DefaultPromptIndex,
        Controls = picker.Controls,
    };

    private static ButtonBinding Binding(string name, int button, string action, string? deviceId = null) => new()
    {
        Name = name,
        DeviceId = deviceId,
        Bank = CompanionConfig.AlwaysBank,
        Button = button,
        Action = action,
    };

    private static JoystickSnapshot Snapshot(DateTimeOffset at) => new(at, new bool[128], [-1], [0]);
    private static JoystickEvent Pressed(int button) => new(JoystickEventKind.ButtonPressed, button - 1, 1);
    private static JoystickEvent Released(int button) => new(JoystickEventKind.ButtonReleased, button - 1, 0);
    private static PromptPickerRequest Request(string picker, PromptPickerGesture gesture) => new(picker, gesture, "cm3", 0);

    private sealed class ImmediateSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => callback(state);
    }

    private sealed class AllowedGuard : IForegroundProcessGuard
    {
        public ForegroundCheck Check(SafetyOptions safety, bool actionMayBringCodexForward) =>
            new(true, "Codex", "allowed");
    }

    private sealed class MutableGuard : IForegroundProcessGuard
    {
        public bool Allowed { get; set; } = true;

        public ForegroundCheck Check(SafetyOptions safety, bool actionMayBringCodexForward) =>
            new(Allowed, Allowed ? "Codex" : "Explorer", Allowed ? "allowed" : "Codex is not foreground");
    }

    private sealed class RecordingInputSender : IInputSender
    {
        public List<string> Texts { get; } = [];
        public bool FailTextInsertion { get; init; }
        public Task SendTextAsync(string text, CancellationToken cancellationToken)
        {
            if (FailTextInsertion)
            {
                throw new InvalidOperationException("text insertion failed");
            }
            Texts.Add(text);
            return Task.CompletedTask;
        }
        public Task SendSequenceAsync(KeySequence sequence, CancellationToken cancellationToken) => Task.CompletedTask;
        public void HoldChord(KeyChord chord) { }
        public void ReleaseChord(KeyChord chord) { }
        public void SendMouseWheel(int delta) { }
    }
}
