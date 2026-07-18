namespace Joydex.Core.TaskAlerts;

public sealed class TaskAlertPool
{
    public static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan RunningLease = TimeSpan.FromHours(12);
    public static readonly TimeSpan AttentionLease = TimeSpan.FromHours(24);

    private readonly Dictionary<int, TaskAlertAssignment> _assignments = [];

    public bool Enabled { get; private set; } = true;

    public long DroppedEventCount { get; private set; }

    public IReadOnlyList<TaskAlertAssignment> Assignments => _assignments.Values
        .OrderBy(assignment => assignment.Slot)
        .ToArray();

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
        if (existing is null)
        {
            var slot = TaskAlertSlots.All
                .Where(candidate => !_assignments.ContainsKey(candidate))
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

        var updated = taskEvent.Event switch
        {
            CodexLifecycleEvent.UserPromptSubmit => existing with
            {
                TurnId = taskEvent.TurnId,
                State = TaskAlertState.Running,
                UpdatedAt = taskEvent.ReceivedAt,
                CompleteAfter = null,
            },
            CodexLifecycleEvent.PermissionRequest => existing with
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

        _assignments[updated.Slot] = updated;
        return true;
    }

    public bool Advance(DateTimeOffset now)
    {
        var changed = false;
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
                changed = true;
            }
        }

        return changed;
    }

    public bool AcknowledgeTerminal(int slot, string sessionId)
    {
        TaskAlertSlots.Validate(slot);
        if (!_assignments.TryGetValue(slot, out var assignment)
            || !string.Equals(assignment.SessionId, sessionId, StringComparison.Ordinal)
            || assignment.State is not (TaskAlertState.Completed or TaskAlertState.Fault))
        {
            return false;
        }

        return _assignments.Remove(slot);
    }

    public bool SetEnabled(bool enabled)
    {
        if (Enabled == enabled)
        {
            return false;
        }

        Enabled = enabled;
        _assignments.Clear();
        return true;
    }

}
