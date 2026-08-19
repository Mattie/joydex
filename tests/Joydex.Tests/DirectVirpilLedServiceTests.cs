using Joydex.Core.TaskAlerts;
using Joydex.Virpil;
using Joydex.Windows.TaskAlerts;

namespace Joydex.Tests;

public sealed class DirectVirpilLedServiceTests
{
    [Fact]
    public async Task AppliesAlertThenCompleteBaselineWithoutThrottleReset()
    {
        var factory = new RecordingFactory();
        var options = TaskAlertLedOptions.CreateDefault() with { Mode = TaskAlertLedOutputMode.DirectHid };
        var initial = Snapshot([], options);
        await using var service = new DirectVirpilLedService(
            factory,
            new NoConflicts(),
            _ => { },
            initial,
            options);

        service.Apply(initial);
        Assert.True(await service.WaitForIdleAsync(TimeSpan.FromSeconds(2)));
        var throttle = factory.For(VirpilDevices.Throttle);
        var alpha = factory.For(VirpilDevices.Alpha);
        Assert.Equal(0x66, Assert.Single(throttle.Reports).Bytes[1]);
        Assert.Equal([0x64, 0x67], alpha.Reports.Select(report => report.Bytes[1]));
        Assert.Equal(1, alpha.Batches);

        var now = DateTimeOffset.UtcNow;
        service.Apply(Snapshot(
            [new TaskAlertAssignment(1, "session", null, TaskAlertState.Approval, now)],
            options));
        Assert.True(await service.WaitForIdleAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0x8F, throttle.Reports[^1].Bytes[5]);
        Assert.Equal(0x8F, alpha.Reports[^1].Bytes[5]);

        var throttleBeforeClear = throttle.Reports.Count;
        var alphaBeforeClear = alpha.Reports.Count;
        var alphaBatchesBeforeClear = alpha.Batches;
        service.Apply(Snapshot([], options));
        Assert.True(await service.WaitForIdleAsync(TimeSpan.FromSeconds(2)));

        var throttleClear = Assert.Single(throttle.Reports.Skip(throttleBeforeClear));
        Assert.Equal(0x66, throttleClear.Bytes[1]);
        Assert.Equal([0x80, 0x80, 0xB0, 0x80, 0x80, 0xB0], throttleClear.Bytes[5..11]);
        Assert.Equal([0x64, 0x67], alpha.Reports.Skip(alphaBeforeClear).Select(report => report.Bytes[1]));
        Assert.Equal(alphaBatchesBeforeClear + 1, alpha.Batches);
    }

    [Fact]
    public async Task BlocksEveryWriteWhileACompetingWriterIsActive()
    {
        var factory = new RecordingFactory();
        var options = TaskAlertLedOptions.CreateDefault() with { Mode = TaskAlertLedOutputMode.DirectHid };
        var snapshot = Snapshot([], options);
        await using var service = new DirectVirpilLedService(
            factory,
            new AlwaysConflict(),
            _ => { },
            snapshot,
            options);

        service.Apply(snapshot);
        Assert.False(await service.WaitForIdleAsync(TimeSpan.FromMilliseconds(150)));
        Assert.Empty(factory.For(VirpilDevices.Throttle).Reports);
        Assert.Empty(factory.For(VirpilDevices.Alpha).Reports);
    }

    [Fact]
    public async Task OneUnavailableDeviceDoesNotBlockOrRefreshTheOther()
    {
        var factory = new RecordingFactory();
        var options = TaskAlertLedOptions.CreateDefault() with { Mode = TaskAlertLedOutputMode.DirectHid };
        var snapshot = Snapshot([], options);
        await using var service = new DirectVirpilLedService(
            factory,
            new NoConflicts(),
            _ => { },
            snapshot,
            options);
        var alpha = factory.For(VirpilDevices.Alpha);
        alpha.ThrowWrites = true;

        service.Apply(snapshot);
        await Task.Delay(1200);

        var throttle = factory.For(VirpilDevices.Throttle);
        Assert.Single(throttle.Reports);
        Assert.True(alpha.Attempts >= 2);
        alpha.ThrowWrites = false;
        service.Apply(snapshot);
        Assert.True(await service.WaitForIdleAsync(TimeSpan.FromSeconds(2)));
        Assert.Single(throttle.Reports);
    }

    [Fact]
    public async Task ReplaysAfterReconnectInvalidatesAnInFlightSend()
    {
        var factory = new RecordingFactory();
        var options = TaskAlertLedOptions.CreateDefault() with { Mode = TaskAlertLedOutputMode.DirectHid };
        var snapshot = Snapshot([], options);
        await using var service = new DirectVirpilLedService(
            factory,
            new NoConflicts(),
            _ => { },
            snapshot,
            options);
        var throttle = factory.For(VirpilDevices.Throttle);
        using var sendStarted = new ManualResetEventSlim();
        using var releaseSend = new ManualResetEventSlim();
        throttle.SendStarted = sendStarted;
        throttle.ReleaseSend = releaseSend;

        service.Apply(snapshot);
        Assert.True(sendStarted.Wait(TimeSpan.FromSeconds(2)));
        service.RestoreAndReplay(replay: true);
        releaseSend.Set();

        Assert.True(await service.WaitForIdleAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(2, throttle.Reports.Count);
    }

    [Fact]
    public async Task InvalidatesStableFramesWhileACompetingWriterIsActive()
    {
        var factory = new RecordingFactory();
        var conflicts = new MutableConflict();
        var options = TaskAlertLedOptions.CreateDefault() with { Mode = TaskAlertLedOutputMode.DirectHid };
        var snapshot = Snapshot([], options);
        await using var service = new DirectVirpilLedService(
            factory,
            conflicts,
            _ => { },
            snapshot,
            options);

        service.Apply(snapshot);
        Assert.True(await service.WaitForIdleAsync(TimeSpan.FromSeconds(2)));
        var throttle = factory.For(VirpilDevices.Throttle);
        Assert.Single(throttle.Reports);

        conflicts.Value = true;
        await Task.Delay(1100);
        Assert.False(await service.WaitForIdleAsync(TimeSpan.FromMilliseconds(100)));
        Assert.Single(throttle.Reports);

        conflicts.Value = false;
        Assert.True(await service.WaitForIdleAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(2, throttle.Reports.Count);
    }

    [Fact]
    public async Task KeepsCrashRecoveryArmedAfterAPartialAlertWrite()
    {
        var factory = new RecordingFactory();
        var options = TaskAlertLedOptions.CreateDefault() with { Mode = TaskAlertLedOutputMode.DirectHid };
        var baseline = Snapshot([], options);
        await using var service = new DirectVirpilLedService(
            factory,
            new NoConflicts(),
            _ => { },
            baseline,
            options);
        service.Apply(baseline);
        Assert.True(await service.WaitForIdleAsync(TimeSpan.FromSeconds(2)));

        var alpha = factory.For(VirpilDevices.Alpha);
        alpha.ThrowWrites = true;
        service.Apply(Snapshot(
            [new TaskAlertAssignment(1, "session", null, TaskAlertState.Approval, DateTimeOffset.UtcNow)],
            options));
        await Task.Delay(1100);

        Assert.True(service.RestorePending);

        alpha.ThrowWrites = false;
        service.SetPaused(true);
        Assert.True(await service.WaitForIdleAsync(TimeSpan.FromSeconds(2)));
        Assert.False(service.RestorePending);
    }

    private static TaskAlertSnapshot Snapshot(
        IReadOnlyList<TaskAlertAssignment> assignments,
        TaskAlertLedOptions options) => new(
            Enabled: true,
            Assignments: assignments,
            DroppedEventCount: 0,
            Bank: 2,
            LedOutput: options);

    private sealed class RecordingFactory : IVirpilHidTransportFactory
    {
        private readonly Dictionary<byte, RecordingTransport> _transports = [];

        public bool IsAvailable(VirpilDeviceSpecification specification) => true;

        public IVirpilHidTransport Create(VirpilDeviceSpecification specification)
        {
            var transport = new RecordingTransport();
            _transports[specification.LedCommand] = transport;
            return transport;
        }

        public RecordingTransport For(VirpilDeviceSpecification specification) =>
            _transports[specification.LedCommand];
    }

    private sealed class RecordingTransport : IVirpilHidTransport
    {
        private readonly object _sync = new();
        private readonly List<VirpilLedReport> _reports = [];
        private int _attempts;
        private int _batches;

        public string? DevicePath => "test-path";

        public bool ThrowWrites { get; set; }

        public ManualResetEventSlim? SendStarted { get; set; }

        public ManualResetEventSlim? ReleaseSend { get; set; }

        public int Attempts => Volatile.Read(ref _attempts);

        public int Batches => Volatile.Read(ref _batches);

        public IReadOnlyList<VirpilLedReport> Reports
        {
            get
            {
                lock (_sync)
                {
                    return [.. _reports];
                }
            }
        }

        public void Send(byte[] logicalReport)
        {
            Interlocked.Increment(ref _attempts);
            SendStarted?.Set();
            if (ReleaseSend is { } release && !release.Wait(TimeSpan.FromSeconds(2)))
            {
                throw new TimeoutException("injected blocked write timed out");
            }

            if (ThrowWrites)
            {
                throw new IOException("injected unavailable device");
            }

            lock (_sync)
            {
                _reports.Add(new VirpilLedReport("test", (byte[])logicalReport.Clone()));
            }
        }

        public void SendBatch(IReadOnlyList<byte[]> logicalReports)
        {
            Interlocked.Increment(ref _batches);
            foreach (var logicalReport in logicalReports)
            {
                Send(logicalReport);
            }
        }

        public byte ReadShiftMask() => 0;

        public void Reset()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class NoConflicts : IVpcConflictDetector
    {
        public bool HasConflict() => false;
    }

    private sealed class AlwaysConflict : IVpcConflictDetector
    {
        public bool HasConflict() => true;
    }

    private sealed class MutableConflict : IVpcConflictDetector
    {
        public bool Value { get; set; }

        public bool HasConflict() => Value;
    }
}
