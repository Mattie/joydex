using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Joydex.Core.TaskAlerts;

public enum TaskAlertBackend { LocalCodex, RemoteT3 }

public sealed record TaskAlertSourceIdentity(TaskAlertBackend Backend, string EnvironmentId)
{
    public static TaskAlertSourceIdentity LocalCodex { get; } = new(TaskAlertBackend.LocalCodex, "local");
}

/// <summary>Native IDs are ordinal. The opaque pool key preserves that distinction through legacy callers.</summary>
public sealed record TaskAlertIdentity(TaskAlertBackend Backend, string EnvironmentId, string NativeThreadId)
{
    // Native IDs cannot contain controls, so this namespace cannot collide with legacy Codex IDs.
    private const string Prefix = "\u001fjoydex2:";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    [JsonIgnore] public TaskAlertSourceIdentity Source => new(Backend, EnvironmentId);
    [JsonIgnore] public string PoolKey
    {
        get
        {
            Validate();
            return Backend == TaskAlertBackend.LocalCodex ? NativeThreadId
                : Prefix + Convert.ToHexString(StrictUtf8.GetBytes(EnvironmentId)) + ":"
                    + Convert.ToHexString(StrictUtf8.GetBytes(NativeThreadId));
        }
    }

    public static TaskAlertIdentity LocalCodex(string threadId) => new(TaskAlertBackend.LocalCodex, "local", threadId);
    public static TaskAlertIdentity RemoteT3(string environmentId, string threadId) => new(TaskAlertBackend.RemoteT3, environmentId, threadId);
    public static string CreateRequestKey(TaskAlertIdentity identity, string? runId, string nativeRequestId)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeRequestId);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[]
            { identity.PoolKey, runId, nativeRequestId })));
    }
    public static TaskAlertIdentity FromPoolKey(string key)
    {
        if (!key.StartsWith(Prefix, StringComparison.Ordinal)) return LocalCodex(key);
        var parts = key[Prefix.Length..].Split(':');
        if (parts.Length != 2) throw new InvalidDataException("Invalid task identity key.");
        try
        {
            var identity = RemoteT3(StrictUtf8.GetString(Convert.FromHexString(parts[0])),
                StrictUtf8.GetString(Convert.FromHexString(parts[1])));
            if (identity.PoolKey != key) throw new InvalidDataException("Noncanonical task identity key.");
            return identity;
        }
        catch (Exception error) when (error is FormatException or ArgumentException)
        {
            throw new InvalidDataException("Invalid task identity key.", error);
        }
    }

    public void Validate()
    {
        if (!Enum.IsDefined(Backend) || !IsNativeId(EnvironmentId) || !IsNativeId(NativeThreadId)
            || Backend == TaskAlertBackend.LocalCodex && EnvironmentId != "local")
            throw new InvalidDataException("Invalid task identity.");
    }

    internal static bool IsNativeId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl)) return false;
        try { StrictUtf8.GetByteCount(value); return true; }
        catch (EncoderFallbackException) { return false; }
    }
}

/// <summary>Display context is separate from persisted alert identity and lifecycle state.</summary>
public sealed record TaskAlertMetadata(string? Title = null, string? Project = null, string? Provider = null, string? Model = null);

public sealed record TaskAlertSourceRequest(string Key, string? RunId = null, DateTimeOffset? CreatedAt = null);

public sealed record TaskAlertSourceObservation(
    string NativeThreadId, string? RunId, TaskAlertState? State, DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt = null, DateTimeOffset? LatestHumanUserTurnAt = null,
    bool ReadComplete = true, bool RequestsComplete = false,
    TaskAlertSourceRequest[]? BlockingRequests = null, TaskAlertSourceRequest[]? AsyncQuestions = null,
    TaskAlertMetadata? Metadata = null, TaskAlertSourceRequest[]? ResolvedQuestions = null);

/// <summary>Absence from either a partial or complete catalog never ends an assignment.</summary>
public sealed record TaskAlertSourceSnapshot(TaskAlertSourceIdentity Source, long Generation, long Sequence,
    DateTimeOffset CapturedAt, bool IsComplete, TaskAlertSourceObservation[] Tasks,
    DateTimeOffset? CatalogObservedAt = null);

public sealed record TaskAlertSourceAcknowledgement(TaskAlertIdentity Identity, string? RunId, string? Key,
    DateTimeOffset AcknowledgedAt);
