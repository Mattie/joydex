using System.Diagnostics;
using Joydex.Core.Config;
using Joydex.Core.TaskAlerts;
using Joydex.Windows.Actions;
using Joydex.Windows.TaskAlerts;

namespace Joydex.Tests;

public sealed class TaskDeepLinkNavigatorTests
{
    [Fact]
    public async Task OpensEscapedSessionThroughShellWhenAllowed()
    {
        ProcessStartInfo? started = null;
        var navigator = new TaskDeepLinkNavigator(
            new SafetyOptions { DryRun = false },
            _ => { },
            new FixedGuard(true),
            info =>
            {
                started = info;
                return null;
            });

        var result = await navigator.NavigateAsync(new TaskAlertNavigationRequest(1, 2, 1, "a/b c"), default);

        Assert.True(result);
        Assert.NotNull(started);
        Assert.Equal("codex://threads/a%2Fb%20c", started.FileName);
        Assert.True(started.UseShellExecute);
    }

    [Fact]
    public async Task BlockedNavigationKeepsAssignmentAcknowledgementPending()
    {
        var started = false;
        var navigator = new TaskDeepLinkNavigator(
            new SafetyOptions { DryRun = false },
            _ => { },
            new FixedGuard(false),
            _ =>
            {
                started = true;
                return null;
            });

        var result = await navigator.NavigateAsync(new TaskAlertNavigationRequest(1, 2, 1, "session"), default);

        Assert.False(result);
        Assert.False(started);
    }

    private sealed class FixedGuard(bool allowed) : IForegroundProcessGuard
    {
        public ForegroundCheck Check(SafetyOptions safety, bool actionMayBringCodexForward) =>
            new(allowed, "test", allowed ? "allowed" : "blocked");
    }
}
