using System.Text.Json;
using Joydex.Windows.Voice;

namespace Joydex.Tests;

public sealed class CodexVoiceConversationReaderTests
{
    [Fact]
    public async Task ReadsStoredThreadWithoutResumingItAndClosesTheClient()
    {
        var threadId = Guid.NewGuid().ToString("D");
        var client = new FakeAppServerClient((method, parameters, _) =>
        {
            Assert.Equal("thread/read", method);
            Assert.Equal(threadId, parameters.GetProperty("threadId").GetString());
            Assert.True(parameters.GetProperty("includeTurns").GetBoolean());
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                thread = new
                {
                    id = threadId,
                    turns = new[]
                    {
                        new
                        {
                            startedAt = 1_700_000_000L,
                            items = new[]
                            {
                                new { id = "user-1", type = "userMessage", content = new[] { new { type = "text", text = "Denver" } } },
                            },
                        },
                    },
                },
            }));
        });
        var reader = new CodexVoiceConversationReader(
            threadId,
            _ => Task.FromResult<ICodexAppServerClient>(client));

        var entries = await reader.ReadAsync();

        Assert.True(client.Started);
        Assert.True(client.Disposed);
        Assert.Equal("Denver", Assert.Single(entries).Text);
        Assert.DoesNotContain(client.Requests, request => request.Method == "thread/resume");
    }

    [Fact]
    public async Task ClosesTheClientWhenStoredThreadReadFails()
    {
        var client = new FakeAppServerClient((_, _, _) =>
            Task.FromException<JsonElement>(new InvalidOperationException("read failed")));
        var reader = new CodexVoiceConversationReader(
            Guid.NewGuid().ToString("D"),
            _ => Task.FromResult<ICodexAppServerClient>(client));

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync());

        Assert.True(client.Disposed);
    }

    private sealed class FakeAppServerClient(
        Func<string, JsonElement, CancellationToken, Task<JsonElement>> request) : ICodexAppServerClient
    {
        public event Action<string, JsonElement>? NotificationReceived
        {
            add { }
            remove { }
        }

        public int? ProcessId => 1234;

        public Task Completion => Task.CompletedTask;

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
            var serialized = JsonSerializer.SerializeToElement(parameters);
            Requests.Add((method, serialized));
            return request(method, serialized, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
