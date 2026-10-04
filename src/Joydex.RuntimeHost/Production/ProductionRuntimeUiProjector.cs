using System.Text.Json;
using Joydex.App;
using Joydex.Contracts;
using Joydex.Core.Config;
using Joydex.Core.TaskAlerts;
using Joydex.Core.Voice;
using Joydex.Windows.TaskAlerts;

namespace Joydex.RuntimeHost.Production;

/// <summary>Keeps one bounded, reconnect-safe projection of runtime-owned presentation state.</summary>
internal sealed class ProductionRuntimeUiProjector
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly Dictionary<string, RuntimeButtonMapVisibility> _buttonMaps =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RuntimeControllerStatus> _controllers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<RuntimeActionActivity> _activity = new();
    private RuntimePromptPickerSnapshot? _promptPicker;
    private RuntimeTaskAlertSnapshot? _taskAlerts;
    private RuntimeVoiceUiSnapshot? _voice;
    private VoicePePreferences? _activeVoicePreferences;
    private IReadOnlyList<DesktopTaskSummary> _desktopTasks = [];
    private bool _desktopBridgeAvailable;
    private RuntimePebbleIndexSnapshot? _pebbleIndex;
    private long _activitySequence;

    public event EventHandler<RuntimeUiEvent>? Changed;

    public RuntimeUiSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var snapshot = new RuntimeUiSnapshot(
                _promptPicker,
                _buttonMaps.Values.OrderBy(item => item.DeviceId, StringComparer.OrdinalIgnoreCase).ToArray(),
                _controllers.Values.OrderBy(item => item.DeviceId, StringComparer.OrdinalIgnoreCase).ToArray(),
                _activity.ToArray(),
                _taskAlerts,
                _voice,
                _pebbleIndex);
            return FitSnapshot(snapshot);
        }
    }

    /// <summary>Drops retired owner state and asks connected clients to replace their snapshot.</summary>
    public void ResetCompanion()
    {
        lock (_gate)
        {
            _controllers.Clear();
            _buttonMaps.Clear();
            _promptPicker = null;
        }
        Raise(new RuntimeUiEvent(RuntimeEventKind.UiResynchronizationRequired));
    }

    /// <summary>Removes the disabled Voice owner from retained state and connected clients.</summary>
    public void ClearVoice()
    {
        lock (_gate)
        {
            _voice = null;
        }
        Raise(new RuntimeUiEvent(RuntimeEventKind.UiResynchronizationRequired));
    }

    public void PublishPromptPicker(PromptPickerSnapshot snapshot)
    {
        var projected = new RuntimePromptPickerSnapshot(
            snapshot.Visible,
            Limit(snapshot.PickerId),
            snapshot.SelectedIndex);
        lock (_gate)
        {
            _promptPicker = projected;
        }
        Raise(new RuntimeUiEvent(RuntimeEventKind.PromptPickerChanged, promptPicker: projected));
    }

    public void PublishButtonMap(string deviceId, bool visible)
    {
        var projected = new RuntimeButtonMapVisibility(Limit(deviceId), visible);
        lock (_gate)
        {
            _buttonMaps[projected.DeviceId] = projected;
        }
        Raise(new RuntimeUiEvent(
            RuntimeEventKind.ButtonMapVisibilityChanged,
            buttonMapVisibility: projected));
    }

    public void PublishController(string deviceId, string displayName, string status, bool hasButtonMap)
    {
        var projected = new RuntimeControllerStatus(
            Limit(deviceId),
            Limit(displayName),
            Limit(status),
            hasButtonMap);
        lock (_gate)
        {
            _controllers[projected.DeviceId] = projected;
        }
        Raise(new RuntimeUiEvent(RuntimeEventKind.ControllerStatusChanged, controllerStatus: projected));
    }

    public void PublishActivity(string message)
    {
        var projected = new RuntimeActionActivity(
            Interlocked.Increment(ref _activitySequence),
            DateTimeOffset.Now,
            Limit(message, RuntimeUiLimits.MaximumActionActivityCharacters));
        lock (_gate)
        {
            _activity.Enqueue(projected);
            while (_activity.Count > RuntimeUiLimits.MaximumActionActivityEntries)
            {
                _activity.Dequeue();
            }
        }
        Raise(new RuntimeUiEvent(RuntimeEventKind.ActionActivityAdded, actionActivity: projected));
    }

    public void PublishTaskAlerts(TaskAlertSnapshot snapshot, RuntimeTaskAlertHookStatus hooks)
    {
        var projected = new RuntimeTaskAlertSnapshot(
            snapshot.Enabled,
            snapshot.Assignments.ToArray(),
            snapshot.DroppedEventCount,
            snapshot.Bank,
            snapshot.BankAutomaticallyDetected,
            (snapshot.RecentEvents ?? []).TakeLast(RuntimeUiLimits.MaximumTaskAlertEvents)
                .Select(item => new RuntimeTaskAlertEventTrace(
                    item.ReceivedAt,
                    item.Event,
                    item.SessionId,
                    item.TurnId,
                    item.Slot,
                    item.State,
                    item.Result switch
                    {
                        TaskAlertEventResult.Assigned => RuntimeTaskAlertEventResult.Assigned,
                        TaskAlertEventResult.Updated => RuntimeTaskAlertEventResult.Updated,
                        TaskAlertEventResult.StopGrace => RuntimeTaskAlertEventResult.StopGrace,
                        TaskAlertEventResult.Dropped => RuntimeTaskAlertEventResult.Dropped,
                        TaskAlertEventResult.Ignored => RuntimeTaskAlertEventResult.Ignored,
                        TaskAlertEventResult.Suppressed => RuntimeTaskAlertEventResult.Suppressed,
                        _ => throw new ArgumentOutOfRangeException(nameof(item.Result)),
                    },
                    item.Workspace))
                .ToArray(),
            (snapshot.Suppressions ?? []).ToArray(),
            snapshot.EffectiveLedOutput,
            hooks);
        lock (_gate)
        {
            _taskAlerts = projected;
        }
        Raise(new RuntimeUiEvent(RuntimeEventKind.TaskAlertsChanged, taskAlerts: projected));
    }

    public void SetActiveVoicePreferences(VoicePePreferences? preferences)
    {
        lock (_gate)
        {
            _activeVoicePreferences = preferences?.Normalize();
        }
    }

    public VoicePePreferences? GetActiveVoicePreferences()
    {
        lock (_gate)
        {
            return _activeVoicePreferences;
        }
    }

    public void PublishVoice(
        ProductionVoiceState state,
        bool timelineReset = false)
    {
        var timeline = ProjectTimeline(state.Timeline, out var timelineTruncated);
        VoicePePreferences normalized;
        IReadOnlyList<DesktopTaskSummary> desktopTasks;
        bool desktopBridgeAvailable;
        RuntimeVoiceUiSnapshot? previous;
        lock (_gate)
        {
            normalized = _activeVoicePreferences
                ?? throw new InvalidOperationException("The active Room Voice preferences are unavailable.");
            desktopTasks = _desktopTasks;
            desktopBridgeAvailable = _desktopBridgeAvailable;
            previous = _voice;
        }
        var messaging = ProjectMessaging(
            normalized,
            desktopTasks,
            desktopBridgeAvailable);
        timelineReset |= previous is not null && !IsIncremental(previous.Timeline, timeline);
        var projected = new RuntimeVoiceUiSnapshot(
            ProjectVoiceSession(state.Snapshot),
            timeline,
            messaging,
            timelineTruncated);
        lock (_gate)
        {
            _voice = projected;
        }
        Raise(new RuntimeUiEvent(
            RuntimeEventKind.VoiceChanged,
            voice: new RuntimeVoiceEvent(
                projected.Session,
                timelineReset ? null : timeline.LastOrDefault(),
                projected.Messaging,
                timelineReset)));
    }

    public void PublishDesktopTasks(IReadOnlyList<DesktopTaskSummary> tasks)
    {
        RuntimeVoiceUiSnapshot? voice;
        lock (_gate)
        {
            _desktopTasks = tasks.ToArray();
            _desktopBridgeAvailable = true;
            voice = _voice is null ? null : _voice with
            {
                Messaging = ProjectMessaging(
                    _activeVoicePreferences
                    ?? throw new InvalidOperationException("The active Room Voice preferences are unavailable."),
                    tasks,
                    bridgeAvailable: true),
            };
            _voice = voice;
        }
        if (voice is null)
        {
            return;
        }
        Raise(new RuntimeUiEvent(
            RuntimeEventKind.VoiceChanged,
            voice: new RuntimeVoiceEvent(voice.Session, Messaging: voice.Messaging)));
    }

    public void RefreshVoiceMessaging(VoicePePreferences preferences)
    {
        var normalized = preferences.Normalize();
        RuntimeVoiceUiSnapshot? voice;
        lock (_gate)
        {
            _activeVoicePreferences = normalized;
            voice = _voice is null ? null : _voice with
            {
                Messaging = ProjectMessaging(
                    normalized,
                    _desktopTasks,
                    _desktopBridgeAvailable),
            };
            _voice = voice;
        }
        if (voice is not null)
        {
            Raise(new RuntimeUiEvent(
                RuntimeEventKind.VoiceChanged,
                voice: new RuntimeVoiceEvent(voice.Session, Messaging: voice.Messaging)));
        }
    }

    public void PublishPebble(PebbleIndexReceiverStatus status, string inboxPath)
    {
        var projected = new RuntimePebbleIndexSnapshot(
            status.Running,
            Limit(status.Message),
            status.OutstandingCount,
            status.Latest is null
                ? null
                : new RuntimePebbleIndexDeliveryStatus(
                    status.Latest.Id,
                    status.Latest.State,
                    Limit(status.Latest.Detail)),
            inboxPath);
        lock (_gate)
        {
            _pebbleIndex = projected;
        }
        Raise(new RuntimeUiEvent(RuntimeEventKind.PebbleIndexChanged, pebbleIndex: projected));
    }

    private static RuntimeVoiceSnapshot ProjectVoiceSession(RuntimeVoiceSnapshot snapshot) => snapshot with
    {
        Status = Limit(snapshot.Status),
        Error = snapshot.Error is null ? null : Limit(snapshot.Error),
    };

    private static RuntimeVoiceTimelineEntry[] ProjectTimeline(
        IReadOnlyList<RuntimeVoiceTimelineEntry> entries,
        out bool truncated)
    {
        var wasTruncated = entries.Count > RuntimeUiLimits.MaximumVoiceTimelineEntries;
        var projected = entries.TakeLast(RuntimeUiLimits.MaximumVoiceTimelineEntries).Select(entry =>
        {
            var text = Limit(entry.Text, RuntimeUiLimits.MaximumVoiceTimelineTextCharacters);
            var textTruncated = text.Length != entry.Text.Length;
            wasTruncated |= textTruncated;
            return entry with { Text = text, TextTruncated = textTruncated };
        }).ToArray();
        truncated = wasTruncated;
        return projected;
    }

    private static RuntimeVoiceMessagingSnapshot ProjectMessaging(
        VoicePePreferences preferences,
        IReadOnlyList<DesktopTaskSummary>? tasks,
        bool bridgeAvailable)
    {
        var taskArray = (tasks ?? []).Take(RuntimeUiLimits.MaximumVoiceTasks)
            .Select(task => new RuntimeDesktopTask(
                task.Id,
                task.HostId,
                Limit(task.Title),
                Limit(task.Status),
                task.ProjectId,
                task.WorkingDirectory,
                task.UpdatedAt,
                task.Pinned))
            .ToArray();
        var drafts = LoadDrafts(preferences, out var draftsTruncated);
        var selected = CodexTaskReference.TryParse(preferences.VoiceTargetTaskId, out var taskId)
            && !string.IsNullOrWhiteSpace(preferences.VoiceTargetHostId)
                ? new RuntimeTaskReference(taskId, preferences.VoiceTargetHostId)
                : null;
        return new RuntimeVoiceMessagingSnapshot(
            preferences.DesktopTaskMessagingEnabled,
            bridgeAvailable,
            preferences.DesktopTaskMessagingEnabled
                ? bridgeAvailable ? "Desktop bridge connected." : "Desktop bridge is connecting."
                : "Desktop task messaging is off.",
            taskArray,
            selected,
            Limit(preferences.VoiceTargetTaskLabel),
            drafts,
            TasksTruncated: tasks is { Count: > RuntimeUiLimits.MaximumVoiceTasks },
            DraftsTruncated: draftsTruncated);
    }

    private static RuntimeVoiceOutboxDraft[] LoadDrafts(
        VoicePePreferences preferences,
        out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrWhiteSpace(preferences.AgentWorkspacePath))
        {
            return [];
        }

        try
        {
            var loaded = new VoiceTaskOutbox(preferences.AgentWorkspacePath).Load();
            truncated = loaded.Count > RuntimeUiLimits.MaximumVoiceOutboxDrafts;
            return loaded.TakeLast(RuntimeUiLimits.MaximumVoiceOutboxDrafts).Select(draft =>
            {
                var preview = Limit(draft.Message, RuntimeUiLimits.MaximumVoiceOutboxPreviewCharacters);
                return new RuntimeVoiceOutboxDraft(
                    draft.Id,
                    new RuntimeTaskReference(draft.TargetTaskId, draft.TargetHostId),
                    Limit(draft.TargetTitle),
                    preview,
                    preview.Length != draft.Message.Length,
                    draft.CreatedAt,
                    draft.Attempts,
                    Limit(draft.LatestError));
            }).ToArray();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException)
        {
            return [];
        }
    }

    private static RuntimeUiSnapshot FitSnapshot(RuntimeUiSnapshot snapshot)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions).Length
            <= RuntimeUiLimits.MaximumEncodedSnapshotBytes)
        {
            return snapshot;
        }

        snapshot = snapshot with { RecentActivity = [] };
        if (snapshot.Voice is { } voice)
        {
            snapshot = snapshot with
            {
                Voice = voice with { Timeline = [], TimelineTruncated = true },
            };
        }
        if (snapshot.TaskAlerts is { } alerts)
        {
            snapshot = snapshot with { TaskAlerts = alerts with { RecentEvents = [] } };
        }
        if (JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions).Length
            > RuntimeUiLimits.MaximumEncodedSnapshotBytes
            && snapshot.TaskAlerts is { Suppressions.Length: > 0 } boundedAlerts)
        {
            // Settings carries the complete suppression list, so the UI projection can omit its
            // duplicate copy when non-ASCII workspace paths exhaust the snapshot wire budget.
            snapshot = snapshot with { TaskAlerts = boundedAlerts with { Suppressions = [] } };
        }
        if (JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions).Length
            > RuntimeUiLimits.MaximumEncodedSnapshotBytes)
        {
            throw new InvalidDataException("The bounded runtime UI snapshot exceeded its wire budget.");
        }
        return snapshot;
    }

    private static bool IsIncremental(
        IReadOnlyList<RuntimeVoiceTimelineEntry> previous,
        IReadOnlyList<RuntimeVoiceTimelineEntry> current)
    {
        if (previous.Count == 0)
        {
            return true;
        }

        var shifted = previous.Count == RuntimeUiLimits.MaximumVoiceTimelineEntries
            && current.Count == RuntimeUiLimits.MaximumVoiceTimelineEntries
            && previous.Skip(1).SequenceEqual(current.Take(current.Count - 1));
        if (shifted)
        {
            return true;
        }
        if (current.Count < previous.Count)
        {
            return false;
        }

        for (var index = 0; index < previous.Count; index++)
        {
            var left = previous[index];
            var right = current[index];
            if (!string.Equals(left.Id, right.Id, StringComparison.Ordinal)
                || index < previous.Count - 1 && left != right)
            {
                return false;
            }
        }
        return true;
    }

    private void Raise(RuntimeUiEvent update)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(update, update.GetType(), JsonOptions).Length
            > RuntimeUiLimits.MaximumEncodedEventBytes)
        {
            update = new RuntimeUiEvent(RuntimeEventKind.UiResynchronizationRequired);
        }
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }
        foreach (EventHandler<RuntimeUiEvent> handler in handlers.GetInvocationList())
        {
            try { handler(this, update); }
            catch { }
        }
    }

    private static string Limit(string? value, int maximum = RuntimeUiLimits.MaximumStatusCharacters)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= maximum ? normalized : normalized[..maximum];
    }
}
