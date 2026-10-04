namespace Joydex.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(
            DocumentationScreenshotRenderer.IsRenderRequest(args)
                ? HighDpiMode.DpiUnaware
                : HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        DemoLaunchPolicy? demoPolicy;
        try
        {
            demoPolicy = DemoLaunchPolicy.FromArguments(args);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "Joydex demo could not start",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        RuntimeAppStartupMode startupMode;
        try
        {
            startupMode = RuntimeAppStartup.SelectMode(args);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "Joydex could not start",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        if (DocumentationScreenshotRenderer.TryRender(args))
        {
            return;
        }

        if (TryRenderButtonMap(args))
        {
            return;
        }

        try
        {
            RuntimeAppStartup.Run(
                args,
                startupMode,
                demoPolicy,
                WindowsFormsRuntimeAppStartupHost.Instance);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                startupMode == RuntimeAppStartupMode.Settings
                    ? "Joydex settings could not start"
                    : "Joydex could not start",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static bool TryRenderButtonMap(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (!string.Equals(args[index], "--render-button-map", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var outputPath = Path.GetFullPath(args[index + 1]);
            var config = Core.Config.ConfigStore.LoadOrCreate(ConfigPathResolver.Resolve(args));
            using var canvas = new ButtonMapCanvas(config);
            using var preview = canvas.RenderPreview(new Size(1600, 1200));
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            preview.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
            return true;
        }

        return false;
    }
}
