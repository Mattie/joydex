using Joydex.Contracts;
using Joydex.Core.Input;

namespace Joydex.App;

internal enum RuntimeClientChangeKind
{
    Initialized,
    RuntimeEvent,
    InputEvent,
    CommandCompleted,
    ResynchronizationRequired,
    Disconnected,
}

internal sealed record RuntimeClientConnectionState(
    RuntimeSnapshot? Snapshot,
    long InputEventCursor,
    bool IsInitialized,
    bool ResynchronizationRequired,
    bool IsDisconnected,
    Exception? DisconnectFailure);

internal sealed record RuntimeClientStateChange(
    RuntimeClientChangeKind Kind,
    RuntimeClientConnectionState State,
    RuntimeEvent? RuntimeEvent = null,
    RuntimeConnectionInputEvent? InputEvent = null,
    RuntimeCommandResult? CommandResult = null);

/// <summary>
/// Applies runtime callbacks to one checked client snapshot and posts concise changes to the UI
/// context. Callbacks never run window code on the JSON-RPC dispatch path.
/// </summary>
internal sealed class RuntimeClientState : IRuntimeRpcClient
{
    private const int MaximumBufferedCallbacks = RuntimeProtocol.MaximumRetainedEvents;
    private readonly object _gate = new();
    private readonly SynchronizationContext _notificationContext;
    private readonly List<PendingCallback> _callbacksBeforeAttach = [];
    private readonly List<PendingCallback> _callbacksDuringResynchronization = [];
    private readonly Queue<RuntimeClientStateChange> _notifications = new();
    private RuntimeSnapshot? _snapshot;
    private long _inputEventCursor;
    private bool _initializing;
    private bool _initialized;
    private bool _resynchronizationRequired;
    private bool _resynchronizationCallbacksLost;
    private bool _notificationScheduled;
    private bool _disconnected;
    private Exception? _disconnectFailure;

    public RuntimeClientState(SynchronizationContext notificationContext)
    {
        _notificationContext = notificationContext
            ?? throw new ArgumentNullException(nameof(notificationContext));
    }

    public event EventHandler<RuntimeClientStateChange>? Changed;

    public RuntimeClientConnectionState Current
    {
        get
        {
            lock (_gate)
            {
                return CurrentLocked();
            }
        }
    }

    public void Initialize(RuntimeAttachResult attachResult)
    {
        ArgumentNullException.ThrowIfNull(attachResult);
        bool scheduleNotifications;
        lock (_gate)
        {
            if (_initialized || _initializing)
            {
                throw new InvalidOperationException("The runtime client state is already initialized.");
            }
            ValidateSnapshot(attachResult.Snapshot);
            var callbacksWereLost = _resynchronizationRequired;
            _initializing = true;
            _snapshot = attachResult.Snapshot;
            _inputEventCursor = attachResult.Snapshot.InputEventCursor;
            _resynchronizationRequired = callbacksWereLost;
            _initialized = true;
            List<RuntimeClientStateChange> changes = [];
            if (!callbacksWereLost)
            {
                changes.Add(NewChangeLocked(RuntimeClientChangeKind.Initialized));
                foreach (var callback in _callbacksBeforeAttach)
                {
                    var replay = ApplyBufferedAfterSnapshotLocked(callback);
                    if (replay is not null)
                    {
                        changes.Add(replay);
                    }
                }
            }
            else
            {
                changes.Add(NewChangeLocked(RuntimeClientChangeKind.ResynchronizationRequired));
            }
            _callbacksBeforeAttach.Clear();
            _initializing = false;
            scheduleNotifications = false;
            foreach (var change in changes)
            {
                scheduleNotifications |= EnqueueNotificationLocked(change);
            }
        }
        ScheduleNotifications(scheduleNotifications);
    }

    public void ApplySnapshot(RuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSnapshot(snapshot);
        bool scheduleNotifications;
        lock (_gate)
        {
            if (!_initialized || _disconnected)
            {
                throw new InvalidOperationException("The runtime client is not connected.");
            }
            _snapshot = snapshot;
            _inputEventCursor = snapshot.InputEventCursor;
            _resynchronizationRequired = _resynchronizationCallbacksLost;
            _resynchronizationCallbacksLost = false;
            var buffered = _callbacksDuringResynchronization.ToArray();
            List<RuntimeClientStateChange> changes = [];
            _callbacksDuringResynchronization.Clear();
            if (!_resynchronizationRequired)
            {
                changes.Add(NewChangeLocked(RuntimeClientChangeKind.Initialized));
                foreach (var callback in buffered)
                {
                    if (_resynchronizationRequired)
                    {
                        BufferDuringResynchronizationLocked(callback);
                    }
                    else
                    {
                        var replay = ApplyBufferedAfterSnapshotLocked(callback);
                        if (replay is not null)
                        {
                            changes.Add(replay);
                        }
                    }
                }
            }
            else
            {
                // Overflow requires another snapshot, but snapshots omit completed captures.
                // Keep their one-shot payloads until a refresh can safely replay them.
                _callbacksDuringResynchronization.AddRange(buffered);
                changes.Add(NewChangeLocked(RuntimeClientChangeKind.ResynchronizationRequired));
            }
            scheduleNotifications = false;
            foreach (var change in changes)
            {
                scheduleNotifications |= EnqueueNotificationLocked(change);
            }
        }
        ScheduleNotifications(scheduleNotifications);
    }

    public void MarkDisconnected(Exception? failure = null)
    {
        var scheduleNotifications = false;
        lock (_gate)
        {
            if (!_disconnected)
            {
                _disconnected = true;
                _disconnectFailure = failure;
                scheduleNotifications = EnqueueNotificationLocked(
                    NewChangeLocked(RuntimeClientChangeKind.Disconnected));
            }
        }
        ScheduleNotifications(scheduleNotifications);
    }

    public Task RuntimeEventAsync(
        RuntimeEvent runtimeEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        cancellationToken.ThrowIfCancellationRequested();
        bool scheduleNotifications;
        lock (_gate)
        {
            var change = QueueOrApplyLocked(new PendingCallback(RuntimeEvent: runtimeEvent));
            scheduleNotifications = change is not null && EnqueueNotificationLocked(change);
        }
        ScheduleNotifications(scheduleNotifications);
        return Task.CompletedTask;
    }

    public Task RuntimeInputEventAsync(
        RuntimeConnectionInputEvent inputEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputEvent);
        cancellationToken.ThrowIfCancellationRequested();
        bool scheduleNotifications;
        lock (_gate)
        {
            var change = QueueOrApplyLocked(new PendingCallback(InputEvent: inputEvent));
            scheduleNotifications = change is not null && EnqueueNotificationLocked(change);
        }
        ScheduleNotifications(scheduleNotifications);
        return Task.CompletedTask;
    }

    public Task RuntimeCommandCompletedAsync(
        RuntimeCommandResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();
        bool scheduleNotifications;
        lock (_gate)
        {
            var change = QueueOrApplyLocked(new PendingCallback(CommandResult: result));
            scheduleNotifications = change is not null && EnqueueNotificationLocked(change);
        }
        ScheduleNotifications(scheduleNotifications);
        return Task.CompletedTask;
    }

    private RuntimeClientStateChange? QueueOrApplyLocked(PendingCallback callback)
    {
        if (_disconnected)
        {
            return null;
        }
        if (!_initialized && !_initializing)
        {
            if (_callbacksBeforeAttach.Count == MaximumBufferedCallbacks)
            {
                _callbacksBeforeAttach.Clear();
                _resynchronizationRequired = true;
            }
            else if (!_resynchronizationRequired)
            {
                _callbacksBeforeAttach.Add(callback);
            }
            return null;
        }
        if (callback.CommandResult is null && _resynchronizationRequired)
        {
            BufferDuringResynchronizationLocked(callback);
            return null;
        }
        return ApplyPendingLocked(callback);
    }

    private RuntimeClientStateChange? ApplyPendingLocked(PendingCallback callback)
    {
        if (callback.RuntimeEvent is { } runtimeEvent)
        {
            return ApplyRuntimeEventLocked(runtimeEvent);
        }
        if (callback.InputEvent is { } inputEvent)
        {
            return ApplyInputEventLocked(inputEvent);
        }
        return callback.CommandResult is { } command
            ? NewChangeLocked(RuntimeClientChangeKind.CommandCompleted, commandResult: command)
            : null;
    }

    private RuntimeClientStateChange? ApplyBufferedAfterSnapshotLocked(PendingCallback callback)
    {
        var snapshot = _snapshot!;
        if (callback.RuntimeEvent is { } runtimeEvent)
        {
            if (runtimeEvent.EngineEpoch != snapshot.EngineEpoch)
            {
                return null;
            }
            return runtimeEvent.Sequence <= snapshot.EventCursor
                ? NewChangeLocked(RuntimeClientChangeKind.RuntimeEvent, runtimeEvent: runtimeEvent)
                : ApplyRuntimeEventLocked(runtimeEvent);
        }
        if (callback.InputEvent is { } inputEvent)
        {
            if (inputEvent.EngineEpoch != snapshot.EngineEpoch)
            {
                return null;
            }
            return inputEvent.Sequence <= _inputEventCursor
                ? ApplyInputPayloadLocked(inputEvent, advanceCursor: false)
                : ApplyInputEventLocked(inputEvent);
        }
        return callback.CommandResult is { } command
            ? NewChangeLocked(RuntimeClientChangeKind.CommandCompleted, commandResult: command)
            : null;
    }

    private RuntimeClientStateChange? ApplyRuntimeEventLocked(RuntimeEvent runtimeEvent)
    {
        var snapshot = _snapshot!;
        if (_resynchronizationRequired)
        {
            return null;
        }
        if (runtimeEvent.EngineEpoch != snapshot.EngineEpoch)
        {
            return RequireResynchronizationLocked(new PendingCallback(RuntimeEvent: runtimeEvent));
        }
        if (runtimeEvent.Sequence <= snapshot.EventCursor)
        {
            // A callback can finish after a snapshot has already captured its sequence. The full
            // snapshot carries the state, while this notification preserves one-shot completion
            // payloads for the presentation adapters that initiated the operation.
            return NewChangeLocked(RuntimeClientChangeKind.RuntimeEvent, runtimeEvent: runtimeEvent);
        }
        if (runtimeEvent.Sequence != checked(snapshot.EventCursor + 1))
        {
            return RequireResynchronizationLocked(new PendingCallback(RuntimeEvent: runtimeEvent));
        }

        if (!TryApplyRuntimePayload(snapshot, runtimeEvent, out var updated))
        {
            return RequireResynchronizationLocked(new PendingCallback(RuntimeEvent: runtimeEvent));
        }
        _snapshot = updated with { EventCursor = runtimeEvent.Sequence };
        return NewChangeLocked(
            RuntimeClientChangeKind.RuntimeEvent,
            runtimeEvent: runtimeEvent);
    }

    private RuntimeClientStateChange? ApplyInputEventLocked(RuntimeConnectionInputEvent inputEvent)
    {
        var snapshot = _snapshot!;
        if (_resynchronizationRequired)
        {
            return null;
        }
        if (inputEvent.EngineEpoch != snapshot.EngineEpoch)
        {
            return RequireResynchronizationLocked(new PendingCallback(InputEvent: inputEvent));
        }
        if (inputEvent.Sequence <= _inputEventCursor)
        {
            return null;
        }
        if (inputEvent.Sequence != checked(_inputEventCursor + 1))
        {
            return RequireResynchronizationLocked(new PendingCallback(InputEvent: inputEvent));
        }

        return ApplyInputPayloadLocked(inputEvent, advanceCursor: true);
    }

    private RuntimeClientStateChange ApplyInputPayloadLocked(
        RuntimeConnectionInputEvent inputEvent,
        bool advanceCursor)
    {
        var snapshot = _snapshot!;
        var input = snapshot.Input;
        switch (inputEvent.Kind)
        {
            case RuntimeConnectionInputEventKind.CaptureChanged
                when inputEvent.Capture is { } capture:
            {
                var captures = input.Captures
                    .Where(item => item.CaptureId != capture.Lease.CaptureId)
                    .ToList();
                if (capture.Lease.Status is InputCaptureStatus.Pending or InputCaptureStatus.Active)
                {
                    captures.Add(capture.Lease);
                }
                _snapshot = snapshot with
                {
                    Input = input with { Captures = [.. captures] },
                };
                break;
            }
            case RuntimeConnectionInputEventKind.InputObserved
                when inputEvent.Observation is not null:
                break;
            default:
                return RequireResynchronizationLocked(new PendingCallback(InputEvent: inputEvent));
        }

        if (advanceCursor)
        {
            _inputEventCursor = inputEvent.Sequence;
        }
        _snapshot = _snapshot! with { InputEventCursor = _inputEventCursor };
        return NewChangeLocked(RuntimeClientChangeKind.InputEvent, inputEvent: inputEvent);
    }

    private RuntimeClientStateChange RequireResynchronizationLocked(PendingCallback callback)
    {
        _resynchronizationRequired = true;
        _callbacksDuringResynchronization.Clear();
        _resynchronizationCallbacksLost = false;
        BufferDuringResynchronizationLocked(callback);
        return NewChangeLocked(RuntimeClientChangeKind.ResynchronizationRequired);
    }

    private void BufferDuringResynchronizationLocked(PendingCallback callback)
    {
        if (callback.InputEvent?.Capture is { } capture)
        {
            var existing = _callbacksDuringResynchronization.FindIndex(item =>
                item.InputEvent?.EngineEpoch == callback.InputEvent.EngineEpoch
                && item.InputEvent?.Capture?.Lease.CaptureId == capture.Lease.CaptureId);
            if (existing >= 0)
            {
                if (_callbacksDuringResynchronization[existing].InputEvent!.Capture!.Lease.Revision
                    >= capture.Lease.Revision)
                {
                    return;
                }
                _callbacksDuringResynchronization.RemoveAt(existing);
            }
            // Capture lifecycle updates cannot be rebuilt from a snapshot. Retain the latest
            // revision per lease even after disposable observations have overflowed.
            _callbacksDuringResynchronization.Add(callback);
            return;
        }
        if (_resynchronizationCallbacksLost)
        {
            return;
        }
        if (_callbacksDuringResynchronization.Count(item => item.InputEvent?.Capture is null)
            == MaximumBufferedCallbacks)
        {
            _callbacksDuringResynchronization.RemoveAll(item => item.InputEvent?.Capture is null);
            _resynchronizationCallbacksLost = true;
            return;
        }
        _callbacksDuringResynchronization.Add(callback);
    }

    private RuntimeClientStateChange NewChangeLocked(
        RuntimeClientChangeKind kind,
        RuntimeEvent? runtimeEvent = null,
        RuntimeConnectionInputEvent? inputEvent = null,
        RuntimeCommandResult? commandResult = null) => new(
        kind,
        CurrentLocked(),
        runtimeEvent,
        inputEvent,
        commandResult);

    private RuntimeClientConnectionState CurrentLocked() => new(
        _snapshot,
        _inputEventCursor,
        _initialized,
        _resynchronizationRequired,
        _disconnected,
        _disconnectFailure);

    private bool EnqueueNotificationLocked(RuntimeClientStateChange change)
    {
        _notifications.Enqueue(change);
        if (_notificationScheduled)
        {
            return false;
        }
        _notificationScheduled = true;
        return true;
    }

    private void ScheduleNotifications(bool schedule)
    {
        if (schedule)
        {
            _notificationContext.Post(static state => ((RuntimeClientState)state!).DrainNotifications(), this);
        }
    }

    private void DrainNotifications()
    {
        while (true)
        {
            RuntimeClientStateChange change;
            lock (_gate)
            {
                if (_notifications.Count == 0)
                {
                    _notificationScheduled = false;
                    return;
                }
                change = _notifications.Dequeue();
            }

            try
            {
                Changed?.Invoke(this, change);
            }
            catch
            {
                // One presentation adapter cannot stall later connection and recovery updates.
            }
        }
    }

    private static bool TryApplyRuntimePayload(
        RuntimeSnapshot snapshot,
        RuntimeEvent runtimeEvent,
        out RuntimeSnapshot updated)
    {
        updated = snapshot;
        var ui = snapshot.Ui ?? new RuntimeUiSnapshot();
        switch (runtimeEvent.Kind)
        {
            case RuntimeEventKind.UiResynchronizationRequired:
                // The retained state is newer than the delta's wire budget could carry.
                return false;
            case RuntimeEventKind.SettingsChanged when runtimeEvent.Settings is { } settings:
                updated = snapshot with { Settings = settings };
                return true;
            case RuntimeEventKind.OperationCompleted when runtimeEvent.Operation is { } operation:
                updated = snapshot with { Settings = operation.Snapshot };
                return true;
            case RuntimeEventKind.RuntimeIdentityChanged when runtimeEvent.Identity is { } identity:
                updated = snapshot with { Identity = identity };
                return true;
            case RuntimeEventKind.InputSourcesChanged when runtimeEvent.InputSources is { } sources:
                updated = snapshot with
                {
                    Input = snapshot.Input with { Sources = sources.Sources },
                };
                return true;
            case RuntimeEventKind.PromptPickerChanged when runtimeEvent.PromptPicker is { } picker:
                updated = snapshot with { Ui = ui with { PromptPicker = picker } };
                return true;
            case RuntimeEventKind.ButtonMapVisibilityChanged
                when runtimeEvent.ButtonMapVisibility is { } visibility:
                updated = snapshot with
                {
                    Ui = ui with
                    {
                        ButtonMaps = Upsert(
                            ui.ButtonMaps,
                            visibility,
                            static item => item.DeviceId),
                    },
                };
                return true;
            case RuntimeEventKind.ControllerStatusChanged
                when runtimeEvent.ControllerStatus is { } controller:
                updated = snapshot with
                {
                    Ui = ui with
                    {
                        Controllers = Upsert(
                            ui.Controllers,
                            controller,
                            static item => item.DeviceId),
                    },
                };
                return true;
            case RuntimeEventKind.ActionActivityAdded when runtimeEvent.ActionActivity is { } activity:
                updated = snapshot with
                {
                    Ui = ui with
                    {
                        RecentActivity = UpsertActivity(
                            ui.RecentActivity,
                            activity,
                            RuntimeUiLimits.MaximumActionActivityEntries),
                    },
                };
                return true;
            case RuntimeEventKind.TaskAlertsChanged when runtimeEvent.TaskAlerts is { } taskAlerts:
                updated = snapshot with { Ui = ui with { TaskAlerts = taskAlerts } };
                return true;
            case RuntimeEventKind.VoiceChanged when runtimeEvent.Voice is { } voice:
                return TryApplyVoice(snapshot, ui, voice, out updated);
            case RuntimeEventKind.PebbleIndexChanged when runtimeEvent.PebbleIndex is { } pebble:
                updated = snapshot with { Ui = ui with { PebbleIndex = pebble } };
                return true;
            default:
                return false;
        }
    }

    private static bool TryApplyVoice(
        RuntimeSnapshot snapshot,
        RuntimeUiSnapshot ui,
        RuntimeVoiceEvent voice,
        out RuntimeSnapshot updated)
    {
        updated = snapshot;
        if (voice.TimelineReset)
        {
            return false;
        }

        var current = ui.Voice;
        var messaging = voice.Messaging ?? current?.Messaging;
        if (messaging is null)
        {
            return false;
        }
        var timeline = current?.Timeline ?? [];
        var truncated = current?.TimelineTruncated ?? false;
        if (voice.TimelineEntry is { } entry)
        {
            var entries = timeline.ToList();
            var existing = entries.FindIndex(item => string.Equals(item.Id, entry.Id, StringComparison.Ordinal));
            if (existing >= 0)
            {
                entries[existing] = entry;
            }
            else
            {
                entries.Add(entry);
            }
            if (entries.Count > RuntimeUiLimits.MaximumVoiceTimelineEntries)
            {
                entries.RemoveRange(0, entries.Count - RuntimeUiLimits.MaximumVoiceTimelineEntries);
                truncated = true;
            }
            timeline = [.. entries];
        }

        updated = snapshot with
        {
            Ui = ui with
            {
                Voice = new RuntimeVoiceUiSnapshot(
                    voice.Session,
                    timeline,
                    messaging,
                    truncated),
            },
        };
        return true;
    }

    private static T[] Upsert<T>(
        T[]? current,
        T value,
        Func<T, string> key)
    {
        var values = current?.ToList() ?? [];
        var valueKey = key(value);
        var existing = values.FindIndex(item => string.Equals(
            key(item),
            valueKey,
            StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            values[existing] = value;
        }
        else
        {
            values.Add(value);
        }
        return [.. values];
    }

    private static RuntimeActionActivity[] UpsertActivity(
        RuntimeActionActivity[]? current,
        RuntimeActionActivity value,
        int maximum)
    {
        var values = current?.ToList() ?? [];
        var existing = values.FindIndex(item => item.Sequence == value.Sequence);
        if (existing >= 0)
        {
            values[existing] = value;
        }
        else
        {
            values.Add(value);
        }
        return [.. values.Skip(Math.Max(0, values.Count - maximum))];
    }

    private static void ValidateSnapshot(RuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot.Identity);
        ArgumentNullException.ThrowIfNull(snapshot.Settings);
        ArgumentNullException.ThrowIfNull(snapshot.Input);
        if (snapshot.EngineEpoch == Guid.Empty
            || snapshot.EventCursor < 0
            || snapshot.InputEventCursor < 0)
        {
            throw new InvalidDataException("The runtime snapshot has an invalid event cursor.");
        }
    }

    private sealed record PendingCallback(
        RuntimeEvent? RuntimeEvent = null,
        RuntimeConnectionInputEvent? InputEvent = null,
        RuntimeCommandResult? CommandResult = null);

}
