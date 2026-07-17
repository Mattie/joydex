namespace Joydex.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (DocumentationScreenshotRenderer.TryRender(args))
        {
            return;
        }

        if (TryRenderButtonMap(args))
        {
            return;
        }

        using var mutex = new Mutex(initiallyOwned: true, "Local\\Joydex", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "Joydex is already running.",
                "Joydex",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            var configPath = ConfigPathResolver.Resolve(args);
            Application.Run(new TrayApplicationContext(configPath));
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "Joydex could not start",
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
