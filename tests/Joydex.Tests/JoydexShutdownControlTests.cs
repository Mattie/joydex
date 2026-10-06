using System.Diagnostics;
using Joydex.App;

namespace Joydex.Tests;

public sealed class JoydexShutdownControlTests
{
    [Fact]
    public async Task ShutdownRequestReachesTheRunningAppsExitCallback()
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The test host path is unavailable.");
        var configurationPath = Path.Combine(Path.GetTempPath(), "joydex-control-test", "config.json");
        var exitRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var control = new JoydexShutdownControl(
            executablePath,
            configurationPath,
            new SynchronizationContext(),
            () => exitRequested.TrySetResult(),
            () => true);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var probed = await JoydexShutdownControl.ProbeTargetAsync(executablePath);
            using (probed.App)
            {
                Assert.Equal(Process.GetCurrentProcess().Id, probed.App.Id);
                Assert.Equal(Path.GetFullPath(configurationPath), probed.ConfigurationPath);
                Assert.False(exitRequested.Task.IsCompleted);
            }
        }

        var target = await JoydexShutdownControl.RequestTargetAsync(executablePath);
        using (target.App)
        {
            Assert.Equal(Process.GetCurrentProcess().Id, target.App.Id);
            Assert.Equal(Path.GetFullPath(configurationPath), target.ConfigurationPath);
        }

        await exitRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ProbeTracksCurrentReadinessWhileShutdownRemainsAvailable()
    {
        var executablePath = Environment.ProcessPath!;
        var ready = 1;
        var exitRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var control = new JoydexShutdownControl(executablePath,
            Path.Combine(Path.GetTempPath(), "joydex-control-test", "config.json"),
            new SynchronizationContext(), () => exitRequested.TrySetResult(),
            () => Volatile.Read(ref ready) == 1);
        var initial = await JoydexShutdownControl.ProbeTargetAsync(executablePath);
        initial.App.Dispose();

        Volatile.Write(ref ready, 0);
        await Assert.ThrowsAsync<EndOfStreamException>(() => JoydexShutdownControl.ProbeTargetAsync(executablePath));
        Assert.False(exitRequested.Task.IsCompleted);

        Volatile.Write(ref ready, 1);
        var reconnected = await JoydexShutdownControl.ProbeTargetAsync(executablePath);
        reconnected.App.Dispose();
        Volatile.Write(ref ready, 0);
        var shutdown = await JoydexShutdownControl.RequestTargetAsync(executablePath);
        shutdown.App.Dispose();
        await exitRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
