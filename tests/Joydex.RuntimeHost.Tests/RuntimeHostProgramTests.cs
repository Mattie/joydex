using System.Text.Json;
using Joydex.Contracts;
using Joydex.Core.Config;

namespace Joydex.RuntimeHost.Tests;

public sealed class RuntimeHostProgramTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "joydex-runtime-program-tests",
        Guid.NewGuid().ToString("N"));

    public RuntimeHostProgramTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ExistingScratchDryRunHostExitsAtStandardInputEof()
    {
        var configPath = SaveConfig(Path.Combine(_root, "scratch"), dryRun: true);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await Program.RunAsync(
            [
                "--synthetic",
                "--config", configPath,
                "--pipe-name", $"Joydex.Tests.{Guid.NewGuid():N}",
            ],
            new StringReader(string.Empty),
            output,
            error,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        using var ready = JsonDocument.Parse(output.ToString());
        Assert.Equal(2, ready.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(2, ready.RootElement.GetProperty("launchTickets").GetArrayLength());
    }

    [Fact]
    public async Task LiveConfigurationFailsBeforeSettingsStoresAreCreated()
    {
        var scratch = Path.Combine(_root, "live");
        var configPath = SaveConfig(scratch, dryRun: false);
        using var error = new StringWriter();

        var exitCode = await Program.RunAsync(
            [
                "--synthetic",
                "--config", configPath,
                "--pipe-name", $"Joydex.Tests.{Guid.NewGuid():N}",
            ],
            new StringReader(string.Empty),
            TextWriter.Null,
            error,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("safety.dryRun", error.ToString(), StringComparison.Ordinal);
        Assert.Equal([configPath], Directory.GetFiles(scratch));
    }

    [Fact]
    public async Task MissingConfigurationFailsWithoutCreatingItsDirectory()
    {
        var missingDirectory = Path.Combine(_root, "missing");
        var configPath = Path.Combine(missingDirectory, "config.json");

        var exitCode = await Program.RunAsync(
            [
                "--synthetic",
                "--config", configPath,
                "--pipe-name", $"Joydex.Tests.{Guid.NewGuid():N}",
            ],
            new StringReader(string.Empty),
            TextWriter.Null,
            TextWriter.Null,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.False(Directory.Exists(missingDirectory));
    }

    [Fact]
    public void ConfigurationUnderNormalDataRootIsRejectedWithoutUsingTheRealProfile()
    {
        var normalRoot = Path.Combine(_root, "normal-profile", "Joydex");
        var configPath = SaveConfig(Path.Combine(normalRoot, "nested"), dryRun: true);

        var exception = Assert.Throws<InvalidDataException>(() =>
            SyntheticRuntimeLaunchPolicy.Validate(configPath, normalRoot));

        Assert.Contains("outside the normal Joydex data directory", exception.Message);
    }

    [Fact]
    public async Task ProductionHostIgnoresStandardInputEofAndPreservesCustomConfigurationName()
    {
        var configPath = Path.Combine(_root, "production", "chosen-name.json");
        var runner = new BlockingLiveRunner();
        var run = Program.RunAsync(
            ["--config", configPath],
            new StringReader(string.Empty),
            TextWriter.Null,
            TextWriter.Null,
            CancellationToken.None,
            runner);

        var policy = await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(run.IsCompleted);
        Assert.Equal(RuntimeHostLaunchMode.Production, policy.Mode);
        Assert.Equal(Path.GetFullPath(configPath), policy.ConfigurationPath);
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(configPath)), policy.DataRoot);
        Assert.Equal(RuntimeInstanceKind.Production, policy.Endpoint.InstanceKind);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "Joydex.App.exe"), policy.JoydexAppPath);
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "Joydex.RuntimeHost.exe"),
            policy.RuntimeHostPath);

        runner.Complete();
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ProductionPolicyCapturesFreshInstallBeforeSettingsCreateConfiguration()
    {
        var selectedConfiguration = Path.Combine(_root, "fresh", "chosen-name.json");
        var defaultConfiguration = Path.Combine(_root, "default", "config.json");
        var provisioningState = Path.Combine(_root, "state", "codex-keybindings.json");
        var policy = RuntimeHostLiveLaunchPolicy.Create(
            RuntimeHostLaunchMode.Production,
            selectedConfiguration,
            pipeName: null,
            instanceName: null,
            deploymentDirectory: _root,
            defaultConfigurationPath: defaultConfiguration,
            provisioningStatePath: provisioningState);

        Assert.False(policy.ExistingCompanionInstall);

        ConfigStore.Save(selectedConfiguration, CompanionConfig.CreateSafeDefault());

        Assert.False(policy.ExistingCompanionInstall);
        Assert.True(RuntimeHostLiveLaunchPolicy.Create(
            RuntimeHostLaunchMode.Production,
            selectedConfiguration,
            pipeName: null,
            instanceName: null,
            deploymentDirectory: _root,
            defaultConfigurationPath: defaultConfiguration,
            provisioningStatePath: provisioningState).ExistingCompanionInstall);
    }

    [Fact]
    public async Task ProductionWithoutArgumentsUsesTheDefaultConfigurationSelection()
    {
        var previous = Environment.GetEnvironmentVariable("JOYDEX_CONFIG");
        Environment.SetEnvironmentVariable("JOYDEX_CONFIG", null);
        try
        {
            var runner = new CompletedLiveRunner();

            var exitCode = await Program.RunAsync(
                [],
                new StringReader(string.Empty),
                TextWriter.Null,
                TextWriter.Null,
                CancellationToken.None,
                runner);

            Assert.Equal(0, exitCode);
            Assert.Equal(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Joydex",
                    "config.json"),
                runner.Policy!.ConfigurationPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("JOYDEX_CONFIG", previous);
        }
    }

    [Fact]
    public async Task DemoUsesValidatedScratchSelectionAndExplicitEndpoint()
    {
        var configPath = SaveConfig(Path.Combine(_root, "demo"), dryRun: true);
        var pipeName = $"Joydex.Demo.{Guid.NewGuid():N}";
        var runner = new CompletedLiveRunner();

        var exitCode = await Program.RunAsync(
            [
                "--demo",
                "--config", configPath,
                "--pipe-name", pipeName,
                "--instance-name", "demo-one",
            ],
            new StringReader(string.Empty),
            TextWriter.Null,
            TextWriter.Null,
            CancellationToken.None,
            runner);

        Assert.Equal(0, exitCode);
        Assert.Equal(RuntimeHostLaunchMode.Demo, runner.Policy!.Mode);
        Assert.Equal(configPath, runner.Policy.ConfigurationPath);
        Assert.Equal(pipeName, runner.Policy.Endpoint.PipeName);
        Assert.Equal(RuntimeInstanceKind.Synthetic, runner.Policy.Endpoint.InstanceKind);
        Assert.Equal("demo-one", runner.Policy.InstanceName);
    }

    [Fact]
    public async Task DemoRequiresAnExplicitPipeName()
    {
        var configPath = SaveConfig(Path.Combine(_root, "demo-missing-pipe"), dryRun: true);
        var runner = new CompletedLiveRunner();
        using var error = new StringWriter();

        var exitCode = await Program.RunAsync(
            ["--demo", "--config", configPath],
            new StringReader(string.Empty),
            TextWriter.Null,
            error,
            CancellationToken.None,
            runner);

        Assert.Equal(2, exitCode);
        Assert.Null(runner.Policy);
        Assert.Contains("--pipe-name is required", error.ToString(), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string SaveConfig(string directory, bool dryRun)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.json");
        ConfigStore.Save(path, new CompanionConfig
        {
            Safety = new SafetyOptions { DryRun = dryRun },
        });
        return path;
    }

    private sealed class CompletedLiveRunner : IRuntimeHostLiveRunner
    {
        public RuntimeHostLiveLaunchPolicy? Policy { get; private set; }

        public Task RunAsync(
            RuntimeHostLiveLaunchPolicy policy,
            CancellationToken stoppingToken)
        {
            stoppingToken.ThrowIfCancellationRequested();
            Policy = policy;
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingLiveRunner : IRuntimeHostLiveRunner
    {
        private readonly TaskCompletionSource _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<RuntimeHostLiveLaunchPolicy> Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RunAsync(
            RuntimeHostLiveLaunchPolicy policy,
            CancellationToken stoppingToken)
        {
            Started.TrySetResult(policy);
            await _completion.Task.WaitAsync(stoppingToken);
        }

        public void Complete() => _completion.TrySetResult();
    }
}
