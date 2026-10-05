using System.Text.Json;
using System.Text.Json.Serialization;

namespace Joydex.Secrets;

/// <summary>Controls whether valid requests ask, auto-allow temporarily, or fail closed globally.</summary>
public enum SecretsOperatingMode
{
    Ask,
    AutoAllow24Hours,
    DenyAll,
}

/// <summary>A durable operating-mode view used by the broker, tray, and Settings page.</summary>
public sealed record SecretsOperatingModeSnapshot(
    long Epoch,
    SecretsOperatingMode Mode,
    DateTimeOffset ChangedAt,
    DateTimeOffset? ExpiresAt);

/// <summary>Stores the global Secrets operating mode without mixing it into scoped consent rules.</summary>
public sealed class SecretsOperatingModeStore
{
    public const int CurrentSchemaVersion = 1;
    private static readonly TimeSpan AutoAllowDuration = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };
    private readonly object _gate = new();
    private readonly string _path;

    public SecretsOperatingModeStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    /// <summary>Reads the current mode and durably returns an expired auto-allow mode to Ask.</summary>
    public SecretsOperatingModeSnapshot Read(DateTimeOffset now)
    {
        lock (_gate)
        {
            return SecretsFileLock.WithLock(_path, () => ReadLocked(now));
        }
    }

    /// <summary>Starts a fresh fixed 24-hour auto-allow window.</summary>
    public SecretsOperatingModeSnapshot AutoAllowFor24Hours(DateTimeOffset now) =>
        Set(SecretsOperatingMode.AutoAllow24Hours, now);

    /// <summary>Rejects every new or pending request until the local user changes this mode.</summary>
    public SecretsOperatingModeSnapshot DenyAll(DateTimeOffset now) =>
        Set(SecretsOperatingMode.DenyAll, now);

    /// <summary>Returns to ordinary remembered-decision evaluation and approval prompts.</summary>
    public SecretsOperatingModeSnapshot Ask(DateTimeOffset now) =>
        Set(SecretsOperatingMode.Ask, now);

    internal TResult WithSnapshotLock<TResult>(
        DateTimeOffset now,
        Func<SecretsOperatingModeSnapshot, TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            return SecretsFileLock.WithLock(_path, () => action(ReadLocked(now)));
        }
    }

    private SecretsOperatingModeSnapshot Set(SecretsOperatingMode mode, DateTimeOffset now)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        lock (_gate)
        {
            return SecretsFileLock.WithLock(_path, () =>
            {
                var current = Load(_path);
                var next = new SecretsOperatingModeSnapshot(
                    checked(current.Epoch + 1),
                    mode,
                    now,
                    mode == SecretsOperatingMode.AutoAllow24Hours ? now + AutoAllowDuration : null);
                Save(next);
                return next;
            });
        }
    }

    private SecretsOperatingModeSnapshot ReadLocked(DateTimeOffset now)
    {
        var current = Load(_path);
        if (current.Mode != SecretsOperatingMode.AutoAllow24Hours || current.ExpiresAt > now)
        {
            return current;
        }
        var expired = new SecretsOperatingModeSnapshot(
            checked(current.Epoch + 1),
            SecretsOperatingMode.Ask,
            now,
            null);
        Save(expired);
        return expired;
    }

    private void Save(SecretsOperatingModeSnapshot snapshot) => SecretsAtomicFile.WriteJson(
        _path,
        new OperatingModeDocument(
            CurrentSchemaVersion,
            snapshot.Epoch,
            snapshot.Mode,
            snapshot.ChangedAt,
            snapshot.ExpiresAt),
        JsonOptions);

    private static SecretsOperatingModeSnapshot Load(string path)
    {
        if (!File.Exists(path))
        {
            return new(1, SecretsOperatingMode.Ask, DateTimeOffset.UnixEpoch, null);
        }
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var document = JsonSerializer.Deserialize<OperatingModeDocument>(stream, JsonOptions)
                ?? throw new InvalidDataException("The Secrets operating mode is empty.");
            if (document.SchemaVersion != CurrentSchemaVersion
                || document.Epoch < 1
                || !Enum.IsDefined(document.Mode)
                || document.ChangedAt == default
                || document.Mode == SecretsOperatingMode.AutoAllow24Hours
                    && (document.ExpiresAt is null
                        || document.ExpiresAt <= document.ChangedAt
                        || document.ExpiresAt != document.ChangedAt + AutoAllowDuration)
                || document.Mode != SecretsOperatingMode.AutoAllow24Hours && document.ExpiresAt is not null)
            {
                throw new InvalidDataException("The Secrets operating mode is invalid.");
            }
            return new(document.Epoch, document.Mode, document.ChangedAt, document.ExpiresAt);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Secrets operating mode is malformed.", exception);
        }
    }

    private sealed record OperatingModeDocument(
        int SchemaVersion,
        long Epoch,
        SecretsOperatingMode Mode,
        DateTimeOffset ChangedAt,
        DateTimeOffset? ExpiresAt);
}
