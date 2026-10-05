using Joydex.App;
using Joydex.Contracts;

namespace Joydex.Tests;

public sealed class RuntimePluginConnectionServicesTests
{
    [Fact]
    public async Task InspectionReadsHealthWithoutReloadingPadConfiguration()
    {
        var runner = new RecordingCommandRunner();
        var services = new RuntimePluginConnectionServices();
        services.BeginConnection(1, runner, supported: true);

        var outcome = await services.InspectAsync(CancellationToken.None);

        Assert.Equal(RuntimeCommandStatus.Completed, outcome.Status);
        Assert.Equal(RuntimeCommandKind.InspectPlugins, outcome.Kind);
        Assert.Equal(RuntimePluginState.Ready, PadHealth(outcome).State);
        var request = Assert.Single(runner.ExecuteRequests);
        Assert.Equal(RuntimeCommandKind.InspectPlugins, request.Kind);
        Assert.Null(request.Arguments);
    }

    [Theory]
    [InlineData(RuntimeCommandKind.RestartPlugin)]
    [InlineData(RuntimeCommandKind.ReloadPluginConfiguration)]
    public async Task SideEffectingCommandRecoversLostReplyAfterReconnect(
        RuntimeCommandKind kind)
    {
        var operations = new Dictionary<Guid, RuntimeCommandResult>();
        var first = new RecordingCommandRunner(operations)
        {
            LoseNextAcceptedReply = true,
            ResultFactory = request => Completed(request, generation: 8),
        };
        var services = new RuntimePluginConnectionServices();
        services.BeginConnection(1, first, supported: true);

        await Assert.ThrowsAsync<IOException>(() => InvokeAsync(services, kind));
        var submitted = Assert.Single(first.ExecuteRequests);
        Assert.Equal(RuntimePluginIds.Pad, submitted.Arguments?.PluginId);

        services.EndConnection(1);
        var replacement = new RecordingCommandRunner(operations);
        services.BeginConnection(2, replacement, supported: true);
        var recovered = await InvokeAsync(services, kind);

        Assert.Equal(RuntimeCommandStatus.Completed, recovered.Status);
        Assert.Equal(kind, recovered.Kind);
        Assert.Equal(8, PadHealth(recovered).Generation);
        Assert.Empty(replacement.ExecuteRequests);
        Assert.Equal(submitted.OperationId, Assert.Single(replacement.LookupOperationIds));
    }

    [Fact]
    public async Task DifferentMutationFirstReconcilesTheEarlierAcceptedOperation()
    {
        var operations = new Dictionary<Guid, RuntimeCommandResult>();
        var first = new RecordingCommandRunner(operations)
        {
            LoseNextAcceptedReply = true,
            ResultFactory = request => Completed(request, generation: 5),
        };
        var services = new RuntimePluginConnectionServices();
        services.BeginConnection(1, first, supported: true);
        await Assert.ThrowsAsync<IOException>(() => services.RestartPadAsync(CancellationToken.None));
        var restart = Assert.Single(first.ExecuteRequests);

        services.EndConnection(1);
        var replacement = new RecordingCommandRunner(operations);
        services.BeginConnection(2, replacement, supported: true);
        var reconciled = await services.ReloadPadConfigurationAsync(CancellationToken.None);

        Assert.Equal(RuntimeCommandKind.RestartPlugin, reconciled.Kind);
        Assert.Equal(restart.OperationId, Assert.Single(replacement.LookupOperationIds));
        Assert.Empty(replacement.ExecuteRequests);

        var reload = await services.ReloadPadConfigurationAsync(CancellationToken.None);
        Assert.Equal(RuntimeCommandKind.ReloadPluginConfiguration, reload.Kind);
        Assert.Equal(
            RuntimeCommandKind.ReloadPluginConfiguration,
            Assert.Single(replacement.ExecuteRequests).Kind);
    }

    [Fact]
    public async Task ConcurrentPadMutationsAreSerializedBeforeCommandSubmission()
    {
        var runner = new RecordingCommandRunner
        {
            BlockFirstExecution = true,
        };
        var services = new RuntimePluginConnectionServices();
        services.BeginConnection(1, runner, supported: true);

        var restart = services.RestartPadAsync(CancellationToken.None);
        await runner.FirstExecuteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancelled = new CancellationTokenSource();
        var reload = services.ReloadPadConfigurationAsync(cancelled.Token);
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reload);
        Assert.Single(runner.ExecuteRequests);
        runner.ReleaseFirstExecute.TrySetResult();
        var outcome = await restart;
        Assert.Equal(RuntimeCommandKind.RestartPlugin, outcome.Kind);
    }

    [Fact]
    public async Task MissingAcceptedOperationIsNeverResubmittedAfterReconnect()
    {
        var first = new RecordingCommandRunner
        {
            LoseNextReplyBeforeAcceptance = true,
        };
        var services = new RuntimePluginConnectionServices();
        services.BeginConnection(1, first, supported: true);
        await Assert.ThrowsAsync<IOException>(() => services.RestartPadAsync(CancellationToken.None));

        services.EndConnection(1);
        var replacement = new RecordingCommandRunner();
        services.BeginConnection(2, replacement, supported: true);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            services.RestartPadAsync(CancellationToken.None));

        Assert.Contains("could not be reconciled", exception.Message);
        Assert.Empty(replacement.ExecuteRequests);
        Assert.Single(replacement.LookupOperationIds);
    }

    [Fact]
    public async Task RestartedRuntimeReleasesUnknownActionOnlyForTheNextExplicitRequest()
    {
        var first = new RecordingCommandRunner { LoseNextReplyBeforeAcceptance = true };
        var services = new RuntimePluginConnectionServices();
        services.BeginConnection(1, first, supported: true, engineEpoch: Guid.NewGuid());
        await Assert.ThrowsAsync<IOException>(() => services.RestartPadAsync(default));
        var pending = Assert.Single(first.ExecuteRequests);
        services.EndConnection(1);
        var replacement = new RecordingCommandRunner();
        services.BeginConnection(2, replacement, supported: true, engineEpoch: Guid.NewGuid());

        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => services.RestartPadAsync(default));
        Assert.Contains("restarted", unknown.Message);
        Assert.Empty(replacement.ExecuteRequests);
        Assert.Equal(pending.OperationId, Assert.Single(replacement.LookupOperationIds));
        await services.RestartPadAsync(default);
        Assert.NotEqual(pending.OperationId, Assert.Single(replacement.ExecuteRequests).OperationId);
    }

    [Fact]
    public async Task ExpiredTerminalResultDoesNotPermanentlyBlockPadActions()
    {
        var operations = new Dictionary<Guid, RuntimeCommandResult>();
        var first = new RecordingCommandRunner(operations) { LoseNextAcceptedReply = true };
        var services = new RuntimePluginConnectionServices();
        services.BeginConnection(1, first, supported: true);
        await Assert.ThrowsAsync<IOException>(() => services.RestartPadAsync(default));
        var pending = Assert.Single(first.ExecuteRequests);
        operations[pending.OperationId] = new RuntimeCommandResult(
            pending.OperationId, pending.Kind, RuntimeCommandStatus.Failed, "Result expired; check effects.");
        var replacement = new RecordingCommandRunner(operations);
        services.BeginConnection(2, replacement, supported: true);

        var expired = await Assert.ThrowsAsync<InvalidOperationException>(() => services.RestartPadAsync(default));
        Assert.Contains("expired", expired.Message);
        Assert.Empty(replacement.ExecuteRequests);
        await services.RestartPadAsync(default);
        Assert.NotEqual(pending.OperationId, Assert.Single(replacement.ExecuteRequests).OperationId);
    }

    [Fact]
    public async Task RejectedReloadReturnsTheStillReadyPriorGeneration()
    {
        var runner = new RecordingCommandRunner
        {
            ResultFactory = request => new RuntimeCommandResult(
                request.OperationId,
                request.Kind,
                RuntimeCommandStatus.Rejected,
                "PAD configuration validation failed.",
                new RuntimeCommandPayload(Plugins: Snapshot(
                    RuntimePluginState.Ready,
                    generation: 4,
                    "PAD is running with the previous configuration."))),
        };
        var services = new RuntimePluginConnectionServices();
        services.BeginConnection(1, runner, supported: true);

        var outcome = await services.ReloadPadConfigurationAsync(CancellationToken.None);

        Assert.Equal(RuntimeCommandStatus.Rejected, outcome.Status);
        Assert.Equal(RuntimeCommandKind.ReloadPluginConfiguration, outcome.Kind);
        var health = PadHealth(outcome);
        Assert.Equal(RuntimePluginState.Ready, health.State);
        Assert.Equal(4, health.Generation);
        Assert.True(health.CanRestart);
        Assert.True(health.CanReload);
    }

    [Fact]
    public async Task UnsupportedConnectionRejectsPluginCommandsLocally()
    {
        var runner = new RecordingCommandRunner();
        var services = new RuntimePluginConnectionServices();
        services.BeginConnection(1, runner, supported: false);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            services.InspectAsync(CancellationToken.None));

        Assert.Contains("does not support PAD management", exception.Message);
        Assert.Empty(runner.ExecuteRequests);
    }

    private static Task<RuntimePluginCommandOutcome> InvokeAsync(
        RuntimePluginConnectionServices services,
        RuntimeCommandKind kind) => kind switch
    {
        RuntimeCommandKind.RestartPlugin => services.RestartPadAsync(CancellationToken.None),
        RuntimeCommandKind.ReloadPluginConfiguration =>
            services.ReloadPadConfigurationAsync(CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static RuntimePluginHealth PadHealth(RuntimePluginCommandOutcome outcome) =>
        Assert.Single(outcome.Plugins.Health, item => item.PluginId == RuntimePluginIds.Pad);

    private static RuntimeCommandResult Completed(
        RuntimeCommandRequest request,
        long generation = 3) => new(
        request.OperationId,
        request.Kind,
        RuntimeCommandStatus.Completed,
        "PAD command completed.",
        new RuntimeCommandPayload(Plugins: Snapshot(RuntimePluginState.Ready, generation)));

    private static RuntimePluginSnapshot Snapshot(
        RuntimePluginState state,
        long generation,
        string detail = "PAD is running.") => new(
        [
            new RuntimePluginRegistration(
                RuntimePluginIds.Pad,
                "1.0.0",
                HostApiMajor: 1,
                MinimumHostApiMinor: 0,
                SettingsSchemaVersion: 1,
                ExecutionInProcess: true),
        ],
        [
            new RuntimePluginHealth(
                RuntimePluginIds.Pad,
                state,
                generation,
                detail,
                CanRestart: true,
                CanReload: true),
        ]);

    private sealed class RecordingCommandRunner : IRuntimeCommandRunner
    {
        private readonly IDictionary<Guid, RuntimeCommandResult> _operations;

        public RecordingCommandRunner(IDictionary<Guid, RuntimeCommandResult>? operations = null)
        {
            _operations = operations ?? new Dictionary<Guid, RuntimeCommandResult>();
        }

        public List<RuntimeCommandRequest> ExecuteRequests { get; } = [];

        public List<Guid> LookupOperationIds { get; } = [];

        public Func<RuntimeCommandRequest, RuntimeCommandResult> ResultFactory { get; init; } =
            static request => Completed(request);

        public bool LoseNextAcceptedReply { get; init; }

        public bool LoseNextReplyBeforeAcceptance { get; init; }

        public bool BlockFirstExecution { get; init; }

        public TaskCompletionSource FirstExecuteStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstExecute { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<RuntimeCommandResult> ExecuteAsync(
            RuntimeCommandRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteRequests.Add(request);
            if (LoseNextReplyBeforeAcceptance && ExecuteRequests.Count == 1)
            {
                throw new IOException("The reply was lost before command acceptance.");
            }
            if (BlockFirstExecution && ExecuteRequests.Count == 1)
            {
                FirstExecuteStarted.TrySetResult();
                await ReleaseFirstExecute.Task.WaitAsync(cancellationToken);
            }

            var result = ResultFactory(request);
            _operations.Add(request.OperationId, result);
            if (LoseNextAcceptedReply && ExecuteRequests.Count == 1)
            {
                throw new IOException("The accepted command reply was lost.");
            }
            return result;
        }

        public Task<RuntimeCommandOperationResult> GetOperationAsync(
            Guid operationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LookupOperationIds.Add(operationId);
            return Task.FromResult(_operations.TryGetValue(operationId, out var result)
                ? new RuntimeCommandOperationResult(
                    operationId,
                    RuntimeCommandOperationState.Completed,
                    result)
                : new RuntimeCommandOperationResult(
                    operationId,
                    RuntimeCommandOperationState.NotFound));
        }
    }
}
