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
            [
                new TaskAlertAssignment(1, "running", null, TaskAlertState.Running, at),
                new TaskAlertAssignment(2, "approval", null, TaskAlertState.Approval, at),
                new TaskAlertAssignment(3, "completed", null, TaskAlertState.Completed, at),
                new TaskAlertAssignment(5, "fault", null, TaskAlertState.Fault, at),
            ],
            0,
            3);

        var state = LinkToolTelemetryState.From(snapshot);

        Assert.Equal(3, state.JoydexBank);
        Assert.Equal(1, state.JoydexPrimaryB1State);
        Assert.Equal(2, state.JoydexPrimaryB2State);
        Assert.Equal(3, state.JoydexPrimaryB4State);
        Assert.Equal(0, state.JoydexPrimaryB5State);
        Assert.Equal(4, state.JoydexOverflowB1State);
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
        Assert.Equal(0, state.JoydexPrimaryB1State);
        Assert.Equal(0, state.JoydexAlphaState);
    }

    [Fact]
    public void GlobalAlphaKeepsHighestStateOnCommandOnlyM5()
    {
        var snapshot = new TaskAlertSnapshot(
            true,
            [
                new TaskAlertAssignment(
                    6,
                    "overflow",
                    null,
                    TaskAlertState.Approval,
                    DateTimeOffset.UtcNow),
            ],
            0,
            5);

        var state = LinkToolTelemetryState.From(snapshot);

        Assert.Equal(5, state.JoydexBank);
        Assert.Equal(2, state.JoydexOverflowB2State);
        Assert.Equal(2, state.JoydexAlphaState);
        Assert.True(state.HasAlert);
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
        await WaitUntilAsync(() => sender.States.Any(state => state.JoydexPrimaryB1State == 1), TimeSpan.FromSeconds(2));

        service.Apply(EmptySnapshot());
        await WaitUntilAsync(
            () => sender.States.LastOrDefault() is { HasAlert: false },
            TimeSpan.FromSeconds(2));

        Assert.Equal([true, false], dirty.ToArray());
        Assert.Equal(2, sender.States[^1].JoydexBank);
        Assert.Equal(0, sender.States[^1].JoydexPrimaryB1State);
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
        await WaitUntilAsync(() => sender.States.Any(state => state.JoydexPrimaryB2State == 2), TimeSpan.FromSeconds(2));
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
        await WaitUntilAsync(() => sender.States.Any(state => state.JoydexPrimaryB1State == 1), TimeSpan.FromSeconds(2));
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
        Assert.DoesNotContain(sender.States, state => state.JoydexPrimaryB1State == 1);
        Assert.Equal(2, sender.States[^1].JoydexPrimaryB1State);
    }

    [Fact]
    public void WritesBankGatedPrimaryAndOverflowRulesWithM5BaselinesOnly()
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "joydex-linktool.led.json");
        try
        {
            LinkToolProfileWriter.Write(path, "throttle-path", "alpha-path");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var rules = document.RootElement.GetProperty("rules").EnumerateArray().ToArray();

            Assert.Equal(106, rules.Length);
            Assert.Equal(48, rules.Count(rule =>
                rule.GetProperty("argument").GetString()?.StartsWith("JoydexPrimary", StringComparison.Ordinal) == true));
            Assert.Equal(24, rules.Count(rule =>
                rule.GetProperty("argument").GetString()?.StartsWith("JoydexOverflow", StringComparison.Ordinal) == true));
            Assert.DoesNotContain(rules, rule =>
                rule.GetProperty("argument").GetString() is "JoydexPrimaryB3State" or "JoydexPrimaryB6State");
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
            foreach (var button in Enumerable.Range(1, 6))
            {
                Assert.Contains(rules, rule =>
                    rule.GetProperty("comment").GetString() == $"Joydex M1 B{button} baseline"
                    && rule.GetProperty("argument").GetString() == "JoydexBank"
                    && rule.GetProperty("primaryValue").GetString() == "1"
                    && rule.GetProperty("colorOne").GetString() == "0"
                    && rule.GetProperty("priority").GetInt32() == 0);
            }

            Assert.Contains(rules, rule =>
                rule.GetProperty("comment").GetString() == "Joydex primary M2 B1 running"
                && rule.GetProperty("argument").GetString() == "JoydexPrimaryB1State"
                && rule.GetProperty("primaryValue").GetString() == "1"
                && rule.GetProperty("colorOne").GetString() == "5592405"
                && rule.GetProperty("priority").GetInt32() == 100
                && HasCondition(rule, "JoydexBank", "2"));
            Assert.Contains(rules, rule =>
                rule.GetProperty("comment").GetString() == "Joydex overflow M1 B3 running"
                && rule.GetProperty("argument").GetString() == "JoydexOverflowB3State"
                && rule.GetProperty("colorOne").GetString() == "5592405"
                && HasCondition(rule, "JoydexBank", "1"));
            Assert.Contains(rules, rule =>
                rule.GetProperty("comment").GetString() == "Joydex Alpha running"
                && rule.GetProperty("argument").GetString() == "JoydexAlphaState"
                && rule.GetProperty("colorOne").GetString() == "5592405");
            Assert.DoesNotContain(rules, rule =>
                rule.GetProperty("comment").GetString()?.Contains("M5", StringComparison.Ordinal) == true
                && rule.GetProperty("priority").GetInt32() == 100);

            foreach (var slot in TaskAlertSlots.Primary)
            {
                var button = TaskAlertSlots.Button(slot);
                foreach (var bank in new[] { 2, 3, 4 })
                {
                    var alertIndex = Array.FindIndex(rules, rule => rule.GetProperty("comment").GetString()
                        == $"Joydex primary M{bank} B{button} running"
                        && HasCondition(rule, "JoydexBank", bank.ToString()));
                    var baselineIndex = Array.FindIndex(rules, rule =>
                        rule.GetProperty("comment").GetString() == $"Joydex M{bank} B{button} baseline");
                    Assert.True(alertIndex >= 0);
                    Assert.True(baselineIndex >= 0);
                    Assert.True(alertIndex < baselineIndex);
                }
            }

            foreach (var slot in TaskAlertSlots.Overflow)
            {
                var button = TaskAlertSlots.Button(slot);
                Assert.Contains(rules, rule =>
                    rule.GetProperty("comment").GetString() == $"Joydex overflow M1 B{button} running"
                    && HasCondition(rule, "JoydexBank", "1"));
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

    private static bool HasCondition(JsonElement rule, string argument, string value) =>
        rule.GetProperty("conditions").EnumerateArray().Any(condition =>
            condition.GetProperty("argument").GetString() == argument
            && condition.GetProperty("condition").GetString() == "=="
            && condition.GetProperty("value").GetString() == value);

    private static TaskAlertSnapshot EmptySnapshot() => new(true, [], 0, 2);

    private static TaskAlertSnapshot Snapshot(params TaskAlertAssignment[] assignments) =>
        new(true, assignments, 0, 2);

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
