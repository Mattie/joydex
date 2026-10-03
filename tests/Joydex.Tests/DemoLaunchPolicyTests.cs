using Joydex.App;
using Joydex.Core.Config;

namespace Joydex.Tests;

public sealed class DemoLaunchPolicyTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"joydex-demo-policy-tests-{Guid.NewGuid():N}");

    public DemoLaunchPolicyTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void OrdinaryArgumentsDoNotEnableDemoMode()
    {
        Assert.Null(DemoLaunchPolicy.FromArguments([]));
        Assert.Null(DemoLaunchPolicy.FromArguments(["--config", "relative.json"]));
    }

    [Fact]
    public void AcceptsAnExistingAbsoluteDryRunConfiguration()
    {
        var configPath = SaveConfig(dryRun: true);

        var policy = DemoLaunchPolicy.FromArguments(["--demo", "--config", configPath]);

        Assert.NotNull(policy);
        Assert.Equal(Path.GetFullPath(configPath), policy.ConfigPath);
        Assert.Equal(Path.GetFullPath(_directory), policy.DataDirectory);
    }

    [Theory]
    [InlineData("missing-config")]
    [InlineData("relative-config")]
    [InlineData("missing-file")]
    [InlineData("extra-argument")]
    [InlineData("duplicate-demo")]
    public void InvalidDemoArgumentsFailClosed(string scenario)
    {
        var existing = SaveConfig(dryRun: true);
        var args = scenario switch
        {
            "missing-config" => new[] { "--demo" },
            "relative-config" => new[] { "--demo", "--config", "demo.json" },
            "missing-file" => new[] { "--demo", "--config", Path.Combine(_directory, "missing.json") },
            "extra-argument" => new[] { "--demo", "--config", existing, "unexpected" },
            "duplicate-demo" => new[] { "--demo", "--demo", "--config", existing },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        Assert.Throws<InvalidDataException>(() => DemoLaunchPolicy.FromArguments(args));
        Assert.False(File.Exists(Path.Combine(_directory, "missing.json")));
    }

    [Theory]
    [InlineData("--render-doc-screenshots")]
    [InlineData("--render-doc-screenshots-dark")]
    [InlineData("--render-button-map")]
    public void SpecialRenderModesCannotBypassMalformedDemoValidation(string renderArgument)
    {
        Assert.Throws<InvalidDataException>(() =>
            DemoLaunchPolicy.FromArguments([renderArgument, "--demo", "output"]));
    }

    [Fact]
    public void RejectsAConfigurationThatCanExecuteLiveActions()
    {
        var configPath = SaveConfig(dryRun: false);

        var exception = Assert.Throws<InvalidDataException>(() =>
            DemoLaunchPolicy.FromArguments(["--demo", "--config", configPath]));

        Assert.Contains("safety.dryRun", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsInvalidConfigurationWithoutReplacingIt()
    {
        var configPath = Path.Combine(_directory, "invalid.json");
        const string invalid = "{ this is not valid JSON";
        File.WriteAllText(configPath, invalid);

        Assert.Throws<InvalidDataException>(() =>
            DemoLaunchPolicy.FromArguments(["--demo", "--config", configPath]));
        Assert.Equal(invalid, File.ReadAllText(configPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string SaveConfig(bool dryRun)
    {
        var path = Path.Combine(_directory, "config.json");
        ConfigStore.Save(path, new CompanionConfig
        {
            Safety = new SafetyOptions { DryRun = dryRun },
        });
        return path;
    }
}
