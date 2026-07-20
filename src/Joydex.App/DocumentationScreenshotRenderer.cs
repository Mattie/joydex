using Joydex.Core.Config;
using Joydex.Core.Mapping;
using Joydex.Windows.Interop;

namespace Joydex.App;

/// <summary>
/// Renders the repository's UI screenshots from live WinForms controls so the
/// documentation stays tied to the source instead of hand-edited mockups.
/// </summary>
internal static class DocumentationScreenshotRenderer
{
    private const string Argument = "--render-doc-screenshots";

    public static bool TryRender(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (!string.Equals(args[index], Argument, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Render(Path.GetFullPath(args[index + 1]));
            return true;
        }

        return false;
    }

    private static void Render(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var config = CreateDocumentationConfig();
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"joydex-doc-screenshots-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var configPath = Path.Combine(temporaryDirectory, "config.json");
        var configurationStatePath = Path.Combine(temporaryDirectory, "configuration-window.json");
        var buttonMapStatePath = Path.Combine(temporaryDirectory, "button-map-window.json");

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
                configuration.Size = new Size(1500, 1000);
                RenderForm(
                    configuration,
                    Path.Combine(outputDirectory, "joydex-configuration.png"));
            }

            foreach (var (tab, fileName) in new[]
            {
                ("Prompt Pickers", "joydex-prompt-pickers.png"),
                ("Button Maps", "joydex-button-maps-configuration.png"),
                ("General", "joydex-general-configuration.png"),
            })
            {
                using var configuration = new ConfigurationForm(
                    configPath,
                    configurationStatePath,
                    cooperativeWindow.Handle,
                    documentationMode: true);
                configuration.Size = new Size(1500, 1000);
                configuration.SelectTabForDocumentation(tab);
                RenderForm(configuration, Path.Combine(outputDirectory, fileName));
            }

            using (var activity = new DryRunActivityForm(config))
            {
                activity.SetConnectionStatus("Connected to VPC Throttle MT-50CM3 (documentation sample).");
                activity.Append("INPUT press from throttle/button 56");
                activity.Append("INPUT release from throttle/button 56");
                activity.Append("INPUT press from throttle/button 62");
                activity.Append("INPUT press from throttle/button 68");
                activity.Append("INPUT press from throttle/button 52");
                activity.Append("INPUT press from throttle/button 54");
                RenderForm(activity, Path.Combine(outputDirectory, "joydex-dry-run.png"));
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
            foreach (var path in new[] { configPath, configurationStatePath, buttonMapStatePath })
            {
                File.Delete(path);
            }

            Directory.Delete(temporaryDirectory, recursive: false);
        }
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
        form.Show();
        Application.DoEvents();
        form.PerformLayout();

        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
        form.Close();
    }

}
