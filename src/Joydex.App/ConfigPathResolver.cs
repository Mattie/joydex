namespace Joydex.App;

internal static class ConfigPathResolver
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Joydex",
        "config.json");

    public static string Resolve(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], "--config", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(args[index + 1]);
            }
        }

        var environmentPath = Environment.GetEnvironmentVariable("JOYDEX_CONFIG");
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            return Path.GetFullPath(environmentPath);
        }

        return DefaultPath;
    }

    public static bool HasExistingInstallation(string selectedConfigPath, string provisioningStatePath)
    {
        return File.Exists(selectedConfigPath)
            || File.Exists(DefaultPath)
            || File.Exists(provisioningStatePath);
    }
}
