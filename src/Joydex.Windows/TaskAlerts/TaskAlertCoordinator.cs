using System.Threading.Channels;
using System.Text.Json;
using Joydex.Core.TaskAlerts;

namespace Joydex.Windows.TaskAlerts;

public sealed record TaskAlertSnapshot(
    bool Enabled,
    IReadOnlyList<TaskAlertAssignment> Assignments,
    long DroppedEventCount,
    int Bank = 2,
    bool BankAutomaticallyDetected = false,
    IReadOnlyList<TaskAlertEventTrace>? RecentEvents = null);

public enum TaskAlertEventResult
{
    Assigned,
    Updated,
    StopGrace,
    Dropped,
    Ignored,
}

public sealed record TaskAlertEventTrace(
    DateTimeOffset ReceivedAt,
    CodexLifecycleEvent Event,
    string SessionId,
    string? TurnId,
    int? Slot,
    TaskAlertState? State,
    TaskAlertEventResult Result);

public sealed class TaskAlertCoordinator : IAsyncDisposable
{
    private const int MaximumRecentEvents = 100;
    private readonly object _sync = new();
    private readonly string _preferencesPath;
    private readonly string _statePath;
    private readonly Action<string> _log;
    private readonly TaskAlertPool _pool;
    private readonly Channel<TaskAlertEvent> _events = Channel.CreateUnbounded<TaskAlertEvent>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _eventSignal = new(0);
    private readonly Queue<TaskAlertEventTrace> _recentEvents = new();
    private readonly Task _reducerTask;
    private TaskAlertPreferences _preferences;
    private int? _detectedBank;

    public TaskAlertCoordinator(
        string preferencesPath,
        string? statePath = null,
        Action<string>? log = null)
    {
        _preferencesPath = preferencesPath ?? throw new ArgumentNullException(nameof(preferencesPath));
        var preferencesDirectory = Path.GetDirectoryName(Path.GetFullPath(preferencesPath))
            ?? throw new InvalidOperationException("The task-alert settings path has no parent directory.");
        _statePath = statePath ?? Path.Combine(preferencesDirectory, "task-alert-state.json");
        _log = log ?? (_ => { });
        _preferences = TaskAlertPreferencesStore.LoadOrCreate(preferencesPath);
        _pool = new TaskAlertPool();
        if (!_preferences.Enabled)
        {
            _pool.SetEnabled(false);
        }
        else
        {
            RestoreStateUnsafe();
        }

        TrySaveStateUnsafe();

        _reducerTask = RunReducerAsync(_cancellation.Token);
    }

    public event EventHandler<TaskAlertSnapshot>? Changed;

    public bool TryPublish(TaskAlertEvent taskEvent)
    {
        ArgumentNullException.ThrowIfNull(taskEvent);
        bool written;
        lock (_sync)
        {
            if (!_pool.Enabled)
            {
                return false;
            }

            written = _events.Writer.TryWrite(taskEvent);
        }

        if (!written)
        {
            return false;
        }

        _eventSignal.Release();
        return true;
    }

    public TaskAlertSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return SnapshotUnsafe();
        }
    }

    public void SetEnabled(bool enabled)
    {
        TaskAlertSnapshot? snapshot = null;
        lock (_sync)
        {
            if (_pool.SetEnabled(enabled))
            {
                if (!enabled)
                {
                    while (_events.Reader.TryRead(out _))
                    {
                    }
                }

                _preferences = _preferences with { Enabled = enabled };
                TaskAlertPreferencesStore.Save(_preferencesPath, _preferences);
                TrySaveStateUnsafe();
                snapshot = SnapshotUnsafe();
            }
        }

        RaiseChanged(snapshot);
    }

    public void SetDetectedBank(int bank)
    {
        if (bank is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(bank), "The throttle bank must be M1 through M5.");
        }

        TaskAlertSnapshot? snapshot = null;
        lock (_sync)
        {
            if (_detectedBank != bank)
            {
                _detectedBank = bank;
                snapshot = SnapshotUnsafe();
            }
        }

        RaiseChanged(snapshot);
    }

    public bool AcknowledgeTerminal(int slot, string sessionId)
    {
        TaskAlertSnapshot? snapshot = null;
        lock (_sync)
        {
            if (_pool.AcknowledgeTerminal(slot, sessionId, DateTimeOffset.UtcNow))
            {
                TrySaveStateUnsafe();
                snapshot = SnapshotUnsafe();
            }
        }

        RaiseChanged(snapshot);
        return snapshot is not null;
    }

    public async ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        _cancellation.Cancel();
        try
        {
            await _reducerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        lock (_sync)
        {
            TrySaveStateUnsafe();
        }

        _cancellation.Dispose();
        _eventSignal.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task RunReducerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _eventSignal
                .WaitAsync(TimeSpan.FromMilliseconds(250), cancellationToken)
                .ConfigureAwait(false);

            var changed = false;
            while (_events.Reader.TryRead(out var taskEvent))
            {
                lock (_sync)
                {
                    var stateChanged = _pool.Advance(taskEvent.ReceivedAt);
                    var existing = _pool.Assignments.FirstOrDefault(assignment =>
                        string.Equals(assignment.SessionId, taskEvent.SessionId, StringComparison.Ordinal));
                    var droppedBefore = _pool.DroppedEventCount;
                    var applied = _pool.Apply(taskEvent);
                    stateChanged |= applied;
                    var dropped = droppedBefore != _pool.DroppedEventCount;
                    var assignment = _pool.Assignments.FirstOrDefault(candidate =>
                        string.Equals(candidate.SessionId, taskEvent.SessionId, StringComparison.Ordinal));
                    AddRecentEventUnsafe(new TaskAlertEventTrace(
                        taskEvent.ReceivedAt,
                        taskEvent.Event,
                        taskEvent.SessionId,
                        taskEvent.TurnId,
                        assignment?.Slot,
                        assignment?.State,
                        dropped
                            ? TaskAlertEventResult.Dropped
                            : !applied
                                ? TaskAlertEventResult.Ignored
                                : taskEvent.Event == CodexLifecycleEvent.Stop
                                    ? TaskAlertEventResult.StopGrace
                                    : existing is null
                                        ? TaskAlertEventResult.Assigned
                                        : TaskAlertEventResult.Updated));
                    if (stateChanged)
                    {
                        TrySaveStateUnsafe();
                    }

                    changed = true;
                }
            }

            lock (_sync)
            {
                var advanced = _pool.Advance(DateTimeOffset.UtcNow);
                if (advanced)
                {
                    TrySaveStateUnsafe();
                    changed = true;
                }
            }

            if (changed)
            {
                RaiseChanged(GetSnapshot());
            }
        }
    }

    private TaskAlertSnapshot SnapshotUnsafe() => new(
        _pool.Enabled,
        _pool.Assignments,
        _pool.DroppedEventCount,
        _detectedBank ?? _preferences.Bank,
        _detectedBank is not null,
        [.. _recentEvents]);

    private void RestoreStateUnsafe()
    {
        try
        {
            _pool.Restore(TaskAlertStateStore.Load(_statePath));
            _pool.Advance(DateTimeOffset.UtcNow);
            if (_pool.Assignments.Count > 0)
            {
                _log($"Restored {_pool.Assignments.Count} task-alert assignment(s).");
            }
        }
        catch (Exception exception) when (exception is JsonException
                                               or InvalidDataException
                                               or NotSupportedException)
        {
            try
            {
                var quarantinePath = TaskAlertStateStore.Quarantine(_statePath);
                _log(quarantinePath is null
                    ? $"Task-alert state was invalid; starting empty: {exception.Message}"
                    : $"Task-alert state was invalid and moved to {quarantinePath}: {exception.Message}");
            }
            catch (Exception quarantineException) when (IsFileFailure(quarantineException))
            {
                _log(
                    $"Task-alert state was invalid and could not be moved aside: "
                    + quarantineException.Message);
            }
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            _log($"Could not load task-alert state; starting empty: {exception.Message}");
        }
    }

    private void TrySaveStateUnsafe()
    {
        try
        {
            TaskAlertStateStore.Save(_statePath, _pool.CaptureState());
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            _log($"Could not save task-alert state: {exception.Message}");
        }
    }

    private static bool IsFileFailure(Exception exception) => exception is IOException
        or UnauthorizedAccessException
        or JsonException
        or NotSupportedException;

    private void AddRecentEventUnsafe(TaskAlertEventTrace trace)
    {
        _recentEvents.Enqueue(trace);
        while (_recentEvents.Count > MaximumRecentEvents)
        {
            _recentEvents.Dequeue();
        }
    }

    private void RaiseChanged(TaskAlertSnapshot? snapshot)
    {
        if (snapshot is not null)
        {
            Changed?.Invoke(this, snapshot);
        }
    }
}
