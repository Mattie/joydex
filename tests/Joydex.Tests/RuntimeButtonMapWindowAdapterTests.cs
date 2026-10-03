using System.Collections.Concurrent;
using Joydex.App;
using Joydex.Contracts;
using Joydex.Core.Config;
using Joydex.Core.TaskAlerts;

namespace Joydex.Tests;

public sealed class RuntimeButtonMapWindowAdapterTests
{
    [Fact]
    public void SnapshotCreatesVisibleViewWithNormalizedConfigStatePathAndTaskAlertsOnUiContext()
    {
        var ui = new QueuedSynchronizationContext();
        var factory = new RecordingViewFactory(ui);
        var statePathCalls = new List<string>();
        using var adapter = new RuntimeButtonMapWindowAdapter(
            ui,
            deviceId =>
            {
                Assert.True(ui.IsExecuting);
                statePathCalls.Add(deviceId);
                return $"state/{deviceId}.json";
            },
            factory);
        var config = Config(
            [Device("alpha", "Alpha", "alpha-warbrd")],
            [Binding("Launch", null, 7)]);
        var alerts = Alerts(new TaskAlertAssignment(
            2,
            "session-1",
            "turn-1",
            TaskAlertState.Approval,
            DateTimeOffset.Parse("2026-09-11T12:00:00Z")));

        adapter.ApplySnapshot(
            config,
            alerts,
            [new RuntimeControllerStatus("alpha", "Alpha", "Connected", true)],
            [new RuntimeButtonMapVisibility("alpha", true)]);

        Assert.Empty(factory.Calls);
        ui.Drain();

        var creation = Assert.Single(factory.Calls);
        var view = creation.View;
        Assert.Equal("alpha", creation.DeviceId);
        Assert.Equal("state/alpha.json", creation.StatePath);
        Assert.Equal(["alpha"], statePathCalls);
        Assert.Equal("alpha", Assert.Single(creation.Config.Bindings).DeviceId);
        Assert.Equal("alpha-warbrd", Assert.Single(creation.Config.Devices).ButtonMapTemplate);
        Assert.Same(creation.Config, Assert.Single(view.ConfigUpdates));
        var taskAlerts = Assert.Single(view.TaskAlertUpdates);
        Assert.Equal(alerts.Assignments, taskAlerts.Assignments);
        Assert.Same(alerts.LedOutput, taskAlerts.LedOptions);
        Assert.Equal(1, view.ShowCount);
        Assert.True(factory.AllCallsWereOnUiContext);
        Assert.True(view.AllCallsWereOnUiContext);
    }

    [Fact]
    public void ManualVisibilitySurvivesEquivalentReconnectState()
    {
        var ui = new QueuedSynchronizationContext();
        var factory = new RecordingViewFactory(ui);
        using var adapter = CreateAdapter(ui, factory);
        var config = Config([Device("alpha", "Alpha", "alpha-warbrd")]);

        adapter.ApplySnapshot(config, null, null, null);
        ui.Drain();
        adapter.ShowManual("ALPHA");
        ui.Drain();
        var view = Assert.Single(factory.Calls).View;

        adapter.ApplySnapshot(Config([Device("alpha", "Alpha", "alpha-warbrd")]), null, [], []);
        ui.Drain();

        Assert.Equal(1, view.ShowCount);
        Assert.Equal(0, view.HideCount);
        Assert.Equal(2, view.ConfigUpdates.Count);
    }

    [Fact]
    public void UserCloseSuppressesRepeatedMappedTrueUntilAFalseEdge()
    {
        var ui = new QueuedSynchronizationContext();
        var factory = new RecordingViewFactory(ui);
        using var adapter = CreateAdapter(ui, factory);
        var config = Config([Device("alpha", "Alpha", "alpha-warbrd")]);

        adapter.ApplySnapshot(
            config,
            null,
            null,
            [new RuntimeButtonMapVisibility("alpha", true)]);
        ui.Drain();
        var view = Assert.Single(factory.Calls).View;

        view.SimulateUserClose();
        adapter.ApplyVisibility(new RuntimeButtonMapVisibility("alpha", true));
        adapter.ApplySnapshot(
            config,
            null,
            null,
            [new RuntimeButtonMapVisibility("alpha", true)]);
        ui.Drain();
        Assert.Equal(1, view.ShowCount);

        adapter.ApplyVisibility(new RuntimeButtonMapVisibility("alpha", false));
        adapter.ApplyVisibility(new RuntimeButtonMapVisibility("alpha", true));
        ui.Drain();

        Assert.Equal(2, view.ShowCount);
        Assert.Equal(0, view.HideCount);
    }

    [Fact]
    public void EscapeHideCanBeReopenedManuallyAndThenRequiresAMappedFalseEdge()
    {
        var ui = new QueuedSynchronizationContext();
        var factory = new RecordingViewFactory(ui);
        using var adapter = CreateAdapter(ui, factory);
        var config = Config([Device("alpha", "Alpha", "alpha-warbrd")]);
        adapter.ApplySnapshot(
            config,
            null,
            null,
            [new RuntimeButtonMapVisibility("alpha", true)]);
        ui.Drain();
        var view = Assert.Single(factory.Calls).View;

        view.SimulateEscapeHide();
        adapter.ShowManual("alpha");
        ui.Drain();
        Assert.Equal(2, view.ShowCount);

        view.SimulateEscapeHide();
        adapter.ApplyVisibility(new RuntimeButtonMapVisibility("alpha", true));
        adapter.ApplySnapshot(
            config,
            null,
            null,
            [new RuntimeButtonMapVisibility("alpha", true)]);
        ui.Drain();
        Assert.Equal(2, view.ShowCount);

        adapter.ApplyVisibility(new RuntimeButtonMapVisibility("alpha", false));
        adapter.ApplyVisibility(new RuntimeButtonMapVisibility("alpha", true));
        ui.Drain();
        Assert.Equal(3, view.ShowCount);
    }

    [Fact]
    public void SnapshotRemovesDeletedMapAndRecreatesChangedPresentation()
    {
        var ui = new QueuedSynchronizationContext();
        var factory = new RecordingViewFactory(ui);
        using var adapter = CreateAdapter(ui, factory);
        adapter.ApplySnapshot(
            Config(
                [
                    Device("alpha", "Alpha", "alpha-warbrd"),
                    Device("throttle", "Throttle", "cm3"),
                ]),
            null,
            null,
            [
                new RuntimeButtonMapVisibility("alpha", true),
                new RuntimeButtonMapVisibility("throttle", true),
            ]);
        ui.Drain();
        var alpha = factory.ForDevice("alpha").Single();
        var firstThrottle = factory.ForDevice("throttle").Single();

        adapter.ApplySnapshot(
            Config(
                [Device("throttle", "Throttle Mk II", "alpha-warbrd")],
                [Binding("Changed binding", "throttle", 12)]),
            null,
            null,
            [new RuntimeButtonMapVisibility("throttle", true)]);
        ui.Drain();

        Assert.Equal(1, alpha.HideCount);
        Assert.Equal(1, alpha.DisposeCount);
        Assert.Equal(1, firstThrottle.HideCount);
        Assert.Equal(1, firstThrottle.DisposeCount);
        var replacement = factory.ForDevice("throttle").Last();
        Assert.NotSame(firstThrottle, replacement);
        Assert.Equal(1, replacement.ShowCount);
        Assert.Equal("Changed binding", Assert.Single(Assert.Single(replacement.ConfigUpdates).Bindings).Name);
        Assert.Equal(2, factory.ForDevice("throttle").Count);
    }

    [Fact]
    public void ControllerEligibilityDisposesAndCanRecoverFromStatusOrCompleteSnapshot()
    {
        var ui = new QueuedSynchronizationContext();
        var factory = new RecordingViewFactory(ui);
        using var adapter = CreateAdapter(ui, factory);
        var config = Config([Device("alpha", "Alpha", "alpha-warbrd")]);
        adapter.ApplySnapshot(
            config,
            null,
            null,
            [new RuntimeButtonMapVisibility("alpha", true)]);
        ui.Drain();
        var first = factory.ForDevice("alpha").Single();

        adapter.ApplyController(new RuntimeControllerStatus("alpha", "Alpha", "Disconnected", false));
        adapter.ShowManual("alpha");
        ui.Drain();
        Assert.Equal(1, first.HideCount);
        Assert.Equal(1, first.DisposeCount);
        Assert.Single(factory.ForDevice("alpha"));

        adapter.ApplyController(new RuntimeControllerStatus("alpha", "Alpha", "Connected", true));
        ui.Drain();
        var second = factory.ForDevice("alpha").Last();
        Assert.Equal(1, second.ShowCount);

        adapter.ApplyController(new RuntimeControllerStatus("alpha", "Alpha", "Disconnected", false));
        ui.Drain();
        adapter.ApplySnapshot(
            config,
            null,
            [],
            [new RuntimeButtonMapVisibility("alpha", true)]);
        ui.Drain();

        var third = factory.ForDevice("alpha").Last();
        Assert.Equal(3, factory.ForDevice("alpha").Count);
        Assert.Equal(1, third.ShowCount);
    }

    [Fact]
    public void DisposalSkipsQueuedUpdatesAndContinuesAfterViewCleanupFailures()
    {
        var ui = new QueuedSynchronizationContext();
        var factory = new RecordingViewFactory(ui);
        var adapter = CreateAdapter(ui, factory);
        adapter.ApplySnapshot(
            Config(
                [
                    Device("alpha", "Alpha", "alpha-warbrd"),
                    Device("throttle", "Throttle", "cm3"),
                ]),
            null,
            null,
            [
                new RuntimeButtonMapVisibility("alpha", true),
                new RuntimeButtonMapVisibility("throttle", true),
            ]);
        ui.Drain();
        var alpha = factory.ForDevice("alpha").Single();
        var throttle = factory.ForDevice("throttle").Single();
        alpha.ThrowWhenHiding = true;
        alpha.ThrowWhenDisposing = true;
        var originalAlertUpdates = alpha.TaskAlertUpdates.Count;

        adapter.ApplyTaskAlerts(Alerts());
        var failure = Assert.Throws<AggregateException>(() => adapter.Dispose());

        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.Equal(1, alpha.HideCount);
        Assert.Equal(1, alpha.DisposeCount);
        Assert.Equal(1, throttle.HideCount);
        Assert.Equal(1, throttle.DisposeCount);
        Assert.True(alpha.AllCallsWereOnUiContext);
        Assert.True(throttle.AllCallsWereOnUiContext);

        ui.Drain();
        Assert.Equal(originalAlertUpdates, alpha.TaskAlertUpdates.Count);
        adapter.ShowManual("alpha");
        Assert.Equal(0, ui.PendingCount);
    }

    private static RuntimeButtonMapWindowAdapter CreateAdapter(
        QueuedSynchronizationContext ui,
        RecordingViewFactory factory) => new(
            ui,
            deviceId => $"state/{deviceId}.json",
            factory);

    private static CompanionConfig Config(
        IEnumerable<DeviceProfile> devices,
        IEnumerable<ButtonBinding>? bindings = null) => new()
        {
            Devices = [.. devices],
            Bindings = bindings is null ? [] : [.. bindings],
        };

    private static DeviceProfile Device(string id, string name, string? template) => new()
    {
        Id = id,
        DisplayName = name,
        ButtonMapTemplate = template,
    };

    private static ButtonBinding Binding(string name, string? deviceId, int button) => new()
    {
        Name = name,
        DeviceId = deviceId,
        Bank = CompanionConfig.AlwaysBank,
        Button = button,
        Action = "left-click",
    };

    private static RuntimeTaskAlertSnapshot Alerts(params TaskAlertAssignment[] assignments) => new(
        true,
        assignments,
        0,
        2,
        true,
        [],
        [],
        TaskAlertLedOptions.CreateDefault(),
        new RuntimeTaskAlertHookStatus(RuntimeTaskAlertHookState.Installed));

    private sealed class RecordingViewFactory(QueuedSynchronizationContext ui)
        : IRuntimeButtonMapViewFactory
    {
        public List<Creation> Calls { get; } = [];
        public bool AllCallsWereOnUiContext { get; private set; } = true;

        public IRuntimeButtonMapView Create(CompanionConfig config, string deviceId, string statePath)
        {
            AllCallsWereOnUiContext &= ui.IsExecuting;
            var view = new RecordingView(ui);
            Calls.Add(new Creation(config, deviceId, statePath, view));
            return view;
        }

        public List<RecordingView> ForDevice(string deviceId) => Calls
            .Where(call => string.Equals(call.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            .Select(call => call.View)
            .ToList();
    }

    private sealed record Creation(
        CompanionConfig Config,
        string DeviceId,
        string StatePath,
        RecordingView View);

    private sealed class RecordingView(QueuedSynchronizationContext ui) : IRuntimeButtonMapView
    {
        public event EventHandler? HiddenByUser;
        public List<CompanionConfig> ConfigUpdates { get; } = [];
        public List<TaskAlertUpdate> TaskAlertUpdates { get; } = [];
        public int ShowCount { get; private set; }
        public int HideCount { get; private set; }
        public int DisposeCount { get; private set; }
        public bool AllCallsWereOnUiContext { get; private set; } = true;
        public bool ThrowWhenHiding { get; set; }
        public bool ThrowWhenDisposing { get; set; }

        public void UpdateConfig(CompanionConfig config)
        {
            RecordContext();
            ConfigUpdates.Add(config);
        }

        public void UpdateTaskAlerts(
            IReadOnlyList<TaskAlertAssignment> assignments,
            TaskAlertLedOptions? ledOptions)
        {
            RecordContext();
            TaskAlertUpdates.Add(new TaskAlertUpdate([.. assignments], ledOptions));
        }

        public void ShowReference()
        {
            RecordContext();
            ShowCount++;
        }

        public void HideReference()
        {
            RecordContext();
            HideCount++;
            if (ThrowWhenHiding)
            {
                throw new InvalidOperationException("hide failed");
            }
        }

        public void Dispose()
        {
            RecordContext();
            DisposeCount++;
            if (ThrowWhenDisposing)
            {
                throw new InvalidOperationException("dispose failed");
            }
        }

        public void SimulateUserClose()
        {
            ui.Post(_ => HiddenByUser?.Invoke(this, EventArgs.Empty), null);
            ui.Drain();
        }

        public void SimulateEscapeHide()
        {
            ui.Post(_ => HiddenByUser?.Invoke(this, EventArgs.Empty), null);
            ui.Drain();
        }

        private void RecordContext() => AllCallsWereOnUiContext &= ui.IsExecuting;
    }

    private sealed record TaskAlertUpdate(
        TaskAlertAssignment[] Assignments,
        TaskAlertLedOptions? LedOptions);

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _pending = new();
        public bool IsExecuting { get; private set; }
        public int PendingCount => _pending.Count;

        public override void Post(SendOrPostCallback callback, object? state) =>
            _pending.Enqueue((callback, state));

        public override void Send(SendOrPostCallback callback, object? state) => Execute(callback, state);

        public void Drain()
        {
            while (_pending.TryDequeue(out var work))
            {
                Execute(work.Callback, work.State);
            }
        }

        private void Execute(SendOrPostCallback callback, object? state)
        {
            var wasExecuting = IsExecuting;
            IsExecuting = true;
            try
            {
                callback(state);
            }
            finally
            {
                IsExecuting = wasExecuting;
            }
        }
    }
}
