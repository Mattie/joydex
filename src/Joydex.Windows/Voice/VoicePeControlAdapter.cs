namespace Joydex.Windows.Voice;

public enum VoiceSessionStartStatus
{
    Requested,
    Confirmed,
    Simulated,
    Canceled,
    OwnershipConflict,
    Rejected,
    SessionActive,
}

public sealed record VoiceSessionStartResult(VoiceSessionStartStatus Status, string Message)
{
    public bool Accepted => Status is VoiceSessionStartStatus.Requested
        or VoiceSessionStartStatus.Confirmed
        or VoiceSessionStartStatus.Simulated;
}

/// <summary>
/// Converts fresh Voice PE wake edges into route-neutral Voice Session requests.
/// </summary>
public sealed class VoicePeControlAdapter(
    IVoicePeControlTransport transport,
    Func<CancellationToken, Task<VoiceSessionStartResult>> startVoice,
    Action<string> log,
    bool startVoiceSetsListeningState = false,
    Func<CancellationToken, Task>? stopVoice = null,
    Func<bool?>? toggleMicrophoneMute = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly object _startGate = new();
    private CancellationTokenSource? _pendingStartCancellation;
    private Task? _pendingStart;
    private bool _startInProgress;
    private Task? _runTask;

    public void Start()
    {
        if (_runTask is { IsCompleted: false })
        {
            throw new InvalidOperationException("The Voice PE control adapter is already running.");
        }

        _runTask = transport.RunAsync(OnSignalAsync, _cancellation.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await ReportSessionStateAsync(VoicePeSessionState.Armed, CancellationToken.None)
            .ConfigureAwait(false);
        _cancellation.Cancel();
        await transport.DisposeAsync().ConfigureAwait(false);
        Task? pendingStart;
        lock (_startGate)
        {
            pendingStart = _pendingStart;
        }
        if (pendingStart is not null)
        {
            await pendingStart.ConfigureAwait(false);
        }
        if (_runTask is not null)
        {
            try
            {
                await _runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
            }
        }

        // A coordinator end callback can still be finishing its final Armed report
        // while runtime disposal unwinds. SemaphoreSlim owns no unmanaged resource
        // unless AvailableWaitHandle is requested, so leave it alive for that tail.
        _cancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Reflects a confirmed Codex realtime start on the room endpoint.
    /// Device communication failures are logged without interrupting Codex Voice.
    /// </summary>
    public Task ConfirmSessionStartedAsync(CancellationToken cancellationToken = default) =>
        ReportSessionStateAsync(VoicePeSessionState.Listening, cancellationToken);

    /// <summary>
    /// Returns the endpoint to Armed after a confirmed Codex realtime stop.
    /// Device communication failures are logged without interrupting shutdown.
    /// </summary>
    public Task ConfirmSessionEndedAsync(CancellationToken cancellationToken = default) =>
        ReportSessionStateAsync(VoicePeSessionState.Armed, cancellationToken);

    private async ValueTask OnSignalAsync(
        VoicePeControlSignal signal,
        CancellationToken cancellationToken)
    {
        try
        {
            if (signal == VoicePeControlSignal.Hangup)
            {
                CancellationTokenSource? pendingStart;
                lock (_startGate)
                {
                    pendingStart = _pendingStartCancellation;
                }
                try
                {
                    pendingStart?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The start finished between the snapshot and cancellation.
                }
                if (stopVoice is null)
                {
                    log("IGNORED Voice PE hangup; the active route does not expose session control.");
                    return;
                }

                await stopVoice(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (signal == VoicePeControlSignal.ToggleMute)
            {
                var muted = toggleMicrophoneMute?.Invoke();
                if (!muted.HasValue)
                {
                    log("IGNORED Voice PE microphone toggle; no Joydex-owned Voice Session is active.");
                    return;
                }

                await ReportSessionStateAsync(
                        muted.Value ? VoicePeSessionState.Muted : VoicePeSessionState.Listening,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            CancellationTokenSource startupCancellation;
            TaskCompletionSource startCompletion;
            lock (_startGate)
            {
                if (_startInProgress)
                {
                    return;
                }

                startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, _cancellation.Token);
                startCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingStartCancellation = startupCancellation;
                _pendingStart = startCompletion.Task;
                _startInProgress = true;
            }

            _ = CompleteStartAsync(startupCancellation, startCompletion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            log($"FAILED Voice PE {signal} callback; error={exception.Message}");
            await ReportSessionStateAsync(VoicePeSessionState.Error, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task CompleteStartAsync(
        CancellationTokenSource startupCancellation,
        TaskCompletionSource completion)
    {
        try
        {
            await StartVoiceAsync(startupCancellation).ConfigureAwait(false);
        }
        finally
        {
            completion.TrySetResult();
        }
    }

    private async Task StartVoiceAsync(CancellationTokenSource startupCancellation)
    {
        try
        {
            var cancellationToken = startupCancellation.Token;
            var result = await startVoice(cancellationToken).ConfigureAwait(false);
            if (result.Status == VoiceSessionStartStatus.Simulated)
            {
                await ReportSessionStateAsync(VoicePeSessionState.Armed, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (result.Status == VoiceSessionStartStatus.Confirmed
                     && !startVoiceSetsListeningState)
            {
                await ReportSessionStateAsync(VoicePeSessionState.Listening, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (result.Status is VoiceSessionStartStatus.Canceled
                     or VoiceSessionStartStatus.OwnershipConflict)
            {
                await ReportSessionStateAsync(VoicePeSessionState.Armed, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else if (result.Status is not (
                         VoiceSessionStartStatus.Requested or
                         VoiceSessionStartStatus.Confirmed or
                         VoiceSessionStartStatus.SessionActive))
            {
                await ReportSessionStateAsync(VoicePeSessionState.Error, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (startupCancellation.IsCancellationRequested)
        {
            await ReportSessionStateAsync(VoicePeSessionState.Armed, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            log($"FAILED Voice PE Wake callback; error={exception.Message}");
            await ReportSessionStateAsync(VoicePeSessionState.Error, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_startGate)
            {
                _startInProgress = false;
                _pendingStart = null;
                _pendingStartCancellation = null;
            }
            startupCancellation.Dispose();
        }
    }

    private async Task ReportSessionStateAsync(
        VoicePeSessionState state,
        CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await transport.SetSessionStateAsync(state, cancellationToken).ConfigureAwait(false);
            log($"Voice PE session state changed to {state}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            log($"Voice PE could not show session state {state}; error={exception.Message}");
        }
        finally
        {
            _stateGate.Release();
        }
    }
}
