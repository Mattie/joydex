using System.Text.Json;
using Joydex.Core.Config;
using Joydex.Core.T3;
using Joydex.Core.TaskAlerts;
using Joydex.Windows.Actions;
using Joydex.Windows.T3;
using Joydex.Windows.TaskAlerts;

namespace Joydex.Tests;

public sealed class T3TaskIntegrationTests
{
    private static readonly T3ConnectionOptions Options = new() { Enabled = true, SshHost = "configured-host", EnvironmentId = "environment-one" };

    [Fact]
    public async Task DisabledTaskAlertsSkipRemoteReadsWithoutFailureOrBackoff()
    {
        using var files = new Scratch();
        await using var coordinator = files.Coordinator(enabled: false);
        var transport = new FixtureTransport { Result = new() { ErrorClass = "Timeout" } };
        var logs = new List<string>();
        await using var monitor = new T3TaskAlertMonitor(coordinator, Options, logs.Add, new T3Reader(transport));
        // True means the poll loop keeps its ordinary five-second idle cadence.
        Assert.True(await monitor.ReadOnceAsync(default));
        Assert.True(await monitor.ReadOnceAsync(default));
        Assert.Equal(0, transport.Calls);
        Assert.Null(coordinator.GetSnapshot().T3Status);
        Assert.Empty(logs);
    }

    [Fact]
    public async Task DisablingTaskAlertsStopsTheNextReadAndReenablingResumesIt()
    {
        using var files = new Scratch();
        await using var coordinator = files.Coordinator();
        var now = DateTimeOffset.UtcNow.AddSeconds(-10);
        var transport = new FixtureTransport { Result = Result(now) };
        await using var monitor = new T3TaskAlertMonitor(coordinator, Options, _ => { }, new T3Reader(transport));
        Assert.True(await monitor.ReadOnceAsync(default));
        Assert.Equal(1, transport.Calls);
        Assert.Single(coordinator.GetSnapshot().Assignments);

        coordinator.SetEnabled(false);
        Assert.True(await monitor.ReadOnceAsync(default));
        Assert.True(await monitor.ReadOnceAsync(default));
        Assert.Equal(1, transport.Calls);
        Assert.Empty(coordinator.GetSnapshot().Assignments);

        coordinator.SetEnabled(true);
        transport.Result = Result(now.AddSeconds(1));
        Assert.True(await monitor.ReadOnceAsync(default));
        Assert.Equal(2, transport.Calls);
        Assert.Single(coordinator.GetSnapshot().Assignments);
    }

    [Fact]
    public async Task DisabledMonitorStillHonorsCancellationWithoutStartingSsh()
    {
        using var files = new Scratch();
        await using var coordinator = files.Coordinator(enabled: false);
        var transport = new FixtureTransport { Result = Result(DateTimeOffset.UtcNow) };
        await using var monitor = new T3TaskAlertMonitor(coordinator, Options, _ => { }, new T3Reader(transport));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.ReadOnceAsync(cancellation.Token));
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task ReaderToPoolPreservesPermissionsAndVisitAcknowledgementsAcrossRestartAndOutage()
    {
        using var files = new Scratch();
        var now = DateTimeOffset.UtcNow.AddSeconds(-10);
        var transport = new FixtureTransport { Result = Result(now) };
        string key;
        await using (var coordinator = files.Coordinator())
        {
            await using var monitor = new T3TaskAlertMonitor(coordinator, Options, _ => { }, new T3Reader(transport));
            Assert.True(await monitor.ReadOnceAsync(default));
            var task = Assert.Single(coordinator.GetSnapshot().Assignments);
            key = task.SessionId;
            Assert.Equal(TaskAlertState.Approval, task.State);
            var question = Assert.Single(task.PendingAsyncQuestions!);
            Assert.Equal(TaskAlertIdentity.CreateRequestKey(task.TaskIdentity, "run-one", "question-one"), question);
            Assert.Equal("Task title", task.Metadata?.Title);
            Assert.Empty(coordinator.GetSnapshot().T3Status!.Tasks);
            var navigator = new QuestionAcknowledgingTaskNavigator(new SuccessfulNavigator(), coordinator.GetSnapshot,
                coordinator.DismissQuestions, _ => { });
            Assert.True(await navigator.NavigateAsync(new(task.Slot, 2, 1, key, task.TaskIdentity), default));
            task = Assert.Single(coordinator.GetSnapshot().Assignments);
            Assert.Empty(task.PendingAsyncQuestions ?? []);
            Assert.Equal(TaskAlertState.Approval, task.State); // Permission remains.
            transport.Result = transport.Result with { CapturedAt = now.AddSeconds(1) };
            Assert.True(await monitor.ReadOnceAsync(default));
            Assert.Empty(Assert.Single(coordinator.GetSnapshot().Assignments).PendingAsyncQuestions ?? []);
            transport.Result = new() { ErrorClass = "Timeout" };
            Assert.False(await monitor.ReadOnceAsync(default));
            task = Assert.Single(coordinator.GetSnapshot().Assignments);
            Assert.True(task.SourceUnavailable);
            Assert.Equal(TaskAlertState.Approval, task.State);
        }
        await using (var coordinator = files.Coordinator())
        {
            Assert.True(Assert.Single(coordinator.GetSnapshot().Assignments).SourceUnavailable);
            transport.Result = Result(now.AddSeconds(2));
            await using var monitor = new T3TaskAlertMonitor(coordinator, Options, _ => { }, new T3Reader(transport));
            Assert.True(await monitor.ReadOnceAsync(default));
            var task = Assert.Single(coordinator.GetSnapshot().Assignments);
            Assert.Equal(key, task.SessionId);
            Assert.Empty(task.PendingAsyncQuestions ?? []);
            Assert.False(task.SourceUnavailable);
            Assert.Contains("thread-one", transport.ScriptOptions!.Value.GetProperty("knownThreadIds").EnumerateArray().Select(x => x.GetString()));
        }
    }

    [Fact]
    public async Task PartialMissingThreadDoesNotRenewItsHealthOrLeaseAndCodexRemainsIndependent()
    {
        using var files = new Scratch();
        var now = DateTimeOffset.UtcNow.AddSeconds(-10);
        await using var coordinator = files.Coordinator();
        var transport = new FixtureTransport { Result = Result(now) };
        await using var monitor = new T3TaskAlertMonitor(coordinator, Options, _ => { }, new T3Reader(transport));
        await monitor.ReadOnceAsync(default);
        var original = Assert.Single(coordinator.GetSnapshot().Assignments);
        transport.Result = new() { ErrorClass = "Timeout" };
        await monitor.ReadOnceAsync(default);
        transport.Result = Result(now.AddSeconds(1)) with { IsComplete = false, Tasks = [] };
        await monitor.ReadOnceAsync(default);
        var retained = Assert.Single(coordinator.GetSnapshot().Assignments);
        Assert.True(retained.SourceUnavailable);
        Assert.Equal(original.ObservedActiveAt, retained.ObservedActiveAt);
        Assert.Equal(original.PendingAsyncQuestions, retained.PendingAsyncQuestions);
        var local = Guid.NewGuid().ToString();
        coordinator.TryPublish(new(CodexLifecycleEvent.UserPromptSubmit, local, "local-run", now));
        for (var i = 0; i < 100 && coordinator.GetSnapshot().Assignments.Count < 2; i++) await Task.Delay(10);
        Assert.Equal(2, coordinator.GetSnapshot().Assignments.Count);
        Assert.DoesNotContain(original.SessionId, coordinator.CaptureReconciliationCheckpoint().KnownTaskIds);
    }

    [Fact]
    public async Task DisabledOrDifferentEnvironmentCannotEnterPoolAndSuppressionUsesTypedIdentity()
    {
        using var files = new Scratch();
        var now = DateTimeOffset.UtcNow.AddSeconds(-1);
        await using var coordinator = files.Coordinator();
        var task = Result(now).Tasks[0];
        var source = new TaskAlertSourceIdentity(TaskAlertBackend.RemoteT3, Options.EnvironmentId);
        var snapshot = new TaskAlertSourceSnapshot(source, 1, 1, now, true, [T3TaskAlertMonitor.Project(source, task)]);
        coordinator.ApplySourceSnapshot(snapshot with { Source = source with { EnvironmentId = "other" } }, Result(now));
        Assert.Empty(coordinator.GetSnapshot().Assignments);
        coordinator.AddSuppression(TaskAlertSuppressionScope.Task, TaskAlertIdentity.RemoteT3(source.EnvironmentId, task.Id).PoolKey);
        coordinator.ApplySourceSnapshot(snapshot, Result(now));
        Assert.Empty(coordinator.GetSnapshot().Assignments);
    }

    [Fact]
    public async Task EveryNavigationRequestRetainsEncodedSourceAndPassesTheUriToTheVerifiedExecutable()
    {
        var identity = TaskAlertIdentity.RemoteT3("env/A", "thread?B");
        var request = new TaskAlertNavigationRequest(1, 2, 1, identity.PoolKey);
        Assert.Equal(identity, request.TaskIdentity);
        string? started = null;
        string? argument = null;
        const string executable = @"C:\Apps\T3 Code (Beta).exe";
        var navigator = new TaskDeepLinkNavigator(new SafetyOptions { DryRun = false }, _ => { },
            new AllowedGuard(), start => { started = start.FileName; argument = Assert.Single(start.ArgumentList); return null; },
            () => executable);
        Assert.True(await navigator.NavigateAsync(request, default));
        Assert.Equal(executable, started);
        Assert.Equal("t3code://threads/env%2FA/thread%3FB", argument);
        Assert.Equal("codex://threads/native", TaskDeepLinkNavigator.BuildUri(TaskAlertIdentity.LocalCodex("native")));
    }

    [Fact]
    public async Task PrivateMetadataSurvivesRestartWithoutEnteringLifecycleStateAndCorruptionIsOptional()
    {
        using var files = new Scratch();
        await using (var coordinator = files.Coordinator())
        {
            var now = DateTimeOffset.UtcNow.AddSeconds(-1);
            coordinator.ApplySourceSnapshot(new(new(TaskAlertBackend.RemoteT3, Options.EnvironmentId), 1, 1, now, true,
                [T3TaskAlertMonitor.Project(new(TaskAlertBackend.RemoteT3, Options.EnvironmentId), Result(now).Tasks[0])]), Result(now));
            Assert.DoesNotContain("Task title", File.ReadAllText(files.State));
        }
        await using (var coordinator = files.Coordinator()) Assert.Equal("Task title", Assert.Single(coordinator.GetSnapshot().Assignments).Metadata?.Title);
        File.WriteAllText(files.State + ".metadata.json", "{bad optional cache");
        await using (var coordinator = files.Coordinator()) Assert.Single(coordinator.GetSnapshot().Assignments);
    }

    private static T3ReadResult Result(DateTimeOffset now) => new()
    {
        Success = true, EnvironmentId = Options.EnvironmentId, SourceEpoch = new string('A', 64), Sequence = 7,
        CapturedAt = now, IsComplete = true, Tasks = [new()
        {
            Id = "thread-one", RunId = "run-one", Status = "running", StartedAt = now.AddMinutes(-1),
            ReadComplete = true, RequestsComplete = true, Title = "Task title", Project = "Project name",
            PendingRequests = [new() { Id = "permission-one", RunId = "run-one", CreatedAt = now.AddSeconds(-2), Blocking = true },
                new() { Id = "question-one", RunId = "run-one", CreatedAt = now.AddSeconds(-2), Blocking = false }],
        }],
    };
    private sealed class FixtureTransport : IT3SshTransport
    {
        public required T3ReadResult Result { get; set; }
        public int Calls { get; private set; }
        public JsonElement? ScriptOptions { get; private set; }
        public Task<string> ExecuteAsync(string host, string script, CancellationToken token)
        {
            Calls++;
            const string marker = "JOYDEX_OPTIONS = json.loads(base64.b64decode('";
            var start = script.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            ScriptOptions = JsonDocument.Parse(Convert.FromBase64String(script[start..script.IndexOf("'", start, StringComparison.Ordinal)])).RootElement.Clone();
            return Task.FromResult(JsonSerializer.Serialize(Result));
        }
    }
    private sealed class Scratch : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "joydex-t3-tests-" + Guid.NewGuid().ToString("N"));
        public string State => Path.Combine(_root, "state.json");
        public Scratch() => Directory.CreateDirectory(_root);
        public TaskAlertCoordinator Coordinator(bool enabled = true) => new(Path.Combine(_root, "preferences.json"), new(Enabled: enabled, T3: Options), State);
        public void Dispose() => Directory.Delete(_root, true);
    }
    private sealed class SuccessfulNavigator : ITaskAlertNavigator
    { public Task<bool> NavigateAsync(TaskAlertNavigationRequest request, CancellationToken token) => Task.FromResult(true); }
    private sealed class AllowedGuard : IForegroundProcessGuard
    { public ForegroundCheck Check(SafetyOptions options, bool actionMayBringCodexForward) => new(true, "fixture", ""); }
}
