using Joydex.Windows.Voice;

namespace Joydex.App;

internal sealed record RoomVoiceConversationEntry(
    string Id,
    DateTimeOffset Timestamp,
    CodexVoiceConversationKind Kind,
    string Text,
    bool IsPartial = false,
    string? RawText = null);

internal sealed record RoomVoiceConversationSnapshot(
    IReadOnlyList<RoomVoiceConversationEntry> Entries,
    VoicePeSessionState SessionState,
    bool OwnerReady,
    bool SessionActive,
    bool HistoryAvailable,
    bool Stale,
    string Status,
    string? Error,
    long ConversationVersion = 0);

/// <summary>
/// Keeps display-only Room Voice state in memory. Transcript text never crosses into Joydex logs
/// or persistent preferences.
/// </summary>
internal sealed class RoomVoiceConversationModel
{
    internal const int MaximumVisibleEntries = 100;

    private readonly object _sync = new();
    private readonly List<RoomVoiceConversationEntry> _entries = [];
    private bool _visibleConversationCleared;
    private VoicePeSessionState _sessionState = VoicePeSessionState.Armed;
    private bool _ownerReady;
    private bool _sessionActive;
    private bool _historyAvailable;
    private bool _stale;
    private string _status = "Starting Room Voice…";
    private string? _error;
    private long _liveSequence;
    private long _conversationVersion;

    public event EventHandler? Changed;
    public event EventHandler? RuntimeStateChanged;

    public RoomVoiceConversationSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return new RoomVoiceConversationSnapshot(
                [.. _entries],
                _sessionState,
                _ownerReady,
                _sessionActive,
                _historyAvailable,
                _stale,
                _status,
                _error,
                _conversationVersion);
        }
    }

    public void ReplaceHistory(IReadOnlyList<CodexVoiceConversationEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        lock (_sync)
        {
            if (!_visibleConversationCleared)
            {
                _entries.Clear();
                _entries.AddRange(entries
                    .OrderBy(entry => entry.Timestamp)
                    .TakeLast(MaximumVisibleEntries)
                    .Select(entry => new RoomVoiceConversationEntry(
                        entry.Id,
                        entry.Timestamp,
                        entry.Kind,
                        entry.Text,
                        RawText: entry.RawText)));
            }
            _historyAvailable = true;
            _stale = false;
            _error = null;
            _conversationVersion++;
        }
        RaiseChanged();
    }

    public void ClearVisibleConversation()
    {
        lock (_sync)
        {
            // Canonical items use their enclosing turn's timestamp, so a later refresh cannot
            // reliably separate entries seen before this clear from live entries added afterward.
            // Keep the post-clear live view authoritative for the remainder of this model's run.
            _visibleConversationCleared = true;
            _entries.Clear();
            _historyAvailable = true;
            _stale = false;
            _error = null;
            _conversationVersion++;
        }
        RaiseChanged();
    }

    public void UpdateLiveTranscript(CodexVoiceConversationKind kind, string text, bool final)
    {
        if (kind == CodexVoiceConversationKind.Activity)
        {
            return;
        }

        var normalized = text.Trim();
        lock (_sync)
        {
            var partialIndex = _entries.FindLastIndex(entry => entry.IsPartial && entry.Kind == kind);
            if (normalized.Length == 0)
            {
                if (final && partialIndex >= 0)
                {
                    _entries[partialIndex] = _entries[partialIndex] with { IsPartial = false };
                }
                else
                {
                    return;
                }
            }
            else if (partialIndex >= 0)
            {
                var current = _entries[partialIndex];
                _entries[partialIndex] = current with
                {
                    Text = final ? normalized : MergeTranscript(current.Text, normalized),
                    IsPartial = !final,
                };
            }
            else
            {
                _entries.Add(new RoomVoiceConversationEntry(
                    $"live-{Interlocked.Increment(ref _liveSequence)}",
                    DateTimeOffset.Now,
                    kind,
                    normalized,
                    IsPartial: !final));
            }
            TrimVisibleEntries();
            _conversationVersion++;
        }
        RaiseChanged();
    }

    public void AddActivity(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (_sync)
        {
            var normalized = text.Trim();
            if (_entries.LastOrDefault() is { Kind: CodexVoiceConversationKind.Activity } last
                && string.Equals(last.Text, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _entries.Add(new RoomVoiceConversationEntry(
                $"activity-{Interlocked.Increment(ref _liveSequence)}",
                DateTimeOffset.Now,
                CodexVoiceConversationKind.Activity,
                normalized));
            TrimVisibleEntries();
            _conversationVersion++;
        }
        RaiseChanged();
    }

    public void SetRuntimeState(
        VoicePeSessionState state,
        bool ownerReady,
        bool sessionActive,
        string status,
        string? error = null,
        bool stale = false)
    {
        lock (_sync)
        {
            _sessionState = state;
            _ownerReady = ownerReady;
            _sessionActive = sessionActive;
            _status = status;
            _error = error;
            _stale = stale;
        }
        RaiseChanged();
        RuntimeStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetFallbackState(bool enabled)
    {
        lock (_sync)
        {
            _ownerReady = false;
            _sessionActive = false;
            _historyAvailable = false;
            _sessionState = enabled ? VoicePeSessionState.Armed : VoicePeSessionState.Error;
            _status = enabled
                ? "LASTVOICE fallback is armed. Conversation history and direct session controls are unavailable."
                : "Room Voice is disabled.";
            _error = null;
            _stale = false;
        }
        RaiseChanged();
        RuntimeStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private void TrimVisibleEntries()
    {
        while (_entries.Count > MaximumVisibleEntries)
        {
            var removable = _entries.FindIndex(entry => !entry.IsPartial);
            _entries.RemoveAt(removable >= 0 ? removable : 0);
        }
    }

    internal static string MergeTranscript(string existing, string candidate)
    {
        existing = existing.Trim();
        candidate = candidate.Trim();
        if (existing.Length == 0 || candidate.StartsWith(existing, StringComparison.OrdinalIgnoreCase))
        {
            return candidate;
        }
        if (candidate.Length == 0 || existing.EndsWith(candidate, StringComparison.OrdinalIgnoreCase))
        {
            return existing;
        }

        return existing + " " + candidate;
    }
}
