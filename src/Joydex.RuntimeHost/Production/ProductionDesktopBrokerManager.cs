using Joydex.App;

namespace Joydex.RuntimeHost.Production;

/// <summary>Shares the existing Desktop bridge worker between Voice, Pebble, and explicit commands.</summary>
internal sealed class ProductionDesktopBrokerManager : IAsyncDisposable
{
    private readonly string _executablePath;
    private readonly string _pipeName;
    private readonly Action<string> _log;
    private readonly CancellationToken _runtimeCancellationToken;
    private readonly IProductionDesktopBrokerStarter _starter;
    private readonly TimeSpan _initialRestartDelay;
    private readonly CancellationTokenSource _restartCancellation = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _disposeGate = new();
    private readonly List<Task> _observerTasks = [];
    private readonly TaskCompletionSource _terminalCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IProductionDesktopBrokerProcess? _broker;
    private DesktopTaskBridgeOwnershipCleanupException? _terminalFailure;
    private int _leases;
    private Task? _restartTask;
    private Task? _disposeTask;
    private bool _disposed;

    public ProductionDesktopBrokerManager(
        string executablePath,
        string pipeName,
        Action<string> log,
        CancellationToken runtimeCancellationToken)
        : this(
            executablePath,
            pipeName,
            log,
            runtimeCancellationToken,
            ProductionDesktopBrokerStarter.Instance,
            TimeSpan.FromSeconds(1))
    {
    }

    internal ProductionDesktopBrokerManager(
        string executablePath,
        string pipeName,
        Action<string> log,
        CancellationToken runtimeCancellationToken,
        IProductionDesktopBrokerStarter starter,
        TimeSpan initialRestartDelay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (initialRestartDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialRestartDelay),
                initialRestartDelay,
                "Restart delay must be positive.");
        }

        _executablePath = Path.GetFullPath(executablePath);
        _pipeName = pipeName.Trim();
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _runtimeCancellationToken = runtimeCancellationToken;
        _starter = starter ?? throw new ArgumentNullException(nameof(starter));
        _initialRestartDelay = initialRestartDelay;
    }

    internal Task TerminalCompletion => _terminalCompletion.Task;

    public async Task<ProductionDesktopBrokerLease> AcquireAsync(
        CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _runtimeCancellationToken,
            cancellationToken);
        await _lifecycle.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfTerminal();
            if (_broker is null || _broker.Completion.IsCompleted)
            {
                if (_broker is not null)
                {
                    var completedBroker = _broker;
                    _broker = null;
                    await DisposeBrokerLockedAsync(
                            completedBroker,
                            "A completed Desktop Task Bridge broker could not be released before replacement.")
                        .ConfigureAwait(false);
                }

                ThrowIfTerminal();
                try
                {
                    _broker = await _starter.StartAsync(
                            _executablePath,
                            _pipeName,
                            _log,
                            linkedCancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (DesktopTaskBridgeOwnershipCleanupException exception)
                {
                    RecordTerminal(exception);
                    throw;
                }
                ObserveBroker(_broker);
            }

            checked { _leases++; }
            return new ProductionDesktopBrokerLease(this, _pipeName);
        }
        finally
        {
            _lifecycle.Release();
        }
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
        _restartCancellation.Cancel();
        var failures = new List<Exception>();
        Task? restartTask;
        Task[] observerTasks;
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _leases = 0;
            if (_broker is not null)
            {
                var broker = _broker;
                _broker = null;
                try
                {
                    await DisposeBrokerLockedAsync(
                            broker,
                            "The active Desktop Task Bridge broker could not be released during shutdown.")
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            restartTask = _restartTask;
            observerTasks = _observerTasks.ToArray();
        }
        finally
        {
            _lifecycle.Release();
        }

        await CollectBackgroundFailureAsync(restartTask, failures).ConfigureAwait(false);
        foreach (var observerTask in observerTasks)
        {
            await CollectBackgroundFailureAsync(observerTask, failures).ConfigureAwait(false);
        }

        if (_terminalFailure is not null && !failures.Contains(_terminalFailure))
        {
            failures.Add(_terminalFailure);
        }

        if (failures.Count == 1)
        {
            throw failures[0];
        }
        if (failures.Count > 1)
        {
            throw new AggregateException(
                "Desktop Task Bridge broker cleanup did not complete.",
                failures);
        }
    }

    internal async ValueTask ReleaseAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }
            if (_leases > 0)
            {
                _leases--;
            }
            if (_leases == 0 && _broker is not null)
            {
                var broker = _broker;
                _broker = null;
                await DisposeBrokerLockedAsync(
                        broker,
                        "The last Desktop Task Bridge lease could not release its broker process.")
                    .ConfigureAwait(false);
            }

            ThrowIfTerminal();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void ObserveBroker(IProductionDesktopBrokerProcess broker)
    {
        _observerTasks.RemoveAll(task => task.IsCompleted);
        var observer = ObserveBrokerAsync(broker);
        _observerTasks.Add(observer);
    }

    private async Task ObserveBrokerAsync(IProductionDesktopBrokerProcess broker)
    {
        try
        {
            await broker.Completion
                .WaitAsync(_restartCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_restartCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _log("Desktop Task Bridge broker monitor failed: " + exception.Message);
        }

        try
        {
            await _lifecycle.WaitAsync(_runtimeCancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_disposed || !ReferenceEquals(_broker, broker))
            {
                return;
            }

            _broker = null;
            try
            {
                await DisposeBrokerLockedAsync(
                        broker,
                        "The exited Desktop Task Bridge broker could not confirm process cleanup.")
                    .ConfigureAwait(false);
            }
            catch (DesktopTaskBridgeOwnershipCleanupException)
            {
                _log("Desktop Task Bridge broker cleanup is unconfirmed; automatic restart is disabled for this process.");
                return;
            }

            if (_leases > 0 && _terminalFailure is null)
            {
                _log("Desktop Task Bridge broker worker exited; scheduling a restart.");
                _restartTask ??= RestartWhileNeededAsync();
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task RestartWhileNeededAsync()
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _runtimeCancellationToken,
            _restartCancellation.Token);
        var delay = _initialRestartDelay;
        while (!linkedCancellation.IsCancellationRequested)
        {
            await Task.Delay(delay, linkedCancellation.Token).ConfigureAwait(false);
            await _lifecycle.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
            try
            {
                if (_disposed || _leases == 0 || _broker is not null || _terminalFailure is not null)
                {
                    _restartTask = null;
                    return;
                }

                try
                {
                    _broker = await _starter.StartAsync(
                            _executablePath,
                            _pipeName,
                            _log,
                            linkedCancellation.Token)
                        .ConfigureAwait(false);
                    ObserveBroker(_broker);
                    _restartTask = null;
                    _log("Desktop Task Bridge broker worker restarted.");
                    return;
                }
                catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (DesktopTaskBridgeOwnershipCleanupException cleanupFailure)
                {
                    RecordTerminal(cleanupFailure);
                    _restartTask = null;
                    _log("Desktop Task Bridge broker cleanup is unconfirmed; automatic restart is disabled for this process.");
                    return;
                }
                catch (Exception exception)
                {
                    _log("Desktop Task Bridge broker restart failed: " + exception.Message);
                    delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
                }
            }
            finally
            {
                _lifecycle.Release();
            }
        }
    }

    private async Task DisposeBrokerLockedAsync(
        IProductionDesktopBrokerProcess broker,
        string failureMessage)
    {
        try
        {
            await broker.DisposeAsync().ConfigureAwait(false);
        }
        catch (DesktopTaskBridgeOwnershipCleanupException exception)
        {
            RecordTerminal(exception);
            throw;
        }
        catch (Exception exception)
        {
            var cleanupFailure = new DesktopTaskBridgeOwnershipCleanupException(
                failureMessage,
                [exception]);
            RecordTerminal(cleanupFailure);
            throw cleanupFailure;
        }
    }

    private void RecordTerminal(DesktopTaskBridgeOwnershipCleanupException exception)
    {
        _terminalFailure ??= exception;
        _terminalCompletion.TrySetException(_terminalFailure);
    }

    private void ThrowIfTerminal()
    {
        if (_terminalFailure is not null)
        {
            throw _terminalFailure;
        }
    }

    private static async Task CollectBackgroundFailureAsync(
        Task? task,
        List<Exception> failures)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }
}

internal sealed class ProductionDesktopBrokerLease : IAsyncDisposable
{
    private ProductionDesktopBrokerManager? _owner;
    private readonly Task _completion;

    public ProductionDesktopBrokerLease(ProductionDesktopBrokerManager owner, string pipeName)
    {
        _owner = owner;
        _completion = owner.TerminalCompletion;
        PipeName = pipeName;
    }

    public string PipeName { get; }

    public Task Completion => _completion;

    public ValueTask DisposeAsync()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        return owner is null ? ValueTask.CompletedTask : owner.ReleaseAsync();
    }
}

internal interface IProductionDesktopBrokerProcess : IAsyncDisposable
{
    Task Completion { get; }
}

internal interface IProductionDesktopBrokerStarter
{
    Task<IProductionDesktopBrokerProcess> StartAsync(
        string executablePath,
        string pipeName,
        Action<string> log,
        CancellationToken cancellationToken);
}

internal sealed class ProductionDesktopBrokerStarter : IProductionDesktopBrokerStarter
{
    public static ProductionDesktopBrokerStarter Instance { get; } = new();

    public async Task<IProductionDesktopBrokerProcess> StartAsync(
        string executablePath,
        string pipeName,
        Action<string> log,
        CancellationToken cancellationToken) => new ProductionDesktopBrokerProcess(
            await DesktopTaskBridgeBrokerProcess.StartAsync(
                    executablePath,
                    pipeName,
                    log,
                    cancellationToken)
                .ConfigureAwait(false));

    private sealed class ProductionDesktopBrokerProcess(DesktopTaskBridgeBrokerProcess process)
        : IProductionDesktopBrokerProcess
    {
        public Task Completion => process.Completion;

        public ValueTask DisposeAsync() => process.DisposeAsync();
    }
}
