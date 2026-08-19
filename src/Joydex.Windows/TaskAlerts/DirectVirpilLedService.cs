using Joydex.Core.TaskAlerts;
using Joydex.Virpil;

namespace Joydex.Windows.TaskAlerts;

/// <summary>Applies complete, bank-aware Joydex LED frames directly through VIRPIL HID feature reports.</summary>
public sealed class DirectVirpilLedService : ITaskAlertLedOutput
{
    private readonly object _sync = new();
    private readonly IVpcConflictDetector _conflicts;
    private readonly Action<string> _log;
    private readonly TaskAlertLedOptions _options;
    private readonly IVirpilHidTransport _throttle;
    private readonly IVirpilHidTransport _alpha;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _worker;
    private TaskAlertSnapshot _snapshot;
    private VirpilTaskAlertFrames _desired;
    private byte[]? _acceptedThrottle;
    private byte[]? _acceptedAlpha;
    private bool _paused;
    private bool _profileDirty;
    private bool _recoveryRequired;
    private bool _blockedByConflict;
    private string? _lastStatus;
    private long _generation;

    public DirectVirpilLedService(
        IVirpilHidTransportFactory transportFactory,
        IVpcConflictDetector conflicts,
        Action<string> log,
        TaskAlertSnapshot initialSnapshot,
        TaskAlertLedOptions options)
    {
        ArgumentNullException.ThrowIfNull(transportFactory);
        _conflicts = conflicts ?? throw new ArgumentNullException(nameof(conflicts));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _snapshot = initialSnapshot ?? throw new ArgumentNullException(nameof(initialSnapshot));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Normalize();
        _throttle = transportFactory.Create(VirpilDevices.Throttle);
        _alpha = transportFactory.Create(VirpilDevices.Alpha);
        _desired = VirpilLedFrameComposer.Compose(_snapshot, _options);
        _recoveryRequired = _desired.HasAlert;
        _profileDirty = _recoveryRequired;
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
                return _recoveryRequired;
            }
        }
    }

    public void Apply(TaskAlertSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            _snapshot = snapshot;
            _desired = VirpilLedFrameComposer.Compose(snapshot, _options, _paused);
            _recoveryRequired |= _desired.HasAlert;
            SetProfileDirtyUnsafe(_recoveryRequired);
            _generation++;
        }

        Signal();
    }

    public void RestoreAndReplay(bool replay)
    {
        lock (_sync)
        {
            _generation++;
            _acceptedThrottle = null;
            _acceptedAlpha = null;
            _throttle.Reset();
            _alpha.Reset();
            _desired = VirpilLedFrameComposer.Compose(_snapshot, _options, suppressAlerts: !replay || _paused);
            _recoveryRequired |= _desired.HasAlert;
            SetProfileDirtyUnsafe(_recoveryRequired);
        }

        Signal();
    }

    public void SetPaused(bool paused)
    {
        lock (_sync)
        {
            _paused = paused;
            _desired = VirpilLedFrameComposer.Compose(_snapshot, _options, suppressAlerts: paused);
            _recoveryRequired |= _desired.HasAlert;
            SetProfileDirtyUnsafe(_recoveryRequired);
            _generation++;
            if (!paused)
            {
                _acceptedThrottle = null;
                _acceptedAlpha = null;
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
                if (MatchesAccepted(_desired))
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

        _throttle.Dispose();
        _alpha.Dispose();
        _cancellation.Dispose();
        _signal.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _signal.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);

            if (_conflicts.HasConflict())
            {
                lock (_sync)
                {
                    if (!_blockedByConflict)
                    {
                        _blockedByConflict = true;
                        _generation++;
                        _acceptedThrottle = null;
                        _acceptedAlpha = null;
                        _throttle.Reset();
                        _alpha.Reset();
                    }
                }

                SetStatus("Direct VIRPIL LED update pending (LinkTool or VPC utility active)");
                continue;
            }

            VirpilTaskAlertFrames desired;
            byte[]? acceptedThrottle;
            byte[]? acceptedAlpha;
            long generation;
            lock (_sync)
            {
                _blockedByConflict = false;
                desired = _desired;
                acceptedThrottle = _acceptedThrottle;
                acceptedAlpha = _acceptedAlpha;
                generation = _generation;
                if (MatchesAccepted(desired))
                {
                    continue;
                }
            }

            Exception? throttleFailure = null;
            Exception? alphaFailure = null;
            if (acceptedThrottle is null || !acceptedThrottle.AsSpan().SequenceEqual(desired.Throttle))
            {
                try
                {
                    acceptedThrottle = SendFrame(
                        _throttle,
                        VirpilDevices.Throttle.LedCommand,
                        VirpilDevices.Throttle.PhysicalLedCount,
                        acceptedThrottle,
                        desired.Throttle);
                    lock (_sync)
                    {
                        if (generation != _generation)
                        {
                            Signal();
                            continue;
                        }

                        _acceptedThrottle = acceptedThrottle;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    lock (_sync)
                    {
                        _acceptedThrottle = null;
                        _throttle.Reset();
                    }

                    throttleFailure = exception;
                }
            }

            lock (_sync)
            {
                if (generation != _generation)
                {
                    Signal();
                    continue;
                }
            }

            if (acceptedAlpha is null || !acceptedAlpha.AsSpan().SequenceEqual(desired.Alpha))
            {
                try
                {
                    acceptedAlpha = SendFrame(
                        _alpha,
                        VirpilDevices.Alpha.LedCommand,
                        VirpilDevices.Alpha.PhysicalLedCount,
                        acceptedAlpha,
                        desired.Alpha);
                    lock (_sync)
                    {
                        if (generation != _generation)
                        {
                            Signal();
                            continue;
                        }

                        _acceptedAlpha = acceptedAlpha;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    lock (_sync)
                    {
                        _acceptedAlpha = null;
                        _alpha.Reset();
                    }

                    alphaFailure = exception;
                }
            }

            if (throttleFailure is not null || alphaFailure is not null)
            {
                var failures = string.Join(
                    "; ",
                    new[]
                    {
                        throttleFailure is null ? null : $"throttle: {throttleFailure.Message}",
                        alphaFailure is null ? null : $"Alpha: {alphaFailure.Message}",
                    }.Where(message => message is not null));
                _log($"Direct VIRPIL LED send failed: {failures}");
                SetStatus("Direct VIRPIL LED update pending");
                continue;
            }

            lock (_sync)
            {
                if (generation != _generation || !ReferenceEquals(desired, _desired))
                {
                    Signal();
                    continue;
                }

                _recoveryRequired = desired.HasAlert;
                SetProfileDirtyUnsafe(desired.HasAlert);
            }

            _log(
                $"Direct VIRPIL LED frame applied: bank=M{_snapshot.Bank}; " +
                $"throttle={FormatVisible(desired.Throttle, 6)}; alpha={desired.Alpha[0]:X2}.");
            SetStatus(desired.HasAlert ? "Direct VIRPIL task LEDs active" : "Direct VIRPIL bank colors active");
        }
    }

    private byte[] SendFrame(
        IVirpilHidTransport transport,
        byte command,
        int physicalLedCount,
        byte[]? accepted,
        byte[] desired)
    {
        var reports = new List<byte[]>();
        if (accepted is null && desired.AsSpan(0, physicalLedCount).Contains((byte)0))
        {
            reports.Add(VirpilLedProtocol.BuildResetReport());
        }

        var plan = VirpilLedProtocol.PlanApply(command, accepted, desired);
        reports.AddRange(plan.Reports.Select(report => report.Bytes));
        if (_conflicts.HasConflict())
        {
            throw new IOException("A competing VIRPIL writer became active before SetFeature.");
        }

        try
        {
            transport.SendBatch(reports);
        }
        catch
        {
            transport.Reset();
            throw;
        }

        return (byte[])desired.Clone();
    }

    private bool MatchesAccepted(VirpilTaskAlertFrames desired) =>
        _acceptedThrottle is not null
        && _acceptedThrottle.AsSpan().SequenceEqual(desired.Throttle)
        && _acceptedAlpha is not null
        && _acceptedAlpha.AsSpan().SequenceEqual(desired.Alpha);

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

    private static string FormatVisible(IReadOnlyList<byte> frame, int count) =>
        string.Join(',', frame.Take(count).Select(value => value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
}
