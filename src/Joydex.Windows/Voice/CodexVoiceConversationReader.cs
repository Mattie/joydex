using Joydex.Core.Voice;

namespace Joydex.Windows.Voice;

/// <summary>
/// Reads one stored Codex conversation through a short-lived App Server without resuming the task.
/// </summary>
public sealed class CodexVoiceConversationReader
{
    private readonly string _threadId;
    private readonly Func<CancellationToken, Task<ICodexAppServerClient>> _createClient;

    public CodexVoiceConversationReader(
        string dedicatedTaskId,
        string appServerPath,
        string workspacePath,
        Action<string>? log = null)
        : this(
            dedicatedTaskId,
            async cancellationToken =>
            {
                var binary = await CodexAppServerRuntimeResolver
                    .ResolveAsync(appServerPath, cancellationToken)
                    .ConfigureAwait(false);
                log?.Invoke(
                    $"Room Voice selected {(binary.IsManagedRuntime ? "automatic" : "override")} "
                    + $"Codex App Server runtime '{binary.ExecutablePath}' for a conversation read.");
                return new CodexAppServerClient(binary, workspacePath, log);
            })
    {
    }

    internal CodexVoiceConversationReader(
        string dedicatedTaskId,
        Func<CancellationToken, Task<ICodexAppServerClient>> createClient)
    {
        if (!CodexTaskReference.TryParse(dedicatedTaskId, out _threadId))
        {
            throw new ArgumentException("A valid Dedicated Voice Task UUID is required.", nameof(dedicatedTaskId));
        }

        _createClient = createClient ?? throw new ArgumentNullException(nameof(createClient));
    }

    public async Task<IReadOnlyList<CodexVoiceConversationEntry>> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        await using var client = await _createClient(cancellationToken).ConfigureAwait(false);
        await client.StartAsync(cancellationToken).ConfigureAwait(false);
        var result = await client.RequestAsync(
                "thread/read",
                new
                {
                    threadId = _threadId,
                    includeTurns = true,
                },
                TimeSpan.FromSeconds(30),
                cancellationToken)
            .ConfigureAwait(false);
        return CodexVoiceConversationParser.ParseThreadRead(result, _threadId);
    }
}
