using System.Runtime.InteropServices;
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
        string? turnId = null;
        try
        {
            var input = JsonSerializer.Deserialize(
                Console.OpenStandardInput(),
                HookJsonContext.Default.HookInput);
            eventName = input?.HookEventName;
            sessionId = input?.SessionId;
            turnId = input?.TurnId;
            if (!string.IsNullOrWhiteSpace(sessionId) && IsSupportedEvent(eventName))
            {
                var relayEvent = MapRelayEvent(eventName!, input?.ToolName);
                if (relayEvent is null)
                {
                    return 0;
                }

                TrySend(
                    GetPipeName(args),
                    relayEvent,
                    sessionId,
                    turnId,
                    AttentionCorrelation.Create(
                        sessionId,
                        turnId,
                        input?.ToolName,
                        input?.ToolInput ?? default),
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
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

    private static void TrySend(
        string pipeName,
        string eventName,
        string sessionId,
        string? turnId,
        string? attentionKey,
        long receivedAtUnixMs)
    {
        try
        {
            var handle = CreateFile(
                $"\\\\.\\pipe\\{pipeName}",
                genericWrite: 0x40000000,
                shareMode: 0,
                securityAttributes: IntPtr.Zero,
                creationDisposition: 3,
                flagsAndAttributes: 0,
                templateFile: IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return;
            }

            // CreateFile performs one immediate open. ERROR_FILE_NOT_FOUND and
            // ERROR_PIPE_BUSY both drop inside the 20 ms connection budget.
            using var pipe = new FileStream(handle, FileAccess.Write, bufferSize: 4096, isAsync: false);
            using var writer = new Utf8JsonWriter(pipe);
            writer.WriteStartObject();
            writer.WriteString("event", eventName);
            writer.WriteString("sessionId", sessionId);
            if (turnId is null)
            {
                writer.WriteNull("turnId");
            }
            else
            {
                writer.WriteString("turnId", turnId);
            }

            if (attentionKey is null)
            {
                writer.WriteNull("attentionKey");
            }
            else
            {
                writer.WriteString("attentionKey", attentionKey);
            }

            writer.WriteNumber("receivedAtUnixMs", receivedAtUnixMs);
            writer.WriteEndObject();
            writer.Flush();
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            // Joydex is optional. Do not retry when it is absent or busy.
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
    [property: JsonPropertyName("tool_name")] string? ToolName,
    [property: JsonPropertyName("tool_input")] JsonElement ToolInput);

[JsonSerializable(typeof(HookInput))]
internal sealed partial class HookJsonContext : JsonSerializerContext;
