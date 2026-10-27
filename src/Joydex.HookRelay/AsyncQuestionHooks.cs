using System.Text.Json;
using Joydex.Core.TaskAlerts;

/// <summary>Read the verified async-question acknowledgment and answer envelope without retaining content.</summary>
internal static class AsyncQuestionHooks
{
    private const string OpeningTag = "<send_user_message_question_reply>";
    private const string ClosingTag = "</send_user_message_question_reply>";

    internal static bool IsReplyEnvelope(string? prompt) => prompt?.Trim() is { } text
        && text.StartsWith(OpeningTag, StringComparison.Ordinal)
        && text.EndsWith(ClosingTag, StringComparison.Ordinal);

    internal static string? ReadTitle(JsonElement question) => question.ValueKind == JsonValueKind.Object
        && question.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String
        && title.GetString() is { } text ? text[..Math.Min(text.Length, 1024)] : null;

    internal static bool WasAccepted(JsonElement response)
    {
        if (response.ValueKind == JsonValueKind.String)
        {
            var text = response.GetString();
            if (text is null || text.Length > 4096) return false;
            try
            {
                using var parsed = JsonDocument.Parse(text);
                return WasAccepted(parsed.RootElement);
            }
            catch (JsonException) { return false; }
        }
        return response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("accepted", out var accepted) && accepted.ValueKind == JsonValueKind.True;
    }

    internal static IReadOnlyList<string> ReadAnswerKeys(string sessionId, string? prompt)
    {
        if (prompt is null || prompt.Length > 64 * 1024) return [];
        var text = prompt.Trim();
        if (!text.StartsWith(OpeningTag, StringComparison.Ordinal)
            || !text.EndsWith(ClosingTag, StringComparison.Ordinal)) return [];
        try
        {
            using var document = JsonDocument.Parse(text[OpeningTag.Length..^ClosingTag.Length]);
            var replies = document.RootElement;
            if (replies.ValueKind == JsonValueKind.Object)
                return AnswerKey(sessionId, replies) is { } key ? [key] : [];
            if (replies.ValueKind != JsonValueKind.Array || replies.GetArrayLength() > 32) return [];
            return replies.EnumerateArray().Select(reply => AnswerKey(sessionId, reply))
                .OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        }
        catch (JsonException) { return []; }
    }

    private static string? AnswerKey(string sessionId, JsonElement reply)
    {
        if (reply.ValueKind != JsonValueKind.Object
            || !reply.TryGetProperty("questionItemId", out var itemId) || itemId.ValueKind != JsonValueKind.String
            || !reply.TryGetProperty("question", out var question) || question.ValueKind != JsonValueKind.String
            || !reply.TryGetProperty("answer", out var answer) || answer.ValueKind != JsonValueKind.String
            || itemId.GetString() is not { Length: <= 1024 } text) return null;
        try
        {
            using var identifier = JsonDocument.Parse(text);
            var parts = identifier.RootElement;
            if (parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() != 3
                || parts[0].ValueKind != JsonValueKind.String || parts[0].GetString() != "request_user_input_async"
                || parts[1].ValueKind != JsonValueKind.String || parts[2].ValueKind != JsonValueKind.Number
                || !parts[2].TryGetInt32(out var index)) return null;
            return AttentionCorrelation.CreateAsyncQuestion(sessionId, parts[1].GetString(), index);
        }
        catch (JsonException) { return null; }
    }
}
