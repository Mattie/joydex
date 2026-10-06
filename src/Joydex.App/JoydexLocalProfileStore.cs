using System.Text;
using Joydex.Secrets;

namespace Joydex.App;

/// <summary>Publishes stable, value-free paths that local Joydex clients may discover.</summary>
internal static class JoydexLocalProfileStore
{
    internal const int CurrentSchemaVersion = 1;
    private const string ProfileFileName = "profile.yaml";

    /// <summary>Reads the normal tray's recorded executable and configuration for local relaunch.</summary>
    internal static (string ApplicationPath, string ConfigurationPath) ReadLaunchPaths()
    {
        var profilePath = Path.Combine(SecretsPaths.GetDefaultDataRoot(), ProfileFileName);
        return ReadLaunchPaths(profilePath);
    }

    internal static (string ApplicationPath, string ConfigurationPath) ReadLaunchPaths(
        string profilePath)
    {
        var lines = File.ReadAllLines(profilePath);
        var schemaLines = lines.Where(line =>
            line.StartsWith("schema_version:", StringComparison.Ordinal)).ToArray();
        if (schemaLines.Length != 1
            || !string.Equals(schemaLines[0], "schema_version: 1", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Joydex local profile schema is missing or unsupported.");
        }

        var applicationPath = Path.GetFullPath(ReadQuotedScalar(lines, "application_path"));
        var configurationPath = Path.GetFullPath(ReadQuotedScalar(lines, "configuration_path"));
        if (!string.Equals(
                Path.GetFileName(applicationPath),
                "Joydex.App.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Joydex local profile names an invalid App executable.");
        }
        return (applicationPath, configurationPath);
    }

    private static string ReadQuotedScalar(string[] lines, string name)
    {
        var prefix = name + ":";
        var values = lines.Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (values.Length != 1)
        {
            throw new InvalidDataException($"The Joydex local profile field '{name}' is missing or repeated.");
        }
        var value = values[0][prefix.Length..].Trim();
        if (value.Length < 2 || value[0] != '\'' || value[^1] != '\'')
        {
            throw new InvalidDataException($"The Joydex local profile field '{name}' is malformed.");
        }
        return value[1..^1].Replace("''", "'");
    }

    public static bool TryPublish(string configurationPath, string deploymentRoot)
    {
        try
        {
            Write(
                Path.Combine(SecretsPaths.GetDefaultDataRoot(), ProfileFileName),
                configurationPath,
                deploymentRoot,
                DateTimeOffset.UtcNow);
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or NotSupportedException)
        {
            return false;
        }
    }

    internal static void Write(
        string profilePath,
        string configurationPath,
        string deploymentRoot,
        DateTimeOffset writtenAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentRoot);

        var fullConfigurationPath = Path.GetFullPath(configurationPath.Trim());
        var dataRoot = Path.GetDirectoryName(fullConfigurationPath)
            ?? throw new InvalidOperationException(
                "The Joydex configuration path has no parent directory.");
        var fullDeploymentRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(deploymentRoot.Trim()));
        var profile = new JoydexLocalProfile(
            CurrentSchemaVersion,
            writtenAtUtc.ToUniversalTime(),
            Path.TrimEndingDirectorySeparator(dataRoot),
            fullConfigurationPath,
            Path.Combine(fullDeploymentRoot, "Joydex.App.exe"),
            Path.Combine(fullDeploymentRoot, "joydex-secrets.exe"));
        var contents = Serialize(profile);

        var fullProfilePath = Path.GetFullPath(profilePath.Trim());
        var directory = Path.GetDirectoryName(fullProfilePath)
            ?? throw new InvalidOperationException(
                "The Joydex local profile path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullProfilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(Encoding.UTF8.GetBytes(contents));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, fullProfilePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Serialize(JoydexLocalProfile profile) => string.Join(
        '\n',
        $"schema_version: {profile.SchemaVersion}",
        $"written_at_utc: {Quote(profile.WrittenAtUtc.ToString("O"))}",
        $"data_root: {Quote(profile.DataRoot)}",
        $"configuration_path: {Quote(profile.ConfigurationPath)}",
        $"application_path: {Quote(profile.ApplicationPath)}",
        $"secrets_cli_path: {Quote(profile.SecretsCliPath)}",
        string.Empty);

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
}

internal sealed record JoydexLocalProfile(
    int SchemaVersion,
    DateTimeOffset WrittenAtUtc,
    string DataRoot,
    string ConfigurationPath,
    string ApplicationPath,
    string SecretsCliPath);
