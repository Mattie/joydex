using System.Text;
using Joydex.Secrets;

namespace Joydex.App;

/// <summary>Publishes stable, value-free paths that local Joydex clients may discover.</summary>
internal static class JoydexLocalProfileStore
{
    internal const int CurrentSchemaVersion = 1;
    private const string ProfileFileName = "profile.yaml";

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
