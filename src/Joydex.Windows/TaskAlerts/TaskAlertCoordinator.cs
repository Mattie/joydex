using System.Threading.Channels;
using Joydex.Core.TaskAlerts;

namespace Joydex.Windows.TaskAlerts;

public sealed record TaskAlertSnapshot(
    bool Enabled,
    IReadOnlyList<TaskAlertAssignment> Assignments,
    long DroppedEventCount,
    int Bank = 2,
    bool BankAutomaticallyDetected = false);

public sealed class TaskAlertCoordinator : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly string _preferencesPath;
    private readonly TaskAlertPool _pool;
    private readonly Channel<TaskAlertEvent> _events = Channel.CreateUnbounded<TaskAlertEvent>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _eventSignal = new(0);
    private readonly Task _reducerTask;
    private TaskAlertPreferences _preferences;
    private int? _detectedBank;

    public TaskAlertCoordinator(string preferencesPath)
    {
        _preferencesPath = preferencesPath ?? throw new ArgumentNullException(nameof(preferencesPath));
        _preferences = TaskAlertPreferencesStore.LoadOrCreate(preferencesPath);
        _pool = new TaskAlertPool();
        if (!_preferences.Enabled)
        {
            _pool.SetEnabled(false);
        }

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
            if (_pool.AcknowledgeTerminal(slot, sessionId))
            {
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
                    var droppedBefore = _pool.DroppedEventCount;
                    changed |= _pool.Apply(taskEvent);
                    changed |= droppedBefore != _pool.DroppedEventCount;
                }
            }

            lock (_sync)
            {
                changed |= _pool.Advance(DateTimeOffset.UtcNow);
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
        _detectedBank is not null);

    private void RaiseChanged(TaskAlertSnapshot? snapshot)
    {
        if (snapshot is not null)
        {
            Changed?.Invoke(this, snapshot);
        }
    }
}
