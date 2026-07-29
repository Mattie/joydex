namespace Joydex.Core.TaskAlerts;

public sealed class TaskAlertPool
{
    public static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan BackfillDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RunningLease = TimeSpan.FromHours(12);
    public static readonly TimeSpan AttentionLease = TimeSpan.FromHours(24);

    private readonly Dictionary<int, TaskAlertAssignment> _assignments = [];
    private readonly Dictionary<int, DateTimeOffset> _backfillAfter = [];
    private readonly Dictionary<string, PendingAttention> _pendingAttention =
        new(StringComparer.Ordinal);

    public bool Enabled { get; private set; } = true;

    public long DroppedEventCount { get; private set; }

    public IReadOnlyList<TaskAlertAssignment> Assignments => _assignments.Values
        .OrderBy(assignment => assignment.Slot)
        .ToArray();

    /// <summary>Captures the state needed to restore assignments after restart.</summary>
    public TaskAlertPoolState CaptureState() => new(
        _assignments.Values
            .OrderBy(assignment => assignment.Slot)
            .Select(assignment =>
            {
                _pendingAttention.TryGetValue(assignment.SessionId, out var pending);
                return new TaskAlertStoredAssignment(
                    assignment.Slot,
                    assignment.SessionId,
                    assignment.TurnId,
                    assignment.State,
                    assignment.UpdatedAt,
                    assignment.CompleteAfter,
                    pending?.CaptureCorrelated() ?? [],
                    pending?.UncorrelatedCount ?? 0);
            })
            .ToArray());

    /// <summary>
    /// Replaces current assignments from a validated, all-or-nothing restart snapshot.
    /// </summary>
    public void Restore(TaskAlertPoolState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Assignments is null || state.Assignments.Length > TaskAlertSlots.All.Length)
        {
            throw new InvalidDataException("The task-alert state has an invalid assignment list.");
        }

        var restoredAssignments = new Dictionary<int, TaskAlertAssignment>();
        var restoredPending = new Dictionary<string, PendingAttention>(StringComparer.Ordinal);
        var sessions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stored in state.Assignments)
        {
            if (stored is null
                || stored.Slot is < 1 or > 10
                || string.IsNullOrWhiteSpace(stored.SessionId)
                || !Enum.IsDefined(stored.State)
                || stored.UpdatedAt == default
                || stored.CompleteAfter is { } completeAfter
                    && (completeAfter == default || completeAfter <= stored.UpdatedAt)
                || !restoredAssignments.TryAdd(
                    stored.Slot,
                    new TaskAlertAssignment(
                        stored.Slot,
                        stored.SessionId,
                        stored.TurnId,
                        stored.State,
                        stored.UpdatedAt,
                        stored.CompleteAfter))
                || !sessions.Add(stored.SessionId))
            {
                throw new InvalidDataException("The task-alert state contains an invalid assignment.");
            }

            var pending = PendingAttention.Restore(
                stored.CorrelatedAttention,
                stored.UncorrelatedAttentionCount);
            if (pending.Any)
            {
                if (stored.State != TaskAlertState.Approval)
                {
                    throw new InvalidDataException(
                        "Only approval assignments can contain pending attention state.");
                }

                restoredPending.Add(stored.SessionId, pending);
            }
        }

        _assignments.Clear();
        foreach (var pair in restoredAssignments)
        {
            _assignments.Add(pair.Key, pair.Value);
        }

        _pendingAttention.Clear();
        foreach (var pair in restoredPending)
        {
            _pendingAttention.Add(pair.Key, pair.Value);
        }

        _backfillAfter.Clear();
        PromoteAndCompactOverflow(TaskAlertSlots.Primary
            .Where(slot => !_assignments.ContainsKey(slot))
            .ToArray());
        DroppedEventCount = 0;
    }

    public bool Apply(TaskAlertEvent taskEvent)
    {
        ArgumentNullException.ThrowIfNull(taskEvent);
        if (!Enabled || string.IsNullOrWhiteSpace(taskEvent.SessionId))
        {
            return false;
        }

        Advance(taskEvent.ReceivedAt);
        var existing = _assignments.Values.FirstOrDefault(candidate =>
            string.Equals(candidate.SessionId, taskEvent.SessionId, StringComparison.Ordinal));
        if (taskEvent.Event == CodexLifecycleEvent.ToolCompleted)
        {
            return existing is not null
                && taskEvent.ReceivedAt >= existing.UpdatedAt
                && ApplyToolCompletion(existing, taskEvent);
        }

        if (existing is null)
        {
            var slot = TaskAlertSlots.All
                .Where(candidate => !_assignments.ContainsKey(candidate)
                    && !_backfillAfter.ContainsKey(candidate))
                .FirstOrDefault();
            if (slot == 0)
            {
                DroppedEventCount++;
                return false;
            }

            existing = new TaskAlertAssignment(
                slot,
                taskEvent.SessionId,
                taskEvent.TurnId,
                TaskAlertState.Running,
                taskEvent.ReceivedAt);
        }
        else if (taskEvent.ReceivedAt < existing.UpdatedAt)
        {
            return false;
        }

        if (taskEvent.Event is CodexLifecycleEvent.PermissionRequest
                or CodexLifecycleEvent.UserInputRequest
            && !string.Equals(existing.TurnId, taskEvent.TurnId, StringComparison.Ordinal))
        {
            _pendingAttention.Remove(taskEvent.SessionId);
        }

        var updated = taskEvent.Event switch
        {
            CodexLifecycleEvent.UserPromptSubmit => existing with
            {
                TurnId = taskEvent.TurnId,
                State = TaskAlertState.Running,
                UpdatedAt = taskEvent.ReceivedAt,
                CompleteAfter = null,
            },
            CodexLifecycleEvent.PermissionRequest or CodexLifecycleEvent.UserInputRequest => existing with
            {
                TurnId = taskEvent.TurnId,
                State = TaskAlertState.Approval,
                UpdatedAt = taskEvent.ReceivedAt,
                CompleteAfter = null,
            },
            CodexLifecycleEvent.Stop => existing with
            {
                TurnId = taskEvent.TurnId,
                UpdatedAt = taskEvent.ReceivedAt,
                CompleteAfter = taskEvent.ReceivedAt + StopGrace,
            },
            CodexLifecycleEvent.Fault => existing with
            {
                TurnId = taskEvent.TurnId,
                State = TaskAlertState.Fault,
                UpdatedAt = taskEvent.ReceivedAt,
                CompleteAfter = null,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(taskEvent)),
        };

        if (taskEvent.Event == CodexLifecycleEvent.UserPromptSubmit)
        {
            _pendingAttention.Remove(taskEvent.SessionId);
        }
        else if (taskEvent.Event is CodexLifecycleEvent.PermissionRequest
                     or CodexLifecycleEvent.UserInputRequest)
        {
            GetPendingAttention(taskEvent.SessionId).Add(taskEvent.AttentionKey);
        }
        else if (taskEvent.Event is CodexLifecycleEvent.Stop or CodexLifecycleEvent.Fault)
        {
            _pendingAttention.Remove(taskEvent.SessionId);
        }

        _assignments[updated.Slot] = updated;
        return true;
    }

    public bool Advance(DateTimeOffset now)
    {
        var changed = false;
        var vacatedPrimarySlots = new List<int>();
        var vacatedOverflowSlot = false;
        foreach (var pair in _assignments.ToArray())
        {
            var assignment = pair.Value;
            if (assignment.CompleteAfter is { } completeAfter && completeAfter <= now)
            {
                assignment = assignment with
                {
                    State = TaskAlertState.Completed,
                    UpdatedAt = completeAfter,
                    CompleteAfter = null,
                };
                _assignments[pair.Key] = assignment;
                changed = true;
            }

            var lease = assignment.State == TaskAlertState.Running ? RunningLease : AttentionLease;
            if (now - assignment.UpdatedAt >= lease)
            {
                _assignments.Remove(pair.Key);
                _pendingAttention.Remove(assignment.SessionId);
                if (TaskAlertSlots.Page(pair.Key) == TaskAlertPage.Primary)
                {
                    vacatedPrimarySlots.Add(pair.Key);
                }
                else
                {
                    vacatedOverflowSlot = true;
                }

                changed = true;
            }
        }

        TrimBackfillReservations();
        foreach (var slot in vacatedPrimarySlots)
        {
            ScheduleBackfill(slot, now);
        }

        if (vacatedOverflowSlot)
        {
            PromoteAndCompactOverflow([]);
        }

        return PromoteDueOverflow(now) || changed;
    }

    public bool AcknowledgeTerminal(int slot, string sessionId) =>
        AcknowledgeTerminal(slot, sessionId, DateTimeOffset.UtcNow);

    public bool AcknowledgeTerminal(int slot, string sessionId, DateTimeOffset acknowledgedAt)
    {
        TaskAlertSlots.Validate(slot);
        if (acknowledgedAt == default)
        {
            throw new ArgumentOutOfRangeException(nameof(acknowledgedAt));
        }

        if (!_assignments.TryGetValue(slot, out var assignment)
            || !string.Equals(assignment.SessionId, sessionId, StringComparison.Ordinal)
            || assignment.State is not (TaskAlertState.Completed or TaskAlertState.Fault))
        {
            return false;
        }

        _pendingAttention.Remove(assignment.SessionId);
        _assignments.Remove(slot);
        TrimBackfillReservations();
        if (TaskAlertSlots.Page(slot) == TaskAlertPage.Primary)
        {
            ScheduleBackfill(slot, acknowledgedAt);
        }

        PromoteAndCompactOverflow([]);
        return true;
    }

    public bool SetEnabled(bool enabled)
    {
        if (Enabled == enabled)
        {
            return false;
        }

        Enabled = enabled;
        _assignments.Clear();
        _backfillAfter.Clear();
        _pendingAttention.Clear();
        return true;
    }

    private bool ApplyToolCompletion(TaskAlertAssignment existing, TaskAlertEvent taskEvent)
    {
        if (string.IsNullOrWhiteSpace(taskEvent.AttentionKey)
            || !_pendingAttention.TryGetValue(taskEvent.SessionId, out var pending)
            || !pending.Complete(taskEvent.AttentionKey))
        {
            return false;
        }

        if (!pending.Any)
        {
            _pendingAttention.Remove(taskEvent.SessionId);
        }

        _assignments[existing.Slot] = existing with
        {
            TurnId = taskEvent.TurnId,
            State = pending.Any ? TaskAlertState.Approval : TaskAlertState.Running,
            UpdatedAt = taskEvent.ReceivedAt,
            CompleteAfter = null,
        };
        return true;
    }

    private PendingAttention GetPendingAttention(string sessionId)
    {
        if (!_pendingAttention.TryGetValue(sessionId, out var pending))
        {
            pending = new PendingAttention();
            _pendingAttention.Add(sessionId, pending);
        }

        return pending;
    }

    private void ScheduleBackfill(int slot, DateTimeOffset vacatedAt)
    {
        if (_assignments.ContainsKey(slot)
            || _backfillAfter.ContainsKey(slot)
            || _backfillAfter.Count >= _assignments.Keys.Count(candidate =>
                TaskAlertSlots.Page(candidate) == TaskAlertPage.Overflow))
        {
            return;
        }

        _backfillAfter.Add(slot, vacatedAt + BackfillDelay);
    }

    private void TrimBackfillReservations()
    {
        foreach (var occupiedSlot in _backfillAfter.Keys
                     .Where(_assignments.ContainsKey)
                     .ToArray())
        {
            _backfillAfter.Remove(occupiedSlot);
        }

        var overflowCount = _assignments.Keys.Count(slot =>
            TaskAlertSlots.Page(slot) == TaskAlertPage.Overflow);
        var excessReservations = _backfillAfter
            .OrderBy(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .Skip(overflowCount)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var slot in excessReservations)
        {
            _backfillAfter.Remove(slot);
        }
    }

    private bool PromoteDueOverflow(DateTimeOffset now)
    {
        TrimBackfillReservations();
        var dueSlots = _backfillAfter
            .Where(pair => pair.Value <= now)
            .OrderBy(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .Select(pair => pair.Key)
            .ToArray();
        if (dueSlots.Length == 0)
        {
            return false;
        }

        var changed = PromoteAndCompactOverflow(dueSlots);
        foreach (var slot in dueSlots)
        {
            _backfillAfter.Remove(slot);
        }

        return changed;
    }

    private bool PromoteAndCompactOverflow(IReadOnlyList<int> primaryVacancies)
    {
        var overflowAssignments = TaskAlertSlots.Overflow
            .Where(_assignments.ContainsKey)
            .Select(slot => _assignments[slot])
            .ToArray();
        var promotionCount = Math.Min(primaryVacancies.Count, overflowAssignments.Length);
        var requiresRebalance = false;

        for (var index = 0; index < promotionCount; index++)
        {
            requiresRebalance |= overflowAssignments[index].Slot != primaryVacancies[index];
        }

        for (var index = promotionCount; index < overflowAssignments.Length; index++)
        {
            requiresRebalance |= overflowAssignments[index].Slot
                != TaskAlertSlots.Overflow[index - promotionCount];
        }

        if (!requiresRebalance)
        {
            return false;
        }

        foreach (var slot in TaskAlertSlots.Overflow)
        {
            _assignments.Remove(slot);
        }

        for (var index = 0; index < promotionCount; index++)
        {
            var assignment = overflowAssignments[index];
            var slot = primaryVacancies[index];
            _assignments.Add(slot, assignment with { Slot = slot });
        }

        for (var index = promotionCount; index < overflowAssignments.Length; index++)
        {
            var assignment = overflowAssignments[index];
            var slot = TaskAlertSlots.Overflow[index - promotionCount];
            _assignments.Add(slot, assignment with { Slot = slot });
        }

        return true;
    }

    private sealed class PendingAttention
    {
        private readonly Dictionary<string, int> _correlated = new(StringComparer.Ordinal);
        private int _uncorrelated;

        public bool Any => _uncorrelated > 0 || _correlated.Count > 0;

        public int UncorrelatedCount => _uncorrelated;

        public TaskAlertCorrelationCount[] CaptureCorrelated() => _correlated
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new TaskAlertCorrelationCount(pair.Key, pair.Value))
            .ToArray();

        public static PendingAttention Restore(
            TaskAlertCorrelationCount[] correlations,
            int uncorrelatedCount)
        {
            if (correlations is null || uncorrelatedCount < 0)
            {
                throw new InvalidDataException("The task-alert pending attention state is invalid.");
            }

            var pending = new PendingAttention { _uncorrelated = uncorrelatedCount };
            foreach (var correlation in correlations)
            {
                if (correlation is null
                    || correlation.Count <= 0
                    || !IsSha256Hash(correlation.Key)
                    || !pending._correlated.TryAdd(correlation.Key, correlation.Count))
                {
                    throw new InvalidDataException(
                        "The task-alert state contains an invalid attention correlation.");
                }
            }

            return pending;
        }

        public void Add(string? key)
        {
            if (!IsSha256Hash(key))
            {
                _uncorrelated++;
                return;
            }

            _correlated[key!] = _correlated.GetValueOrDefault(key!) + 1;
        }

        public bool Complete(string key)
        {
            if (!IsSha256Hash(key) || !_correlated.TryGetValue(key, out var count))
            {
                return false;
            }

            if (count == 1)
            {
                _correlated.Remove(key);
            }
            else
            {
                _correlated[key] = count - 1;
            }

            return true;
        }

        private static bool IsSha256Hash(string? value) =>
            value is { Length: 64 }
            && value.All(character => character is >= '0' and <= '9'
                or >= 'A' and <= 'F'
                or >= 'a' and <= 'f');
    }
}
