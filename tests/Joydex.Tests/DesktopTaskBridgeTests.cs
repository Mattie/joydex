using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Joydex.Core.Voice;
using Joydex.Windows.Voice;

namespace Joydex.Tests;

public sealed class DesktopTaskBridgeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "joydex-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FramingRoundTripsAndRejectsOversizedFrames()
    {
        await using var stream = new MemoryStream();
        var payload = Encoding.UTF8.GetBytes("hello");
        await DesktopTaskBridgeFraming.WriteAsync(stream, payload, 32);
        stream.Position = 0;

        Assert.Equal(payload, await DesktopTaskBridgeFraming.ReadAsync(stream, 32));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DesktopTaskBridgeFraming.WriteAsync(new MemoryStream(), new byte[33], 32));
    }

    [Fact]
    public async Task ListFiltersCloudAndOwnedTaskAndOrdersRunningThenPinned()
    {
        var pipe = "Joydex.Tests." + Guid.NewGuid().ToString("N");
        var source = Guid.NewGuid().ToString("D");
        var owned = Guid.NewGuid().ToString("D");
        var running = Guid.NewGuid().ToString("D");
        var pinned = Guid.NewGuid().ToString("D");
        var ordinary = Guid.NewGuid().ToString("D");
        var archived = Guid.NewGuid().ToString("D");
        var body = JsonSerializer.Serialize(new
        {
            pinnedThreads = new[]
            {
                Thread(pinned, "local", "Pinned", "idle", 2),
            },
            threads = new[]
            {
                Thread(ordinary, "local", "Ordinary", "idle", 30),
                Thread(owned, "local", "Owned", "active", 40),
                Thread(running, "local", "Running", "inProgress", 1),
                Thread(archived, "local", "Archived", "archived", 60),
                Thread(Guid.NewGuid().ToString("D"), "cloud", "Cloud", "active", 50),
            },
        });
        var server = ServeOnceAsync(pipe, request =>
        {
            Assert.Equal(DesktopTaskBridgeProtocol.ListTasksMethod, request.Method);
            return JsonSerializer.SerializeToElement(new { content = body });
        });

        var catalog = await new DesktopTaskBridgeClient(pipe).ListTasksAsync(source, owned);
        await server;

        Assert.Equal([running, pinned, ordinary], catalog.Tasks.Select(task => task.Id));
    }

    [Fact]
    public async Task SendPreservesExactPromptAndNeverRequestsResume()
    {
        var pipe = "Joydex.Tests." + Guid.NewGuid().ToString("N");
        var source = Guid.NewGuid().ToString("D");
        var target = new DesktopTaskSummary(
            Guid.NewGuid().ToString("D"), "local", "Target", "active", null, null, 0);
        const string prompt = "Keep  spaces, punctuation — and <tags> exactly.";
        var server = ServeOnceAsync(pipe, request =>
        {
            Assert.Equal(DesktopTaskBridgeProtocol.SendMessageMethod, request.Method);
            Assert.Equal(target.Id, request.Arguments.GetProperty("threadId").GetString());
            Assert.Equal(target.HostId, request.Arguments.GetProperty("hostId").GetString());
            Assert.Equal(prompt, request.Arguments.GetProperty("prompt").GetString());
            Assert.DoesNotContain("resume", request.Method, StringComparison.OrdinalIgnoreCase);
            return JsonSerializer.SerializeToElement(new { content = "ok" });
        });

        var result = await new DesktopTaskBridgeClient(pipe).SendMessageAsync(source, target, prompt);
        await server;

        Assert.True(result.Queued);
    }

    [Fact]
    public void TargetResolverUsesExactPrefixThenSubstringAndRejectsAmbiguity()
    {
        var tasks = new[]
        {
            Summary("Alpha work"),
            Summary("Alpha notes"),
            Summary("Release Alpha"),
        };

        Assert.Equal("Alpha work", VoiceTaskTargetResolution.Resolve(tasks, "alpha work", null, null).Target?.Title);
        Assert.Equal(
            VoiceTaskTargetResolutionKind.Ambiguous,
            VoiceTaskTargetResolution.Resolve(tasks, "alpha", null, null).Kind);
        Assert.Equal("Release Alpha", VoiceTaskTargetResolution.Resolve(tasks, "release", null, null).Target?.Title);
    }

    [Fact]
    public void OutboxIsAtomicAndRequiresExplicitRemovalAfterRetry()
    {
        Directory.CreateDirectory(_root);
        var outbox = new VoiceTaskOutbox(_root);
        var target = Summary("Target");

        var draft = outbox.Hold(target, "do the thing", "session-1", "offline");
        var loaded = Assert.Single(outbox.Load());
        Assert.Equal("do the thing", loaded.Message);
        Assert.Equal(1, loaded.Attempts);

        var failed = outbox.RecordFailedAttempt(draft, "still offline");
        Assert.Equal(2, Assert.Single(outbox.Load()).Attempts);
        outbox.Retarget(failed, Summary("Other"));
        Assert.Equal("Other", Assert.Single(outbox.Load()).TargetTitle);
        outbox.Remove(draft.Id);
        Assert.Empty(outbox.Load());
        Assert.Empty(Directory.EnumerateFiles(outbox.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public void ManagedConfigurationPreservesUnrelatedContentAndRefusesUnmanagedConflict()
    {
        Directory.CreateDirectory(_root);
        var config = Path.Combine(_root, "config.toml");
        var host = Path.Combine(_root, "Joydex.DesktopBridgeHost.exe");
        var adapter = Path.Combine(_root, "codex-app-tools");
        Directory.CreateDirectory(adapter);
        File.WriteAllText(Path.Combine(adapter, "server.mjs"), string.Empty);
        File.WriteAllText(host, string.Empty);
        File.WriteAllText(config, "model = \"gpt-test\"\n");
        var manager = new DesktopBridgeConfigurationManager(config, adapter);

        manager.InstallOrRepair(host);
        var installed = File.ReadAllText(config);
        Assert.Contains("model = \"gpt-test\"", installed, StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.joydex_desktop_task_bridge]", installed, StringComparison.Ordinal);
        Assert.Contains("enabled = false", installed, StringComparison.Ordinal);
        Assert.Contains("disabled_tools = [\"joydex_desktop_bridge_status\"]", installed, StringComparison.Ordinal);
        Assert.True(File.Exists(config + ".joydex-desktop-bridge.backup"));
        Assert.Equal(DesktopBridgeConfigurationState.Installed, manager.Inspect(host).State);

        manager.Remove();
        Assert.DoesNotContain("joydex_desktop_task_bridge", File.ReadAllText(config), StringComparison.Ordinal);
        File.WriteAllText(config, "[mcp_servers.joydex_desktop_task_bridge]\ncommand='someone-else'\n");
        Assert.Equal(DesktopBridgeConfigurationState.Conflict, manager.Inspect(host).State);
        Assert.Throws<InvalidDataException>(() => manager.InstallOrRepair(host));
    }

    [Fact]
    public void AppServerAddsOnlyConstrainedVoiceMcpConfiguration()
    {
        Directory.CreateDirectory(_root);
        var host = Path.Combine(_root, "Joydex.DesktopBridgeHost.exe");
        var preferences = Path.Combine(_root, "voice-pe.json");
        File.WriteAllText(host, string.Empty);
        var source = Guid.NewGuid().ToString("D");

        var start = CodexAppServerClient.CreateStartInfo(
            @"C:\runtime\codex.exe",
            _root,
            new CodexVoiceToolConfiguration(host, preferences, source));
        var arguments = start.ArgumentList.ToArray();

        Assert.Contains(arguments, value => value.StartsWith("mcp_servers.joydex_voice.command=", StringComparison.Ordinal));
        Assert.Contains(arguments, value => value.StartsWith("mcp_servers.joydex_voice.args=", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, value => value.Contains("thread/resume", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, value => value.Contains("send_message_to_thread", StringComparison.Ordinal));
    }

    [Fact]
    public void DesktopLaunchHostAdvertisesNoModelCallableTools()
    {
        using var request = JsonDocument.Parse("{\"id\":7}");

        var response = JsonSerializer.SerializeToElement(
            DesktopBridgeProgram.BuildDesktopBridgeToolList(request.RootElement.GetProperty("id")));

        Assert.Empty(response.GetProperty("result").GetProperty("tools").EnumerateArray());
    }

    [Fact]
    public void ImmediateDuplicateVoiceDeliveryReusesConfirmedResultOnlyWithinSameSessionAndTarget()
    {
        var now = DateTimeOffset.UtcNow;
        var target = new DesktopTaskSummary(
            Guid.NewGuid().ToString("D"),
            "local",
            "Example Target Task",
            "idle",
            null,
            null,
            0);
        var otherTarget = target with { Id = Guid.NewGuid().ToString("D") };
        var deduplicator = new VoiceTaskDeliveryDeduplicator(TimeSpan.FromSeconds(15));

        Assert.False(deduplicator.TryGet("session-a", target, "Hello", now, out _));
        deduplicator.Record("session-a", target, "Hello", "delivered", now);

        Assert.True(deduplicator.TryGet("session-a", target, "Hello", now.AddSeconds(1), out var result));
        Assert.Equal("delivered", result);
        Assert.False(deduplicator.TryGet("session-b", target, "Hello", now.AddSeconds(1), out _));
        Assert.False(deduplicator.TryGet("session-a", otherTarget, "Hello", now.AddSeconds(1), out _));
        Assert.False(deduplicator.TryGet("session-a", target, "Hello", now.AddSeconds(16), out _));
    }

    [Fact]
    public async Task DesktopTransportHasOnlyOneOwnerAndCanBeReacquired()
    {
        var semaphoreName = @"Local\Joydex.DesktopTasks.Tests." + Guid.NewGuid().ToString("N");
        using (var first = Assert.IsType<DesktopTaskBridgeTransportLease>(
                   DesktopTaskBridgeTransportLease.TryAcquire(semaphoreName)))
        {
            Assert.Null(await Task.Run(() => DesktopTaskBridgeTransportLease.TryAcquire(semaphoreName)));
        }

        using var reacquired = DesktopTaskBridgeTransportLease.TryAcquire(semaphoreName);
        Assert.NotNull(reacquired);
    }

    [Fact]
    public void DesktopBrokerDescriptorCanBeRecoveredFromAppServerCommandLine()
    {
        const string pipe = @"\\.\pipe\codex-test-00000000-0000-4000-8000-000000000000";
        const string node = @"C:\Test\Codex\node.exe";
        const string adapter = @"C:\Test\Codex\resources\plugins\codex-app-tools";
        var descriptor = $"{{\"command\"=\"cmd.exe\",\"cwd\"={JsonSerializer.Serialize(adapter)},\"env\"={{"
            + $"\"CODEX_APP_TOOLS_PIPE_PATH\"={JsonSerializer.Serialize(pipe)},"
            + $"\"CODEX_MCP_NODE_PATH\"={JsonSerializer.Serialize(node)}}}}}";
        var commandLine = $"codex.exe -c features.code_mode_host=true app-server -c \"mcp_servers.codex_app={descriptor.Replace("\"", "\\\"")}\"";

        Assert.True(DesktopAppToolsEnvironmentResolver.TryExtractAppToolsEnvironment(
            commandLine,
            out var actualPipe,
            out var actualNode,
            out var actualAdapter));
        Assert.Equal(pipe, actualPipe);
        Assert.Equal(node, actualNode);
        Assert.Equal(adapter, actualAdapter);
    }

    [Theory]
    [InlineData("codex.exe app-server")]
    [InlineData("codex.exe -c \"mcp_servers.other={\\\"env\\\":{\\\"CODEX_APP_TOOLS_PIPE_PATH\\\":\\\"\\\\\\\\.\\\\pipe\\\\wrong\\\"}}\"")]
    [InlineData("codex.exe -c \"mcp_servers.codex_app={\\\"env\\\":{\\\"CODEX_APP_TOOLS_PIPE_PATH\\\":\\\"https://example.test\\\"}}\"")]
    public void DesktopBrokerDescriptorRejectsMissingOrUnrelatedConfiguration(string commandLine)
    {
        Assert.False(DesktopAppToolsEnvironmentResolver.TryExtractAppToolsEnvironment(
            commandLine,
            out _,
            out _,
            out _));
    }

    private static object Thread(string id, string hostId, string title, string status, long updatedAt) => new
    {
        id,
        kind = "codex",
        projectId = "project",
        hostId,
        status,
        cwd = @"C:\work",
        updatedAt,
        title,
    };

    private static DesktopTaskSummary Summary(string title) => new(
        Guid.NewGuid().ToString("D"), "local", title, "idle", null, null, 0);

    private static async Task ServeOnceAsync(
        string pipeName,
        Func<DesktopTaskBridgeRequest, JsonElement> response)
    {
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await server.WaitForConnectionAsync();
        var requestBytes = await DesktopTaskBridgeFraming.ReadAsync(
            server,
            DesktopTaskBridgeProtocol.MaximumFrameBytes);
        var request = JsonSerializer.Deserialize<DesktopTaskBridgeRequest>(
            requestBytes,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var result = response(request);
        var envelope = new DesktopTaskBridgeResponse(
            DesktopTaskBridgeProtocol.Version,
            request.Id,
            true,
            result);
        await DesktopTaskBridgeFraming.WriteAsync(
            server,
            JsonSerializer.SerializeToUtf8Bytes(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            DesktopTaskBridgeProtocol.MaximumFrameBytes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
