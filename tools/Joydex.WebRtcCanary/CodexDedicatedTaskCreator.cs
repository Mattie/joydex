using System.Text.Json;

namespace Joydex.WebRtcCanary;

/// <summary>
/// Creates one durable, no-turn Codex task reserved for the Joydex-owned App Server path, then
/// proves a fresh App Server can reacquire it after the creator releases its writer.
/// </summary>
internal sealed class CodexDedicatedTaskCreator
{
    private readonly string _codexPath;
    private readonly string _title;
    private readonly string _cwd;

    public CodexDedicatedTaskCreator(string codexPath, string title, string cwd)
    {
        _codexPath = codexPath;
        _title = title;
        _cwd = cwd;
    }

    public async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        string threadId;
        await using (var creator = CreateClient())
        {
            Console.WriteLine("DEDICATED-TASK-STARTING app-server");
            await creator.StartAsync(cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"DEDICATED-TASK-CHECKING title={_title}");
            await EnsureTitleIsUnusedAsync(creator, cancellationToken).ConfigureAwait(false);

            Console.WriteLine("DEDICATED-TASK-CREATING");
            var startResult = await creator.RequestAsync(
                "thread/start",
                new
                {
                    ephemeral = false,
                    cwd = _cwd,
                    approvalPolicy = "never",
                    sandbox = "read-only",
                },
                TimeSpan.FromMinutes(2),
                cancellationToken).ConfigureAwait(false);
            threadId = ReadThreadId(startResult);

            await creator.RequestAsync(
                "thread/name/set",
                new { threadId, name = _title },
                TimeSpan.FromMinutes(1),
                cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"DEDICATED-TASK-CREATED id={threadId} title={_title}");
        }

        await using (var verifier = CreateClient())
        {
            await verifier.StartAsync(cancellationToken).ConfigureAwait(false);
            var resumeResult = await verifier.RequestAsync(
                "thread/resume",
                new { threadId, excludeTurns = true },
                TimeSpan.FromMinutes(2),
                cancellationToken).ConfigureAwait(false);
            var resumedId = ReadThreadId(resumeResult);
            if (!string.Equals(threadId, resumedId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Reacquire returned task {resumedId} instead of newly created task {threadId}.");
            }

            Console.WriteLine($"DEDICATED-TASK-REACQUIRED id={threadId}");
        }

        Console.WriteLine($"DEDICATED-TASK-READY id={threadId} deepLink=codex://threads/{threadId}");
        return threadId;
    }

    private async Task EnsureTitleIsUnusedAsync(
        CodexAppServerClient client,
        CancellationToken cancellationToken)
    {
        var result = await client.RequestAsync(
            "thread/list",
            new
            {
                searchTerm = _title,
                archived = false,
                limit = 100,
            },
            TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);
        if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("thread/list returned no task data.");
        }

        var duplicate = data.EnumerateArray().Any(thread =>
            thread.TryGetProperty("name", out var name) &&
            name.ValueKind == JsonValueKind.String &&
            string.Equals(name.GetString(), _title, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
        {
            throw new InvalidOperationException(
                $"An active task named '{_title}' already exists. Choose a unique Dedicated Voice Task title.");
        }
    }

    private CodexAppServerClient CreateClient() => new(_codexPath, requestAttestation: false);

    private static string ReadThreadId(JsonElement result)
    {
        if (result.TryGetProperty("thread", out var thread) &&
            thread.TryGetProperty("id", out var idElement) &&
            idElement.GetString() is { Length: > 0 } id)
        {
            return id;
        }

        throw new InvalidOperationException("App Server returned no task id.");
    }
}
