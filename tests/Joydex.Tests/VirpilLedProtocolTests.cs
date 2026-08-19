using System.Text.Json;
using Joydex.Core.TaskAlerts;
using Joydex.Virpil;
using Joydex.Windows.TaskAlerts;

namespace Joydex.Tests;

public sealed class VirpilLedProtocolTests
{
    [Theory]
    [InlineData(0x00, 0x00, 0x00, 0x80)]
    [InlineData(0xFF, 0xFF, 0x00, 0x8F)]
    [InlineData(0x55, 0x55, 0x55, 0x95)]
    [InlineData(0x00, 0x40, 0x00, 0x84)]
    [InlineData(0xFF, 0x00, 0x00, 0x83)]
    [InlineData(0x00, 0xFF, 0x00, 0x8C)]
    [InlineData(0x00, 0x00, 0xFF, 0xB0)]
    [InlineData(0x80, 0x20, 0x60, 0x92)]
    public void EncodesOverrideColors(byte red, byte green, byte blue, byte expected)
    {
        Assert.Equal(expected, VirpilLedProtocol.EncodeColor(red, green, blue));
    }

    [Theory]
    [InlineData(42, 0x80)]
    [InlineData(43, 0x81)]
    [InlineData(127, 0x81)]
    [InlineData(128, 0x82)]
    [InlineData(212, 0x82)]
    [InlineData(213, 0x83)]
    public void UsesNearestRepresentableColorLevels(byte red, byte expected)
    {
        Assert.Equal(expected, VirpilLedProtocol.EncodeColor(red, 0, 0));
    }

    [Fact]
    public void Retries38Then39ReopensAndRepeats()
    {
        var lengths = new List<int>();
        var reopenCount = 0;

        VirpilFeatureWriteRetry.Send(
            VirpilLedProtocol.BuildResetReport(),
            payload =>
            {
                lengths.Add(payload.Length);
                if (lengths.Count < 3)
                {
                    throw new IOException("injected");
                }
            },
            () => reopenCount++);

        Assert.Equal([38, 39, 38], lengths);
        Assert.Equal(1, reopenCount);
    }

    [Fact]
    public void BuildsExactCm3FeatureReport()
    {
        var frame = VirpilLedProtocol.EmptyFrame();
        frame[0] = 0x8F;

        var report = VirpilLedProtocol.BuildReport(VirpilDevices.Throttle.LedCommand, frame);

        Assert.Equal(38, report.Length);
        Assert.Equal([0x02, 0x66, 0x00, 0x00, 0x00, 0x8F], report[..6]);
        Assert.All(report[6..], value => Assert.Equal(0, value));
    }

    [Fact]
    public void MapsEveryCm3PhysicalSlotIntoTheFullFrame()
    {
        var frame = VirpilLedProtocol.EmptyFrame();
        frame[0] = 0x80;
        frame[1] = 0x8F;
        frame[2] = 0x95;
        frame[3] = 0x84;
        frame[4] = 0x83;
        frame[5] = 0xB0;

        var report = VirpilLedProtocol.BuildReport(VirpilDevices.Throttle.LedCommand, frame);

        Assert.Equal([0x80, 0x8F, 0x95, 0x84, 0x83, 0xB0], report[5..11]);
        Assert.All(report[11..], value => Assert.Equal(0, value));
    }

    [Fact]
    public void BuildsExactAlphaAndResetHeaders()
    {
        var alpha = VirpilLedProtocol.BuildReport(
            VirpilDevices.Alpha.LedCommand,
            VirpilLedProtocol.EmptyFrame());
        var reset = VirpilLedProtocol.BuildResetReport();

        Assert.Equal([0x02, 0x67, 0x00, 0x00, 0x00], alpha[..5]);
        Assert.Equal([0x02, 0x64, 0x00, 0x00, 0x00], reset[..5]);
        Assert.Equal(38, alpha.Length);
        Assert.Equal(38, reset.Length);
    }

    [Fact]
    public void SkipsAnAcceptedDuplicateFrame()
    {
        var frame = VirpilLedProtocol.EmptyFrame();
        frame[0] = 0x8F;

        var plan = VirpilLedProtocol.PlanApply(VirpilDevices.Throttle.LedCommand, frame, frame);

        Assert.Empty(plan.Reports);
    }

    [Fact]
    public void ReleaseTransitionPlansResetThenCompleteFrame()
    {
        var accepted = VirpilLedProtocol.EmptyFrame();
        accepted[0] = 0x8F;
        var desired = VirpilLedProtocol.EmptyFrame();

        var plan = VirpilLedProtocol.PlanApply(VirpilDevices.Alpha.LedCommand, accepted, desired);

        Assert.Equal(2, plan.Reports.Count);
        Assert.Equal(0x64, plan.Reports[0].Bytes[1]);
        Assert.Equal(0x67, plan.Reports[1].Bytes[1]);
    }

    [Fact]
    public void ExhaustsExactRetrySequenceBeforeFailing()
    {
        var lengths = new List<int>();
        var reopenCount = 0;

        Assert.Throws<IOException>(() => VirpilFeatureWriteRetry.Send(
            VirpilLedProtocol.BuildResetReport(),
            payload =>
            {
                lengths.Add(payload.Length);
                throw new IOException("injected");
            },
            () => reopenCount++));

        Assert.Equal([38, 39, 38, 39], lengths);
        Assert.Equal(1, reopenCount);
    }

    [Fact]
    public void ComposerUsesCompleteM2BaselineWithoutFirmwareFallback()
    {
        var frames = VirpilLedFrameComposer.Compose(
            EmptySnapshot(bank: 2),
            TaskAlertLedOptions.CreateDefault());

        Assert.Equal([0x80, 0x80, 0xB0, 0x80, 0x80, 0xB0], frames.Throttle[..6]);
        Assert.All(frames.Throttle[..6], value => Assert.True(value >= 0x80));
        Assert.Equal(0, frames.Alpha[0]);
    }

    [Fact]
    public void AlertClearReturnsDirectlyToConfiguredBankBaseline()
    {
        var now = DateTimeOffset.UtcNow;
        var alert = EmptySnapshot(bank: 2) with
        {
            Assignments =
            [
                new TaskAlertAssignment(1, "session", null, TaskAlertState.Approval, now),
            ],
        };
        var alertFrames = VirpilLedFrameComposer.Compose(alert, TaskAlertLedOptions.CreateDefault());
        var clearFrames = VirpilLedFrameComposer.Compose(
            alert with { Assignments = [] },
            TaskAlertLedOptions.CreateDefault());

        var plan = VirpilLedProtocol.PlanApply(
            VirpilDevices.Throttle.LedCommand,
            alertFrames.Throttle,
            clearFrames.Throttle);

        var report = Assert.Single(plan.Reports);
        Assert.Equal(0x66, report.Bytes[1]);
        Assert.Equal([0x80, 0x80, 0xB0, 0x80, 0x80, 0xB0], report.Bytes[5..11]);
    }

    [Fact]
    public void GuardianRecoveryDocumentContainsNoAlertDirectFrames()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"joydex-led-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryPath = Path.Combine(directory, "recovery.json");
        try
        {
            using var guardian = new GuardianController(
                Path.Combine(directory, "missing-guardian.exe"),
                _ => { },
                recoveryPath);
            guardian.UpdateRecovery(EmptySnapshot(bank: 2) with
            {
                LedOutput = TaskAlertLedOptions.CreateDefault() with { Mode = TaskAlertLedOutputMode.DirectHid },
            });

            var document = JsonSerializer.Deserialize<JoydexLedRecoveryDocument>(File.ReadAllText(recoveryPath));
            Assert.NotNull(document);
            Assert.Equal(JoydexLedRecoveryDocument.DirectHidBackend, document.Backend);
            Assert.Equal([0x80, 0x80, 0xB0, 0x80, 0x80, 0xB0], document.ThrottleFrame![..6]);
            Assert.True(document.ResetAlpha);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static TaskAlertSnapshot EmptySnapshot(int bank) => new(
        Enabled: true,
        Assignments: [],
        DroppedEventCount: 0,
        Bank: bank,
        LedOutput: TaskAlertLedOptions.CreateDefault());
}
