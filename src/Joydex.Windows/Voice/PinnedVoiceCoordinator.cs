using System.Diagnostics;
using Joydex.Core.Config;
using Joydex.Core.Mapping;
using Joydex.Core.Voice;
using Joydex.Windows.Actions;

namespace Joydex.Windows.Voice;

public enum PinnedVoiceStartStatus
{
    Requested,
    Simulated,
    Disabled,
    InvalidConfiguration,
    Busy,
    SessionActive,
    NavigationFailed,
    FocusTimedOut,
    ActionBlocked,
}

public sealed record PinnedVoiceStartResult(PinnedVoiceStartStatus Status, string Message)
{
    public bool Accepted => Status is PinnedVoiceStartStatus.Requested or PinnedVoiceStartStatus.Simulated;
}

/// <summary>
/// Opens one explicitly configured Codex task and invokes Joydex's resolved native Voice action.
/// Session end detection and room-audio transport remain separate compatibility adapters.
/// </summary>
public sealed class PinnedVoiceCoordinator
{
    private static readonly TimeSpan DefaultFocusTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DefaultStartConfirmationTimeout = TimeSpan.FromSeconds(20);

    private readonly SafetyOptions _safety;
    private readonly Action<string> _log;
    private readonly IPinnedVoiceTargetNavigator _navigator;
    private readonly IForegroundProcessGuard _foregroundGuard;
    private readonly Func<ActionRequest, CancellationToken, Task<ActionExecutionResult>> _executeAction;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _focusTimeout;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _startConfirmationTimeout;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly object _sessionGate = new();
    private long _sessionGeneration;
    private SessionLatchState _sessionState;

    public PinnedVoiceCoordinator(
        SafetyOptions safety,
        Action<string> log,
        IPinnedVoiceTargetNavigator navigator,
        Func<ActionRequest, CancellationToken, Task<ActionExecutionResult>> executeAction,
        IForegroundProcessGuard? foregroundGuard = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? focusTimeout = null,
        TimeSpan? pollInterval = null,
        TimeSpan? startConfirmationTimeout = null)
    {
        _safety = safety ?? throw new ArgumentNullException(nameof(safety));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        _executeAction = executeAction ?? throw new ArgumentNullException(nameof(executeAction));
        _foregroundGuard = foregroundGuard ?? new ForegroundProcessGuard();
        _delay = delay ?? Task.Delay;
        _focusTimeout = focusTimeout ?? DefaultFocusTimeout;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _startConfirmationTimeout = startConfirmationTimeout ?? DefaultStartConfirmationTimeout;
        if (_focusTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(focusTimeout));
        }

        if (_pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }

        if (_startConfirmationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(startConfirmationTimeout));
        }
    }

    public async Task<PinnedVoiceStartResult> StartAsync(
        VoicePePreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (!await _startGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Result(PinnedVoiceStartStatus.Busy, "BLOCKED Voice PE wake; another start is already in progress.");
        }

        var startAccepted = false;
        var sessionLatchAcquired = false;
        long sessionGeneration = 0;
        try
        {
            var sessionAlreadyActive = false;
            lock (_sessionGate)
            {
                if (_sessionState is not SessionLatchState.None)
                {
                    sessionAlreadyActive = true;
                }
                else
                {
                    sessionLatchAcquired = true;
                    sessionGeneration = ++_sessionGeneration;
                    _sessionState = SessionLatchState.AwaitingStart;
                }
            }

            if (sessionAlreadyActive)
            {
                return Result(
                    PinnedVoiceStartStatus.SessionActive,
                    "BLOCKED Voice PE wake; a started session is awaiting a confirmed Codex realtime stop.");
            }

            var normalized = preferences.Normalize();
            if (!normalized.Enabled)
            {
                return Result(PinnedVoiceStartStatus.Disabled, "BLOCKED Voice PE wake; the bridge is disabled.");
            }

            var errors = normalized.ValidatePinnedTask(required: true);
            if (errors.Count > 0)
            {
                return Result(
                    PinnedVoiceStartStatus.InvalidConfiguration,
                    $"BLOCKED Voice PE wake; error={string.Join("; ", errors)}");
            }

            var label = string.IsNullOrWhiteSpace(normalized.PinnedTaskLabel)
                ? normalized.PinnedTaskId
                : normalized.PinnedTaskLabel;
            if (_safety.DryRun)
            {
                return Result(
                    PinnedVoiceStartStatus.Simulated,
                    $"DRY RUN Voice PE wake; pinned={label}; target={CodexTaskReference.BuildDeepLink(normalized.PinnedTaskId)}; action=voice-chat");
            }

            if (!await _navigator.NavigateAsync(normalized.PinnedTaskId, cancellationToken).ConfigureAwait(false))
            {
                return Result(
                    PinnedVoiceStartStatus.NavigationFailed,
                    $"BLOCKED Voice PE wake; pinned={label}; error=The pinned task could not be opened.");
            }

            if (!await WaitForCodexForegroundAsync(cancellationToken).ConfigureAwait(false))
            {
                return Result(
                    PinnedVoiceStartStatus.FocusTimedOut,
                    $"BLOCKED Voice PE wake; pinned={label}; error=Codex did not become the foreground app within {_focusTimeout.TotalSeconds:g} seconds.");
            }

            var actionResult = await _executeAction(
                    new ActionRequest(
                        "Voice PE wake",
                        CompanionConfig.AlwaysBank,
                        0,
                        "wake",
                        CodexAction.StartVoiceChat,
                        DateTimeOffset.UtcNow,
                        DeviceId: "voice-pe"),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!actionResult.Executed)
            {
                return Result(
                    PinnedVoiceStartStatus.ActionBlocked,
                    $"BLOCKED Voice PE wake; pinned={label}; error={actionResult.Message}");
            }

            startAccepted = true;
            _ = ReleaseUnconfirmedStartAsync(sessionGeneration);
            return Result(
                PinnedVoiceStartStatus.Requested,
                $"EXECUTED Voice PE wake; pinned={label}; awaiting Codex realtime-session confirmation.");
        }
        finally
        {
            if (sessionLatchAcquired && !startAccepted)
            {
                ReleaseSessionLatch(sessionGeneration);
            }
            _startGate.Release();
        }
    }

    public bool ConfirmSessionStarted()
    {
        lock (_sessionGate)
        {
            if (_sessionState is SessionLatchState.None)
            {
                return false;
            }

            _sessionState = SessionLatchState.Started;
        }

        _log("CONFIRMED Codex Voice realtime session started.");
        return true;
    }

    public bool ConfirmSessionEnded()
    {
        lock (_sessionGate)
        {
            if (_sessionState is SessionLatchState.None)
            {
                return false;
            }

            _sessionState = SessionLatchState.None;
            _sessionGeneration++;
        }

        _log("CONFIRMED Codex Voice session ended; Voice PE wake is ready.");
        return true;
    }

    private async Task ReleaseUnconfirmedStartAsync(long sessionGeneration)
    {
        try
        {
            await _delay(_startConfirmationTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log($"FAILED Voice PE start-confirmation timeout; error={exception.Message}");
            return;
        }

        lock (_sessionGate)
        {
            if (_sessionGeneration != sessionGeneration
                || _sessionState is not SessionLatchState.AwaitingStart)
            {
                return;
            }

            _sessionState = SessionLatchState.None;
            _sessionGeneration++;
        }

        _log("TIMED OUT waiting for Codex Voice realtime-session confirmation; native LASTVOICE wake is ready.");
    }

    private void ReleaseSessionLatch(long sessionGeneration)
    {
        lock (_sessionGate)
        {
            if (_sessionGeneration == sessionGeneration)
            {
                _sessionState = SessionLatchState.None;
                _sessionGeneration++;
            }
        }
    }

    private async Task<bool> WaitForCodexForegroundAsync(CancellationToken cancellationToken)
    {
        var strictSafety = new SafetyOptions
        {
            DryRun = false,
            RequireCodexForeground = true,
            CodexProcessNames = _safety.CodexProcessNames,
            SimulatorProcessNames = _safety.SimulatorProcessNames,
        };
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < _focusTimeout)
        {
            if (_foregroundGuard.Check(strictSafety, actionMayBringCodexForward: false).Allowed)
            {
                return true;
            }

            await _delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }

        return _foregroundGuard.Check(strictSafety, actionMayBringCodexForward: false).Allowed;
    }

    private PinnedVoiceStartResult Result(PinnedVoiceStartStatus status, string message)
    {
        _log(message);
        return new PinnedVoiceStartResult(status, message);
    }

    private enum SessionLatchState
    {
        None,
        AwaitingStart,
        Started,
    }
}
