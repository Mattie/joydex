using Joydex.Core.TaskAlerts;
using Joydex.RuntimeHost.Plugins;
using Joydex.RuntimeHost.Production;
using Joydex.Windows.TaskAlerts;

namespace Joydex.RuntimeHost.Tests;

public sealed class VirpilPluginLifecycleTests
{
    [Fact]
    public async Task ResumeReappliesCurrentStateAndRestoresTheLedProfile()
    {
        var calls = new List<string>();
        var shift = new Shift(calls);
        shift.Join.TrySetResult();
        await using var plugin = new VirpilProductionPlugin(new Led(calls), shift,
            new Device(calls), new Guardian(calls), () => { }, _ => { }, 1);
        var snapshot = new TaskAlertSnapshot(true, [], 0);

        plugin.SetPaused(true, snapshot);
        Assert.Equal(["pause"], calls);
        plugin.SetPaused(false, snapshot);
        Assert.Equal(["pause", "resume", "apply", "replay"], calls);
    }

    [Fact]
    public async Task DisposalJoinsShiftThenRestoresLedBeforeGuardianCleanExit()
    {
        var calls = new List<string>();
        var shift = new Shift(calls);
        var led = new Led(calls);
        var guardian = new Guardian(calls);
        var plugin = new VirpilProductionPlugin(led, shift, new Device(calls), guardian,
            () => calls.Add("unsubscribe"), _ => { }, 7);
        var first = plugin.DisposeAsync().AsTask();
        var second = plugin.DisposeAsync().AsTask();
        Assert.Same(first, second);
        Assert.Equal(BundledPluginLifecycleState.Stopping, plugin.HealthSnapshot.State);
        Assert.DoesNotContain("pause", calls);
        shift.Join.TrySetResult();
        await first;
        Assert.Equal(new[] { "unsubscribe", "device", "shift", "pause", "led", "clean", "guardian" }, calls);
        Assert.Equal(BundledPluginLifecycleState.Stopped, plugin.HealthSnapshot.State);
        Assert.Equal(7, plugin.HealthSnapshot.Generation);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UnconfirmedLedCleanupPreservesGuardianAndCachesBlocked(bool pending, bool failure)
    {
        var calls = new List<string>();
        var shift = new Shift(calls);
        shift.Join.TrySetResult();
        var plugin = new VirpilProductionPlugin(new Led(calls) { RestorePending = pending, FailDispose = failure },
            shift, new Device(calls), new Guardian(calls), () => { }, _ => { }, 3);
        var first = plugin.DisposeAsync().AsTask();
        await Assert.ThrowsAsync<AggregateException>(() => first);
        var second = plugin.DisposeAsync().AsTask();
        Assert.Same(first, second);
        await Assert.ThrowsAsync<AggregateException>(() => second);
        Assert.DoesNotContain("clean", calls);
        Assert.Equal(1, calls.Count(call => call == "led"));
        Assert.Equal(BundledPluginLifecycleState.Blocked, plugin.HealthSnapshot.State);
    }

    [Fact]
    public async Task GuardianExitTimeoutBlocksReplacementAfterLedHasJoined()
    {
        var calls = new List<string>();
        var shift = new Shift(calls);
        shift.Join.TrySetResult();
        var plugin = new VirpilProductionPlugin(new Led(calls), shift, new Device(calls),
            new Guardian(calls) { FailClean = true }, () => { }, _ => { }, 4);
        await Assert.ThrowsAsync<AggregateException>(() => plugin.DisposeAsync().AsTask());
        Assert.True(calls.IndexOf("led") < calls.IndexOf("clean"));
        Assert.Equal(BundledPluginLifecycleState.Blocked, plugin.HealthSnapshot.State);
    }

    private sealed class Shift(List<string> calls) : IAsyncDisposable
    {
        public TaskCompletionSource Join { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask DisposeAsync() { await Join.Task; calls.Add("shift"); }
    }
    private sealed class Device(List<string> calls) : IDisposable
    {
        public void Dispose() => calls.Add("device");
    }
    private sealed class Guardian(List<string> calls) : IVirpilGuardian
    {
        public bool FailClean { get; init; }
        public bool Start() => true;
        public void SetRestoreRequired(bool required) { }
        public void UpdateRecovery(TaskAlertSnapshot snapshot) { }
        public void SignalCleanExit() { calls.Add("clean"); if (FailClean) throw new TimeoutException(); }
        public void Dispose() => calls.Add("guardian");
    }
    private sealed class Led(List<string> calls) : ITaskAlertLedOutput
    {
        public bool RestorePending { get; init; }
        public bool FailDispose { get; init; }
        public event EventHandler<string>? StatusChanged { add { } remove { } }
        public event EventHandler<bool>? ProfileDirtyChanged { add { } remove { } }
        public void Apply(TaskAlertSnapshot snapshot) => calls.Add("apply");
        public void RestoreAndReplay(bool replay) => calls.Add(replay ? "replay" : "restore");
        public void SetPaused(bool paused) => calls.Add(paused ? "pause" : "resume");
        public Task<bool> WaitForIdleAsync(TimeSpan timeout) => Task.FromResult(false);
        public ValueTask DisposeAsync() { calls.Add("led"); if (FailDispose) throw new IOException(); return ValueTask.CompletedTask; }
    }
}
