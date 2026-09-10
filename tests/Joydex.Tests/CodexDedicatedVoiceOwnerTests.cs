using System.Text.Json;
using Joydex.Windows.Voice;

namespace Joydex.Tests;

public sealed class CodexDedicatedVoiceOwnerTests
{
    [Fact]
    public async Task ResumesExactTaskAndRetainsClientUntilOwnerDisposal()
    {
        var threadId = Guid.NewGuid().ToString("D");
        var client = new FakeAppServerClient((method, _, _) =>
            method == "thread/resume"
                ? Task.FromResult(JsonSerializer.SerializeToElement(new { thread = new { id = threadId } }))
                : method == "thread/realtime/listVoices"
                    ? Task.FromResult(CompatibleVoices())
                : throw new InvalidOperationException(method));
        await using var owner = new CodexDedicatedVoiceOwner(
            threadId,
            _ => Task.FromResult<ICodexAppServerClient>(client),
            "cove");

        await owner.StartAsync();

        Assert.True(owner.IsReady);
        Assert.False(client.Disposed);
        Assert.Equal(2, client.Requests.Count);
        var request = client.Requests[0];
        Assert.Equal("thread/resume", request.Method);
        Assert.Equal(threadId, request.Parameters.GetProperty("threadId").GetString());
        Assert.True(request.Parameters.GetProperty("excludeTurns").GetBoolean());
        Assert.Equal("never", request.Parameters.GetProperty("approvalPolicy").GetString());
        Assert.Equal("danger-full-access", request.Parameters.GetProperty("sandbox").GetString());
        Assert.Equal("thread/realtime/listVoices", client.Requests[1].Method);
        await using var session = owner.CreateRealtimeSession();
        Assert.IsType<CodexRealtimeSession>(session);

        await owner.DisposeAsync();
        Assert.False(owner.IsReady);
        Assert.True(client.Disposed);
        await owner.Completion;
    }

    [Fact]
    public async Task RejectsWriterConflictAsTypedOwnershipFailure()
    {
        var threadId = Guid.NewGuid().ToString("D");
        var client = new FakeAppServerClient((_, _, _) =>
            Task.FromException<JsonElement>(new CodexAppServerRpcException(
                -32603,
                "task already has an active writer")));
        await using var owner = new CodexDedicatedVoiceOwner(
            threadId,
            _ => Task.FromResult<ICodexAppServerClient>(client));

        await Assert.ThrowsAsync<CodexDedicatedVoiceOwnershipException>(() => owner.StartAsync());

        Assert.False(owner.IsReady);
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task RejectsInitializationRpcFailureAsCompatibilityFailure()
    {
        var threadId = Guid.NewGuid().ToString("D");
        var client = new FakeAppServerClient(
            (_, _, _) => throw new InvalidOperationException("No request was expected."),
            new CodexAppServerRpcException(-32602, "invalid initialize parameters"));
        await using var owner = new CodexDedicatedVoiceOwner(
            threadId,
            _ => Task.FromResult<ICodexAppServerClient>(client));

        var exception = await Assert.ThrowsAsync<CodexDedicatedVoiceCompatibilityException>(
            () => owner.StartAsync());

        Assert.Contains("initialization contract", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task RejectsResumeRpcFailureAsCompatibilityFailure()
    {
        var threadId = Guid.NewGuid().ToString("D");
        var client = new FakeAppServerClient((_, _, _) =>
            Task.FromException<JsonElement>(new CodexAppServerRpcException(
                -32602,
                "invalid resume parameters")));
        await using var owner = new CodexDedicatedVoiceOwner(
            threadId,
            _ => Task.FromResult<ICodexAppServerClient>(client));

        var exception = await Assert.ThrowsAsync<CodexDedicatedVoiceCompatibilityException>(
            () => owner.StartAsync());

        Assert.Contains("could not resume", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task RejectsResumeThatReturnsAnotherTask()
    {
        var configuredId = Guid.NewGuid().ToString("D");
        var returnedId = Guid.NewGuid().ToString("D");
        var client = new FakeAppServerClient((_, _, _) =>
            Task.FromResult(JsonSerializer.SerializeToElement(new { thread = new { id = returnedId } })));
        await using var owner = new CodexDedicatedVoiceOwner(
            configuredId,
            _ => Task.FromResult<ICodexAppServerClient>(client));

        var exception = await Assert.ThrowsAsync<CodexDedicatedVoiceCompatibilityException>(
            () => owner.StartAsync());

        Assert.Contains(configuredId, exception.Message, StringComparison.Ordinal);
        Assert.Contains(returnedId, exception.Message, StringComparison.Ordinal);
        Assert.False(owner.IsReady);
    }

    [Fact]
    public async Task RejectsOwnerReadinessWhenRealtimeV3VoiceCapabilityIsMissing()
    {
        var threadId = Guid.NewGuid().ToString("D");
        var client = new FakeAppServerClient((method, _, _) =>
            method == "thread/resume"
                ? Task.FromResult(JsonSerializer.SerializeToElement(new { thread = new { id = threadId } }))
                : method == "thread/realtime/listVoices"
                    ? Task.FromResult(JsonSerializer.SerializeToElement(new
                    {
                        voices = new
                        {
                            defaultV1 = "cove",
                            defaultV2 = "alloy",
                            v1 = Array.Empty<string>(),
                            v2 = new[] { "alloy" },
                        },
                    }))
                    : throw new InvalidOperationException(method));
        await using var owner = new CodexDedicatedVoiceOwner(
            threadId,
            _ => Task.FromResult<ICodexAppServerClient>(client));

        var exception = await Assert.ThrowsAsync<CodexDedicatedVoiceCompatibilityException>(
            () => owner.StartAsync());

        Assert.Contains("Realtime V3", exception.Message, StringComparison.Ordinal);
        Assert.False(owner.IsReady);
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task RejectsConfiguredVoiceMissingFromCapabilityList()
    {
        var threadId = Guid.NewGuid().ToString("D");
        var client = new FakeAppServerClient((method, _, _) =>
            method == "thread/resume"
                ? Task.FromResult(JsonSerializer.SerializeToElement(new { thread = new { id = threadId } }))
                : method == "thread/realtime/listVoices"
                    ? Task.FromResult(CompatibleVoices())
                    : throw new InvalidOperationException(method));
        await using var owner = new CodexDedicatedVoiceOwner(
            threadId,
            _ => Task.FromResult<ICodexAppServerClient>(client),
            "verse");

        var exception = await Assert.ThrowsAsync<CodexDedicatedVoiceCompatibilityException>(
            () => owner.StartAsync());

        Assert.Contains("verse", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VoiceTaskMessagingRequiresToolInventoryBeforeOwnerIsReady()
    {
        var threadId = Guid.NewGuid().ToString("D");
        var client = new FakeAppServerClient((method, _, _) =>
            method == "thread/resume"
                ? Task.FromResult(JsonSerializer.SerializeToElement(new { thread = new { id = threadId } }))
                : method == "mcpServerStatus/list"
                    ? Task.FromResult(JsonSerializer.SerializeToElement(new
                    {
                        data = new[]
                        {
                            new
                            {
                                name = "joydex_voice",
                                tools = new Dictionary<string, object>
                                {
                                    ["send_message_to_codex_task"] = new { },
                                },
                            },
                        },
                    }))
                    : method == "thread/realtime/listVoices"
                        ? Task.FromResult(CompatibleVoices())
                        : throw new InvalidOperationException(method));
        await using var owner = new CodexDedicatedVoiceOwner(
            threadId,
            _ => Task.FromResult<ICodexAppServerClient>(client),
            voiceToolsEnabled: true);

        await owner.StartAsync();

        Assert.True(owner.IsReady);
        Assert.Equal("mcpServerStatus/list", client.Requests[1].Method);
        Assert.Equal(threadId, client.Requests[1].Parameters.GetProperty("threadId").GetString());
        Assert.Equal("toolsAndAuthOnly", client.Requests[1].Parameters.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task VoiceTaskMessagingRejectsMissingToolInventory()
    {
        var threadId = Guid.NewGuid().ToString("D");
        var client = new FakeAppServerClient((method, _, _) =>
            method == "thread/resume"
                ? Task.FromResult(JsonSerializer.SerializeToElement(new { thread = new { id = threadId } }))
                : method == "mcpServerStatus/list"
                    ? Task.FromResult(JsonSerializer.SerializeToElement(new
                    {
                        data = Array.Empty<object>(),
                    }))
                    : throw new InvalidOperationException(method));
        await using var owner = new CodexDedicatedVoiceOwner(
            threadId,
            _ => Task.FromResult<ICodexAppServerClient>(client),
            voiceToolsEnabled: true);

        var exception = await Assert.ThrowsAsync<CodexDedicatedVoiceCompatibilityException>(
            () => owner.StartAsync());

        Assert.Contains("did not initialize", exception.Message, StringComparison.Ordinal);
        Assert.False(owner.IsReady);
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task ResumesWithConfiguredWorkspaceAndVerifiesReturnedDirectory()
    {
        var threadId = Guid.NewGuid().ToString("D");
        var workspace = Path.Combine(Path.GetTempPath(), "joydex-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var client = new FakeAppServerClient((method, _, _) =>
                method == "thread/resume"
                    ? Task.FromResult(JsonSerializer.SerializeToElement(new
                    {
                        thread = new { id = threadId, cwd = workspace },
                    }))
                    : method == "thread/realtime/listVoices"
                        ? Task.FromResult(CompatibleVoices())
                        : throw new InvalidOperationException(method));
            await using var owner = new CodexDedicatedVoiceOwner(
                threadId,
                _ => Task.FromResult<ICodexAppServerClient>(client),
                workspacePath: workspace);

            await owner.StartAsync();

            var resume = client.Requests[0].Parameters;
            Assert.Equal(Path.GetFullPath(workspace), resume.GetProperty("cwd").GetString());
            Assert.Equal(
                Path.GetFullPath(workspace),
                resume.GetProperty("runtimeWorkspaceRoots")[0].GetString());
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsWorkspaceMismatchBeforeOwnerBecomesReady()
    {
        var threadId = Guid.NewGuid().ToString("D");
        var root = Path.Combine(Path.GetTempPath(), "joydex-tests", Guid.NewGuid().ToString("N"));
        var configured = Path.Combine(root, "configured");
        var returned = Path.Combine(root, "returned");
        Directory.CreateDirectory(configured);
        Directory.CreateDirectory(returned);
        try
        {
            var client = new FakeAppServerClient((method, _, _) =>
                method == "thread/resume"
                    ? Task.FromResult(JsonSerializer.SerializeToElement(new
                    {
                        thread = new { id = threadId, cwd = returned },
                    }))
                    : throw new InvalidOperationException(method));
            await using var owner = new CodexDedicatedVoiceOwner(
                threadId,
                _ => Task.FromResult<ICodexAppServerClient>(client),
                workspacePath: configured);

            var exception = await Assert.ThrowsAsync<CodexDedicatedVoiceCompatibilityException>(
                () => owner.StartAsync());

            Assert.Contains(configured, exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(returned, exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(owner.IsReady);
            Assert.True(client.Disposed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static JsonElement CompatibleVoices() => JsonSerializer.SerializeToElement(new
    {
        voices = new
        {
            defaultV1 = "cove",
            defaultV2 = "alloy",
            v1 = new[] { "cove" },
            v2 = new[] { "alloy" },
        },
    });

    private sealed class FakeAppServerClient(
        Func<string, JsonElement, CancellationToken, Task<JsonElement>> request,
        Exception? startFailure = null) : ICodexAppServerClient
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<string, JsonElement>? NotificationReceived
        {
            add { }
            remove { }
        }

        public int? ProcessId => 1234;

        public Task Completion => _completion.Task;

        public bool Disposed { get; private set; }

        public List<(string Method, JsonElement Parameters)> Requests { get; } = [];

        public Task StartAsync(CancellationToken cancellationToken = default) =>
            startFailure is null ? Task.CompletedTask : Task.FromException(startFailure);

        public Task<JsonElement> RequestAsync(
            string method,
            object parameters,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var serialized = JsonSerializer.SerializeToElement(parameters);
            Requests.Add((method, serialized));
            return request(method, serialized, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _completion.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
