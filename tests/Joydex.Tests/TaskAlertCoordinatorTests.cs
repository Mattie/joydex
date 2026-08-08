using System.Text.Json;
using Joydex.Core.TaskAlerts;
using Joydex.Windows.TaskAlerts;

namespace Joydex.Tests;

public sealed class TaskAlertCoordinatorTests
{
    private static readonly string AttentionKey = new('B', 64);

    [Fact]
    public async Task PublishesChangedSnapshotWhenOverflowEventIsDropped()
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-coordinator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var coordinator = new TaskAlertCoordinator(Path.Combine(directory, "task-alerts.json"));
            foreach (var index in Enumerable.Range(1, 10))
            {
                Assert.True(coordinator.TryPublish(Event(index)));
            }

            await WaitUntilAsync(
                () => coordinator.GetSnapshot().Assignments.Count == 10,
                TimeSpan.FromSeconds(3));

            long observedDroppedEventCount = 0;
            coordinator.Changed += (_, snapshot) =>
                Interlocked.Exchange(ref observedDroppedEventCount, snapshot.DroppedEventCount);

            Assert.True(coordinator.TryPublish(Event(11)));
            await WaitUntilAsync(
                () => Interlocked.Read(ref observedDroppedEventCount) == 1,
                TimeSpan.FromSeconds(3));

            var dropped = Assert.Single(coordinator.GetSnapshot().RecentEvents!, trace =>
                trace.SessionId == "session-11");
            Assert.Equal(TaskAlertEventResult.Dropped, dropped.Result);
            Assert.Null(dropped.Slot);
            Assert.Null(dropped.State);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RecordsReceivedEventAndReducerOutcome()
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-coordinator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var coordinator = new TaskAlertCoordinator(Path.Combine(directory, "task-alerts.json"));
            Assert.True(coordinator.TryPublish(Event(1)));
            await WaitUntilAsync(
                () => coordinator.GetSnapshot().RecentEvents?.Count == 1,
                TimeSpan.FromSeconds(3));

            var trace = Assert.Single(coordinator.GetSnapshot().RecentEvents!);
            Assert.Equal(CodexLifecycleEvent.UserPromptSubmit, trace.Event);
            Assert.Equal("session-1", trace.SessionId);
            Assert.Equal("turn-1", trace.TurnId);
            Assert.Equal(1, trace.Slot);
            Assert.Equal(TaskAlertState.Running, trace.State);
            Assert.Equal(TaskAlertEventResult.Assigned, trace.Result);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RetainsOnlyTheMostRecentHundredEvents()
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-coordinator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var coordinator = new TaskAlertCoordinator(Path.Combine(directory, "task-alerts.json"));
            foreach (var index in Enumerable.Range(1, 105))
            {
                Assert.True(coordinator.TryPublish(Event(index)));
            }

            await WaitUntilAsync(
                () => coordinator.GetSnapshot().DroppedEventCount == 95,
                TimeSpan.FromSeconds(3));

            var traces = coordinator.GetSnapshot().RecentEvents!;
            Assert.Equal(100, traces.Count);
            Assert.Equal("session-6", traces[0].SessionId);
            Assert.Equal("session-105", traces[^1].SessionId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RestoresAssignmentCorrelationAndContentFreeJson()
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-coordinator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var preferencesPath = Path.Combine(directory, "task-alerts.json");
            var statePath = Path.Combine(directory, "task-alert-state.json");
            TaskAlertPreferencesStore.Save(preferencesPath, TaskAlertPreferences.Default);
            var at = DateTimeOffset.UtcNow.AddMinutes(-1);
            var pool = new TaskAlertPool();
            pool.Apply(new TaskAlertEvent(
                CodexLifecycleEvent.PermissionRequest,
                "restored-session",
                "restored-turn",
                at,
                AttentionKey));
            TaskAlertStateStore.Save(statePath, pool.CaptureState());

            await using var coordinator = new TaskAlertCoordinator(preferencesPath, statePath);

            var restored = Assert.Single(coordinator.GetSnapshot().Assignments);
            Assert.Equal("restored-session", restored.SessionId);
            Assert.Equal("restored-turn", restored.TurnId);
            Assert.Equal(TaskAlertState.Approval, restored.State);
            Assert.True(coordinator.TryPublish(new TaskAlertEvent(
                CodexLifecycleEvent.ToolCompleted,
                "restored-session",
                "restored-turn",
                DateTimeOffset.UtcNow,
                AttentionKey)));
            await WaitUntilAsync(
                () => coordinator.GetSnapshot().Assignments.Single().State == TaskAlertState.Running,
                TimeSpan.FromSeconds(3));

            using var document = JsonDocument.Parse(File.ReadAllText(statePath));
            Assert.Equal(
                ["assignments", "schemaVersion"],
                document.RootElement.EnumerateObject().Select(property => property.Name).Order());
            Assert.Equal(
                [
                    "completeAfter",
                    "correlatedAttention",
                    "sessionId",
                    "slot",
                    "state",
                    "turnId",
                    "uncorrelatedAttentionCount",
                    "updatedAt",
                    "workspaceKey",
                ],
                document.RootElement
                    .GetProperty("assignments")[0]
                    .EnumerateObject()
                    .Select(property => property.Name)
                    .Order());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"schemaVersion\":2,\"assignments\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"assignments\":[{\"slot\":0,\"sessionId\":\"bad\",\"state\":\"running\",\"updatedAt\":\"2026-07-20T12:00:00Z\",\"correlatedAttention\":[],\"uncorrelatedAttentionCount\":0}]}")]
    public async Task QuarantinesInvalidStateAndStartsEmpty(string invalidJson)
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-coordinator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var preferencesPath = Path.Combine(directory, "task-alerts.json");
            var statePath = Path.Combine(directory, "task-alert-state.json");
            TaskAlertPreferencesStore.Save(preferencesPath, TaskAlertPreferences.Default);
            File.WriteAllText(statePath, invalidJson);

            await using var coordinator = new TaskAlertCoordinator(preferencesPath, statePath);

            Assert.Empty(coordinator.GetSnapshot().Assignments);
            Assert.Single(Directory.GetFiles(directory, "task-alert-state.invalid-*.json"));
            Assert.Empty(TaskAlertStateStore.Load(statePath).Assignments);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExpiresStaleStateDuringRestoreAndPersistsTheCleanup()
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-coordinator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var preferencesPath = Path.Combine(directory, "task-alerts.json");
            var statePath = Path.Combine(directory, "task-alert-state.json");
            TaskAlertPreferencesStore.Save(preferencesPath, TaskAlertPreferences.Default);
            var pool = new TaskAlertPool();
            pool.Apply(new TaskAlertEvent(
                CodexLifecycleEvent.UserPromptSubmit,
                "stale-session",
                "stale-turn",
                DateTimeOffset.UtcNow - TaskAlertPool.RunningLease - TimeSpan.FromMinutes(1)));
            TaskAlertStateStore.Save(statePath, pool.CaptureState());

            await using var coordinator = new TaskAlertCoordinator(preferencesPath, statePath);

            Assert.Empty(coordinator.GetSnapshot().Assignments);
            Assert.Empty(TaskAlertStateStore.Load(statePath).Assignments);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task VersionOneAssignmentLearnsWorkspaceFromToolCompletionAndCanBeSuppressed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-coordinator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var preferencesPath = Path.Combine(directory, "task-alerts.json");
            var statePath = Path.Combine(directory, "task-alert-state.json");
            var updatedAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O");
            File.WriteAllText(statePath, $$"""
                {
                  "schemaVersion": 1,
                  "assignments": [
                    {
                      "slot": 1,
                      "sessionId": "legacy-session",
                      "turnId": "legacy-turn",
                      "state": "running",
                      "updatedAt": "{{updatedAt}}",
                      "completeAfter": null,
                      "correlatedAttention": [],
                      "uncorrelatedAttentionCount": 0
                    }
                  ]
                }
                """);

            await using var coordinator = new TaskAlertCoordinator(preferencesPath, statePath);

            var restored = Assert.Single(coordinator.GetSnapshot().Assignments);
            Assert.Equal("legacy-session", restored.SessionId);
            Assert.Null(restored.WorkspaceKey);

            var workspace = Path.Combine(directory, "realtime-voice-chat");
            Assert.True(coordinator.TryPublish(new TaskAlertEvent(
                CodexLifecycleEvent.ToolCompleted,
                "legacy-session",
                "legacy-turn",
                DateTimeOffset.UtcNow,
                AttentionKey,
                workspace)));
            await WaitUntilAsync(
                () => coordinator.GetSnapshot().Assignments.Single().WorkspaceKey is not null,
                TimeSpan.FromSeconds(3));

            Assert.True(coordinator.AddSuppression(
                TaskAlertSuppressionScope.Workspace,
                workspace));
            Assert.Empty(coordinator.GetSnapshot().Assignments);
            Assert.Empty(TaskAlertStateStore.Load(statePath).Assignments);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PersistsMutationsAcknowledgementAndDisable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-coordinator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var preferencesPath = Path.Combine(directory, "task-alerts.json");
            var statePath = Path.Combine(directory, "task-alert-state.json");
            await using var coordinator = new TaskAlertCoordinator(preferencesPath, statePath);
            Assert.True(coordinator.TryPublish(new TaskAlertEvent(
                CodexLifecycleEvent.Fault,
                "fault-session",
                "fault-turn",
                DateTimeOffset.UtcNow)));
            await WaitUntilAsync(
                () => TaskAlertStateStore.Load(statePath).Assignments.Length == 1,
                TimeSpan.FromSeconds(3));

            Assert.True(coordinator.AcknowledgeTerminal(1, "fault-session"));
            Assert.Empty(TaskAlertStateStore.Load(statePath).Assignments);

            Assert.True(coordinator.TryPublish(Event(2)));
            await WaitUntilAsync(
                () => TaskAlertStateStore.Load(statePath).Assignments.Length == 1,
                TimeSpan.FromSeconds(3));
            coordinator.SetEnabled(false);
            Assert.Empty(TaskAlertStateStore.Load(statePath).Assignments);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CleanDisposeRetriesAStateWriteThatPreviouslyFailed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-coordinator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var preferencesPath = Path.Combine(directory, "task-alerts.json");
            var statePath = Path.Combine(directory, "blocked-state");
            Directory.CreateDirectory(statePath);
            var log = new List<string>();
            await using (var coordinator = new TaskAlertCoordinator(preferencesPath, statePath, log.Add))
            {
                Assert.True(coordinator.TryPublish(Event(1)));
                await WaitUntilAsync(
                    () => coordinator.GetSnapshot().Assignments.Count == 1,
                    TimeSpan.FromSeconds(3));
                Assert.Contains(log, message => message.StartsWith(
                    "Could not save task-alert state:",
                    StringComparison.Ordinal));
                Directory.Delete(statePath);
            }

            Assert.Equal("session-1", Assert.Single(TaskAlertStateStore.Load(statePath).Assignments).SessionId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TaskSuppressionImmediatelyClearsAndBlocksOnlyThatTask()
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-coordinator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var coordinator = new TaskAlertCoordinator(Path.Combine(directory, "task-alerts.json"));
            foreach (var index in Enumerable.Range(1, 5))
            {
                Assert.True(coordinator.TryPublish(Event(index)));
            }

            await WaitUntilAsync(
                () => coordinator.GetSnapshot().Assignments.Count == 5,
                TimeSpan.FromSeconds(3));

            Assert.True(coordinator.AddSuppression(TaskAlertSuppressionScope.Task, "session-1"));
            Assert.Equal(
                new Dictionary<string, int>
                {
                    ["session-2"] = 2,
                    ["session-3"] = 3,
                    ["session-4"] = 4,
                    ["session-5"] = 5,
                },
                coordinator.GetSnapshot().Assignments.ToDictionary(item => item.SessionId, item => item.Slot));
            Assert.True(coordinator.TryPublish(Event(1)));
            await WaitUntilAsync(
                () => coordinator.GetSnapshot().RecentEvents?.Last().Result == TaskAlertEventResult.Suppressed,
                TimeSpan.FromSeconds(3));
            Assert.DoesNotContain(
                coordinator.GetSnapshot().Assignments,
                item => item.SessionId == "session-1");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceSuppressionCoversNewTaskIdsAndCanBeReenabled()
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-coordinator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var voiceWorkspace = Path.Combine(directory, "realtime-voice-chat");
        try
        {
            var preferencesPath = Path.Combine(directory, "task-alerts.json");
            await using var coordinator = new TaskAlertCoordinator(preferencesPath);
            Assert.True(coordinator.AddSuppression(
                TaskAlertSuppressionScope.Workspace,
                voiceWorkspace + Path.DirectorySeparatorChar));

            Assert.True(coordinator.TryPublish(Event(1, voiceWorkspace)));
            Assert.True(coordinator.TryPublish(Event(2, Path.Combine(directory, "other"))));
            await WaitUntilAsync(
                () => coordinator.GetSnapshot().RecentEvents?.Count == 2,
                TimeSpan.FromSeconds(3));

            var snapshot = coordinator.GetSnapshot();
            Assert.Equal(TaskAlertEventResult.Suppressed, snapshot.RecentEvents![0].Result);
            Assert.Equal("session-2", Assert.Single(snapshot.Assignments).SessionId);
            var rule = Assert.Single(snapshot.Suppressions!);
            Assert.Equal(TaskAlertSuppressionScope.Workspace, rule.Scope);
            Assert.True(coordinator.RemoveSuppression(rule));

            Assert.True(coordinator.TryPublish(Event(3, voiceWorkspace)));
            await WaitUntilAsync(
                () => coordinator.GetSnapshot().Assignments.Any(item => item.SessionId == "session-3"),
                TimeSpan.FromSeconds(3));
            Assert.Empty(TaskAlertPreferencesStore.LoadOrCreate(preferencesPath).Suppressions!);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DelayedSuppressedWorkspaceEventDoesNotRemoveNewerAssignment()
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-coordinator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var ignoredWorkspace = Path.Combine(directory, "ignored");
        var activeWorkspace = Path.Combine(directory, "active");
        try
        {
            await using var coordinator = new TaskAlertCoordinator(Path.Combine(directory, "task-alerts.json"));
            Assert.True(coordinator.AddSuppression(
                TaskAlertSuppressionScope.Workspace,
                ignoredWorkspace));

            var delayedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            var activeAt = delayedAt.AddSeconds(1);
            Assert.True(coordinator.TryPublish(new TaskAlertEvent(
                CodexLifecycleEvent.UserPromptSubmit,
                "shared-session",
                "active-turn",
                activeAt,
                Workspace: activeWorkspace)));
            await WaitUntilAsync(
                () => coordinator.GetSnapshot().Assignments.Count == 1,
                TimeSpan.FromSeconds(3));

            Assert.True(coordinator.TryPublish(new TaskAlertEvent(
                CodexLifecycleEvent.UserPromptSubmit,
                "shared-session",
                "delayed-turn",
                delayedAt,
                Workspace: ignoredWorkspace)));
            await WaitUntilAsync(
                () => coordinator.GetSnapshot().RecentEvents?.Count == 2,
                TimeSpan.FromSeconds(3));

            var snapshot = coordinator.GetSnapshot();
            var assignment = Assert.Single(snapshot.Assignments);
            Assert.Equal("shared-session", assignment.SessionId);
            Assert.Equal("active-turn", assignment.TurnId);
            Assert.Equal(activeAt, assignment.UpdatedAt);
            Assert.Equal(
                TaskAlertSuppression.CreateWorkspaceKey(activeWorkspace),
                assignment.WorkspaceKey);
            Assert.Equal(TaskAlertEventResult.Suppressed, snapshot.RecentEvents![^1].Result);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PersistedWorkspaceSuppressionFiltersRestoredHashedAssignment()
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-coordinator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var voiceWorkspace = Path.Combine(directory, "realtime-voice-chat");
        try
        {
            var preferencesPath = Path.Combine(directory, "task-alerts.json");
            var statePath = Path.Combine(directory, "task-alert-state.json");
            TaskAlertPreferencesStore.Save(preferencesPath, new TaskAlertPreferences(
                Suppressions:
                [
                    new(TaskAlertSuppressionScope.Workspace, voiceWorkspace),
                ]));
            var pool = new TaskAlertPool();
            pool.Apply(Event(1, voiceWorkspace));
            TaskAlertStateStore.Save(statePath, pool.CaptureState());

            await using var coordinator = new TaskAlertCoordinator(preferencesPath, statePath);

            Assert.Empty(coordinator.GetSnapshot().Assignments);
            Assert.Empty(TaskAlertStateStore.Load(statePath).Assignments);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static TaskAlertEvent Event(int index, string? workspace = null) => new(
        CodexLifecycleEvent.UserPromptSubmit,
        $"session-{index}",
        $"turn-{index}",
        DateTimeOffset.UtcNow,
        Workspace: workspace);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(condition());
    }
}
