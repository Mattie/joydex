using Joydex.Core.Config;

namespace Joydex.App;

/// <summary>
/// Validates the deliberately narrow command line used for an isolated, dry-run demo.
/// </summary>
internal sealed record DemoLaunchPolicy(string ConfigPath, string DataDirectory)
{
    private const string DemoArgument = "--demo";
    private const string ConfigArgument = "--config";

    public static DemoLaunchPolicy? FromArguments(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!args.Any(argument => string.Equals(argument, DemoArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        if (args.Count != 3
            || args.Count(argument => string.Equals(argument, DemoArgument, StringComparison.OrdinalIgnoreCase)) != 1
            || args.Count(argument => string.Equals(argument, ConfigArgument, StringComparison.OrdinalIgnoreCase)) != 1)
        {
            throw new InvalidDataException(
                "Demo mode requires exactly: --demo --config <absolute-existing-config>.");
        }

        var configIndex = FindArgument(args, ConfigArgument);
        if (configIndex < 0 || configIndex + 1 >= args.Count)
        {
            throw new InvalidDataException(
                "Demo mode requires exactly: --demo --config <absolute-existing-config>.");
        }

        var suppliedPath = args[configIndex + 1];
        if (string.IsNullOrWhiteSpace(suppliedPath) || !Path.IsPathFullyQualified(suppliedPath))
        {
            throw new InvalidDataException("The demo configuration path must be absolute.");
        }

        var configPath = Path.GetFullPath(suppliedPath);
        if (!File.Exists(configPath))
        {
            throw new InvalidDataException("The demo configuration file must already exist.");
        }

        var dataDirectory = Path.GetDirectoryName(configPath)
            ?? throw new InvalidDataException("The demo configuration path has no parent directory.");
        var normalDataDirectory = Path.GetDirectoryName(Path.GetFullPath(ConfigPathResolver.DefaultPath))!;
        if (IsWithin(dataDirectory, normalDataDirectory))
        {
            throw new InvalidDataException(
                "The demo configuration must be in a dedicated scratch directory outside the normal Joydex data directory.");
        }

        CompanionConfig config;
        try
        {
            config = ConfigStore.LoadOrCreate(configPath);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or System.Text.Json.JsonException)
        {
            throw new InvalidDataException(
                "The demo configuration could not be loaded and validated: " + exception.Message,
                exception);
        }

        if (!config.Safety.DryRun)
        {
            throw new InvalidDataException("The demo configuration must have safety.dryRun set to true.");
        }

        return new DemoLaunchPolicy(configPath, dataDirectory);
    }

    private static int FindArgument(IReadOnlyList<string> args, string expected)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], expected, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsWithin(string candidate, string directory)
    {
        var relative = Path.GetRelativePath(directory, candidate);
        return !Path.IsPathRooted(relative)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
