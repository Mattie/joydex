namespace Joydex.Virpil;

public sealed record VirpilDeviceSpecification(
    string Name,
    ushort VendorId,
    ushort ProductId,
    byte LedCommand,
    int PhysicalLedCount);

public static class VirpilDevices
{
    public static VirpilDeviceSpecification Throttle { get; } = new(
        "MongoosT-50CM3 Throttle",
        0x3344,
        0x8194,
        0x66,
        6);

    public static VirpilDeviceSpecification Alpha { get; } = new(
        "Constellation ALPHA-R Grip",
        0x3344,
        0x40CC,
        0x67,
        1);
}

public sealed record VirpilLedReport(string Purpose, byte[] Bytes);

public sealed record VirpilLedUpdatePlan(
    IReadOnlyList<VirpilLedReport> Reports,
    bool IsDuplicate);

public static class VirpilLedProtocol
{
    public const int FrameLength = 32;
    public const int ReportLength = 38;
    public const byte ReportId = 0x02;
    public const byte ResetCommand = 0x64;

    public static byte[] EmptyFrame() => new byte[FrameLength];

    public static byte EncodeColor(byte red, byte green, byte blue) => (byte)(
        0x80
        | Quantize(red)
        | (Quantize(green) << 2)
        | (Quantize(blue) << 4));

    public static byte[] BuildReport(byte command, ReadOnlySpan<byte> frame)
    {
        if (frame.Length != FrameLength)
        {
            throw new ArgumentException($"A VIRPIL LED frame must contain {FrameLength} bytes.", nameof(frame));
        }

        var report = new byte[ReportLength];
        report[0] = ReportId;
        report[1] = command;
        frame.CopyTo(report.AsSpan(5, FrameLength));
        return report;
    }

    public static byte[] BuildResetReport() => BuildReport(ResetCommand, EmptyFrame());

    public static VirpilLedUpdatePlan PlanApply(
        byte command,
        byte[]? acceptedFrame,
        ReadOnlySpan<byte> desiredFrame)
    {
        if (desiredFrame.Length != FrameLength)
        {
            throw new ArgumentException($"A VIRPIL LED frame must contain {FrameLength} bytes.", nameof(desiredFrame));
        }

        if (acceptedFrame is not null && acceptedFrame.AsSpan().SequenceEqual(desiredFrame))
        {
            return new VirpilLedUpdatePlan([], IsDuplicate: true);
        }

        var reports = new List<VirpilLedReport>();
        if (acceptedFrame is not null && RequiresReleaseReset(acceptedFrame, desiredFrame))
        {
            reports.Add(new VirpilLedReport("release-reset", BuildResetReport()));
        }

        reports.Add(new VirpilLedReport("complete-frame", BuildReport(command, desiredFrame)));
        return new VirpilLedUpdatePlan(reports, IsDuplicate: false);
    }

    public static bool RequiresReleaseReset(
        ReadOnlySpan<byte> acceptedFrame,
        ReadOnlySpan<byte> desiredFrame)
    {
        if (acceptedFrame.Length != FrameLength || desiredFrame.Length != FrameLength)
        {
            throw new ArgumentException($"VIRPIL LED frames must contain {FrameLength} bytes.");
        }

        for (var index = 0; index < FrameLength; index++)
        {
            if (acceptedFrame[index] >= 0x80 && desiredFrame[index] == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static byte Quantize(byte channel)
    {
        var nearest = 0;
        var nearestDistance = int.MaxValue;
        for (var level = 0; level <= 3; level++)
        {
            var value = level * 85;
            var distance = Math.Abs(channel - value);
            if (distance < nearestDistance)
            {
                nearest = level;
                nearestDistance = distance;
            }
        }

        return (byte)nearest;
    }
}
