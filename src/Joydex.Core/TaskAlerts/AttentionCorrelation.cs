using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Joydex.Core.TaskAlerts;

/// <summary>
/// Creates a stable, privacy-preserving key for matching an attention request
/// with the successful tool completion that follows it.
/// </summary>
public static class AttentionCorrelation
{
    public static string? Create(
        string? sessionId,
        string? turnId,
        string? toolName,
        JsonElement toolInput)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || string.IsNullOrWhiteSpace(toolName)
            || toolInput.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(sessionId);
            writer.WriteStringValue(turnId ?? string.Empty);
            writer.WriteStringValue(toolName);
            WriteCanonical(
                writer,
                toolInput,
                omitDescription: string.Equals(toolName, "Bash", StringComparison.Ordinal));
            writer.WriteEndArray();
            writer.Flush();
        }

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(buffer.WrittenSpan, hash);
        return Convert.ToHexString(hash);
    }

    private static void WriteCanonical(
        Utf8JsonWriter writer,
        JsonElement element,
        bool omitDescription = false)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element
                             .EnumerateObject()
                             .Where(property => !omitDescription
                                 || !property.NameEquals("description"))
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(element), element.ValueKind, null);
        }
    }
}
