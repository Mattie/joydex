using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Joydex.Core.Voice;

public sealed record VoiceTaskOutboxDraft(
    string Id,
    string TargetTaskId,
    string TargetHostId,
    string TargetTitle,
    string Message,
    string SourceSession,
    DateTimeOffset CreatedAt,
    int Attempts,
    string LatestError);

public sealed class VoiceTaskOutbox
{
    private const int MaximumDraftBytes = 128 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private readonly string _directory;

    public VoiceTaskOutbox(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        if (!Path.IsPathFullyQualified(workspacePath))
        {
            throw new ArgumentException("The Voice Agent Workspace must be fully qualified.", nameof(workspacePath));
        }
        _directory = Path.Combine(Path.GetFullPath(workspacePath), ".joydex", "voice-outbox");
    }

    public string DirectoryPath => _directory;

    public IReadOnlyList<VoiceTaskOutboxDraft> Load()
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        var drafts = new List<VoiceTaskOutboxDraft>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaximumDraftBytes)
            {
                continue;
            }
            try
            {
                var draft = JsonSerializer.Deserialize<VoiceTaskOutboxDraft>(File.ReadAllBytes(path), JsonOptions);
                if (draft is not null && IsValidId(draft.Id))
                {
                    drafts.Add(draft);
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                // One damaged draft must not hide the rest of the review queue.
            }
        }
        return drafts.OrderBy(draft => draft.CreatedAt).ToArray();
    }

    public VoiceTaskOutboxDraft Hold(
        DesktopTaskSummary target,
        string message,
        string sourceSession,
        string error)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var draft = new VoiceTaskOutboxDraft(
            Guid.NewGuid().ToString("N"),
            target.Id,
            target.HostId,
            target.Title,
            message,
            sourceSession.Trim(),
            DateTimeOffset.UtcNow,
            Attempts: 1,
            LatestError: error.Trim());
        Save(draft);
        return draft;
    }

    public VoiceTaskOutboxDraft RecordFailedAttempt(VoiceTaskOutboxDraft draft, string error)
    {
        var updated = draft with { Attempts = checked(draft.Attempts + 1), LatestError = error.Trim() };
        Save(updated);
        return updated;
    }

    public VoiceTaskOutboxDraft Retarget(VoiceTaskOutboxDraft draft, DesktopTaskSummary target)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(target);
        var updated = draft with
        {
            TargetTaskId = target.Id,
            TargetHostId = target.HostId,
            TargetTitle = target.Title,
            LatestError = string.Empty,
        };
        Save(updated);
        return updated;
    }

    public void Remove(string id)
    {
        if (!IsValidId(id))
        {
            throw new ArgumentException("A valid outbox draft ID is required.", nameof(id));
        }
        var path = Path.Combine(_directory, id + ".json");
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private void Save(VoiceTaskOutboxDraft draft)
    {
        if (!IsValidId(draft.Id))
        {
            throw new InvalidDataException("The outbox draft ID is invalid.");
        }
        if (!CodexTaskReference.TryParse(draft.TargetTaskId, out _)
            || string.IsNullOrWhiteSpace(draft.TargetHostId)
            || string.IsNullOrWhiteSpace(draft.Message)
            || draft.Message.Length > DesktopTaskBridgeClientLimits.MaximumMessageLength)
        {
            throw new InvalidDataException("The outbox draft is invalid.");
        }

        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, draft.Id + ".json");
        var temporary = Path.Combine(_directory, $".{draft.Id}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(draft, JsonOptions);
            if (bytes.Length > MaximumDraftBytes)
            {
                throw new InvalidDataException("The outbox draft is too large.");
            }
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.WriteByte((byte)'\n');
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool IsValidId(string? id) => Guid.TryParseExact(id, "N", out _);
}

public static class DesktopTaskBridgeClientLimits
{
    public const int MaximumMessageLength = 32 * 1024;
}
