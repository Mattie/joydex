using Joydex.Core.TaskAlerts;

namespace Joydex.Windows.TaskAlerts;

/// <summary>
/// Publishes complete Joydex LED state snapshots to VIRPIL LinkTool.
/// LinkTool keeps the matching rules active, so Joydex does not need to refresh HID colors.
/// </summary>
public sealed class LinkToolLedService : IAsyncDisposable
{
    private static readonly TimeSpan MinimumSpacing = TimeSpan.FromMilliseconds(250);
    private readonly object _sync = new();
    private readonly ILinkToolTelemetrySender _sender;
    private readonly IVpcConflictDetector _conflicts;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _worker;
    private TaskAlertSnapshot _snapshot;
    private LinkToolTelemetryState _desired;
    private LinkToolTelemetryState? _lastSent;
    private bool _paused;
    private bool _profileDirty;
    private string? _lastStatus;
    private DateTimeOffset _lastSendAt = DateTimeOffset.MinValue;

    public LinkToolLedService(
        ILinkToolTelemetrySender sender,
        IVpcConflictDetector conflicts,
        Action<string> log,
        TaskAlertSnapshot initialSnapshot)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _conflicts = conflicts ?? throw new ArgumentNullException(nameof(conflicts));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _snapshot = initialSnapshot ?? throw new ArgumentNullException(nameof(initialSnapshot));
        _desired = LinkToolTelemetryState.From(initialSnapshot);
        _lastSent = _desired;
        _worker = RunAsync(_cancellation.Token);
    }

    public event EventHandler<string>? StatusChanged;

    public event EventHandler<bool>? ProfileDirtyChanged;

    public bool RestorePending
    {
        get
        {
            lock (_sync)
            {
                return _profileDirty && !_desired.HasAlert && _lastSent?.HasAlert == true;
            }
        }
    }

    public void Apply(TaskAlertSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            _snapshot = snapshot;
            _desired = LinkToolTelemetryState.From(snapshot, _paused);
        }

        Signal();
    }

    public void RestoreAndReplay(bool replay)
    {
        lock (_sync)
        {
            _desired = LinkToolTelemetryState.From(_snapshot, suppressAlerts: !replay || _paused);
            if (replay)
            {
                _lastSent = null;
            }
        }

        Signal();
    }

    public void SetPaused(bool paused)
    {
        lock (_sync)
        {
            _paused = paused;
            _desired = LinkToolTelemetryState.From(_snapshot, suppressAlerts: paused);
            if (!paused)
            {
                _lastSent = null;
            }
        }

        Signal();
    }

    public async Task<bool> WaitForIdleAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            lock (_sync)
            {
                if (_desired == _lastSent)
                {
                    return true;
                }
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        SetPaused(true);
        await WaitForIdleAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        _cancellation.Cancel();
        Signal();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cancellation.Dispose();
        _signal.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _signal.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);

            LinkToolTelemetryState desired;
            lock (_sync)
            {
                desired = _desired;
                if (desired == _lastSent)
                {
                    continue;
                }
            }

            if (_conflicts.HasConflict())
            {
                SetStatus("LinkTool update pending (VPC tool active)");
                continue;
            }

            if (!_sender.IsListening)
            {
                SetStatus("LinkTool inactive; start it to enable task LEDs");
                continue;
            }

            var remaining = MinimumSpacing - (DateTimeOffset.UtcNow - _lastSendAt);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
            }

            lock (_sync)
            {
                if (desired != _desired)
                {
                    Signal();
                    continue;
                }
            }

            bool sent;
            try
            {
                sent = await _sender.SendAsync(desired, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _log($"LinkTool telemetry send failed: {exception.Message}");
                sent = false;
            }

            _lastSendAt = DateTimeOffset.UtcNow;
            if (!sent)
            {
                SetStatus("LinkTool update pending");
                continue;
            }

            lock (_sync)
            {
                _lastSent = desired;
                SetProfileDirtyUnsafe(desired.HasAlert);
            }

            _log(
                $"LinkTool telemetry applied: bank=M{desired.JoydexBank}; " +
                $"primary={desired.JoydexPrimaryB1State},{desired.JoydexPrimaryB2State}," +
                $"{desired.JoydexPrimaryB4State},{desired.JoydexPrimaryB5State}; " +
                $"overflow={desired.JoydexOverflowB1State},{desired.JoydexOverflowB2State}," +
                $"{desired.JoydexOverflowB3State},{desired.JoydexOverflowB4State}," +
                $"{desired.JoydexOverflowB5State},{desired.JoydexOverflowB6State}; " +
                $"Alpha={desired.JoydexAlphaState}.");
            SetStatus(desired.HasAlert ? "LinkTool task LEDs active" : "LinkTool bank colors active");
        }
    }

    private void SetProfileDirtyUnsafe(bool dirty)
    {
        if (_profileDirty == dirty)
        {
            return;
        }

        _profileDirty = dirty;
        ProfileDirtyChanged?.Invoke(this, dirty);
    }

    private void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private void SetStatus(string status)
    {
        lock (_sync)
        {
            if (string.Equals(_lastStatus, status, StringComparison.Ordinal))
            {
                return;
            }

            _lastStatus = status;
        }

        _log(status);
        StatusChanged?.Invoke(this, status);
    }
}
