using VirpilCodexPad.App;

namespace VirpilCodexPad.Core.Tests;

public sealed class ConfigPathResolverTests
{
    [Fact]
    public void ExistingProvisioningStateMarksCustomProfileAsExistingInstall()
    {
        var root = Path.Combine(Path.GetTempPath(), $"virpil-codex-pad-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var selectedConfigPath = Path.Combine(root, "custom", "config.json");
            var provisioningStatePath = Path.Combine(root, "codex-keybinding-provisioning.json");
            File.WriteAllText(provisioningStatePath, "{}");

            Assert.True(ConfigPathResolver.HasExistingInstallation(
                selectedConfigPath,
                provisioningStatePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
