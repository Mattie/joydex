using Joydex.Windows.Input;
using Joydex.Windows.Interop;

namespace Joydex.RuntimeHost.Production;

/// <summary>
/// Owns the hidden WinForms pump and cooperative DirectInput window used by production controller
/// and device-notification adapters.
/// </summary>
internal sealed class ProductionWindowsStaHost : IAsyncDisposable
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
    private readonly TaskCompletionSource<StaResources> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _threadExited =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IJoystickSourceFactory _inputSources;
    private ApplicationContext? _applicationContext;
    private Task? _disposeTask;
    private int _threadId;
    private int _stopping;

    private ProductionWindowsStaHost(
        TimeSpan startupTimeout,
        TimeSpan operationTimeout,
        TimeSpan shutdownTimeout)
    {
        _startupTimeout = RequirePositive(startupTimeout, nameof(startupTimeout));
        _operationTimeout = RequirePositive(operationTimeout, nameof(operationTimeout));
        _shutdownTimeout = RequirePositive(shutdownTimeout, nameof(shutdownTimeout));
        _inputSources = new StaJoystickSourceFactory(this);
        _thread = new Thread(RunMessagePump)
        {
            IsBackground = true,
            Name = "Joydex production Windows STA",
        };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public Task Completion => _completion.Task;

    internal Task ThreadExited => _threadExited.Task;

    internal bool IsThreadAlive => _thread.IsAlive;

    internal int ThreadId => Volatile.Read(ref _threadId);

    internal IntPtr WindowHandle => GetResources().CooperativeWindow.Handle;

    internal SynchronizationContext SynchronizationContext => GetResources().Context;

    internal IJoystickSourceFactory InputSources => _inputSources;

    public static Task<ProductionWindowsStaHost> StartAsync(
        CancellationToken cancellationToken = default) =>
        StartAsync(
            DefaultStartupTimeout,
            DefaultOperationTimeout,
            DefaultShutdownTimeout,
            cancellationToken);

    internal static async Task<ProductionWindowsStaHost> StartAsync(
        TimeSpan startupTimeout,
        TimeSpan operationTimeout,
        TimeSpan shutdownTimeout,
        CancellationToken cancellationToken = default)
    {
        var host = new ProductionWindowsStaHost(
            startupTimeout,
            operationTimeout,
            shutdownTimeout);
        host._thread.Start();
        try
        {
            await host._ready.Task
                .WaitAsync(host._startupTimeout, cancellationToken)
                .ConfigureAwait(false);
            return host;
        }
        catch (Exception startupFailure)
        {
            try
            {
                await host.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                throw new ProductionOwnershipCleanupException(
                    "The production Windows STA failed to start and its window or thread could "
                    + "not be released.",
                    [startupFailure, cleanupFailure]);
            }

            throw;
        }
    }

    internal bool CheckAccess() =>
        Environment.CurrentManagedThreadId == Volatile.Read(ref _threadId);

    internal void Invoke(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (CheckAccess())
        {
            action();
            return;
        }

        InvokeAsync(
                () =>
                {
                    action();
                    return Task.CompletedTask;
                },
                cancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    internal async Task<T> InvokeAsync<T>(
        Func<T> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        T result = default!;
        await InvokeAsync(
                () =>
                {
                    result = action();
                    return Task.CompletedTask;
                },
                cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    internal async Task<T> InvokeAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        T result = default!;
        await InvokeAsync(
                async () =>
                {
                    result = await action().ConfigureAwait(true);
                },
                cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    internal Task InvokeAsync(
        Func<Task> action,
        CancellationToken cancellationToken = default)
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

        Exception? windowCleanupFailure = null;
        try
        {
            await InvokeCoreAsync(
                    () =>
                    {
                        ApplicationContext? applicationContext;
                        CooperativeWindow? cooperativeWindow;
                        lock (_stateGate)
                        {
                            applicationContext = _applicationContext;
                            cooperativeWindow = _ready.Task.IsCompletedSuccessfully
                                ? _ready.Task.Result.CooperativeWindow
                                : null;
                        }

                        try
                        {
                            cooperativeWindow?.Dispose();
                        }
                        catch (Exception exception)
                        {
                            windowCleanupFailure = exception;
                        }
                        finally
                        {
                            applicationContext?.ExitThread();
                        }

                        return Task.CompletedTask;
                    },
                    CancellationToken.None,
                    allowWhileStopping: true)
                .WaitAsync(_shutdownTimeout)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SignalFailure(new InvalidOperationException(
                "The production Windows STA did not accept its shutdown request.",
                exception));
        }

        try
        {
            await _threadExited.Task.WaitAsync(_shutdownTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            var failure = new TimeoutException(
                "The production Windows STA did not exit within the shutdown timeout. Its "
                + "window and DirectInput ownership must not be replaced while this thread is alive.",
                exception);
            SignalFailure(failure);
            throw failure;
        }

        if (!_thread.Join(_shutdownTimeout))
        {
            var failure = new TimeoutException(
                "The production Windows STA signaled exit but its thread could not be joined. "
                + "Its window and DirectInput ownership must not be replaced.");
            SignalFailure(failure);
            throw failure;
        }

        if (windowCleanupFailure is not null)
        {
            throw new InvalidOperationException(
                "The production DirectInput window could not be released on its owning STA.",
                windowCleanupFailure);
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

        StaResources resources;
        try
        {
            resources = await _ready.Task
                .WaitAsync(_startupTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            var failure = new TimeoutException(
                "The production Windows STA did not become ready within the startup timeout.",
                exception);
            SignalFailure(failure);
            throw failure;
        }

        if (!allowWhileStopping && _completion.Task.IsCompleted)
        {
            await ThrowForCompletedHostAsync().ConfigureAwait(false);
        }

        if (CheckAccess())
        {
            await action().ConfigureAwait(true);
            return;
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
            resources.Context.Post(
                async _ =>
                {
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
                "The production Windows STA rejected a dispatched operation.",
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
                "A production Windows STA operation did not complete within the dispatch timeout.",
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

    private StaResources GetResources()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopping) != 0, this);
        if (_completion.Task.IsCompleted)
        {
            ThrowForCompletedHostAsync().GetAwaiter().GetResult();
        }
        return _ready.Task.GetAwaiter().GetResult();
    }

    private async Task ThrowForCompletedHostAsync()
    {
        try
        {
            await _completion.Task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("The production Windows STA is unavailable.", exception);
        }

        throw new InvalidOperationException("The production Windows STA has stopped.");
    }

    private void RunMessagePump()
    {
        WindowsFormsSynchronizationContext? context = null;
        ApplicationContext? applicationContext = null;
        CooperativeWindow? cooperativeWindow = null;
        Exception? pumpFailure = null;
        try
        {
            Volatile.Write(ref _threadId, Environment.CurrentManagedThreadId);
            context = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);
            applicationContext = new ApplicationContext();
            cooperativeWindow = new CooperativeWindow("Joydex RuntimeHost");
            lock (_stateGate)
            {
                _applicationContext = applicationContext;
            }

            var resources = new StaResources(context, cooperativeWindow);
            context.Post(_ => _ready.TrySetResult(resources), null);
            Application.Run(applicationContext);
            if (Volatile.Read(ref _stopping) == 0)
            {
                pumpFailure = new InvalidOperationException(
                    "The production Windows STA stopped unexpectedly.");
            }
        }
        catch (Exception exception)
        {
            _ready.TrySetException(exception);
            pumpFailure = exception;
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            lock (_stateGate)
            {
                _applicationContext = null;
            }

            try
            {
                cooperativeWindow?.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
            try
            {
                SynchronizationContext.SetSynchronizationContext(null);
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
            try
            {
                applicationContext?.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
            try
            {
                context?.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
            finally
            {
                Volatile.Write(ref _threadId, 0);
                try
                {
                    if (pumpFailure is not null && cleanupFailures.Count > 0)
                    {
                        SignalFailure(new AggregateException(
                            "The production Windows STA stopped and cleanup did not complete.",
                            [pumpFailure, .. cleanupFailures]));
                    }
                    else if (pumpFailure is not null)
                    {
                        SignalFailure(pumpFailure);
                    }
                    else if (cleanupFailures.Count > 0)
                    {
                        SignalFailure(new AggregateException(
                            "The production Windows STA cleanup did not complete.",
                            cleanupFailures));
                    }
                    else
                    {
                        _completion.TrySetResult();
                    }
                }
                finally
                {
                    _threadExited.TrySetResult();
                }
            }
        }
    }

    private void SignalFailure(Exception exception) => _completion.TrySetException(exception);

    private static TimeSpan RequirePositive(TimeSpan value, string parameterName) =>
        value > TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, value, "Timeout must be positive.");

    private sealed record StaResources(
        WindowsFormsSynchronizationContext Context,
        CooperativeWindow CooperativeWindow);

    private sealed class StaJoystickSourceFactory(ProductionWindowsStaHost owner)
        : IJoystickSourceFactory
    {
        public IReadOnlyList<DirectInputDeviceInfo> EnumerateDevices() =>
            owner.InvokeAsync(() => CreateFactory().EnumerateDevices()).GetAwaiter().GetResult();

        public IJoystickSource Create() =>
            new StaJoystickSource(
                owner,
                owner.InvokeAsync(() => CreateFactory().Create()).GetAwaiter().GetResult());

        private DirectInputJoystickSourceFactory CreateFactory() =>
            new(owner.GetResources().CooperativeWindow.Handle);
    }

    private sealed class StaJoystickSource(
        ProductionWindowsStaHost owner,
        IJoystickSource source) : IJoystickSource
    {
        public DirectInputDeviceInfo? ConnectedDevice => source.ConnectedDevice;

        public IReadOnlyList<Joydex.Core.Input.JoystickEvent> LatestBufferedButtonEvents =>
            source.LatestBufferedButtonEvents;

        public bool TryConnect(Joydex.Core.Config.DeviceSelector selector, out string message) =>
            source.TryConnect(selector, out message);

        public bool TryRead(
            out Joydex.Core.Input.JoystickSnapshot? snapshot,
            out string? error) => source.TryRead(out snapshot, out error);

        public void Disconnect() => source.Disconnect();

        public void Dispose() => owner.Invoke(source.Dispose);
    }
}
