using Joydex.Core.TaskAlerts;
using Joydex.Windows.TaskAlerts;

namespace Joydex.Tests;

public sealed class VirpilShiftModeMonitorTests
{
    [Theory]
    [InlineData(0x01, 1)]
    [InlineData(0x02, 2)]
    [InlineData(0x04, 3)]
    [InlineData(0x08, 4)]
    [InlineData(0x10, 5)]
    public void DecodesSingleVirpilShiftChannel(byte shiftMask, int expectedBank) =>
        Assert.Equal(expectedBank, VirpilShiftModeReader.DecodeBank(shiftMask));

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x03)]
    [InlineData(0x20)]
    public void RejectsAmbiguousOrUnsupportedVirpilShiftMask(byte shiftMask) =>
        Assert.Null(VirpilShiftModeReader.DecodeBank(shiftMask));

    [Fact]
    public async Task PublishesDistinctPhysicalBanksAndDisposesSource()
    {
        var source = new SequenceShiftModeSource([0x02, 0x02, 0x04]);
        var banks = new List<int>();
        var observedBoth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = new VirpilShiftModeMonitor(
            source,
            bank =>
            {
                lock (banks)
                {
                    banks.Add(bank);
                    if (banks.Count == 2)
                    {
                        observedBoth.TrySetResult();
                    }
                }
            },
            _ => { },
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1));

        monitor.Start();
        await observedBoth.Task.WaitAsync(TimeSpan.FromSeconds(1));

        lock (banks)
        {
            Assert.Equal([2, 3], banks);
        }

        await monitor.DisposeAsync();
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task DetectedBankIsLiveStateAndDoesNotRewriteFallbackPreference()
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-shift-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "task-alerts.json");
        Directory.CreateDirectory(directory);
        try
        {
            await using (var coordinator = new TaskAlertCoordinator(path))
            {
                coordinator.SetDetectedBank(4);

                var snapshot = coordinator.GetSnapshot();
                Assert.Equal(4, snapshot.Bank);
                Assert.True(snapshot.BankAutomaticallyDetected);
            }

            var stored = TaskAlertPreferencesStore.LoadOrCreate(path);
            Assert.Equal(2, stored.Bank);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class SequenceShiftModeSource(IEnumerable<byte> values) : IVirpilShiftModeSource
    {
        private readonly Queue<byte> _values = new(values);
        private byte _last;

        public bool Disposed { get; private set; }

        public byte ReadShiftMask()
        {
            lock (_values)
            {
                if (_values.TryDequeue(out var value))
                {
                    _last = value;
                }

                return _last;
            }
        }

        public void Dispose() => Disposed = true;
    }
}
