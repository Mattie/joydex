using System.Collections.Concurrent;
using System.Text.Json;
using Joydex.Core.TaskAlerts;
using Joydex.Windows.TaskAlerts;

namespace Joydex.Tests;

public sealed class LinkToolLedServiceTests
{
    [Fact]
    public void BuildsOneAtomicStateWithHighestPriorityOnAlpha()
    {
        var at = DateTimeOffset.UtcNow;
        var snapshot = new TaskAlertSnapshot(
            true,
            [1, 2, 4, 5],
            [
                new TaskAlertAssignment(1, "running", null, TaskAlertState.Running, at),
                new TaskAlertAssignment(2, "approval", null, TaskAlertState.Approval, at),
                new TaskAlertAssignment(4, "completed", null, TaskAlertState.Completed, at),
                new TaskAlertAssignment(5, "fault", null, TaskAlertState.Fault, at),
            ],
            0,
            3);

        var state = LinkToolTelemetryState.From(snapshot);

        Assert.Equal(3, state.JoydexBank);
        Assert.Equal(1, state.JoydexB1State);
        Assert.Equal(2, state.JoydexB2State);
        Assert.Equal(3, state.JoydexB4State);
        Assert.Equal(4, state.JoydexB5State);
        Assert.Equal(4, state.JoydexAlphaState);
        Assert.True(state.HasAlert);
    }

    [Fact]
    public void SuppressedStateKeepsBankAndClearsEveryAlert()
    {
        var snapshot = Snapshot(new TaskAlertAssignment(
            1,
            "running",
            null,
            TaskAlertState.Running,
            DateTimeOffset.UtcNow));

        var state = LinkToolTelemetryState.From(snapshot, suppressAlerts: true);

        Assert.Equal(2, state.JoydexBank);
        Assert.False(state.HasAlert);
        Assert.Equal(0, state.JoydexB1State);
        Assert.Equal(0, state.JoydexAlphaState);
    }

    [Fact]
    public async Task EmptyStartupDoesNotPushAConfiguredBankToHardware()
    {
        var sender = new RecordingSender();
        await using var service = new LinkToolLedService(
            sender,
            new FixedConflictDetector(false),
            _ => { },
            EmptySnapshot());

        service.Apply(EmptySnapshot());
        await Task.Delay(100);

        Assert.Empty(sender.States);
    }

    [Fact]
    public async Task ClearingAssignmentPublishesBaselineWithoutADeviceRestore()
    {
        var sender = new RecordingSender();
        var conflicts = new FixedConflictDetector(false);
        await using var service = new LinkToolLedService(sender, conflicts, _ => { }, EmptySnapshot());
        var dirty = new ConcurrentQueue<bool>();
        service.ProfileDirtyChanged += (_, value) => dirty.Enqueue(value);

        service.Apply(Snapshot(new TaskAlertAssignment(
            1,
            "running",
            null,
            TaskAlertState.Running,
            DateTimeOffset.UtcNow)));
        await WaitUntilAsync(() => sender.States.Any(state => state.JoydexB1State == 1), TimeSpan.FromSeconds(2));

        service.Apply(EmptySnapshot());
        await WaitUntilAsync(
            () => sender.States.LastOrDefault() is { HasAlert: false },
            TimeSpan.FromSeconds(2));

        Assert.Equal([true, false], dirty.ToArray());
        Assert.Equal(2, sender.States[^1].JoydexBank);
        Assert.Equal(0, sender.States[^1].JoydexB1State);
    }

    [Fact]
    public async Task WaitsForVpcConflictThenPublishesLatestState()
    {
        var sender = new RecordingSender();
        var conflicts = new FixedConflictDetector(true);
        await using var service = new LinkToolLedService(sender, conflicts, _ => { }, EmptySnapshot());

        service.Apply(Snapshot(new TaskAlertAssignment(
            2,
            "approval",
            null,
            TaskAlertState.Approval,
            DateTimeOffset.UtcNow)));
        await Task.Delay(100);
        Assert.Empty(sender.States);

        conflicts.Value = false;
        service.RestoreAndReplay(replay: true);
        await WaitUntilAsync(() => sender.States.Any(state => state.JoydexB2State == 2), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ReportsPendingUntilLinkToolListenerAppears()
    {
        var sender = new RecordingSender { IsListening = false };
        await using var service = new LinkToolLedService(
            sender,
            new FixedConflictDetector(false),
            _ => { },
            EmptySnapshot());
        var statuses = new ConcurrentQueue<string>();
        service.StatusChanged += (_, status) => statuses.Enqueue(status);

        service.Apply(Snapshot(new TaskAlertAssignment(
            1,
            "running",
            null,
            TaskAlertState.Running,
            DateTimeOffset.UtcNow)));
        await WaitUntilAsync(
            () => statuses.Any(status => status.Contains("inactive", StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(2));
        Assert.Empty(sender.States);

        sender.IsListening = true;
        service.RestoreAndReplay(replay: true);
        await WaitUntilAsync(() => sender.States.Any(state => state.JoydexB1State == 1), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CoalescesPendingChangesToLatestCompleteSnapshot()
    {
        var sender = new RecordingSender { IsListening = false };
        await using var service = new LinkToolLedService(
            sender,
            new FixedConflictDetector(false),
            _ => { },
            EmptySnapshot());
        var at = DateTimeOffset.UtcNow;

        service.Apply(Snapshot(new TaskAlertAssignment(1, "one", null, TaskAlertState.Running, at)));
        service.Apply(Snapshot(new TaskAlertAssignment(1, "one", null, TaskAlertState.Approval, at)));
        sender.IsListening = true;
        service.RestoreAndReplay(replay: true);

        await WaitUntilAsync(() => sender.States.Count > 0, TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(sender.States, state => state.JoydexB1State == 1);
        Assert.Equal(2, sender.States[^1].JoydexB1State);
    }

    [Fact]
    public void WritesLinkToolProfileWithAlertRulesOnlyForSelectedLedsAndBaselinesForEveryLed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "joydex-linktool.led.json");
        try
        {
            LinkToolProfileWriter.Write(path, "throttle-path", "alpha-path");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var rules = document.RootElement.GetProperty("rules").EnumerateArray().ToArray();

            Assert.Equal(50, rules.Length);
            Assert.DoesNotContain(rules, rule =>
                rule.GetProperty("argument").GetString() is "JoydexB3State" or "JoydexB6State");
            Assert.Contains(rules, rule =>
                rule.GetProperty("comment").GetString() == "Joydex M2 B3 baseline"
                && rule.GetProperty("colorOne").GetString() == "16711680");
            Assert.Contains(rules, rule =>
                rule.GetProperty("comment").GetString() == "Joydex M2 B6 baseline"
                && rule.GetProperty("colorOne").GetString() == "16711680");
            Assert.Contains(rules, rule =>
                rule.GetProperty("comment").GetString() == "Joydex M2 B1 baseline"
                && rule.GetProperty("argument").GetString() == "JoydexBank"
                && rule.GetProperty("primaryValue").GetString() == "2"
                && rule.GetProperty("colorOne").GetString() == "16711680"
                && rule.GetProperty("priority").GetInt32() == 0);
            Assert.Contains(rules, rule =>
                rule.GetProperty("comment").GetString() == "Joydex B1 running"
                && rule.GetProperty("argument").GetString() == "JoydexB1State"
                && rule.GetProperty("primaryValue").GetString() == "1"
                && rule.GetProperty("colorOne").GetString() == "16777215"
                && rule.GetProperty("priority").GetInt32() == 100);

            foreach (var channel in TaskAlertChannels.Selectable)
            {
                var alertIndex = Array.FindIndex(rules, rule =>
                    rule.GetProperty("comment").GetString() == $"Joydex B{channel} running");
                var baselineIndex = Array.FindIndex(rules, rule =>
                    rule.GetProperty("comment").GetString() == $"Joydex M2 B{channel} baseline");
                Assert.True(alertIndex >= 0);
                Assert.True(baselineIndex >= 0);
                Assert.True(alertIndex < baselineIndex);
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static TaskAlertSnapshot EmptySnapshot() => new(true, [1, 2, 4, 5], [], 0, 2);

    private static TaskAlertSnapshot Snapshot(params TaskAlertAssignment[] assignments) =>
        new(true, [1, 2, 4, 5], assignments, 0, 2);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(condition());
    }

    private sealed class RecordingSender : ILinkToolTelemetrySender
    {
        private readonly object _sync = new();
        private readonly List<LinkToolTelemetryState> _states = [];

        public bool IsListening { get; set; } = true;

        public IReadOnlyList<LinkToolTelemetryState> States
        {
            get
            {
                lock (_sync)
                {
                    return [.. _states];
                }
            }
        }

        public Task<bool> SendAsync(LinkToolTelemetryState state, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _states.Add(state);
            }

            return Task.FromResult(true);
        }
    }

    private sealed class FixedConflictDetector(bool value) : IVpcConflictDetector
    {
        public bool Value { get; set; } = value;

        public bool HasConflict() => Value;
    }
}
