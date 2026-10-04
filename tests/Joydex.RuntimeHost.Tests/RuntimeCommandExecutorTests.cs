using Joydex.Contracts;

namespace Joydex.RuntimeHost.Tests;

public sealed class RuntimeCommandExecutorTests
{
    [Fact]
    public async Task EvictedCommandRemainsTerminalAndCannotExecuteAgain()
    {
        var router = new RecordingRouter();
        var executor = new RuntimeCommandExecutor(router, CancellationToken.None);
        var request = new RuntimeCommandRequest(Guid.NewGuid(), RuntimeCommandKind.InstallTaskAlertHooks);
        _ = await executor.ExecuteAsync("first", request, CancellationToken.None);

        await EvictHistoryAsync(executor);
        var recovered = executor.GetOperation("first", request.OperationId);

        Assert.Equal(RuntimeCommandOperationState.Completed, recovered.State);
        Assert.Equal(RuntimeCommandStatus.Failed, recovered.Result!.Status);
        Assert.Equal(request.Kind, recovered.Result.Kind);
        Assert.Null(recovered.Result.Payload);
        Assert.Contains("expired", recovered.Result.Detail);
        executor.Disconnect("first");
        Assert.Equal(recovered, executor.GetOperation("replacement", request.OperationId));
        var count = router.ExecutionCount;
        Assert.Equal(RuntimeCommandStatus.Rejected,
            (await executor.ExecuteAsync("replacement", request, CancellationToken.None)).Status);
        Assert.Equal(count, router.ExecutionCount);
        Assert.Equal(RuntimeCommandOperationState.NotFound,
            executor.GetOperation("replacement", Guid.NewGuid()).State);
        Assert.Equal(RuntimeCommandStatus.Completed,
            (await executor.ExecuteAsync("replacement", request with { OperationId = Guid.NewGuid() },
                CancellationToken.None)).Status);
        Assert.Equal(count + 1, router.ExecutionCount);
    }

    [Fact]
    public async Task SensitiveReadsLeaveNoRecoverableTombstone()
    {
        var executor = new RuntimeCommandExecutor(new RecordingRouter(), CancellationToken.None);
        var request = new RuntimeCommandRequest(Guid.NewGuid(), RuntimeCommandKind.ReadPebbleIndexAccess);
        _ = await executor.ExecuteAsync("first", request, CancellationToken.None);
        Assert.Equal(RuntimeCommandOperationState.NotFound,
            executor.GetOperation("other", request.OperationId).State);

        await EvictHistoryAsync(executor);

        Assert.Equal(RuntimeCommandOperationState.NotFound,
            executor.GetOperation("first", request.OperationId).State);
        Assert.Equal(RuntimeCommandOperationState.NotFound,
            executor.GetOperation("other", request.OperationId).State);
    }

    private static async Task EvictHistoryAsync(RuntimeCommandExecutor executor)
    {
        for (var index = 0; index < 256; index++)
        {
            var result = await executor.ExecuteAsync("reader",
                new RuntimeCommandRequest(Guid.NewGuid(), RuntimeCommandKind.ReadVoiceConversationPage),
                CancellationToken.None);
            Assert.Equal(RuntimeCommandStatus.Completed, result.Status);
        }
    }

    private sealed class RecordingRouter : IRuntimeCommandRouter
    {
        public int ExecutionCount { get; private set; }

        public Task<RuntimeCommandResult> ExecuteAsync(string connectionId, RuntimeCommandRequest request,
            CancellationToken runtimeCancellationToken)
        {
            ExecutionCount++;
            return Task.FromResult(new RuntimeCommandResult(request.OperationId, request.Kind,
                RuntimeCommandStatus.Completed, Payload: new RuntimeCommandPayload(VoiceProjects: [])));
        }
    }
}
