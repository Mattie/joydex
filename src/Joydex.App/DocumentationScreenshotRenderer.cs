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

    private static CompanionConfig CreateDocumentationConfig()
    {
        var profile = CodexMicroStarterProfile.Create(Cm3ModeDialProfile.FiveWayShift);
        return new CompanionConfig
        {
            BankSelectors = new Dictionary<string, int>(profile.BankSelectors, StringComparer.OrdinalIgnoreCase),
            Bindings = profile.Bindings.ToList(),
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

        using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
        form.Close();
    }
}
