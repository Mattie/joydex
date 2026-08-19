using Joydex.Virpil;

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
    private readonly IVirpilHidTransport _transport = new VirpilHidTransport(
        VirpilDevices.Throttle with { VendorId = vendorId, ProductId = productId });

    /// <summary>
    /// Reads the eight-bit shift-channel mask currently reported by the device.
    /// </summary>
    public byte ReadShiftMask()
        => _transport.ReadShiftMask();

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
        _transport.Dispose();
        GC.SuppressFinalize(this);
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
