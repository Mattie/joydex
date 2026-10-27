using System.Runtime.InteropServices;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using System.Text.Json;
using System.Text.Json.Serialization;
using Joydex.Core.TaskAlerts;

return HookRelay.Run(args);

internal static partial class HookRelay
{
    internal const string DefaultPipeName = "Joydex.TaskAlerts.v1";
    public static int Run(string[] args)
    {
        string? eventName = null;
        string? sessionId = null;
        try
        {
            var input = JsonSerializer.Deserialize(
                Console.OpenStandardInput(),
                HookJsonContext.Default.HookInput);
            eventName = input?.HookEventName;
            sessionId = input?.SessionId;
            if (IsTrackableSession(input)
                && !string.IsNullOrWhiteSpace(sessionId)
                && IsSupportedEvent(eventName))
            {
                var events = BuildEvents(input!, DateTimeOffset.UtcNow).ToArray();
                if (events.Length > 0) TrySend(GetPipeName(args), events);
            }
        }
        catch
        {
            // Hooks must never hold up or fail a Codex lifecycle event.
        }

        if (string.Equals(eventName, "Stop", StringComparison.Ordinal))
        {
            Console.Out.Write("{}");
        }

        return 0;
    }

    internal static void TrySend(string pipeName, IReadOnlyList<TaskAlertEvent> events)
    {
        try
        {
            var deadline = Stopwatch.StartNew();
            SafeFileHandle handle;
            while (true)
            {
                handle = CreateFile(
                    $"\\\\.\\pipe\\{pipeName}",
                    genericWrite: 0x40000000,
                    shareMode: 0,
                    securityAttributes: IntPtr.Zero,
                    creationDisposition: 3,
                    flagsAndAttributes: 0,
                    templateFile: IntPtr.Zero);
                if (!handle.IsInvalid) break;
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                // Retry only before a connection exists. Never replay a possibly written hook.
                // This covers the receiver's next-instance gap without consuming the hook timeout.
                if (error is not (2 or 231) || deadline.ElapsedMilliseconds >= 250) return;
                Thread.Sleep(10);
            }

            using var pipe = new FileStream(handle, FileAccess.Write, bufferSize: 4096, isAsync: false);
            using var writer = new Utf8JsonWriter(pipe);
            // One hook uses one connection. Immediate, best-effort opens could otherwise drop
            // later questions while the receiver is preparing its next pipe instance.
            if (events.Count > 1) writer.WriteStartArray();
            foreach (var item in events)
            {
                writer.WriteStartObject();
                writer.WriteString("event", item.Event.ToString());
                writer.WriteString("sessionId", item.SessionId);
                writer.WriteString("turnId", item.TurnId);
                writer.WriteString("attentionKey", item.AttentionKey);
                writer.WriteString("workspace", item.Workspace);
                writer.WriteNumber("receivedAtUnixMs", item.ReceivedAt.ToUnixTimeMilliseconds());
                writer.WriteEndObject();
            }
            if (events.Count > 1) writer.WriteEndArray();
            writer.Flush();
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            // Joydex is optional; a failed write must not fail the Codex hook or be replayed.
        }
    }

    private static string GetPipeName(IReadOnlyList<string> args)
    {
        for (var index = 0; index + 1 < args.Count; index++)
        {
            if (string.Equals(args[index], "--pipe", StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }

        return DefaultPipeName;
    }

    private static bool IsSupportedEvent(string? value) => value is
        "UserPromptSubmit" or "PermissionRequest" or "PreToolUse" or "PostToolUse" or "Stop";

    internal static bool IsSubagent(HookInput? input) =>
        !string.IsNullOrWhiteSpace(input?.AgentId);

    internal static bool IsTrackableSession(HookInput? input) =>
        !IsSubagent(input)
        && !string.IsNullOrWhiteSpace(input?.TranscriptPath);

    /// <summary>Translate hook metadata into content-free lifecycle events for persistent top-level tasks.</summary>
    internal static IEnumerable<TaskAlertEvent> BuildEvents(HookInput input, DateTimeOffset receivedAt)
    {
        if (!IsTrackableSession(input) || string.IsNullOrWhiteSpace(input.SessionId)
            || !IsSupportedEvent(input.HookEventName)) yield break;

        TaskAlertEvent Create(CodexLifecycleEvent kind, string? key = null) => new(kind,
            input.SessionId, input.TurnId, receivedAt, key, TaskAlertSuppression.NormalizeWorkspace(input.Cwd));

        if (input.HookEventName == "PostToolUse" && input.ToolName == "request_user_input_async")
        {
            // PostToolUse acknowledges that the questions were sent, not that they were answered.
            // Failed or blocked calls never create attention that the user cannot resolve.
            if (!AsyncQuestionHooks.WasAccepted(input.ToolResponse)
                || input.ToolInput.ValueKind != JsonValueKind.Object
                || !input.ToolInput.TryGetProperty("questions", out var questions)
                || questions.ValueKind != JsonValueKind.Array
                || questions.GetArrayLength() is < 1 or > 32) yield break;
            for (var index = 0; index < questions.GetArrayLength(); index++)
            {
                var key = AttentionCorrelation.CreateAsyncQuestion(input.SessionId, input.ToolUseId, index);
                if (key is not null) yield return Create(CodexLifecycleEvent.AsyncUserInputRequest, key);
            }
            yield break;
        }

        var mapped = MapRelayEvent(input.HookEventName!, input.ToolName);
        if (mapped is null) yield break;
        yield return Create(Enum.Parse<CodexLifecycleEvent>(mapped), AttentionCorrelation.Create(
            input.SessionId, input.TurnId, input.ToolName, input.ToolInput));
        if (input.HookEventName == "UserPromptSubmit")
        {
            foreach (var key in AsyncQuestionHooks.ReadAnswerKeys(input.SessionId, input.Prompt))
                yield return Create(CodexLifecycleEvent.AsyncUserInputAnswered, key);
        }
    }

    private static string? MapRelayEvent(string eventName, string? toolName) => eventName switch
    {
        "PreToolUse" when string.Equals(toolName, "request_user_input", StringComparison.Ordinal) =>
            "UserInputRequest",
        "PreToolUse" => null,
        "PostToolUse" => "ToolCompleted",
        _ => eventName,
    };

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint genericWrite,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}

internal sealed record HookInput(
    [property: JsonPropertyName("hook_event_name")] string? HookEventName,
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("turn_id")] string? TurnId,
    [property: JsonPropertyName("agent_id")] string? AgentId,
    [property: JsonPropertyName("transcript_path")] string? TranscriptPath,
    [property: JsonPropertyName("tool_name")] string? ToolName,
    [property: JsonPropertyName("tool_input")] JsonElement ToolInput,
    [property: JsonPropertyName("cwd")] string? Cwd = null,
    [property: JsonPropertyName("tool_use_id")] string? ToolUseId = null,
    [property: JsonPropertyName("tool_response")] JsonElement ToolResponse = default,
    [property: JsonPropertyName("prompt")] string? Prompt = null);

[JsonSerializable(typeof(HookInput))]
internal sealed partial class HookJsonContext : JsonSerializerContext;
