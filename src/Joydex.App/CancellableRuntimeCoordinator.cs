namespace Joydex.App;

internal sealed class CancellableRuntimeCoordinator<T> where T : class, IAsyncDisposable
{
    private readonly object _gate = new();
    private T? _runtime;
    private Task? _startup;
    private CancellationTokenSource? _startupCancellation;
    private bool _stopping;

    public bool IsRunning
    {
        get { lock (_gate) return _runtime is not null; }
    }

    public Task Start(
        Func<CancellationToken, Task<T>> start,
        Action<Exception> reportError)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(reportError);
        lock (_gate)
        {
            if (_stopping || _runtime is not null || _startup is not null)
                return _startup ?? Task.CompletedTask;
            var cancellation = new CancellationTokenSource();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _startupCancellation = cancellation;
            _startup = completion.Task;
            _ = RunStartupAsync(start, reportError, cancellation, completion);
            return completion.Task;
        }
    }

    public async Task StopAsync(Action<Exception> reportError)
    {
        ArgumentNullException.ThrowIfNull(reportError);
        Task? startup;
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            _stopping = true;
            startup = _startup;
            cancellation = _startupCancellation;
        }
        cancellation?.Cancel();
        if (startup is not null) await startup.ConfigureAwait(false);

        T? runtime;
        lock (_gate)
        {
            runtime = _runtime;
            _runtime = null;
        }
        try
        {
            if (runtime is not null) await runtime.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Report(reportError, exception);
        }
        finally
        {
            lock (_gate) _stopping = false;
        }
    }

    private async Task RunStartupAsync(
        Func<CancellationToken, Task<T>> start,
        Action<Exception> reportError,
        CancellationTokenSource cancellation,
        TaskCompletionSource completion)
    {
        T? candidate = null;
        try
        {
            await Task.Yield();
            candidate = await start(cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_stopping
                    && ReferenceEquals(_startup, completion.Task)
                    && ReferenceEquals(_startupCancellation, cancellation))
                {
                    _runtime = candidate;
                    candidate = null;
                }
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
            lock (_gate)
            {
                if (ReferenceEquals(_startup, completion.Task)) _startup = null;
                if (ReferenceEquals(_startupCancellation, cancellation)) _startupCancellation = null;
            }
            cancellation.Dispose();
            completion.TrySetResult();
        }
    }

    private static void Report(Action<Exception> reportError, Exception exception)
    {
        try { reportError(exception); }
        catch { }
    }
}
