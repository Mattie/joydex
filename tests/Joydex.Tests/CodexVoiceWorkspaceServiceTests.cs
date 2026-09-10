using System.Text.Json;
using Joydex.Windows.Voice;

namespace Joydex.Tests;

public sealed class CodexVoiceWorkspaceServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "joydex-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ListsEveryLocalRootFromMultiRootProjects()
    {
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");
        var client = new FakeClient((method, _, _) => method switch
        {
            "project/list" => Result(new
            {
                data = new[]
                {
                    new
                    {
                        id = "project-1",
                        name = "Project one",
                        roots = new[] { new { path = first }, new { path = second } },
                    },
                },
                nextCursor = (string?)null,
            }),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateService(client);

        var catalog = await service.ListProjectRootsAsync();

        Assert.Collection(
            catalog.Roots,
            root => Assert.Equal(Path.GetFullPath(first), root.RootPath),
            root => Assert.Equal(Path.GetFullPath(second), root.RootPath));
        Assert.True(client.Started);
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task ReusesMatchingProjectAndCreatesVerifiedNamedTask()
    {
        Directory.CreateDirectory(_root);
        var taskId = Guid.NewGuid().ToString("D");
        var client = new FakeClient((method, parameters, _) => method switch
        {
            "project/list" => Result(new
            {
                data = new[]
                {
                    new
                    {
                        id = "project-existing",
                        name = "Existing project",
                        roots = new[] { new { path = _root + Path.DirectorySeparatorChar } },
                    },
                },
                nextCursor = (string?)null,
            }),
            "thread/start" => Result(new { thread = new { id = taskId, cwd = _root } }),
            "thread/name/set" => Result(new { }),
            _ => throw new InvalidOperationException(method),
        });
        string? childWorkingDirectory = null;
        var service = new CodexVoiceWorkspaceService((workingDirectory, _) =>
        {
            childWorkingDirectory = workingDirectory;
            return Task.FromResult<ICodexAppServerClient>(client);
        });

        var result = await service.ProvisionAsync(new CodexVoiceWorkspaceProvisioningRequest(
            _root,
            RegisterProject: true,
            ProjectLabel: "Joydex Voice",
            TaskName: "Joydex Voice Chat — Owned (joydex_voice)"));

        Assert.Equal(Path.GetFullPath(_root), childWorkingDirectory);
        Assert.Equal("project-existing", result.ProjectId);
        Assert.Equal(taskId, result.TaskId);
        Assert.DoesNotContain(client.Requests, request => request.Method == "project/create");
        var start = Assert.Single(client.Requests, request => request.Method == "thread/start").Parameters;
        Assert.Equal(Path.GetFullPath(_root), start.GetProperty("cwd").GetString());
        Assert.Equal("project-existing", start.GetProperty("projectId").GetString());
        Assert.Equal("never", start.GetProperty("approvalPolicy").GetString());
        Assert.Equal("danger-full-access", start.GetProperty("sandbox").GetString());
        var name = Assert.Single(client.Requests, request => request.Method == "thread/name/set").Parameters;
        Assert.Equal("Joydex Voice Chat — Owned (joydex_voice)", name.GetProperty("name").GetString());
    }

    [Fact]
    public async Task ProjectFailureFallsBackToFolderOnlyTask()
    {
        Directory.CreateDirectory(_root);
        var taskId = Guid.NewGuid().ToString("D");
        var client = new FakeClient((method, _, _) => method switch
        {
            "project/list" => Result(new { data = Array.Empty<object>(), nextCursor = (string?)null }),
            "project/create" => Task.FromException<JsonElement>(
                new CodexAppServerRpcException(-32601, "project/create unavailable")),
            "thread/start" => Result(new { thread = new { id = taskId, cwd = _root } }),
            "thread/name/set" => Result(new { }),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateService(client);

        var result = await service.ProvisionAsync(new CodexVoiceWorkspaceProvisioningRequest(
            _root,
            RegisterProject: true,
            ProjectLabel: "Joydex Voice"));

        Assert.Empty(result.ProjectId);
        Assert.Contains("folder-only", result.Warning, StringComparison.OrdinalIgnoreCase);
        var start = Assert.Single(client.Requests, request => request.Method == "thread/start").Parameters;
        Assert.Equal(JsonValueKind.Null, start.GetProperty("projectId").ValueKind);
    }

    private static CodexVoiceWorkspaceService CreateService(FakeClient client) => new(
        (_, _) => Task.FromResult<ICodexAppServerClient>(client));

    private static Task<JsonElement> Result(object value) =>
        Task.FromResult(JsonSerializer.SerializeToElement(value));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeClient(
        Func<string, JsonElement, CancellationToken, Task<JsonElement>> request) : ICodexAppServerClient
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<string, JsonElement>? NotificationReceived
        {
            add { }
            remove { }
        }

        public int? ProcessId => 1;
        public Task Completion => _completion.Task;
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public List<(string Method, JsonElement Parameters)> Requests { get; } = [];

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task<JsonElement> RequestAsync(
            string method,
            object parameters,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var json = JsonSerializer.SerializeToElement(parameters);
            Requests.Add((method, json));
            return request(method, json, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _completion.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
