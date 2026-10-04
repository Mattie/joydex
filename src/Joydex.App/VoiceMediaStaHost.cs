using Joydex.Windows.Voice;

namespace Joydex.App;

/// <summary>
/// Dispatches thread-affine Voice media work independently of ordinary UI threads.
/// </summary>
internal interface IVoiceMediaDispatcher
{
    Task Completion { get; }

    bool CheckAccess();

    Task InvokeAsync(Func<Task> action, CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns the STA and WinForms message pump used by Voice WebView2 sessions.
/// </summary>
internal sealed class VoiceMediaStaHost : IVoiceMediaDispatcher, IAsyncDisposable
{
    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _operationTimeout;
    private readonly TimeSpan _shutdownTimeout;
    private readonly Thread _thread;
    private readonly object _stateGate = new();
    private readonly object _disposeGate = new();
    private readonly TaskCompletionSource<WindowsFormsSynchronizationContext> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _threadExited =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ApplicationContext? _applicationContext;
    private Task? _disposeTask;
    private int _threadId;
    private int _stopping;

    private VoiceMediaStaHost(
        TimeSpan startupTimeout,
        TimeSpan operationTimeout,
        TimeSpan shutdownTimeout)
    {
        _startupTimeout = RequirePositive(startupTimeout, nameof(startupTimeout));
        _operationTimeout = RequirePositive(operationTimeout, nameof(operationTimeout));
        _shutdownTimeout = RequirePositive(shutdownTimeout, nameof(shutdownTimeout));
        _thread = new Thread(RunMessagePump)
        {
            IsBackground = true,
            Name = "Joydex Voice media STA",
        };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public Task Completion => _completion.Task;

    internal Task ThreadExited => _threadExited.Task;

    internal bool IsThreadAlive => _thread.IsAlive;

    public static Task<VoiceMediaStaHost> StartAsync(
        CancellationToken cancellationToken = default) =>
        StartAsync(
            DefaultStartupTimeout,
            DefaultOperationTimeout,
            DefaultShutdownTimeout,
            cancellationToken);

    internal static async Task<VoiceMediaStaHost> StartAsync(
        TimeSpan startupTimeout,
        TimeSpan operationTimeout,
        TimeSpan shutdownTimeout,
        CancellationToken cancellationToken = default)
    {
        var host = new VoiceMediaStaHost(startupTimeout, operationTimeout, shutdownTimeout);
        host._thread.Start();
        try
        {
            await host._ready.Task
                .WaitAsync(host._startupTimeout, cancellationToken)
                .ConfigureAwait(false);
            return host;
        }
        catch (Exception startupException)
        {
            try
            {
                await host.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                throw new VoiceOwnershipCleanupException(
                    "The Voice media STA failed to start and its thread could not be released. "
                    + "Its owner generation must not be replaced.",
                    [startupException, cleanupException]);
            }

            throw;
        }
    }

    public bool CheckAccess() =>
        Environment.CurrentManagedThreadId == Volatile.Read(ref _threadId);

    public Task InvokeAsync(Func<Task> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopping) != 0, this);
        return InvokeCoreAsync(action, cancellationToken, allowWhileStopping: false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _stopping, 1);
        if (!_thread.IsAlive)
        {
            await _threadExited.Task.ConfigureAwait(false);
            GC.SuppressFinalize(this);
            return;
        }

        try
        {
            await InvokeCoreAsync(
                    () =>
                    {
                        ApplicationContext? applicationContext;
                        lock (_stateGate)
                        {
                            applicationContext = _applicationContext;
                        }

                        applicationContext?.ExitThread();
                        return Task.CompletedTask;
                    },
                    CancellationToken.None,
                    allowWhileStopping: true)
                .WaitAsync(_shutdownTimeout)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SignalFailure(
                new InvalidOperationException(
                    "The Voice media STA did not accept its shutdown request.",
                    exception));
        }

        try
        {
            await _threadExited.Task.WaitAsync(_shutdownTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            var failure = new TimeoutException(
                "The Voice media STA did not exit within the shutdown timeout. "
                + "Its owner generation must not be replaced while this thread is still alive.",
                exception);
            SignalFailure(failure);
            throw failure;
        }

        if (!_thread.Join(_shutdownTimeout))
        {
            var failure = new TimeoutException(
                "The Voice media STA signaled exit but its thread could not be joined. "
                + "Its owner generation must not be replaced.");
            SignalFailure(failure);
            throw failure;
        }

        GC.SuppressFinalize(this);
    }

    private async Task InvokeCoreAsync(
        Func<Task> action,
        CancellationToken cancellationToken,
        bool allowWhileStopping)
    {
        if (!allowWhileStopping)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopping) != 0, this);
        }

        WindowsFormsSynchronizationContext context;
        try
        {
            context = await _ready.Task
                .WaitAsync(_startupTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            var failure = new TimeoutException(
                "The Voice media STA did not become ready within the startup timeout.",
                exception);
            SignalFailure(failure);
            throw failure;
        }

        if (!allowWhileStopping && _completion.Task.IsCompleted)
        {
            await ThrowForCompletedHostAsync().ConfigureAwait(false);
        }

        var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchState = 0;
        using var cancellationRegistration = cancellationToken.Register(
            () =>
            {
                if (Interlocked.CompareExchange(ref dispatchState, 2, 0) == 0)
                {
                    operation.TrySetCanceled(cancellationToken);
                }
            });
        try
        {
            context.Post(
                async _ =>
                {
                    // Cancellation can retract work that is still queued. Once an operation has
                    // begun on the STA, its caller keeps observing it through completion so
                    // cleanup cannot race an abandoned WebView2/COM operation.
                    if (Interlocked.CompareExchange(ref dispatchState, 1, 0) != 0)
                    {
                        return;
                    }

                    try
                    {
                        await action().ConfigureAwait(true);
                        operation.TrySetResult();
                    }
                    catch (Exception exception)
                    {
                        operation.TrySetException(exception);
                    }
                },
                null);
        }
        catch (Exception exception)
        {
            var failure = new InvalidOperationException(
                "The Voice media STA rejected a dispatched operation.",
                exception);
            SignalFailure(failure);
            throw failure;
        }

        Task completed;
        try
        {
            completed = await Task.WhenAny(operation.Task, _completion.Task)
                .WaitAsync(_operationTimeout)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            var failure = new TimeoutException(
                "A Voice media STA operation did not complete within the dispatch timeout.",
                exception);
            SignalFailure(failure);
            throw failure;
        }

        if (ReferenceEquals(completed, operation.Task))
        {
            await operation.Task.ConfigureAwait(false);
            return;
        }

        await ThrowForCompletedHostAsync().ConfigureAwait(false);
    }

    private async Task ThrowForCompletedHostAsync()
    {
        try
        {
            await _completion.Task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("The Voice media STA is unavailable.", exception);
        }

        throw new InvalidOperationException("The Voice media STA has stopped.");
    }

    private void RunMessagePump()
    {
        WindowsFormsSynchronizationContext? context = null;
        ApplicationContext? applicationContext = null;
        try
        {
            Volatile.Write(ref _threadId, Environment.CurrentManagedThreadId);
            context = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);
            applicationContext = new ApplicationContext();
            lock (_stateGate)
            {
                _applicationContext = applicationContext;
            }

            // Readiness is published from the running pump, so WebView2 can never be constructed
            // merely because the STA thread was created before it began processing messages.
            context.Post(_ => _ready.TrySetResult(context), null);
            Application.Run(applicationContext);
            if (Volatile.Read(ref _stopping) == 0)
            {
                SignalFailure(new InvalidOperationException("The Voice media STA stopped unexpectedly."));
            }
            else
            {
                _completion.TrySetResult();
            }
        }
        catch (Exception exception)
        {
            _ready.TrySetException(exception);
            SignalFailure(exception);
        }
        finally
        {
            lock (_stateGate)
            {
                _applicationContext = null;
            }

            SynchronizationContext.SetSynchronizationContext(null);
            applicationContext?.Dispose();
            context?.Dispose();
            Volatile.Write(ref _threadId, 0);
            _threadExited.TrySetResult();
        }
    }

    private void SignalFailure(Exception exception) =>
        _completion.TrySetException(exception);

    private static TimeSpan RequirePositive(TimeSpan value, string parameterName) =>
        value > TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, value, "Timeout must be positive.");
}
