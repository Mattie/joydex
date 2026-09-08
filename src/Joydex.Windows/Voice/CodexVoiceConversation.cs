using System.Text.Json;

namespace Joydex.Windows.Voice;

public enum CodexVoiceConversationKind
{
    User,
    Assistant,
    Activity,
}

/// <summary>
/// One display-safe item from the Dedicated Voice Task. Tool arguments, command output, and
/// reasoning are deliberately excluded from this boundary.
/// </summary>
public sealed record CodexVoiceConversationEntry(
    string Id,
    DateTimeOffset Timestamp,
    CodexVoiceConversationKind Kind,
    string Text,
    string? RawText = null);

internal static class CodexVoiceConversationParser
{
    public static IReadOnlyList<CodexVoiceConversationEntry> ParseThreadRead(
        JsonElement result,
        string? expectedThreadId = null)
    {
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("thread", out var thread)
            || !thread.TryGetProperty("turns", out var turns)
            || turns.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("thread/read returned no task turns.");
        }
        if (expectedThreadId is not null
            && (!TryReadString(thread, "id", out var returnedThreadId)
                || !string.Equals(expectedThreadId, returnedThreadId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("thread/read returned a different task.");
        }

        var entries = new List<CodexVoiceConversationEntry>();
        foreach (var turn in turns.EnumerateArray())
        {
            if (!turn.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var startedAt = ReadTimestamp(turn, "startedAt") ?? DateTimeOffset.Now;
            foreach (var item in items.EnumerateArray())
            {
                if (!TryReadString(item, "type", out var type)
                    || !TryReadString(item, "id", out var id))
                {
                    continue;
                }

                switch (type)
                {
                    case "userMessage":
                        var userText = ReadUserText(item, out var rawUserText);
                        if (userText.Length > 0)
                        {
                            entries.Add(new CodexVoiceConversationEntry(
                                id,
                                startedAt,
                                CodexVoiceConversationKind.User,
                                userText,
                                rawUserText));
                        }
                        break;

                    case "agentMessage":
                        if (TryReadString(item, "text", out var assistantText)
                            && !string.IsNullOrWhiteSpace(assistantText))
                        {
                            entries.Add(new CodexVoiceConversationEntry(
                                id,
                                startedAt,
                                CodexVoiceConversationKind.Assistant,
                                assistantText.Trim()));
                        }
                        break;

                    case "commandExecution":
                        AddActivity(entries, Activity(id, startedAt, item, "Ran a command"));
                        break;

                    case "mcpToolCall":
                    case "dynamicToolCall":
                        AddActivity(entries, Activity(id, startedAt, item, "Used a tool"));
                        break;

                    case "webSearch":
                        AddActivity(entries, new CodexVoiceConversationEntry(
                            id,
                            startedAt,
                            CodexVoiceConversationKind.Activity,
                            "Searched the web"));
                        break;

                    case "fileChange":
                        AddActivity(entries, Activity(id, startedAt, item, "Changed files"));
                        break;

                    case "collabAgentToolCall":
                    case "subAgentActivity":
                        AddActivity(entries, new CodexVoiceConversationEntry(
                            id,
                            startedAt,
                            CodexVoiceConversationKind.Activity,
                            "Worked with another agent"));
                        break;

                    case "imageView":
                    case "imageGeneration":
                        AddActivity(entries, Activity(id, startedAt, item, "Worked with an image"));
                        break;
                }
            }
        }

        return entries;
    }

    private static void AddActivity(
        List<CodexVoiceConversationEntry> entries,
        CodexVoiceConversationEntry entry)
    {
        if (entries.LastOrDefault() is { Kind: CodexVoiceConversationKind.Activity } previous
            && string.Equals(previous.Text, entry.Text, StringComparison.Ordinal))
        {
            return;
        }

        entries.Add(entry);
    }

    private static CodexVoiceConversationEntry Activity(
        string id,
        DateTimeOffset timestamp,
        JsonElement item,
        string completedText)
    {
        var status = TryReadString(item, "status", out var value) ? value : string.Empty;
        var text = status switch
        {
            "inProgress" => completedText.Replace("Ran", "Running", StringComparison.Ordinal)
                .Replace("Used", "Using", StringComparison.Ordinal)
                .Replace("Changed", "Changing", StringComparison.Ordinal),
            "failed" => completedText + " (failed)",
            "declined" => completedText + " (declined)",
            _ => completedText,
        };
        return new CodexVoiceConversationEntry(id, timestamp, CodexVoiceConversationKind.Activity, text);
    }

    private static string ReadUserText(JsonElement item, out string? rawText)
    {
        rawText = null;
        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var text = string.Join(
                Environment.NewLine,
                content.EnumerateArray()
                    .Where(part => TryReadString(part, "type", out var type) && type == "text")
                    .Select(part => TryReadString(part, "text", out var text) ? text.Trim() : string.Empty)
                    .Where(text => text.Length > 0))
            .Trim();
        if (!TryExtractRealtimeInput(text, out var input))
        {
            return text;
        }

        rawText = text;
        return input;
    }

    private static bool TryExtractRealtimeInput(string text, out string input)
    {
        input = string.Empty;
        const string envelopeStart = "<realtime_delegation>";
        const string envelopeEnd = "</realtime_delegation>";
        const string inputStart = "<input>";
        const string inputEnd = "</input>";

        var candidate = text.Trim();
        if (!candidate.StartsWith(envelopeStart, StringComparison.Ordinal)
            || !candidate.EndsWith(envelopeEnd, StringComparison.Ordinal))
        {
            return false;
        }

        var valueStart = candidate.IndexOf(inputStart, envelopeStart.Length, StringComparison.Ordinal);
        if (valueStart < 0)
        {
            return false;
        }
        valueStart += inputStart.Length;
        var valueEnd = candidate.IndexOf(inputEnd, valueStart, StringComparison.Ordinal);
        if (valueEnd < 0)
        {
            return false;
        }

        input = System.Net.WebUtility.HtmlDecode(candidate[valueStart..valueEnd]).Trim();
        return input.Length > 0;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var timestamp) || !timestamp.TryGetInt64(out var unixSeconds))
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool TryReadString(JsonElement value, string property, out string result)
    {
        result = string.Empty;
        if (!value.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        result = element.GetString() ?? string.Empty;
        return true;
    }
}
