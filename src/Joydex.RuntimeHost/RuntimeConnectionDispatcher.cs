using Joydex.Contracts;

namespace Joydex.RuntimeHost;

/// <summary>
/// Serializes callbacks for one live client. Retained runtime events and capture lifecycle changes
/// are bounded and lossless; high-rate observations occupy one replaceable queue slot per source.
/// Overflow ends the callback stream so the client reconnects and resynchronizes.
/// </summary>
internal sealed class RuntimeConnectionDispatcher : IAsyncDisposable
{
    internal const int MaximumQueuedItems = 256;
    private readonly object _gate = new();
    private readonly IRuntimeRpcClient _client;
    private readonly Guid _engineEpoch;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly Queue<QueuedCallback> _queue = new();
    private readonly Dictionary<string, QueuedCallback> _observations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly TaskCompletionSource<Exception?> _termination = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _pump;
    private Exception? _failure;
    private long _inputSequence;
    private long _inputCursor;
    private bool _disposed;

    public RuntimeConnectionDispatcher(IRuntimeRpcClient client, Guid engineEpoch)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _engineEpoch = engineEpoch;
        _pump = PumpAsync();
    }

    public Task Completion => _pump;

    public Task<Exception?> Termination => _termination.Task;

    public long InputCursor => Interlocked.Read(ref _inputCursor);

    public void EnqueueRuntimeEvent(RuntimeEvent runtimeEvent) => EnqueueCritical(
        new QueuedCallback(CallbackKind.RuntimeEvent) { RuntimeEvent = runtimeEvent });

    public void EnqueueCapture(RuntimeCaptureUpdate capture) => EnqueueCritical(
        new QueuedCallback(CallbackKind.Capture) { Capture = capture });

    public void EnqueueCommandResult(RuntimeCommandResult result) => EnqueueCritical(
        new QueuedCallback(CallbackKind.Command) { Command = result });

    public void EnqueueObservation(RuntimeInputObservation observation)
    {
        lock (_gate)
        {
            ThrowIfUnavailable();
            if (_observations.TryGetValue(observation.SourceId, out var pending))
            {
                pending.Observation = observation;
                return;
            }

            EnsureCapacityLocked();
            var queued = new QueuedCallback(CallbackKind.Observation)
            {
                Observation = observation,
            };
            _observations.Add(observation.SourceId, queued);
            _queue.Enqueue(queued);
            _available.Release();
        }
    }

    public void ThrowIfUnavailable()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_failure is not null)
            {
                throw new InvalidOperationException("The runtime callback stream has ended.", _failure);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _cancellation.Cancel();
            _available.Release();
            _termination.TrySetResult(null);
        }

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _available.Dispose();
            _cancellation.Dispose();
        }
    }

    private void EnqueueCritical(QueuedCallback queued)
    {
        lock (_gate)
        {
            ThrowIfUnavailable();
            EnsureCapacityLocked();
            _queue.Enqueue(queued);
            _available.Release();
        }
    }

    private void EnsureCapacityLocked()
    {
        if (_queue.Count < MaximumQueuedItems)
        {
            return;
        }

        _failure = new InvalidOperationException(
            "The runtime client did not consume callbacks before its bounded queue filled.");
        _termination.TrySetResult(_failure);
        _cancellation.Cancel();
        throw _failure;
    }

    private async Task PumpAsync()
    {
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                await _available.WaitAsync(_cancellation.Token).ConfigureAwait(false);
                QueuedCallback? queued;
                lock (_gate)
                {
                    if (_queue.Count == 0)
                    {
                        continue;
                    }
                    queued = _queue.Dequeue();
                    if (queued.Kind == CallbackKind.Observation
                        && queued.Observation is { } observation)
                    {
                        _observations.Remove(observation.SourceId);
                    }
                }

                await DispatchAsync(queued, _cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _failure ??= exception;
                _termination.TrySetResult(_failure);
                _cancellation.Cancel();
            }
            throw;
        }

        Exception? failure;
        lock (_gate)
        {
            failure = _failure;
        }
        if (failure is not null)
        {
            throw failure;
        }
    }

    private async Task DispatchAsync(QueuedCallback queued, CancellationToken cancellationToken)
    {
        if (queued.Kind == CallbackKind.RuntimeEvent)
        {
            await _client.RuntimeEventAsync(queued.RuntimeEvent!, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (queued.Kind == CallbackKind.Command)
        {
            await _client.RuntimeCommandCompletedAsync(queued.Command!, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (queued.Kind is not (CallbackKind.Capture or CallbackKind.Observation))
        {
            throw new InvalidOperationException("Unknown runtime callback kind.");
        }

        var sequence = checked(++_inputSequence);
        var inputEvent = queued.Kind == CallbackKind.Capture
            ? new RuntimeConnectionInputEvent(
                _engineEpoch,
                sequence,
                RuntimeConnectionInputEventKind.CaptureChanged,
                Capture: queued.Capture)
            : new RuntimeConnectionInputEvent(
                _engineEpoch,
                sequence,
                RuntimeConnectionInputEventKind.InputObserved,
                Observation: queued.Observation);
        await _client.RuntimeInputEventAsync(inputEvent, cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _inputCursor, sequence);
    }

    private enum CallbackKind
    {
        RuntimeEvent,
        Capture,
        Observation,
        Command,
    }

    private sealed class QueuedCallback(CallbackKind kind)
    {
        public CallbackKind Kind { get; } = kind;
        public RuntimeEvent? RuntimeEvent { get; init; }
        public RuntimeCaptureUpdate? Capture { get; init; }
        public RuntimeInputObservation? Observation { get; set; }
        public RuntimeCommandResult? Command { get; init; }
    }
}
