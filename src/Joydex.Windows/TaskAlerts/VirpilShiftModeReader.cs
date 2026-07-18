using HidSharp;

namespace Joydex.Windows.TaskAlerts;

public interface IVirpilShiftModeSource : IDisposable
{
    byte ReadShiftMask();
}

/// <summary>
/// Reads the current VIRPIL shift mask through the controller's read-only
/// software-link feature report.
/// </summary>
public sealed class VirpilShiftModeReader(
    ushort vendorId = LinkToolProfileWriter.ThrottleVendorId,
    ushort productId = LinkToolProfileWriter.ThrottleProductId) : IVirpilShiftModeSource
{
    private const byte SoftwareLinkReportId = 4;
    private readonly ushort _vendorId = vendorId;
    private readonly ushort _productId = productId;
    private HidStream? _stream;

    /// <summary>
    /// Reads the eight-bit shift-channel mask currently reported by the device.
    /// </summary>
    public byte ReadShiftMask()
    {
        EnsureOpen();
        Exception? firstFailure = null;
        foreach (var length in new[] { 19, 20 })
        {
            var payload = new byte[length];
            payload[0] = SoftwareLinkReportId;
            try
            {
                _stream!.GetFeature(payload);
                if (payload[0] == SoftwareLinkReportId)
                {
                    return payload[2];
                }
            }
            catch (Exception exception)
            {
                firstFailure ??= exception;
            }
        }

        Reset();
        throw new IOException(
            "The VIRPIL software-link feature report could not be read.",
            firstFailure);
    }

    /// <summary>
    /// Maps a single active shift channel to the CM3 mode bank M1 through M5.
    /// Returns null for an empty, multi-channel, or out-of-range mask.
    /// </summary>
    public static int? DecodeBank(byte shiftMask)
    {
        if (shiftMask == 0 || (shiftMask & (shiftMask - 1)) != 0)
        {
            return null;
        }

        for (var bit = 0; bit < 5; bit++)
        {
            if (shiftMask == 1 << bit)
            {
                return bit + 1;
            }
        }

        return null;
    }

    public void Dispose()
    {
        Reset();
        GC.SuppressFinalize(this);
    }

    private void EnsureOpen()
    {
        if (_stream is not null)
        {
            return;
        }

        var device = DeviceList.Local
            .GetHidDevices(_vendorId, _productId)
            .FirstOrDefault(candidate => candidate.GetMaxFeatureReportLength() >= 38)
            ?? throw new IOException($"VIRPIL HID device {_vendorId:X4}:{_productId:X4} is unavailable.");
        if (!device.TryOpen(out var stream) || stream is null)
        {
            throw new IOException("The VIRPIL throttle HID interface could not be opened.");
        }

        stream.ReadTimeout = 1000;
        _stream = stream;
    }

    private void Reset()
    {
        _stream?.Dispose();
        _stream = null;
    }
}

/// <summary>
/// Polls the read-only shift report and publishes only physical bank changes.
/// </summary>
public sealed class VirpilShiftModeMonitor(
    IVirpilShiftModeSource source,
    Action<int> bankChanged,
    Action<string> log,
    TimeSpan? pollInterval = null,
    TimeSpan? failureInterval = null) : IAsyncDisposable
{
    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(200);
    private readonly TimeSpan _failureInterval = failureInterval ?? TimeSpan.FromSeconds(1);
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _runTask;
    private int _disposed;

    public void Start()
    {
        _runTask ??= RunAsync(_cancellation.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cancellation.Cancel();
        if (_runTask is not null)
        {
            try
            {
                await _runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        source.Dispose();
        _cancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        int? lastBank = null;
        var readFailed = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var shiftMask = await Task.Run(source.ReadShiftMask, cancellationToken).ConfigureAwait(false);
                var bank = VirpilShiftModeReader.DecodeBank(shiftMask);
                if (bank is not null && bank != lastBank)
                {
                    bankChanged(bank.Value);
                    lastBank = bank;
                    log($"Detected VIRPIL throttle bank M{bank.Value} from shift report 0x{shiftMask:X2}.");
                }

                if (readFailed)
                {
                    log("VIRPIL throttle bank detection resumed.");
                    readFailed = false;
                }

                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                if (!readFailed)
                {
                    log($"VIRPIL throttle bank detection paused: {exception.Message}");
                    readFailed = true;
                }

                await Task.Delay(_failureInterval, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
