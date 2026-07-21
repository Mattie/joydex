using Joydex.Core.Config;
using Joydex.Core.Mapping;
using Joydex.Core.TaskAlerts;
using Joydex.Windows.Interop;
using Joydex.Windows.TaskAlerts;

namespace Joydex.App;

/// <summary>
/// Renders the repository's UI screenshots from live WinForms controls so the
/// documentation stays tied to the source instead of hand-edited mockups.
/// </summary>
internal static class DocumentationScreenshotRenderer
{
    private const string Argument = "--render-doc-screenshots";
    private const string DarkArgument = "--render-doc-screenshots-dark";

    internal static bool IsRenderRequest(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], Argument, StringComparison.OrdinalIgnoreCase)
                || string.Equals(args[index], DarkArgument, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryRender(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            var dark = string.Equals(args[index], DarkArgument, StringComparison.OrdinalIgnoreCase);
            if (!dark && !string.Equals(args[index], Argument, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Render(Path.GetFullPath(args[index + 1]), dark);
            return true;
        }

        return false;
    }

    private static void Render(string outputDirectory, bool dark)
    {
        using var themeOverride = JoydexTheme.OverrideDarkMode(dark);
        Directory.CreateDirectory(outputDirectory);
        var config = CreateDocumentationConfig();
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"joydex-doc-screenshots-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var configPath = Path.Combine(temporaryDirectory, "config.json");
        var configurationStatePath = Path.Combine(temporaryDirectory, "configuration-window.json");
        var buttonMapStatePath = Path.Combine(temporaryDirectory, "button-map-window.json");
        var taskAlertPreferencesPath = Path.Combine(temporaryDirectory, "task-alerts.json");
        var taskAlertStatePath = Path.Combine(temporaryDirectory, "task-alert-state.json");
        var hooksPath = Path.Combine(temporaryDirectory, "hooks.json");
        var relayPath = Path.Combine(temporaryDirectory, "Joydex.HookRelay.exe");
        var linkToolProfilePath = Path.Combine(temporaryDirectory, "joydex-linktool.led.json");

        try
        {
            ConfigStore.Save(configPath, config);
            using var cooperativeWindow = new CooperativeWindow("Joydex documentation renderer");

            using (var configuration = new ConfigurationForm(
                       configPath,
                       configurationStatePath,
                       cooperativeWindow.Handle,
                       documentationMode: true))
            {
                configuration.Size = new Size(1200, 800);
                configuration.Shown += (_, _) => configuration.ExerciseBindingGridEditingForDocumentation();
                RenderForm(
                    configuration,
                    Path.Combine(outputDirectory, "joydex-configuration.png"));
            }

            foreach (var (tab, fileName, size) in new[]
            {
                ("Prompt Pickers", "joydex-prompt-pickers.png", new Size(1200, 800)),
                ("Button Maps", "joydex-button-maps-configuration.png", new Size(1200, 800)),
                ("General", "joydex-general-configuration.png", new Size(1200, 800)),
            })
            {
                using var configuration = new ConfigurationForm(
                    configPath,
                    configurationStatePath,
                    cooperativeWindow.Handle,
                    documentationMode: true);
                configuration.Size = size;
                configuration.SelectTabForDocumentation(tab);
                RenderForm(configuration, Path.Combine(outputDirectory, fileName));
            }

            using (var activity = new DryRunActivityForm(config))
            {
                activity.SetConnectionStatus("Connected to VPC Throttle MT-50CM3 (documentation sample).");
                var timestamp = new DateTime(2026, 7, 20, 9, 41, 10, DateTimeKind.Local);
                foreach (var message in new[]
                {
                    "INPUT press from throttle/button 56",
                    "INPUT release from throttle/button 56",
                    "INPUT press from throttle/button 62",
                    "INPUT press from throttle/button 68",
                    "INPUT press from throttle/button 52",
                    "INPUT press from throttle/button 54",
                })
                {
                    activity.AppendForDocumentation(message, timestamp);
                    timestamp = timestamp.AddSeconds(1);
                }

                RenderForm(activity, Path.Combine(outputDirectory, "joydex-dry-run.png"));
            }

            File.WriteAllText(relayPath, "Documentation placeholder");
            var taskAlertCoordinator = new TaskAlertCoordinator(taskAlertPreferencesPath, taskAlertStatePath);
            try
            {
                var hookManager = new CodexHookManager(hooksPath);
                foreach (var (size, fileName) in new[]
                {
                    (new Size(1100, 720), "joydex-task-alerts.png"),
                    (new Size(820, 640), "joydex-task-alerts-compact.png"),
                })
                {
                    using var taskAlerts = new TaskAlertsForm(
                        taskAlertCoordinator,
                        hookManager,
                        relayPath,
                        linkToolProfilePath,
                        _ => Task.CompletedTask);
                    taskAlerts.SetSnapshotForDocumentation(CreateTaskAlertDocumentationSnapshot());
                    taskAlerts.Size = size;
                    RenderForm(taskAlerts, Path.Combine(outputDirectory, fileName));
                }
            }
            finally
            {
                taskAlertCoordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            using var map = new ButtonMapCanvas(config);
            using var preview = map.RenderPreview(new Size(1600, 1200));
            preview.Save(
                Path.Combine(outputDirectory, "joydex-button-map.png"),
                System.Drawing.Imaging.ImageFormat.Png);

            var alphaConfig = CreateAlphaDocumentationConfig();
            using var alphaMap = new ButtonMapCanvas(alphaConfig, "alpha-warbrd");
            using var alphaPreview = alphaMap.RenderPreview(new Size(1600, 1014));
            alphaPreview.Save(
                Path.Combine(outputDirectory, "joydex-alpha-button-map.png"),
                System.Drawing.Imaging.ImageFormat.Png);
        }
        finally
        {
            foreach (var path in new[]
            {
                configPath,
                configurationStatePath,
                buttonMapStatePath,
                taskAlertPreferencesPath,
                taskAlertStatePath,
                hooksPath,
                relayPath,
                linkToolProfilePath,
            })
            {
                File.Delete(path);
            }

            Directory.Delete(temporaryDirectory, recursive: false);
        }
    }

    private static TaskAlertSnapshot CreateTaskAlertDocumentationSnapshot()
    {
        var now = DateTimeOffset.Now;
        var assignments = new[]
        {
            new TaskAlertAssignment(1, "00000000-0000-7000-8000-000000000001", "turn-01", TaskAlertState.Running, now),
            new TaskAlertAssignment(2, "00000000-0000-7000-8000-000000000002", "turn-02", TaskAlertState.Completed, now),
            new TaskAlertAssignment(3, "00000000-0000-7000-8000-000000000003", "turn-03", TaskAlertState.Completed, now),
            new TaskAlertAssignment(4, "00000000-0000-7000-8000-000000000004", "turn-04", TaskAlertState.Completed, now),
            new TaskAlertAssignment(5, "00000000-0000-7000-8000-000000000005", "turn-05", TaskAlertState.Completed, now),
        };
        var recentEvents = assignments.Select((assignment, index) => new TaskAlertEventTrace(
            now.AddSeconds(-index),
            index == 0 ? CodexLifecycleEvent.UserPromptSubmit : CodexLifecycleEvent.Stop,
            assignment.SessionId,
            assignment.TurnId,
            assignment.Slot,
            assignment.State,
            index == 0 ? TaskAlertEventResult.Assigned : TaskAlertEventResult.Updated)).ToArray();
        return new TaskAlertSnapshot(
            Enabled: true,
            Assignments: assignments,
            DroppedEventCount: 0,
            Bank: 2,
            BankAutomaticallyDetected: true,
            RecentEvents: recentEvents);
    }

    private static CompanionConfig CreateAlphaDocumentationConfig()
    {
        var cm3Selector = new DeviceSelector { ProductNameContains = "LEFT VPC MongoosT-50CM3" };
        var alphaSelector = new DeviceSelector { ProductNameContains = "RIGHT VPC Stick WarBRD" };
        return new CompanionConfig
        {
            Device = cm3Selector,
            Devices =
            [
                new DeviceProfile
                {
                    Id = "cm3",
                    DisplayName = "LEFT VPC MongoosT-50CM3",
                    Selector = cm3Selector,
                    ButtonMapTemplate = "cm3",
                },
                new DeviceProfile
                {
                    Id = "alpha-warbrd",
                    DisplayName = "RIGHT VPC Stick WarBRD",
                    Selector = alphaSelector,
                    ButtonMapTemplate = "alpha-warbrd",
                },
            ],
            Bindings =
            [
                new ButtonBinding { Name = "Back", DeviceId = "alpha-warbrd", Bank = "always", Button = 7, Action = "navigate-back" },
                new ButtonBinding { Name = "Forward", DeviceId = "alpha-warbrd", Bank = "always", Button = 8, Action = "navigate-forward" },
            ],
            PromptPickers =
            [
                CompanionConfigNormalizer.CreateDefaultPicker("cm3"),
                new PromptPickerConfig
                {
                    Id = "picker-2",
                    Name = "Review and debug",
                    Prompts = ["$ponytail-review-boss", "Debug the issue and remedy the problem.", "$critical-review"],
                    SubmitAfterInsert = [true, true, true],
                    IncludeExitOption = true,
                    DefaultPromptIndex = 1,
                    Controls = new PromptPickerControls
                    {
                        Up = CompanionConfigNormalizer.Control("alpha-warbrd", 24),
                        Down = CompanionConfigNormalizer.Control("alpha-warbrd", 23),
                        Insert = CompanionConfigNormalizer.Control("alpha-warbrd", 21),
                    },
                },
            ],
        };
    }

    private static CompanionConfig CreateDocumentationConfig()
    {
        var profile = CodexMicroStarterProfile.Create(Cm3ModeDialProfile.FiveWayShift);
        var cm3Selector = new DeviceSelector { ProductNameContains = "VPC Throttle MT-50CM3" };
        var alphaSelector = new DeviceSelector { ProductNameContains = "RIGHT VPC Stick WarBRD" };
        return new CompanionConfig
        {
            Device = cm3Selector,
            Devices =
            [
                new DeviceProfile
                {
                    Id = "cm3",
                    DisplayName = "VPC Throttle MT-50CM3",
                    Selector = cm3Selector,
                    ButtonMapTemplate = "cm3",
                    ButtonMapHoldControl = CompanionConfigNormalizer.Control("cm3", 36),
                },
                new DeviceProfile
                {
                    Id = "alpha-warbrd",
                    DisplayName = "RIGHT VPC Stick WarBRD",
                    Selector = alphaSelector,
                    ButtonMapTemplate = "alpha-warbrd",
                    ButtonMapHoldControl = CompanionConfigNormalizer.Control("cm3", 34),
                },
            ],
            BankSelectors = new Dictionary<string, int>(profile.BankSelectors, StringComparer.OrdinalIgnoreCase),
            Bindings = profile.Bindings
                .Where(binding => !string.Equals(binding.Action, "button-map", StringComparison.OrdinalIgnoreCase))
                .ToList(),
            PromptPickers = profile.PromptPickers.ToList(),
        };
    }

    private static void RenderForm(Form form, string outputPath)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-32000, -32000);
        form.ShowInTaskbar = false;
        if (form is ThemedForm themedForm)
        {
            themedForm.SuppressActivation = true;
        }

        form.Show();
        Application.DoEvents();
        form.MinimumSize = Size.Empty;
        form.PerformLayout();
        PrepareControlsForScreenshot(form);
        form.Invalidate(invalidateChildren: true);
        form.Update();
        Application.DoEvents();

        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
        form.Close();
    }

    private static void PrepareControlsForScreenshot(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is BorderedTextBox input)
            {
                input.ShowPlaceholderForDocumentation();
            }

            PrepareControlsForScreenshot(child);
        }
    }

}
