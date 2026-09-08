using System.Text.Json;
using Joydex.Core.Voice;

namespace Joydex.Windows.Voice;

internal interface ICodexRealtimeControl
{
    string ThreadId { get; }

    event Action<string, JsonElement>? NotificationReceived;

    Task Completion { get; }

    Task<JsonElement> RequestAsync(
        string method,
        object parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed class CodexDedicatedVoiceOwnershipException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public sealed class CodexDedicatedVoiceCompatibilityException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

/// <summary>
/// Acquires one Dedicated Voice Task through a private App Server and retains its writer for this
/// object's lifetime.
/// </summary>
public sealed class CodexDedicatedVoiceOwner : ICodexRealtimeControl, IAsyncDisposable
{
    private readonly string _threadId;
    private readonly string _workspacePath;
    private readonly string _realtimeVoice;
    private readonly bool _voiceToolsEnabled;
    private readonly Func<CancellationToken, Task<ICodexAppServerClient>> _createClient;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ICodexAppServerClient? _client;
    private int _ready;
    private int _disposed;

    public CodexDedicatedVoiceOwner(
        string dedicatedTaskId,
        string appServerPath,
        Action<string>? log = null)
        : this(dedicatedTaskId, appServerPath, string.Empty, string.Empty, log)
    {
    }

    public CodexDedicatedVoiceOwner(
        string dedicatedTaskId,
        string appServerPath,
        string realtimeVoice,
        Action<string>? log = null)
        : this(dedicatedTaskId, appServerPath, string.Empty, realtimeVoice, log)
    {
    }

    public CodexDedicatedVoiceOwner(
        string dedicatedTaskId,
        string appServerPath,
        string workspacePath,
        string realtimeVoice,
        Action<string>? log = null,
        CodexVoiceToolConfiguration? voiceTools = null)
        : this(
            dedicatedTaskId,
            async cancellationToken =>
            {
                var binary = await CodexAppServerBinaryPolicy
                    .VerifyAsync(appServerPath, cancellationToken)
                    .ConfigureAwait(false);
                return new CodexAppServerClient(binary, workspacePath, log, voiceTools);
            },
            realtimeVoice,
            workspacePath,
            voiceTools is not null)
    {
    }

    internal CodexDedicatedVoiceOwner(
        string dedicatedTaskId,
        Func<CancellationToken, Task<ICodexAppServerClient>> createClient,
        string realtimeVoice = "",
        string workspacePath = "",
        bool voiceToolsEnabled = false)
    {
        if (!CodexTaskReference.TryParse(dedicatedTaskId, out _threadId))
        {
            throw new ArgumentException("A valid Dedicated Voice Task UUID is required.", nameof(dedicatedTaskId));
        }

        _createClient = createClient ?? throw new ArgumentNullException(nameof(createClient));
        _realtimeVoice = realtimeVoice.Trim().ToLowerInvariant();
        _workspacePath = NormalizeWorkspacePath(workspacePath);
        _voiceToolsEnabled = voiceToolsEnabled;
    }

    public string ThreadId => _threadId;

    public bool IsReady => Volatile.Read(ref _ready) != 0;

    public Task Completion => _completion.Task;

    public event Action<string, JsonElement>? NotificationReceived;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsReady)
            {
                return;
            }

            if (_client is not null)
            {
                throw new InvalidOperationException("The Dedicated Voice Task owner could not be restarted.");
            }

            if (_workspacePath.Length > 0 && !Directory.Exists(_workspacePath))
            {
                throw new DirectoryNotFoundException(
                    $"The configured Voice Agent Workspace does not exist: {_workspacePath}");
            }

            var client = await _createClient(cancellationToken).ConfigureAwait(false);
            try
            {
                await client.StartAsync(cancellationToken).ConfigureAwait(false);
                JsonElement result;
                try
                {
                    var resumeParameters = new Dictionary<string, object?>
                    {
                        ["threadId"] = _threadId,
                        ["excludeTurns"] = true,
                        ["approvalPolicy"] = "never",
                        ["sandbox"] = "danger-full-access",
                    };
                    if (_workspacePath.Length > 0)
                    {
                        resumeParameters["cwd"] = _workspacePath;
                        resumeParameters["runtimeWorkspaceRoots"] = new[] { _workspacePath };
                    }

                    result = await client.RequestAsync(
                        "thread/resume",
                        resumeParameters,
                        TimeSpan.FromSeconds(30),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (CodexAppServerRpcException exception) when (
                    exception.Message.Contains("active writer", StringComparison.OrdinalIgnoreCase))
                {
                    throw new CodexDedicatedVoiceOwnershipException(
                        $"The Dedicated Voice Task {_threadId} already has an active writer.",
                        exception);
                }

                var resumedId = ReadThreadId(result);
                if (!string.Equals(_threadId, resumedId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Codex App Server resumed task {resumedId} instead of configured Dedicated Voice Task {_threadId}.");
                }
                ValidateWorkspace(result, _workspacePath);

                if (_voiceToolsEnabled)
                {
                    await ValidateVoiceToolInventoryAsync(client, cancellationToken).ConfigureAwait(false);
                }

                JsonElement voicesResult;
                try
                {
                    voicesResult = await client.RequestAsync(
                        "thread/realtime/listVoices",
                        new { },
                        TimeSpan.FromSeconds(30),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (CodexAppServerRpcException exception)
                {
                    throw new CodexDedicatedVoiceCompatibilityException(
                        "Codex App Server does not expose the required Realtime voice-list method.",
                        exception);
                }

                ValidateRealtimeVoices(voicesResult, _realtimeVoice);

                client.NotificationReceived += OnNotification;
                _client = client;
                Volatile.Write(ref _ready, 1);
                _ = ObserveClientCompletionAsync(client);
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    public CodexRealtimeSession CreateRealtimeSession()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!IsReady)
        {
            throw new InvalidOperationException("The Dedicated Voice Task owner is not ready.");
        }

        return new CodexRealtimeSession(this, _realtimeVoice, _voiceToolsEnabled);
    }

    /// <summary>
    /// Reads the complete visible conversation from the owned task without releasing its writer.
    /// </summary>
    public async Task<IReadOnlyList<CodexVoiceConversationEntry>> ReadThreadAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync(
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

    Task<JsonElement> ICodexRealtimeControl.RequestAsync(
        string method,
        object parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        RequestAsync(method, parameters, timeout, cancellationToken);

    internal Task<JsonElement> RequestAsync(
        string method,
        object parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var client = _client;
        if (!IsReady || client is null)
        {
            throw new InvalidOperationException("The Dedicated Voice Task owner is not ready.");
        }

        return client.RequestAsync(method, parameters, timeout, cancellationToken);
    }

    private void OnNotification(string method, JsonElement parameters)
    {
        var handlers = NotificationReceived;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<string, JsonElement> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(method, parameters);
            }
            catch (Exception)
            {
                // The App Server reader must remain alive if one session consumer fails.
            }
        }
    }

    private async Task ObserveClientCompletionAsync(ICodexAppServerClient client)
    {
        try
        {
            await client.Completion.ConfigureAwait(false);
            _completion.TrySetResult();
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
        }
        finally
        {
            Volatile.Write(ref _ready, 0);
        }
    }

    private static string ReadThreadId(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("thread", out var thread)
            && thread.TryGetProperty("id", out var idElement)
            && idElement.GetString() is { Length: > 0 } id)
        {
            return id;
        }

        throw new InvalidOperationException("thread/resume returned no task id.");
    }

    private static void ValidateWorkspace(JsonElement result, string expectedWorkspace)
    {
        if (expectedWorkspace.Length == 0)
        {
            return;
        }

        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("thread", out var thread)
            || !thread.TryGetProperty("cwd", out var cwdElement)
            || cwdElement.GetString() is not { Length: > 0 } returnedWorkspace)
        {
            throw new InvalidOperationException("thread/resume returned no working directory.");
        }

        if (!CodexVoiceWorkspaceService.PathsEqual(expectedWorkspace, returnedWorkspace))
        {
            throw new InvalidOperationException(
                $"Codex resumed the Dedicated Voice Task in '{returnedWorkspace}' instead of configured workspace '{expectedWorkspace}'.");
        }
    }

    private async Task ValidateVoiceToolInventoryAsync(
        ICodexAppServerClient client,
        CancellationToken cancellationToken)
    {
        JsonElement result;
        try
        {
            result = await client.RequestAsync(
                    "mcpServerStatus/list",
                    new
                    {
                        threadId = _threadId,
                        detail = "toolsAndAuthOnly",
                    },
                    TimeSpan.FromSeconds(15),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CodexAppServerRpcException exception)
        {
            throw new CodexDedicatedVoiceCompatibilityException(
                "Codex App Server could not inspect the Joydex voice task tool.",
                exception);
        }

        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("data", out var servers)
            || servers.ValueKind != JsonValueKind.Array
            || !servers.EnumerateArray().Any(server =>
                server.ValueKind == JsonValueKind.Object
                && server.TryGetProperty("name", out var name)
                && string.Equals(name.GetString(), "joydex_voice", StringComparison.Ordinal)
                && server.TryGetProperty("tools", out var tools)
                && tools.ValueKind == JsonValueKind.Object
                && tools.TryGetProperty("send_message_to_codex_task", out _)))
        {
            throw new CodexDedicatedVoiceCompatibilityException(
                "The Joydex voice task tool did not initialize, so Room Voice cannot safely claim Desktop message delivery.");
        }
    }

    private static string NormalizeWorkspacePath(string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return string.Empty;
        }
        if (!Path.IsPathFullyQualified(workspacePath.Trim()))
        {
            throw new ArgumentException("The Voice Agent Workspace path must be fully qualified.", nameof(workspacePath));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath.Trim()));
    }

    private static void ValidateRealtimeVoices(JsonElement result, string requestedVoice)
    {
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("voices", out var voices)
            || voices.ValueKind != JsonValueKind.Object
            // Joydex uses Realtime V3 (Frameless Bidi). Codex routes V3 through the
            // V1 session kind and voice catalog, even though its event protocol is V3.
            || !voices.TryGetProperty("defaultV1", out var defaultVoice)
            || defaultVoice.GetString() is not { Length: > 0 } defaultVoiceName
            || !voices.TryGetProperty("v1", out var v1Voices)
            || v1Voices.ValueKind != JsonValueKind.Array
            || !v1Voices.EnumerateArray().Any(voice =>
                string.Equals(voice.GetString(), defaultVoiceName, StringComparison.Ordinal)))
        {
            throw new CodexDedicatedVoiceCompatibilityException(
                "Codex App Server does not expose a compatible Realtime V3 voice capability.");
        }

        if (requestedVoice.Length > 0
            && !v1Voices.EnumerateArray().Any(voice =>
                string.Equals(voice.GetString(), requestedVoice, StringComparison.Ordinal)))
        {
            throw new CodexDedicatedVoiceCompatibilityException(
                $"Codex App Server does not expose the configured Realtime voice '{requestedVoice}'.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _startGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _ready, 0);
            var client = Interlocked.Exchange(ref _client, null);
            if (client is not null)
            {
                client.NotificationReceived -= OnNotification;
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _startGate.Release();
        }

        _completion.TrySetResult();
        _startGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
