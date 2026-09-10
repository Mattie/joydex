using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Joydex.Core.Voice;

public enum PebbleIndexDeliveryState
{
    Received,
    Sent,
    DeliveryUncertain,
}

public sealed record PebbleIndexDelivery(
    string Id,
    string Transcription,
    string RecordedAt,
    string Client,
    string Trigger,
    string TargetTaskId,
    string TargetHostId,
    string TargetTaskLabel,
    PebbleIndexDeliveryState State,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? CompletedAt = null,
    string Detail = "");

public sealed record PebbleIndexAcceptResult(PebbleIndexDelivery Delivery, bool IsDuplicate);

public sealed record PebbleIndexRecoverySummary(
    int OutstandingCount,
    PebbleIndexDelivery? LatestOutstanding);

public sealed class PebbleIndexDeliveryStore(string directory)
{
    public const int MaximumTranscriptLength = 4_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private readonly string _directory = Path.GetFullPath(directory);
    private readonly object _gate = new();

    public PebbleIndexAcceptResult Accept(
        string transcription,
        string recordedAt,
        string client,
        string trigger,
        string? deliveryId,
        PebbleIndexPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        transcription = transcription?.Trim() ?? string.Empty;
        if (transcription.Length is 0 or > MaximumTranscriptLength)
        {
            throw new InvalidDataException($"The transcription must contain 1 to {MaximumTranscriptLength} characters.");
        }
        if (!CodexTaskReference.TryParse(preferences.TargetTaskId, out var targetTaskId))
        {
            throw new InvalidDataException("The Pebble Index target task is unavailable.");
        }
        recordedAt = (recordedAt ?? string.Empty).Trim();
        client = (client ?? string.Empty).Trim();
        trigger = (trigger ?? string.Empty).Trim();
        if (!long.TryParse(recordedAt, out _))
        {
            throw new InvalidDataException("The recordedAt field must be a Unix-millisecond integer.");
        }
        if (client.Length is 0 or > 64 || client.Any(char.IsControl))
        {
            throw new InvalidDataException("The client field must contain 1 to 64 printable characters.");
        }
        var providedId = NormalizeDeliveryId(deliveryId);
        var id = providedId ?? ComputeId(recordedAt, client, trigger, transcription);
        var path = Path.Combine(_directory, id + ".json");
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            if (File.Exists(path))
            {
                var existing = Read(path);
                if (providedId is not null
                    && (!string.Equals(existing.Transcription, transcription, StringComparison.Ordinal)
                        || !string.Equals(existing.RecordedAt, recordedAt, StringComparison.Ordinal)
                        || !string.Equals(existing.Client, client, StringComparison.Ordinal)
                        || !string.Equals(existing.Trigger, trigger, StringComparison.Ordinal)))
                {
                    throw new InvalidDataException(
                        "The delivery ID was already used for a different Pebble Index payload.");
                }
                return new PebbleIndexAcceptResult(existing, true);
            }
            var delivery = new PebbleIndexDelivery(
                id, transcription, recordedAt, client, trigger,
                targetTaskId, preferences.TargetHostId, preferences.TargetTaskLabel,
                PebbleIndexDeliveryState.Received, DateTimeOffset.UtcNow);
            WriteNew(path, delivery);
            return new PebbleIndexAcceptResult(delivery, false);
        }
    }

    public PebbleIndexDelivery Update(string id, PebbleIndexDeliveryState state, string detail)
    {
        var path = Path.Combine(_directory, ValidateId(id) + ".json");
        lock (_gate)
        {
            var current = Read(path);
            var updated = current with
            {
                State = state,
                CompletedAt = state == PebbleIndexDeliveryState.Received ? null : DateTimeOffset.UtcNow,
                Detail = detail?.Trim() ?? string.Empty,
            };
            WriteReplace(path, updated);
            return updated;
        }
    }

    public IReadOnlyList<PebbleIndexDelivery> Recent(int limit = 20)
    {
        lock (_gate)
        {
            if (!Directory.Exists(_directory)) return [];
            return Directory.EnumerateFiles(_directory, "*.json")
                .Select(path => { try { return Read(path); } catch { return null; } })
                .Where(item => item is not null)
                .Cast<PebbleIndexDelivery>()
                .OrderByDescending(item => item.ReceivedAt)
                .Take(Math.Clamp(limit, 1, 100))
                .ToArray();
        }
    }

    public PebbleIndexRecoverySummary GetRecoverySummary()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_directory)) return new PebbleIndexRecoverySummary(0, null);
            var outstandingCount = 0;
            PebbleIndexDelivery? latestOutstanding = null;
            foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
            {
                PebbleIndexDelivery delivery;
                try { delivery = Read(path); }
                catch { continue; }
                if (delivery.State == PebbleIndexDeliveryState.Sent) continue;
                outstandingCount++;
                if (latestOutstanding is null || delivery.ReceivedAt > latestOutstanding.ReceivedAt)
                    latestOutstanding = delivery;
            }
            return new PebbleIndexRecoverySummary(outstandingCount, latestOutstanding);
        }
    }

    private static string ComputeId(params string[] parts)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts)));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string? NormalizeDeliveryId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return "provided-" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ValidateId(string id) =>
        id.Length is > 0 and <= 80 && id.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')
            ? id
            : throw new InvalidDataException("The delivery ID is invalid.");

    private static PebbleIndexDelivery Read(string path) =>
        JsonSerializer.Deserialize<PebbleIndexDelivery>(File.ReadAllBytes(path), JsonOptions)
        ?? throw new InvalidDataException("The Pebble Index delivery record was empty.");

    private static void WriteNew(string path, PebbleIndexDelivery value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.WriteByte((byte)'\n');
        stream.Flush(true);
    }

    private static void WriteReplace(string path, PebbleIndexDelivery value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteNew(temporary, value);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
