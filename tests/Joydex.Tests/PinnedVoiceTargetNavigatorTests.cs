using System.Diagnostics;
using Joydex.Core.Config;
using Joydex.Windows.Actions;
using Joydex.Windows.Voice;

namespace Joydex.Tests;

public sealed class PinnedVoiceTargetNavigatorTests
{
    [Fact]
    public async Task OpensOnlyTheNormalizedPinnedTaskThroughTheShell()
    {
        ProcessStartInfo? started = null;
        var taskId = Guid.NewGuid();
        var navigator = new PinnedVoiceTargetNavigator(
            new SafetyOptions { DryRun = false },
            _ => { },
            new FixedGuard(true),
            info =>
            {
                started = info;
                return null;
            });

        var opened = await navigator.NavigateAsync(taskId.ToString("D").ToUpperInvariant(), default);

        Assert.True(opened);
        Assert.Equal($"codex://threads/{taskId:D}", started?.FileName);
        Assert.True(started?.UseShellExecute);
    }

    [Fact]
    public async Task DryRunNeverLaunchesTheTask()
    {
        var launched = false;
        var navigator = new PinnedVoiceTargetNavigator(
            new SafetyOptions { DryRun = true },
            _ => { },
            new FixedGuard(true),
            _ =>
            {
                launched = true;
                return null;
            });

        var opened = await navigator.NavigateAsync(Guid.NewGuid().ToString("D"), default);

        Assert.False(opened);
        Assert.False(launched);
    }

    private sealed class FixedGuard(bool allowed) : IForegroundProcessGuard
    {
        public ForegroundCheck Check(SafetyOptions safety, bool actionMayBringCodexForward) =>
            new(allowed, "test", allowed ? "allowed" : "blocked");
    }
}
