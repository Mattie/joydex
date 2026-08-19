using System.Collections.Concurrent;
using HidSharp;

namespace Joydex.Virpil;

public interface IVirpilHidTransport : IDisposable
{
    string? DevicePath { get; }

    void Send(byte[] logicalReport);

    void SendBatch(IReadOnlyList<byte[]> logicalReports);

    byte ReadShiftMask();

    void Reset();
}

public interface IVirpilHidTransportFactory
{
    bool IsAvailable(VirpilDeviceSpecification specification);

    IVirpilHidTransport Create(VirpilDeviceSpecification specification);
}

public sealed class VirpilHidTransportFactory : IVirpilHidTransportFactory
{
    public bool IsAvailable(VirpilDeviceSpecification specification) =>
        VirpilHidTransport.FindDevice(specification) is not null;

    public IVirpilHidTransport Create(VirpilDeviceSpecification specification) =>
        new VirpilHidTransport(specification);
}

public sealed class VirpilHidTransport : IVirpilHidTransport
{
    private const byte SoftwareLinkReportId = 0x04;
    private static readonly ConcurrentDictionary<string, object> OperationLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private readonly VirpilDeviceSpecification _specification;
    private HidDevice? _device;
    private HidStream? _stream;

    public VirpilHidTransport(VirpilDeviceSpecification specification)
    {
        _specification = specification ?? throw new ArgumentNullException(nameof(specification));
    }

    public string? DevicePath
    {
        get
        {
            lock (_sync)
            {
                return _device?.DevicePath;
            }
        }
    }

    public void Send(byte[] logicalReport) => SendBatch([logicalReport]);

    public void SendBatch(IReadOnlyList<byte[]> logicalReports)
    {
        ArgumentNullException.ThrowIfNull(logicalReports);
        if (logicalReports.Count == 0)
        {
            return;
        }

        foreach (var logicalReport in logicalReports)
        {
            ArgumentNullException.ThrowIfNull(logicalReport);
            if (logicalReport.Length != VirpilLedProtocol.ReportLength)
            {
                throw new ArgumentException(
                    $"A logical VIRPIL report must contain {VirpilLedProtocol.ReportLength} bytes.",
                    nameof(logicalReports));
            }
        }

        lock (_sync)
        {
            EnsureDevice();
            lock (PathLock())
            {
                foreach (var logicalReport in logicalReports)
                {
                    EnsureOpen();
                    VirpilFeatureWriteRetry.Send(
                        logicalReport,
                        payload => _stream!.SetFeature(payload),
                        () =>
                        {
                            ResetStream();
                            EnsureOpen();
                        });
                }
            }
        }
    }

    public byte ReadShiftMask()
    {
        lock (_sync)
        {
            EnsureDevice();
            lock (PathLock())
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

                ResetStream();
                throw new IOException(
                    "The VIRPIL software-link feature report could not be read.",
                    firstFailure);
            }
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            ResetStream();
            _device = null;
        }
    }

    public void Dispose()
    {
        Reset();
        GC.SuppressFinalize(this);
    }

    internal static HidDevice? FindDevice(VirpilDeviceSpecification specification) => DeviceList.Local
        .GetHidDevices(specification.VendorId, specification.ProductId)
        .FirstOrDefault(candidate => candidate.GetMaxFeatureReportLength() >= VirpilLedProtocol.ReportLength);

    private void EnsureDevice()
    {
        _device ??= FindDevice(_specification)
            ?? throw new IOException(
                $"VIRPIL HID feature collection {_specification.VendorId:X4}:{_specification.ProductId:X4} is unavailable.");
    }

    private void EnsureOpen()
    {
        if (_stream is not null)
        {
            return;
        }

        if (!_device!.TryOpen(out var stream) || stream is null)
        {
            _device = null;
            throw new IOException($"Could not open the {_specification.Name} HID feature collection.");
        }

        stream.ReadTimeout = 1000;
        stream.WriteTimeout = 1000;
        _stream = stream;
    }

    private object PathLock() => OperationLocks.GetOrAdd(_device!.DevicePath, static _ => new object());

    private void ResetStream()
    {
        _stream?.Dispose();
        _stream = null;
    }
}
