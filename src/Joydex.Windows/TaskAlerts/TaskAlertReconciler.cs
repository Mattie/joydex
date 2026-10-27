using System.Text.Json;
using Joydex.Core.TaskAlerts;

namespace Joydex.Windows.TaskAlerts;

/// <summary>Refreshes lifecycle gaps from positive Desktop observations while hooks keep working.</summary>
public sealed class TaskAlertReconciler(
    TaskAlertCoordinator coordinator,
    Func<IReadOnlyList<string>, CancellationToken, Task<TaskAlertDesktopSnapshot>> read,
    Action<string> log) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _loop;
    private int _unknownCount;
    private bool _waitingForIdentity;

    public Task Completion => _completion.Task;

    public void Start() => _loop ??= Task.Run(RunAsync);

    /// <summary>Captures a hook watermark before reading, then applies only unsuperseded observations.</summary>
    public async Task<int> ReconcileOnceAsync(CancellationToken cancellationToken)
    {
        if (!coordinator.GetSnapshot().Enabled) return 0;
        var checkpoint = coordinator.CaptureReconciliationCheckpoint();
        if (checkpoint.KnownTaskIds.Length == 0)
        {
            if (!_waitingForIdentity) log("Task-alert Desktop reconciliation is waiting for a local hook identity; hooks remain active.");
            _waitingForIdentity = true;
            return 0;
        }
        if (_waitingForIdentity) log("Task-alert Desktop reconciliation received a local hook identity.");
        _waitingForIdentity = false;
        var snapshot = await read(checkpoint.KnownTaskIds, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.UnavailableCount != _unknownCount)
        {
            if (snapshot.UnavailableCount > 0)
                log($"Task-alert Desktop reconciliation left {snapshot.UnavailableCount} unknown task states unchanged.");
            else log("Task-alert Desktop task states are readable again.");
            _unknownCount = snapshot.UnavailableCount;
        }
        return coordinator.Reconcile(checkpoint, snapshot);
    }

    private async Task RunAsync()
    {
        var unavailable = false;
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                try
                {
                    var changed = await ReconcileOnceAsync(_stopping.Token).ConfigureAwait(false);
                    if (unavailable) log("Task-alert Desktop reconciliation resumed.");
                    unavailable = false;
                    if (changed > 0) log($"Task-alert Desktop reconciliation updated {changed} assignments.");
                }
                catch (Exception exception) when (exception is IOException or TimeoutException
                    or InvalidOperationException or JsonException)
                {
                    if (!unavailable) log($"Task-alert Desktop reconciliation unavailable ({exception.GetType().Name}); hooks remain active.");
                    unavailable = true;
                }
                await Task.Delay(TimeSpan.FromSeconds(5), _stopping.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (Exception exception) { _completion.TrySetException(exception); }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        if (_loop is not null) await _loop.ConfigureAwait(false);
        _stopping.Dispose();
        _completion.TrySetResult();
    }
}
