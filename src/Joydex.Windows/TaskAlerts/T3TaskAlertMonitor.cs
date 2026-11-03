using Joydex.Core.TaskAlerts;
using Joydex.Core.T3;
using Joydex.Windows.T3;

namespace Joydex.Windows.TaskAlerts;

/// <summary>One finite SSH sample at a time; remote failure never becomes task completion.</summary>
public sealed class T3TaskAlertMonitor(
    TaskAlertCoordinator coordinator, T3ConnectionOptions options, Action<string> log,
    T3Reader? reader = null,
    Func<IReadOnlyCollection<string>, CancellationToken, Task<T3ReadResult>>? readEnvironment = null) : IAsyncDisposable
{
    private readonly T3Reader _reader = reader ?? new T3Reader();
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _loop;
    private static long _nextGeneration = DateTimeOffset.UtcNow.UtcTicks;
    private long _generation = Interlocked.Increment(ref _nextGeneration);
    private string? _epoch;
    private string? _lastError;

    public Task Completion => _completion.Task;
    public void Start() => _loop ??= Task.Run(RunAsync);

    public async Task<bool> ReadOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = coordinator.GetSnapshot();
        // Disabled monitoring is an idle iteration, so the loop retains its normal
        // five-second cadence without SSH, temporary grants or failure backoff.
        if (!snapshot.Enabled) return true;
        var source = new TaskAlertSourceIdentity(TaskAlertBackend.RemoteT3, options.EnvironmentId);
        var known = snapshot.Assignments
            .Where(task => task.TaskIdentity.Source == source)
            .Select(task => task.TaskIdentity.NativeThreadId).ToArray();
        var result = await (readEnvironment is null ? _reader.ReadAsync(options, known, cancellationToken)
            : readEnvironment(known, cancellationToken)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Success && result.EnvironmentId != options.EnvironmentId)
            result = new() { ErrorClass = "EnvironmentMismatch", Reason = "The metadata source does not match the desktop environment identity." };
        if (!result.Success)
        {
            coordinator.SetSourceUnavailable(source, result);
            var diagnostic = result.ErrorClass == "CliFailed" && result.CliExitCode is { } exitCode
                ? "CliFailed; exit=" + exitCode : result.ErrorClass;
            if (_lastError != diagnostic)
                log("T3 environment " + options.EnvironmentId + " task monitoring unavailable (" + diagnostic + "); other sources remain independent.");
            _lastError = diagnostic;
            return false;
        }
        if (_epoch != result.SourceEpoch) { _epoch = result.SourceEpoch; _generation = Interlocked.Increment(ref _nextGeneration); }
        if (_lastError is not null) log("T3 environment " + options.EnvironmentId + " task monitoring resumed.");
        _lastError = null;
        coordinator.ApplySourceSnapshot(new(source, _generation, result.Sequence,
            result.CapturedAt, result.IsComplete, result.Tasks.Select(task => Project(source, task)).ToArray(), result.CatalogObservedAt), result);
        return true;
    }

    internal static TaskAlertSourceObservation Project(TaskAlertSourceIdentity source, T3TaskMetadata task) => new(
        task.Id, task.RunId, task.Status switch
        {
            "queued" or "preparing" or "starting" or "running" => TaskAlertState.Running,
            "completed" => TaskAlertState.Completed,
            "failed" or "interrupted" or "cancelled" or "rolled_back" => TaskAlertState.Fault,
            _ => null,
        }, task.StartedAt, task.CompletedAt, task.LatestUserAuthoredMessageAt,
        task.ReadComplete, task.RequestsComplete,
        task.PendingRequests.Where(request => request.Blocking).Select(request => Request(source, task.Id, request)).ToArray(),
        task.PendingRequests.Where(request => !request.Blocking).Select(request => Request(source, task.Id, request)).ToArray(),
        new(task.Title, task.Project, task.Provider, task.Model),
        task.ResolvedQuestions.Select(request => Request(source, task.Id, request)).ToArray());

    private static TaskAlertSourceRequest Request(TaskAlertSourceIdentity source, string threadId, T3PendingRequest request) =>
        new(TaskAlertIdentity.CreateRequestKey(new(source.Backend, source.EnvironmentId, threadId), request.RunId, request.Id),
            request.RunId, request.CreatedAt);

    private async Task RunAsync()
    {
        var delay = 5;
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                bool success;
                try { success = await ReadOnceAsync(_stopping.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    var source = new TaskAlertSourceIdentity(TaskAlertBackend.RemoteT3, options.EnvironmentId);
                    coordinator.SetSourceUnavailable(source, new() { ErrorClass = error.GetType().Name, Reason = "T3 metadata is unavailable." });
                    success = false;
                }
                delay = success ? 5 : Math.Min(delay * 2, 60);
                await Task.Delay(TimeSpan.FromSeconds(delay), _stopping.Token).ConfigureAwait(false);
            }
            _completion.TrySetResult();
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { _completion.TrySetResult(); }
        catch (Exception error) { _completion.TrySetException(error); }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        if (_loop is not null) await _loop.ConfigureAwait(false);
        _completion.TrySetResult();
        _stopping.Dispose();
    }
}
