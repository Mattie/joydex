namespace Joydex.Core.TaskAlerts;

public sealed class TaskAlertPool
{
    public static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan RunningLease = TimeSpan.FromHours(12);
    public static readonly TimeSpan AttentionLease = TimeSpan.FromHours(24);

    private readonly Dictionary<int, TaskAlertAssignment> _assignments = [];
    private readonly Dictionary<string, PendingAttention> _pendingAttention =
        new(StringComparer.Ordinal);

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
        if (taskEvent.Event == CodexLifecycleEvent.ToolCompleted)
        {
            return existing is not null
                && taskEvent.ReceivedAt >= existing.UpdatedAt
                && ApplyToolCompletion(existing, taskEvent);
        }

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

        _pendingAttention.Remove(assignment.SessionId);
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

    private sealed class PendingAttention
    {
        private readonly Dictionary<string, int> _correlated = new(StringComparer.Ordinal);
        private int _uncorrelated;

        public bool Any => _uncorrelated > 0 || _correlated.Count > 0;

        public void Add(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                _uncorrelated++;
                return;
            }

            _correlated[key] = _correlated.GetValueOrDefault(key) + 1;
        }

        public bool Complete(string key)
        {
            if (!_correlated.TryGetValue(key, out var count))
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
    }
}
