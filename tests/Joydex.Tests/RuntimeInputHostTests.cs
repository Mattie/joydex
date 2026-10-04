using Joydex.Core.Input;
using Joydex.Core.Config;
using Joydex.Core.Mapping;
using Joydex.Core.Runtime;

namespace Joydex.Tests;

public sealed class RuntimeInputHostTests
{
    private static readonly TimeSpan AsyncTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task HeldAtActivationIsIgnoredAndCapturedPressIsSuppressedThroughRelease()
    {
        using var host = new RuntimeInputHost();
        var session = host.ConnectSource(Source("stick-a"), () => { });
        await Route(host, session, Snapshot(), []);
        var updates = new List<InputCaptureChangedEventArgs>();
        host.CaptureChanged += (_, update) => updates.Add(update);

        var started = host.BeginCapture(new InputCaptureRequest("ui-1", "stick-a", "binding"));
        var heldBeforeActive = await Route(
            host,
            session,
            Snapshot(pressed: [1]),
            [Press(1)]);
        var heldRelease = await Route(
            host,
            session,
            Snapshot(),
            [Release(1)]);
        var capturedPress = await Route(
            host,
            session,
            Snapshot(pressed: [1]),
            [Press(1)]);
        var capturedRelease = await Route(
            host,
            session,
            Snapshot(),
            [Release(1)]);
        var ordinaryPress = await Route(
            host,
            session,
            Snapshot(pressed: [2]),
            [Press(2)]);

        Assert.True(started.Accepted);
        Assert.Equal(InputCaptureStatus.Pending, started.Lease!.Status);
        Assert.Empty(heldBeforeActive);
        Assert.Empty(heldRelease);
        Assert.Empty(capturedPress);
        Assert.Empty(capturedRelease);
        Assert.Equal(Press(2), Assert.Single(ordinaryPress));
        Assert.Collection(
            updates,
            update => Assert.Equal(InputCaptureStatus.Pending, update.Lease.Status),
            update => Assert.Equal(InputCaptureStatus.Active, update.Lease.Status),
            update =>
            {
                Assert.Equal(InputCaptureStatus.Completed, update.Lease.Status);
                Assert.Equal(Press(1), update.CapturedInput);
            });
        Assert.True(updates.Select(update => update.Lease.Revision).SequenceEqual([1, 2, 3]));
    }

    [Fact]
    public async Task CaptureOnOneSourceLeavesAnotherSourcesOrdinaryInputUntouched()
    {
        using var host = new RuntimeInputHost();
        var sourceA = host.ConnectSource(Source("stick-a"), () => { });
        var sourceB = host.ConnectSource(Source("stick-b"), () => { });
        await Route(host, sourceA, Snapshot(), []);
        await Route(host, sourceB, Snapshot(), []);
        host.BeginCapture(new InputCaptureRequest("ui-1", "stick-a", "binding"));
        await Route(host, sourceA, Snapshot(), []);

        var sourceBPress = await Route(
            host,
            sourceB,
            Snapshot(pressed: [3]),
            [Press(3)]);
        var sourceAPress = await Route(
            host,
            sourceA,
            Snapshot(pressed: [3]),
            [Press(3)]);

        Assert.Equal(Press(3), Assert.Single(sourceBPress));
        Assert.Empty(sourceAPress);
    }

    [Fact]
    public async Task IgnoredHoldsRemainQuarantinedWhenAnotherControlCompletesCapture()
    {
        using var host = new RuntimeInputHost();
        var session = host.ConnectSource(Source("stick-a"), () => { });
        await Route(host, session, Snapshot(), []);
        host.BeginCapture(new InputCaptureRequest("ui-1", "stick-a", "binding"));
        await Route(host, session, Snapshot(pressed: [1]), [Press(1)]);

        var completion = await Route(
            host,
            session,
            Snapshot(pressed: [1, 2]),
            [Press(2)]);
        var releases = await Route(
            host,
            session,
            Snapshot(),
            [Release(1), Release(2)]);

        Assert.Empty(completion);
        Assert.Empty(releases);
    }

    [Fact]
    public async Task CaptureActivationWaitsForThePriorDispatchAndSuccessfulCleanup()
    {
        using var host = new RuntimeInputHost();
        var prepareCalls = 0;
        var session = host.ConnectSource(Source("stick-a"), () => prepareCalls++);
        await Route(host, session, Snapshot(), []);
        var dispatchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.CaptureChanged += (_, update) =>
        {
            if (update.Lease.Status == InputCaptureStatus.Active)
            {
                active.TrySetResult();
            }
        };

        var priorRoute = host.RouteAsync(
            session,
            Snapshot(pressed: [1]),
            new[] { Press(1) },
            async routed =>
            {
                dispatchEntered.TrySetResult();
                await releaseDispatch.Task;
                return routed.Events;
            });
        await dispatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var started = host.BeginCapture(new InputCaptureRequest("ui-1", "stick-a", "binding"));
        var activationRoute = Route(host, session, Snapshot(), [Release(1)]);

        Assert.Equal(InputCaptureStatus.Pending, started.Lease!.Status);
        Assert.Equal(0, prepareCalls);
        Assert.False(activationRoute.IsCompleted);
        Assert.False(active.Task.IsCompleted);

        releaseDispatch.TrySetResult();
        await priorRoute.WaitAsync(TimeSpan.FromSeconds(1));
        await active.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await activationRoute.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, prepareCalls);
    }

    [Fact]
    public async Task FailedCaptureCleanupNeverPublishesActive()
    {
        using var host = new RuntimeInputHost();
        var session = host.ConnectSource(
            Source("stick-a"),
            () => throw new InvalidOperationException("release failed"));
        await Route(host, session, Snapshot(), []);
        var statuses = new List<InputCaptureStatus>();
        host.CaptureChanged += (_, update) => statuses.Add(update.Lease.Status);
        host.BeginCapture(new InputCaptureRequest("ui-1", "stick-a", "binding"));

        var routed = await Route(host, session, Snapshot(pressed: [1]), [Press(1)]);

        Assert.Equal([InputCaptureStatus.Pending, InputCaptureStatus.Failed], statuses);
        Assert.DoesNotContain(InputCaptureStatus.Active, statuses);
        Assert.Equal(Press(1), Assert.Single(routed));
    }

    [Fact]
    public async Task DisconnectAndGenerationChangeEndTheExactLease()
    {
        using var host = new RuntimeInputHost();
        var first = host.ConnectSource(Source("stick-a"), () => { });
        await Route(host, first, Snapshot(), []);
        var updates = new List<InputCaptureChangedEventArgs>();
        host.CaptureChanged += (_, update) => updates.Add(update);
        var started = host.BeginCapture(new InputCaptureRequest(
            "ui-1",
            "stick-a",
            "binding",
            first.Generation));
        await Route(host, first, Snapshot(), []);

        host.DisconnectSource(first);
        var second = host.ConnectSource(Source("stick-a"), () => { });
        var staleStart = host.BeginCapture(new InputCaptureRequest(
            "ui-1",
            "stick-a",
            "binding",
            first.Generation));

        Assert.Equal(InputCaptureStatus.SourceDisconnected, updates[^1].Lease.Status);
        Assert.Equal(started.Lease!.CaptureId, updates[^1].Lease.CaptureId);
        Assert.True(second.Generation > first.Generation);
        Assert.False(staleStart.Accepted);
        Assert.Contains("generation", staleStart.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClientDisconnectCancelsCaptureAndSuppressesHeldRelease()
    {
        using var host = new RuntimeInputHost();
        var session = host.ConnectSource(Source("stick-a"), () => { });
        await Route(host, session, Snapshot(), []);
        InputCaptureStatus? terminal = null;
        host.CaptureChanged += (_, update) =>
        {
            if (update.Lease.Status == InputCaptureStatus.ClientDisconnected)
            {
                terminal = update.Lease.Status;
            }
        };
        host.BeginCapture(new InputCaptureRequest("ui-1", "stick-a", "binding"));
        await Route(host, session, Snapshot(pressed: [1]), [Press(1)]);

        host.DisconnectClient("ui-1");
        var released = await Route(host, session, Snapshot(), [Release(1)]);

        Assert.Equal(InputCaptureStatus.ClientDisconnected, terminal);
        Assert.Empty(released);
    }

    [Fact]
    public void ExpiredCaptureCannotBeRenewedBetweenTimerTicks()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-11T00:00:00Z"));
        using var host = new RuntimeInputHost(clock);
        host.PublishAvailableSources([Source("stick-a")]);
        var updates = new List<InputCaptureChangedEventArgs>();
        host.CaptureChanged += (_, update) => updates.Add(update);
        var started = host.BeginCapture(new InputCaptureRequest(
            "ui-1",
            "stick-a",
            "binding",
            Timeout: TimeSpan.FromSeconds(1)));
        clock.Advance(TimeSpan.FromSeconds(2));

        var renewed = host.RenewCapture(started.Lease!.CaptureId, "ui-1");

        Assert.False(renewed);
        Assert.Equal(InputCaptureStatus.TimedOut, updates[^1].Lease.Status);
        Assert.True(updates[^1].Lease.Revision > updates[0].Lease.Revision);
    }

    [Fact]
    public async Task ThrowingSubscribersCannotStopObservationOrCapture()
    {
        using var host = new RuntimeInputHost();
        host.InputObserved += (_, _) => throw new ObjectDisposedException("closed form");
        host.CaptureChanged += (_, _) => throw new ObjectDisposedException("closed form");
        var session = host.ConnectSource(Source("stick-a"), () => { });
        host.BeginCapture(new InputCaptureRequest("ui-1", "stick-a", "binding"));

        var routed = await Route(host, session, Snapshot(), []);

        Assert.Empty(routed);
    }

    [Fact]
    public async Task RevisionsLetClientsRejectAnActiveUpdateDeliveredAfterTimeout()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-11T00:00:00Z"));
        using var host = new RuntimeInputHost(clock);
        var session = host.ConnectSource(Source("stick-a"), () => { });
        await Route(host, session, Snapshot(), []);
        var activeEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseActive = new ManualResetEventSlim();
        var delivered = new List<InputCaptureLease>();
        var deliveredGate = new object();
        host.CaptureChanged += (_, update) =>
        {
            if (update.Lease.Status == InputCaptureStatus.Active)
            {
                activeEntered.TrySetResult();
                if (!releaseActive.Wait(AsyncTimeout))
                {
                    throw new TimeoutException("The test did not release the active capture update.");
                }
            }
            lock (deliveredGate)
            {
                delivered.Add(update.Lease);
            }
        };
        var started = host.BeginCapture(new InputCaptureRequest(
            "ui-1",
            "stick-a",
            "binding",
            Timeout: TimeSpan.FromSeconds(1)));

        var activation = Task.Factory.StartNew(
                () => Route(host, session, Snapshot(), []),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();
        try
        {
            await activeEntered.Task.WaitAsync(AsyncTimeout);
            clock.Advance(TimeSpan.FromSeconds(2));
            host.ExpireCaptures(clock.GetUtcNow());
        }
        finally
        {
            releaseActive.Set();
        }

        await activation.WaitAsync(AsyncTimeout);

        InputCaptureLease[] updates;
        lock (deliveredGate)
        {
            updates = delivered.ToArray();
        }
        var terminal = Assert.Single(updates, lease => lease.Status == InputCaptureStatus.TimedOut);
        var lateActive = Assert.Single(updates, lease => lease.Status == InputCaptureStatus.Active);
        Assert.Equal(started.Lease!.CaptureId, terminal.CaptureId);
        Assert.True(lateActive.Revision < terminal.Revision);
        Assert.True(Array.IndexOf(updates, terminal) < Array.IndexOf(updates, lateActive));
    }

    [Fact]
    public async Task CapturedBankSelectorCannotChooseABankUntilReleasedAndFreshlyPressed()
    {
        using var host = new RuntimeInputHost();
        var engine = BankedEngine();
        var session = host.ConnectSource(Source("stick-a"), engine.ResetDispatchState);
        await RouteEngine(host, engine, session, Snapshot(), []);
        host.BeginCapture(new InputCaptureRequest("ui-1", "stick-a", "bank selector"));
        await RouteEngine(host, engine, session, Snapshot(), []);

        var capturedSelector = await RouteEngine(
            host,
            engine,
            session,
            Snapshot(pressed: [1]),
            [Press(1)]);
        var buttonWhileCapturedSelectorHeld = await RouteEngine(
            host,
            engine,
            session,
            Snapshot(pressed: [1, 2]),
            [Press(2)]);
        await RouteEngine(
            host,
            engine,
            session,
            Snapshot(),
            [Release(1), Release(2)]);
        await RouteEngine(
            host,
            engine,
            session,
            Snapshot(pressed: [1]),
            [Press(1)]);
        var buttonAfterFreshSelector = await RouteEngine(
            host,
            engine,
            session,
            Snapshot(pressed: [1, 2]),
            [Press(2)]);

        Assert.Empty(capturedSelector.ActionRequests);
        Assert.Empty(buttonWhileCapturedSelectorHeld.ActionRequests);
        Assert.Equal("work-action", Assert.Single(buttonAfterFreshSelector.ActionRequests).BindingName);
    }

    private static InputSourceDescriptor Source(string id) => new(id, id, $"hardware-{id}");

    private static JoystickSnapshot Snapshot(params int[] pressed)
    {
        var buttons = new bool[8];
        foreach (var displayIndex in pressed)
        {
            buttons[displayIndex - 1] = true;
        }
        return new JoystickSnapshot(DateTimeOffset.UtcNow, buttons, [-1], [0]);
    }

    private static JoystickEvent Press(int displayIndex) => new(
        JoystickEventKind.ButtonPressed,
        displayIndex - 1,
        1);

    private static JoystickEvent Release(int displayIndex) => new(
        JoystickEventKind.ButtonReleased,
        displayIndex - 1,
        0);

    private static Task<IReadOnlyList<JoystickEvent>> Route(
        RuntimeInputHost host,
        InputSourceSession session,
        JoystickSnapshot snapshot,
        IReadOnlyList<JoystickEvent> events) =>
        host.RouteAsync(
            session,
            snapshot,
            events,
            routed => Task.FromResult(routed.Events));

    private static Task<EngineResult> RouteEngine(
        RuntimeInputHost host,
        CompanionEngine engine,
        InputSourceSession session,
        JoystickSnapshot snapshot,
        IReadOnlyList<JoystickEvent> bufferedEvents)
    {
        var observed = engine.Observe(snapshot, bufferedEvents);
        return host.RouteAsync(
            session,
            snapshot,
            observed,
            routed => Task.FromResult(engine.ProcessRouted(routed.Snapshot, routed.Events)));
    }

    private static CompanionEngine BankedEngine() => new(new CompanionConfig
    {
        Devices =
        [
            new DeviceProfile
            {
                Id = "stick-a",
                DisplayName = "Stick A",
                BankSelectors = new Dictionary<string, int> { ["work"] = 1 },
            },
        ],
        Bindings =
        [
            new ButtonBinding
            {
                Name = "work-action",
                DeviceId = "stick-a",
                Bank = "work",
                Button = 2,
                Action = "new-task",
            },
        ],
    }, deviceId: "stick-a");

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
