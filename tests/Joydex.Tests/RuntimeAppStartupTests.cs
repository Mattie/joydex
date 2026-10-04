using Joydex.App;
using Joydex.Contracts;
using Joydex.Ipc;

namespace Joydex.Tests;

public sealed class RuntimeAppStartupTests
{
    [Fact]
    public void SettingsModeRequiresTheExactStandaloneLauncherFlag()
    {
        Assert.Equal(RuntimeAppStartupMode.Tray, RuntimeAppStartup.SelectMode([]));
        Assert.Equal(
            RuntimeAppStartupMode.Settings,
            RuntimeAppStartup.SelectMode(["--SETTINGS"]));

        Assert.Throws<InvalidDataException>(() =>
            RuntimeAppStartup.SelectMode(["--settings", "extra"]));
        Assert.Throws<InvalidDataException>(() =>
            RuntimeAppStartup.SelectMode(["--settings", "--settings"]));
        Assert.Throws<InvalidDataException>(() =>
            RuntimeAppStartup.SelectMode(["--config", "chosen.json", "--settings"]));
    }

    [Fact]
    public void NormalTrayRoutePreservesTheSelectedConfiguration()
    {
        var host = new RecordingStartupHost();
        var args = new[] { "--config", Path.Combine("profiles", "chosen.json") };

        RuntimeAppStartup.Run(args, RuntimeAppStartupMode.Tray, demoPolicy: null, host);

        var run = Assert.Single(host.TrayRuns);
        Assert.Equal(Path.GetFullPath(args[1]), run.ConfigurationPath);
        Assert.False(run.DemoMode);
        Assert.Null(run.DemoPipeName);
        Assert.Equal(0, host.OpenSettingsChannelCount);
        Assert.Empty(host.SettingsRuns);
    }

    [Fact]
    public void DemoTrayRouteUsesTheAlreadyValidatedIsolatedSelection()
    {
        var host = new RecordingStartupHost();
        var configurationPath = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "joydex-demo", "chosen.json"));
        var policy = new DemoLaunchPolicy(
            configurationPath,
            Path.GetDirectoryName(configurationPath)!);
        var args = new[] { "--demo", "--config", configurationPath };

        RuntimeAppStartup.Run(args, RuntimeAppStartupMode.Tray, policy, host);

        var run = Assert.Single(host.TrayRuns);
        Assert.Equal(configurationPath, run.ConfigurationPath);
        Assert.True(run.DemoMode);
        Assert.Null(run.DemoPipeName);
        Assert.Equal(0, host.OpenSettingsChannelCount);
        Assert.Empty(host.SettingsRuns);
    }

    [Fact]
    public async Task SettingsRouteConsumesOnlyBootstrapAndTransfersTheSameReadableStream()
    {
        var bootstrap = CreateBootstrap();
        var channel = await CreateChannelAsync(
            bootstrap,
            RuntimeSettingsChannel.CreateActivate());
        var host = new RecordingStartupHost { SettingsChannel = channel };

        RuntimeAppStartup.Run(
            [RuntimeAppStartup.SettingsArgument],
            RuntimeAppStartupMode.Settings,
            demoPolicy: null,
            host);

        var run = Assert.Single(host.SettingsRuns);
        Assert.Equal(bootstrap, run.Bootstrap);
        Assert.Same(channel, run.Channel);
        Assert.Equal(1, host.OpenSettingsChannelCount);
        Assert.Empty(host.TrayRuns);
        Assert.Equal(0, channel.DisposeCount);

        var remaining = await RuntimeSettingsChannel.ReadAsync(channel);
        Assert.NotNull(remaining);
        Assert.Equal(RuntimeSettingsChannelMessageKind.Activate, remaining.Kind);
        channel.Dispose();
    }

    [Fact]
    public void SettingsRouteRejectsEofAndDisposesTheUntransferredStream()
    {
        var channel = new TrackingMemoryStream();
        var host = new RecordingStartupHost { SettingsChannel = channel };

        var exception = Assert.Throws<EndOfStreamException>(() =>
            RuntimeAppStartup.Run(
                [RuntimeAppStartup.SettingsArgument],
                RuntimeAppStartupMode.Settings,
                demoPolicy: null,
                host));

        Assert.Contains("before Bootstrap", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, channel.DisposeCount);
        Assert.Empty(host.SettingsRuns);
        Assert.Empty(host.TrayRuns);
    }

    [Fact]
    public async Task SettingsRouteRejectsANonBootstrapFirstMessageAndDisposesTheStream()
    {
        var channel = await CreateChannelAsync(RuntimeSettingsChannel.CreateActivate());
        var host = new RecordingStartupHost { SettingsChannel = channel };

        var exception = Assert.Throws<InvalidDataException>(() =>
            RuntimeAppStartup.Run(
                [RuntimeAppStartup.SettingsArgument],
                RuntimeAppStartupMode.Settings,
                demoPolicy: null,
                host));

        Assert.Contains("Bootstrap", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, channel.DisposeCount);
        Assert.Empty(host.SettingsRuns);
        Assert.Empty(host.TrayRuns);
    }

    private static RuntimeSettingsChannelMessage CreateBootstrap()
    {
        var dataRoot = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "joydex-startup", Guid.NewGuid().ToString("N")));
        var configurationPath = Path.Combine(dataRoot, "chosen.json");
        var endpoint = RuntimeIpcEndpoint.CreateSynthetic(
            dataRoot,
            configurationPath,
            "joydex-startup-" + Guid.NewGuid().ToString("N"));
        return RuntimeSettingsChannel.CreateBootstrap(
            endpoint,
            dataRoot,
            configurationPath,
            "settings-ticket");
    }

    private static async Task<TrackingMemoryStream> CreateChannelAsync(
        params RuntimeSettingsChannelMessage[] messages)
    {
        var channel = new TrackingMemoryStream();
        foreach (var message in messages)
        {
            await RuntimeSettingsChannel.WriteAsync(channel, message);
        }
        channel.Position = 0;
        return channel;
    }

    private sealed class RecordingStartupHost : IRuntimeAppStartupHost
    {
        public TrackingMemoryStream? SettingsChannel { get; init; }
        public int OpenSettingsChannelCount { get; private set; }
        public List<SettingsRun> SettingsRuns { get; } = [];
        public List<TrayRun> TrayRuns { get; } = [];

        public Stream OpenSettingsChannel()
        {
            OpenSettingsChannelCount++;
            return SettingsChannel
                ?? throw new InvalidOperationException("No fake Settings channel was supplied.");
        }

        public void RunSettings(RuntimeSettingsChannelMessage bootstrap, Stream channel) =>
            SettingsRuns.Add(new SettingsRun(bootstrap, channel));

        public void RunTray(string configurationPath, bool demoMode, string? demoPipeName) =>
            TrayRuns.Add(new TrayRun(configurationPath, demoMode, demoPipeName));
    }

    private sealed record SettingsRun(
        RuntimeSettingsChannelMessage Bootstrap,
        Stream Channel);

    private sealed record TrayRun(
        string ConfigurationPath,
        bool DemoMode,
        string? DemoPipeName);

    private sealed class TrackingMemoryStream : MemoryStream
    {
        public int DisposeCount { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;
            }
            base.Dispose(disposing);
        }
    }
}
