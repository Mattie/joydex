namespace Joydex.App;

internal sealed class CancellableRuntimeCoordinator<T> where T : class, IAsyncDisposable
{
    private readonly object _gate = new();
    private T? _runtime;
    private Task? _startup;
    private Task? _cleanup;
    private Task? _stop;
    private CancellationTokenSource? _startupCancellation;

    public bool IsRunning
    {
        get { lock (_gate) return _runtime is not null; }
    }

    public Task Start(
        Func<CancellationToken, Task<T>> start,
        Action<Exception> reportError,
        Func<T, Task>? observeCompletion = null)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(reportError);
        lock (_gate)
        {
            if (_stop is not null || _runtime is not null || _startup is not null)
                return _stop ?? _startup ?? Task.CompletedTask;
            var cancellation = new CancellationTokenSource();
            var startupCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _startupCancellation = cancellation;
            _startup = startupCompletion.Task;
            _ = RunStartupAsync(
                start, reportError, cancellation, startupCompletion, observeCompletion, _cleanup);
            return startupCompletion.Task;
        }
    }

    public Task StopAsync(Action<Exception> reportError)
    {
        ArgumentNullException.ThrowIfNull(reportError);
        Task? startup;
        CancellationTokenSource? cancellation;
        TaskCompletionSource stopCompletion;
        lock (_gate)
        {
            if (_stop is not null) return _stop;
            stopCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stop = stopCompletion.Task;
            startup = _startup;
            cancellation = _startupCancellation;
            if (cancellation is not null) _startupCancellation = null;
        }
        _ = RunStopAsync(reportError, startup, cancellation, stopCompletion);
        return stopCompletion.Task;
    }

    private async Task RunStopAsync(
        Action<Exception> reportError,
        Task? startup,
        CancellationTokenSource? cancellation,
        TaskCompletionSource stopCompletion)
    {
        try
        {
            try { cancellation?.Cancel(); }
            catch (Exception exception) { Report(reportError, exception); }
            if (startup is not null) await startup.ConfigureAwait(false);

            T? runtime;
            Task? cleanup;
            lock (_gate)
            {
                runtime = _runtime;
                _runtime = null;
                cleanup = _cleanup;
            }
            try
            {
                if (runtime is not null) await runtime.DisposeAsync().ConfigureAwait(false);
                if (cleanup is not null) await cleanup.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Report(reportError, exception);
            }
        }
        finally
        {
            cancellation?.Dispose();
            lock (_gate)
            {
                if (ReferenceEquals(_stop, stopCompletion.Task)) _stop = null;
            }
            stopCompletion.TrySetResult();
        }
    }

    private async Task RunStartupAsync(
        Func<CancellationToken, Task<T>> start,
        Action<Exception> reportError,
        CancellationTokenSource cancellation,
        TaskCompletionSource startupCompletion,
        Func<T, Task>? runtimeCompletion,
        Task? previousCleanup)
    {
        T? candidate = null;
        try
        {
            if (previousCleanup is not null)
                await previousCleanup.WaitAsync(cancellation.Token).ConfigureAwait(false);
            await Task.Yield();
            candidate = await start(cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            T? installed = null;
            lock (_gate)
            {
                if (_stop is null
                    && ReferenceEquals(_startup, startupCompletion.Task)
                    && ReferenceEquals(_startupCancellation, cancellation))
                {
                    _runtime = candidate;
                    installed = candidate;
                    candidate = null;
                }
            }
            if (installed is not null && runtimeCompletion is not null)
            {
                Task completionTask;
                try { completionTask = runtimeCompletion(installed); }
                catch (Exception exception)
                {
                    Report(reportError, exception);
                    completionTask = Task.CompletedTask;
                }
                _ = RetireWhenCompletedAsync(installed, completionTask, reportError);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Report(reportError, exception);
        }
        finally
        {
            if (candidate is not null)
            {
                try { await candidate.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { Report(reportError, exception); }
            }
            var disposeCancellation = false;
            lock (_gate)
            {
                if (ReferenceEquals(_startup, startupCompletion.Task)) _startup = null;
                if (ReferenceEquals(_startupCancellation, cancellation))
                {
                    _startupCancellation = null;
                    disposeCancellation = true;
                }
            }
            if (disposeCancellation) cancellation.Dispose();
            startupCompletion.TrySetResult();
        }
    }

    private async Task RetireWhenCompletedAsync(T runtime, Task completion, Action<Exception> reportError)
    {
        try { await completion.ConfigureAwait(false); }
        catch (Exception exception) { Report(reportError, exception); }

        TaskCompletionSource? cleanupCompletion = null;
        lock (_gate)
        {
            if (ReferenceEquals(_runtime, runtime))
            {
                _runtime = null;
                cleanupCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _cleanup = cleanupCompletion.Task;
            }
        }
        if (cleanupCompletion is null) return;

        try { await runtime.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { Report(reportError, exception); }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_cleanup, cleanupCompletion.Task)) _cleanup = null;
            }
            cleanupCompletion.TrySetResult();
        }
    }

    private static void Report(Action<Exception> reportError, Exception exception)
    {
        try { reportError(exception); }
        catch { }
    }
}
