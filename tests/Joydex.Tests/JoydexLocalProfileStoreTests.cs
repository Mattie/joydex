using Joydex.App;

namespace Joydex.Tests;

public sealed class JoydexLocalProfileStoreTests
{
    [Fact]
    public void WritePublishesNormalizedDiscoveryPathsAndReplacesAnOlderProfile()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "joydex-profile-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var profilePath = Path.Combine(root, "discovery", "profile.yaml");
            var configurationPath = Path.Combine(root, "runtime", "config.json");
            var deploymentRoot = Path.Combine(root, "package");
            var writtenAt = new DateTimeOffset(2026, 9, 15, 8, 30, 0, TimeSpan.FromHours(-5));
            Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
            File.WriteAllText(profilePath, "stale");

            JoydexLocalProfileStore.Write(
                profilePath,
                configurationPath,
                deploymentRoot,
                writtenAt);

            var profile = ParseProfile(File.ReadAllLines(profilePath));
            Assert.Equal(
                JoydexLocalProfileStore.CurrentSchemaVersion.ToString(),
                profile["schema_version"]);
            Assert.Equal(
                writtenAt.ToUniversalTime().ToString("O"),
                profile["written_at_utc"]);
            Assert.Equal(
                Path.GetFullPath(Path.GetDirectoryName(configurationPath)!),
                profile["data_root"]);
            Assert.Equal(
                Path.GetFullPath(configurationPath),
                profile["configuration_path"]);
            Assert.Equal(
                Path.Combine(Path.GetFullPath(deploymentRoot), "Joydex.App.exe"),
                profile["application_path"]);
            Assert.Equal(
                Path.Combine(Path.GetFullPath(deploymentRoot), "joydex-secrets.exe"),
                profile["secrets_cli_path"]);
            Assert.Empty(Directory.GetFiles(
                Path.GetDirectoryName(profilePath)!,
                ".profile.yaml.*.tmp"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    private static Dictionary<string, string> ParseProfile(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines.Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            var separator = line.IndexOf(':');
            Assert.True(separator > 0);
            var key = line[..separator];
            var value = line[(separator + 1)..].Trim();
            if (value.StartsWith('\'') && value.EndsWith('\''))
            {
                value = value[1..^1].Replace("''", "'");
            }
            Assert.True(result.TryAdd(key, value));
        }
        return result;
    }
}
