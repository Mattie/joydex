using System.Collections.Concurrent;
using Joydex.Core.Config;
using Joydex.Core.Input;
using Joydex.Core.Mapping;
using Joydex.Core.Runtime;
using Joydex.Windows.Actions;
using Joydex.Windows.Input;
using Joydex.Windows.Runtime;
using Joydex.Windows.TaskAlerts;

namespace Joydex.Tests;

public sealed class DeviceAcquisitionTests
{
    [Fact]
    public async Task PollingContinuesAndBufferedEdgesStayOrderedWhileActionWaits()
    {
        using var host = new RuntimeInputHost();
        var source = new ScriptedSource();
        var resolver = new GatedResolver();
        var lifecycle = new Lifecycle();
        var observed = new ConcurrentQueue<JoystickEvent>();
        host.InputObserved += (_, update) => { foreach (var edge in update.Observation.Events) observed.Enqueue(edge); };
        await using var worker = Worker(source, resolver, lifecycle, host);
        using var unblock = new UnblockOnDispose(resolver.Release);
        worker.Start();
        await Until(() => source.Reads > 1);
        source.Enqueue(true, Press);
        await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var before = source.Reads;
        source.Enqueue(false, Release, Press, Release);
        await Until(() => source.Reads >= before + 5);
        Assert.Single(observed);
        resolver.Release.TrySetResult();
        await Until(() => observed.Count == 4);
        Assert.Equal(new[] { Press, Release, Press, Release }, observed.ToArray());
        await worker.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureDiscardsOldQueuedTapAndReleasesEvenIfClientCancels(bool cancel)
    {
        using var host = new RuntimeInputHost();
        var source = new ScriptedSource();
        var resolver = new GatedResolver();
        var lifecycle = new Lifecycle();
        var captures = new ConcurrentQueue<InputCaptureStatus>();
        host.CaptureChanged += (_, update) => captures.Enqueue(update.Lease.Status);
        await using var worker = Worker(source, resolver, lifecycle, host);
        using var unblock = new UnblockOnDispose(resolver.Release);
        worker.Start();
        await Until(() => source.Reads > 1);
        source.Enqueue(true, Press);
        await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var before = source.Reads;
        source.Enqueue(false, Release, Press, Release);
        await Until(() => source.Reads >= before + 4);
        var capture = host.BeginCapture(new InputCaptureRequest("test-ui", "cm3", "binding"));
        Assert.True(capture.Accepted);
        Assert.DoesNotContain(InputCaptureStatus.Active, captures);
        if (cancel) host.DisconnectClient("test-ui");
        resolver.Release.TrySetResult();
        await Until(() => lifecycle.Releases > 0);
        if (!cancel) await Until(() => captures.Contains(InputCaptureStatus.Active));
        Assert.DoesNotContain(InputCaptureStatus.Completed, captures);
        Assert.Equal(1, resolver.Calls);
        if (!cancel)
        {
            source.Enqueue(true, Press);
            await Until(() => captures.Contains(InputCaptureStatus.Completed));
            Assert.Equal(1, resolver.Calls);
        }
        await worker.StopAsync();
    }

    [Fact]
    public async Task DisconnectJoinsUncancellableActionBeforeSourceCleanupAndDropsQueuedWork()
    {
        using var host = new RuntimeInputHost();
        var source = new ScriptedSource();
        var resolver = new GatedResolver();
        var lifecycle = new Lifecycle();
        await using var worker = Worker(source, resolver, lifecycle, host);
        using var unblock = new UnblockOnDispose(resolver.Release);
        worker.Start();
        await Until(() => source.Reads > 1);
        source.Enqueue(true, Press);
        await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        source.Enqueue(false, Release, Press, Release);
        source.DisconnectReads = true;
        await source.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stop = worker.StopAsync();
        Assert.False(stop.IsCompleted);
        Assert.Equal(0, lifecycle.Releases);
        resolver.Release.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, lifecycle.Releases);
        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public async Task AcquisitionOverflowFailsWithoutDroppingBufferedEdges()
    {
        var source = new ScriptedSource { AlwaysEdge = true };
        source.TryConnect(new DeviceSelector(), out _);
        var pump = new DirectInputAcquisition(source, new PollingOptions { ConnectWarmupMs = 0, PollIntervalMs = 0 });
        var failure = await Assert.ThrowsAsync<IOException>(() => pump.RunAsync(CancellationToken.None));
        Assert.Contains("queue filled", failure.Message);
        Assert.Equal(DirectInputAcquisition.Capacity, pump.Frames.Count);
        var baseline = await pump.Frames.ReadAsync();
        Assert.Empty(baseline.Events);
        for (var index = 1; index < DirectInputAcquisition.Capacity; index++)
        {
            var frame = await pump.Frames.ReadAsync();
            Assert.Equal(index + 1, frame.Sequence);
            Assert.Equal(Press, Assert.Single(frame.Events));
        }
    }

    [Fact]
    public async Task CancelledResolverCannotReachForegroundOrInjection()
    {
        var resolver = new GatedResolver();
        var guard = new RecordingGuard();
        var executor = new CodexActionExecutor(new SafetyOptions { DryRun = true }, _ => { }, resolver,
            new OpenWorkingDirectoryOptions(), foregroundGuard: guard);
        using var cancellation = new CancellationTokenSource();
        var action = executor.ExecuteAsync(new ActionRequest("test", "always", 1, "press", CodexAction.NewTask,
            DateTimeOffset.UtcNow), cancellation.Token);
        await resolver.Entered.Task;
        cancellation.Cancel();
        resolver.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action);
        Assert.Equal(0, guard.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GuardianDeletesRecoveryOnlyAfterConfirmedExit(bool exited)
    {
        var signalled = false;
        var deleted = false;
        void Run() => GuardianController.CompleteCleanExit(() => signalled = true, () => exited, () => deleted = true);
        if (exited) Run(); else Assert.Throws<TimeoutException>(Run);
        Assert.True(signalled);
        Assert.Equal(exited, deleted);
    }

    [Fact]
    public async Task PermanentKeyCleanupFailureBlocksStopDisposalAndReconnect()
    {
        using var host = new RuntimeInputHost();
        var source = new ScriptedSource();
        var resolver = new GatedResolver();
        var lifecycle = new Lifecycle { FailRelease = true };
        var worker = Worker(source, resolver, lifecycle, host);
        worker.Start();
        await Until(() => source.Reads > 1);
        source.DisconnectReads = true;
        await Until(() => lifecycle.Releases > 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.StopAsync());
        var first = worker.DisposeAsync().AsTask();
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        var second = worker.DisposeAsync().AsTask();
        Assert.Same(first, second);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        Assert.Equal(1, source.Connects);
        Assert.Equal(0, source.Disposals);
        Assert.True(worker.CleanupPending);
    }

    [Fact]
    public async Task CaptureFenceRejectsWaitingFrameEvenAfterCaptureCancellation()
    {
        using var host = new RuntimeInputHost();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = host.ConnectSource(new InputSourceDescriptor("source", "Synthetic"), () => { }, () => 2);
        var snapshot = new JoystickSnapshot(DateTimeOffset.UtcNow, [false], [-1], [0]);
        var first = host.RouteAsync(source, snapshot, [], async _ => { entered.TrySetResult(); await release.Task; return 0; }, 1);
        await entered.Task;
        var consumed = false;
        var queued = host.RouteAsync(source, snapshot, [Press], _ => { consumed = true; return Task.FromResult(0); }, 2);
        host.BeginCapture(new InputCaptureRequest("ui", "source", "binding"));
        host.DisconnectClient("ui");
        release.TrySetResult();
        await first;
        await Assert.ThrowsAsync<InputSourceFrameSupersededException>(() => queued);
        Assert.False(consumed);
    }

    [Fact]
    public async Task CaptureFenceRechecksAfterActivationCallbacksBeforeRoutingEdges()
    {
        using var host = new RuntimeInputHost();
        long watermark = 1;
        var source = host.ConnectSource(new InputSourceDescriptor("source", "Synthetic"), () => { }, () => watermark);
        var restarted = false;
        host.CaptureChanged += (_, update) =>
        {
            if (update.Lease.Status != InputCaptureStatus.Active || restarted) return;
            restarted = true;
            host.DisconnectClient("ui");
            watermark = 2;
            host.BeginCapture(new InputCaptureRequest("ui2", "source", "binding"));
        };
        host.BeginCapture(new InputCaptureRequest("ui", "source", "binding"));
        var consumed = false;
        var snapshot = new JoystickSnapshot(DateTimeOffset.UtcNow, [false], [-1], [0]);
        await Assert.ThrowsAsync<InputSourceFrameSupersededException>(() =>
            host.RouteAsync(source, snapshot, [Press], _ => { consumed = true; return Task.FromResult(0); }, 2));
        Assert.True(restarted);
        Assert.False(consumed);
    }

    private static CompanionWorker Worker(ScriptedSource source, GatedResolver resolver, Lifecycle lifecycle, RuntimeInputHost host) =>
        new(new CompanionConfig
        {
            Polling = new PollingOptions { ConnectWarmupMs = 1, PollIntervalMs = 1, ReconnectIntervalMs = 1 },
            Safety = new SafetyOptions { DryRun = true },
            Bindings = [new ButtonBinding { Name = "action", Bank = "always", Button = 1, Action = "new-task" }],
        }, source, new CodexActionExecutor(new SafetyOptions { DryRun = true }, _ => { }, resolver,
            new OpenWorkingDirectoryOptions(), foregroundGuard: new RecordingGuard()), _ => { }, lifecycle, inputHost: host);

    private static readonly JoystickEvent Press = new(JoystickEventKind.ButtonPressed, 0, 1);
    private static readonly JoystickEvent Release = new(JoystickEventKind.ButtonReleased, 0, 0);
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }

    private sealed class ScriptedSource : IJoystickSource
    {
        private readonly ConcurrentQueue<(bool Held, JoystickEvent[] Events)> _input = new();
        private bool _held;
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public int Connects { get; private set; }
        public int Disposals { get; private set; }
        public bool DisconnectReads { get; set; }
        public bool AlwaysEdge { get; init; }
        public TaskCompletionSource Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DirectInputDeviceInfo? ConnectedDevice { get; private set; }
        public IReadOnlyList<JoystickEvent> LatestBufferedButtonEvents { get; private set; } = [];
        public void Enqueue(bool held, params JoystickEvent[] events) => _input.Enqueue((held, events));
        public bool TryConnect(DeviceSelector selector, out string message)
        {
            Connects++;
            ConnectedDevice = new("synthetic", "synthetic", Guid.NewGuid(), Guid.NewGuid());
            message = "synthetic";
            return true;
        }
        public bool TryRead(out JoystickSnapshot? snapshot, out string? error)
        {
            if (DisconnectReads)
            {
                ConnectedDevice = null;
                snapshot = null;
                error = "synthetic disconnect";
                Disconnected.TrySetResult();
                return false;
            }
            LatestBufferedButtonEvents = AlwaysEdge ? [Press] : [];
            if (_input.TryDequeue(out var frame)) { _held = frame.Held; LatestBufferedButtonEvents = frame.Events; }
            snapshot = new(DateTimeOffset.UtcNow, [_held], [-1], [0]);
            error = null;
            Interlocked.Increment(ref _reads);
            return true;
        }
        public void Disconnect() => ConnectedDevice = null;
        public void Dispose() => Disposals++;
    }
    private sealed class GatedResolver : ICodexKeybindingResolver
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<CodexBindingResolution> ResolveAsync(CodexAction action, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            await Release.Task;
            return new(action, "synthetic", null, CodexBindingSource.None, CodexBindingSnapshotState.Unavailable, "synthetic");
        }
    }
    private sealed class RecordingGuard : IForegroundProcessGuard
    {
        public int Calls { get; private set; }
        public ForegroundCheck Check(SafetyOptions safety, bool actionMayBringCodexForward) { Calls++; return new(true, "synthetic", "allowed"); }
    }
    private sealed class UnblockOnDispose(TaskCompletionSource release) : IDisposable
    {
        public void Dispose() => release.TrySetResult();
    }
    private sealed class Lifecycle : IInjectedKeyStateLifecycle
    {
        private int _releases;
        public int Releases => Volatile.Read(ref _releases);
        public void ClearInjectedKeyState() { }
        public bool FailRelease { get; init; }
        public void ReleaseHeldKeys(InputSourceSession source)
        {
            Interlocked.Increment(ref _releases);
            if (FailRelease) throw new IOException("synthetic cleanup failure");
        }
        public void ReleaseAllHeldKeys() { }
    }
}
