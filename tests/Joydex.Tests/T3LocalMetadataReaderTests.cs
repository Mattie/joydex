using System.Text.Json;
using Joydex.Core.T3;
using Joydex.Windows.T3;

namespace Joydex.Tests;

public sealed class T3LocalMetadataReaderTests
{
    [Fact]
    public async Task CancellationAfterGrantUsesIndependentRevocationAndEndpointMismatchIssuesNothing()
    {
        using var files = new Scratch(); using var cancellation = new CancellationTokenSource();
        var revoked = false;
        var reader = new T3LocalMetadataReader((_, arguments, token) =>
        {
            if (arguments[2] == "revoke") { Assert.False(token.IsCancellationRequested); revoked = true; return Task.FromResult("revoked"); }
            cancellation.Cancel();
            return Task.FromResult("{\"sessionId\":\"own\",\"token\":\"synthetic\",\"scopes\":[\"orchestration:read\"]}");
        }, (uri, _, token) => uri.AbsolutePath == "/.well-known/t3/environment"
            ? Task.FromResult(JsonDocument.Parse("{\"environmentId\":\"local-native\"}"))
            : Task.FromCanceled<JsonDocument>(token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(files.Home, "local-native", [], cancellation.Token));
        Assert.True(revoked);
        var issues = 0;
        var mismatch = new T3LocalMetadataReader((_, _, _) => { issues++; return Task.FromResult(""); },
            (_, _, _) => Task.FromResult(JsonDocument.Parse("{\"environmentId\":\"other\"}")));
        var error = await Assert.ThrowsAsync<T3TransportException>(() => mismatch.ReadAsync(files.Home, "local-native", [], default));
        Assert.Equal("EnvironmentMismatch", error.ErrorClass); Assert.Equal(0, issues);
    }

    [Fact]
    public async Task LocalReadUsesOwnExactlyScopedGrantAndRevokesBeforeReturningMetadata()
    {
        using var files = new Scratch();
        var calls = new List<string[]>();
        var reader = new T3LocalMetadataReader((_, arguments, _) =>
        {
            calls.Add(arguments);
            return Task.FromResult(arguments[2] == "issue" ? """
                {"sessionId":"owned-session","scopes":["orchestration:read"],"token":"synthetic-token"}
                """ : "revoked");
        }, (uri, token, _) =>
        {
            if (uri.AbsolutePath == "/.well-known/t3/environment")
            { Assert.Null(token); return Task.FromResult(JsonDocument.Parse("{\"environmentId\":\"local-native\"}")); }
            Assert.Equal("synthetic-token", token);
            return Task.FromResult(JsonDocument.Parse("""
                {"snapshotSequence":7,"threads":[{"id":"thread","latestRunId":"run","status":"completed",
                  "latestRunStartedAt":"2026-01-01T00:00:00Z","latestRunCompletedAt":"2026-01-01T00:01:00Z","title":"Task title"}],"projects":[]}
                """));
        });
        var result = await reader.ReadAsync(files.Home, "local-native", [], default);
        Assert.True(result.Success); Assert.True(result.IsComplete);
        Assert.Equal("completed", Assert.Single(result.Tasks).Status);
        Assert.Equal("Task title", result.Tasks[0].Title);
        Assert.Equal(2, calls.Count);
        Assert.Equal(new[] { "auth", "session", "issue", "--scope", "orchestration:read", "--ttl", "5m", "--label", "Joydex metadata reader", "--json" }, calls[0]);
        Assert.Equal(new[] { "auth", "session", "revoke", "owned-session" }, calls[1]);
        Assert.DoesNotContain("synthetic-token", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedScopeAndHttpFailureStillRevokeOwnGrant(bool unexpectedScopes)
    {
        using var files = new Scratch(); var revoked = false;
        var reader = new T3LocalMetadataReader((_, arguments, _) =>
        {
            if (arguments[2] == "revoke") { revoked = true; return Task.FromResult("revoked"); }
            return Task.FromResult("{\"sessionId\":\"own\",\"token\":\"synthetic\",\"scopes\":[\"" + (unexpectedScopes ? "orchestration:operate" : "orchestration:read") + "\"]}");
        }, (uri, _, _) => uri.AbsolutePath == "/.well-known/t3/environment"
            ? Task.FromResult(JsonDocument.Parse("{\"environmentId\":\"local-native\"}"))
            : throw new IOException("synthetic request failure"));
        await Assert.ThrowsAnyAsync<Exception>(() => reader.ReadAsync(files.Home, "local-native", [], default));
        Assert.True(revoked);
    }

    [Fact]
    public void FullProjectionRecoversSingleActiveRunAndMissingShellTitlesWithoutAnsweringRequests()
    {
        using var detail = JsonDocument.Parse("""
            {"projection":{"thread":{"id":"thread","title":"Recovered title","projectId":"p"},
              "runs":[{"id":"native-run","status":"running","startedAt":"2026-01-01T00:00:00Z","ordinal":2}],
              "nodes":[{"id":"n","runId":"native-run"}],"runtimeRequests":[
                {"id":"q","nodeId":"n","status":"pending","responseCapability":{"type":"message"}},
                {"id":"permission","nodeId":"n","status":"pending","responseCapability":{"type":"live"}}]}}
            """);
        using var shell = JsonDocument.Parse("{\"projects\":[{\"id\":\"p\",\"title\":\"Project\"}]}");
        var task = T3LocalMetadataReader.DetailTask(new() { Id = "thread", Status = "running" }, detail.RootElement, true, shell.RootElement);
        Assert.Equal("native-run", task.RunId); Assert.True(task.ReadComplete); Assert.True(task.RequestsComplete);
        Assert.Equal("Recovered title", task.Title); Assert.Equal("Project", task.Project);
        Assert.False(task.PendingRequests[0].Blocking); Assert.True(task.PendingRequests[1].Blocking);
        var omitted = T3LocalMetadataReader.DetailTask(new() { Id = "thread" }, detail.RootElement, false, shell.RootElement);
        Assert.Equal("Recovered title", omitted.Title); Assert.Equal("native-run", omitted.RunId);
    }

    [Fact]
    public async Task FollowingDesktopNeverWritesShortcuts()
    {
        var result = await new T3Reader().SetupShortcutsAsync(new() { FollowDesktop = true });
        Assert.False(result.Success); Assert.Equal("FollowDesktopReadOnly", result.ErrorClass);
    }

    private sealed class Scratch : IDisposable
    {
        public string Home { get; } = Path.Combine(Path.GetTempPath(), "joydex-t3-local-" + Guid.NewGuid().ToString("N"));
        public Scratch()
        {
            var root = Directory.CreateDirectory(Path.Combine(Home, "userdata")).FullName;
            File.WriteAllText(Path.Combine(root, "environment-id"), "local-native");
            File.WriteAllText(Path.Combine(root, "server-runtime.json"), "{\"origin\":\"http://127.0.0.1:3773\",\"startedAt\":\"2026-01-01T00:00:00Z\",\"pid\":1}");
        }
        public void Dispose() => Directory.Delete(Home, true);
    }
}
