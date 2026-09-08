using Joydex.Core.Config;
using Joydex.Core.Mapping;
using Joydex.Core.Voice;
using Joydex.Windows.Actions;
using Joydex.Windows.Voice;

namespace Joydex.Tests;

public sealed class PinnedVoiceCoordinatorTests
{
    private static readonly VoicePePreferences EnabledPreferences = new(
        Enabled: true,
        DeviceEndpoint: "http://voice-pe.local/",
        PinnedTaskId: "01900000-0000-7000-8000-000000000001",
        PinnedTaskLabel: "Codex Voice Chat");

    [Fact]
    public async Task OpensPinnedTaskBeforeExecutingResolvedVoiceAction()
    {
        var events = new List<string>();
        ActionRequest? action = null;
        var coordinator = new PinnedVoiceCoordinator(
            new SafetyOptions { DryRun = false },
            _ => { },
            new StubNavigator((_, _) =>
            {
                events.Add("navigate");
                return Task.FromResult(true);
            }),
            (request, _) =>
            {
                events.Add("voice");
                action = request;
                return Task.FromResult(ActionExecutionResult.Success("started"));
            },
            new FixedGuard(true));

        var result = await coordinator.StartAsync(EnabledPreferences);

        Assert.Equal(PinnedVoiceStartStatus.Requested, result.Status);
        Assert.Equal(["navigate", "voice"], events);
        Assert.Equal(CodexAction.StartVoiceChat, action?.Action);
        Assert.Equal("voice-pe", action?.DeviceId);
    }

    [Fact]
    public async Task DryRunDescribesCompositeWithoutNavigationOrInput()
    {
        var navigated = false;
        var executed = false;
        var coordinator = new PinnedVoiceCoordinator(
            new SafetyOptions { DryRun = true },
            _ => { },
            new StubNavigator((_, _) =>
            {
                navigated = true;
                return Task.FromResult(true);
            }),
            (_, _) =>
            {
                executed = true;
                return Task.FromResult(ActionExecutionResult.Success("started"));
            });

        var result = await coordinator.StartAsync(EnabledPreferences);

        Assert.Equal(PinnedVoiceStartStatus.Simulated, result.Status);
        Assert.False(navigated);
        Assert.False(executed);
        Assert.Contains("Codex Voice Chat", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualRoutingCanaryDoesNotRequireAConfiguredDeviceEndpoint()
    {
        var coordinator = new PinnedVoiceCoordinator(
            new SafetyOptions { DryRun = true },
            _ => { },
            new StubNavigator((_, _) => Task.FromResult(true)),
            (_, _) => Task.FromResult(ActionExecutionResult.Success("started")));

        var result = await coordinator.StartAsync(EnabledPreferences with { DeviceEndpoint = "" });

        Assert.Equal(PinnedVoiceStartStatus.Simulated, result.Status);
    }

    [Fact]
    public async Task NavigationFailureNeverSendsVoiceShortcut()
    {
        var executed = false;
        var coordinator = new PinnedVoiceCoordinator(
            new SafetyOptions { DryRun = false },
            _ => { },
            new StubNavigator((_, _) => Task.FromResult(false)),
            (_, _) =>
            {
                executed = true;
                return Task.FromResult(ActionExecutionResult.Success("started"));
            });

        var result = await coordinator.StartAsync(EnabledPreferences);

        Assert.Equal(PinnedVoiceStartStatus.NavigationFailed, result.Status);
        Assert.False(executed);
    }

    [Fact]
    public async Task AcceptedSessionBlocksEveryStartPathUntilLogObserverConfirmsStop()
    {
        var executions = 0;
        var coordinator = new PinnedVoiceCoordinator(
            new SafetyOptions { DryRun = false },
            _ => { },
            new StubNavigator((_, _) => Task.FromResult(true)),
            (_, _) =>
            {
                executions++;
                return Task.FromResult(ActionExecutionResult.Success("started"));
            },
            new FixedGuard(true));

        var first = await coordinator.StartAsync(EnabledPreferences);
        var blocked = await coordinator.StartAsync(EnabledPreferences);
        coordinator.ConfirmSessionEnded();
        var afterStop = await coordinator.StartAsync(EnabledPreferences);

        Assert.Equal(PinnedVoiceStartStatus.Requested, first.Status);
        Assert.Equal(PinnedVoiceStartStatus.SessionActive, blocked.Status);
        Assert.Equal(PinnedVoiceStartStatus.Requested, afterStop.Status);
        Assert.Equal(2, executions);
    }

    private sealed class StubNavigator(
        Func<string, CancellationToken, Task<bool>> navigate) : IPinnedVoiceTargetNavigator
    {
        public Task<bool> NavigateAsync(string taskId, CancellationToken cancellationToken) =>
            navigate(taskId, cancellationToken);
    }

    private sealed class FixedGuard(bool allowed) : IForegroundProcessGuard
    {
        public ForegroundCheck Check(SafetyOptions safety, bool actionMayBringCodexForward) =>
            new(allowed, allowed ? "Codex" : "test", allowed ? "allowed" : "blocked");
    }
}
