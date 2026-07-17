using Joydex.App;

namespace Joydex.Tests;

public sealed class ConfigPathResolverTests
{
    [Fact]
    public void ExistingProvisioningStateMarksCustomProfileAsExistingInstall()
    {
        var root = Path.Combine(Path.GetTempPath(), $"joydex-{Guid.NewGuid():N}");
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
