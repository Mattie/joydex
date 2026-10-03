using Joydex.Core.Config;
using Joydex.Core.Input;
using Joydex.Core.Runtime;
using Joydex.Windows.Input;
using Joydex.Windows.Runtime;

namespace Joydex.Tests;

public sealed class RuntimeInputSourceProviderTests
{
    [Fact]
    public async Task RefreshMapsConfiguredDeviceAndObservesUnconfiguredCaptureInRuntime()
    {
        var configuredHardware = Device("Configured Stick");
        var newHardware = Device("New Stick");
        var factory = new FakeJoystickSourceFactory([configuredHardware, newHardware]);
        using var host = new RuntimeInputHost();
        await using var provider = new RuntimeInputSourceProvider(
            host,
            factory,
            new PollingOptions
            {
                ConnectWarmupMs = 1,
                PollIntervalMs = 1,
                ReconnectIntervalMs = 1,
                AxisTraceThreshold = 10,
            },
            _ => { });
        var config = Configured(configuredHardware);

        var catalog = provider.Refresh(config);

        var configured = Assert.Single(catalog, entry => entry.InstanceGuid == configuredHardware.InstanceGuid);
        var unconfigured = Assert.Single(catalog, entry => entry.InstanceGuid == newHardware.InstanceGuid);
        Assert.Equal("configured-stick", configured.Source.SourceId);
        Assert.Equal("configured-stick", configured.ConfiguredDeviceId);
        Assert.Equal($"directinput:{newHardware.InstanceGuid:D}", unconfigured.Source.SourceId);
        Assert.Null(unconfigured.ConfiguredDeviceId);
        Assert.True(provider.ObserveForCapture(configured.Source.SourceId));
        Assert.Empty(factory.CreatedSources);

        var active = new TaskCompletionSource<InputCaptureChangedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<InputCaptureChangedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        host.CaptureChanged += (_, update) =>
        {
            if (update.Lease.Status == InputCaptureStatus.Active)
            {
                active.TrySetResult(update);
            }
            if (update.Lease.Status == InputCaptureStatus.Completed)
            {
                completed.TrySetResult(update);
            }
        };
        var capture = host.BeginCapture(new InputCaptureRequest(
            "ui-1",
            unconfigured.Source.SourceId,
            "new binding"));
        Assert.True(capture.Accepted);
        Assert.True(provider.ObserveForCapture(unconfigured.Source.SourceId));
        var source = Assert.Single(factory.CreatedSources);
        await source.Connected.WaitAsync(TimeSpan.FromSeconds(1));
        var activeUpdate = await active.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(capture.Lease!.CaptureId, activeUpdate.Lease.CaptureId);

        source.SetSnapshot(Snapshot(1), [Press(1)]);
        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(Press(1), result.CapturedInput);
        provider.ReleaseCaptureObservation(unconfigured.Source.SourceId);
        await source.Disposed.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(host.Sources.Single(state =>
            state.Descriptor.SourceId == unconfigured.Source.SourceId).Connected);
    }

    [Fact]
    public async Task DisposeCancelsAndJoinsEveryTemporaryObserver()
    {
        var first = Device("First New Stick");
        var second = Device("Second New Stick");
        var factory = new FakeJoystickSourceFactory([first, second]);
        using var host = new RuntimeInputHost();
        var provider = new RuntimeInputSourceProvider(
            host,
            factory,
            new PollingOptions { ConnectWarmupMs = 1, PollIntervalMs = 1, ReconnectIntervalMs = 1 },
            _ => { });
        var catalog = provider.Refresh(new CompanionConfig());
        foreach (var entry in catalog)
        {
            host.BeginCapture(new InputCaptureRequest(
                $"ui-{entry.InstanceGuid:D}",
                entry.Source.SourceId,
                "binding"));
            Assert.True(provider.ObserveForCapture(entry.Source.SourceId));
        }
        var sources = factory.CreatedSources.ToArray();
        await Task.WhenAll(sources.Select(source => source.Connected.WaitAsync(TimeSpan.FromSeconds(1))));

        await provider.DisposeAsync();

        await Task.WhenAll(sources.Select(source => source.Disposed.WaitAsync(TimeSpan.FromSeconds(1))));
        Assert.All(sources, source => Assert.True(source.DisconnectCalls > 0));
    }

    private static CompanionConfig Configured(DirectInputDeviceInfo device) => new()
    {
        Devices =
        [
            new DeviceProfile
            {
                Id = "configured-stick",
                DisplayName = "Configured Stick",
                Selector = new DeviceSelector
                {
                    ProductNameContains = device.ProductName,
                    InstanceGuid = device.InstanceGuid.ToString("D"),
                    ProductGuid = device.ProductGuid.ToString("D"),
                },
            },
        ],
    };

    private static DirectInputDeviceInfo Device(string name) => new(
        name,
        name,
        Guid.NewGuid(),
        Guid.NewGuid());

    private static JoystickSnapshot Snapshot(params int[] pressed)
    {
        var buttons = new bool[8];
        foreach (var displayIndex in pressed)
        {
            buttons[displayIndex - 1] = true;
        }
        return new JoystickSnapshot(DateTimeOffset.UtcNow, buttons, [-1], [0]);
    }

    private static JoystickEvent Press(int displayIndex) => new(
        JoystickEventKind.ButtonPressed,
        displayIndex - 1,
        1);

    private sealed class FakeJoystickSourceFactory(
        IReadOnlyList<DirectInputDeviceInfo> devices) : IJoystickSourceFactory
    {
        public List<FakeJoystickSource> CreatedSources { get; } = [];

        public IReadOnlyList<DirectInputDeviceInfo> EnumerateDevices() => devices;

        public IJoystickSource Create()
        {
            var source = new FakeJoystickSource(devices);
            CreatedSources.Add(source);
            return source;
        }
    }

    private sealed class FakeJoystickSource(IReadOnlyList<DirectInputDeviceInfo> devices) : IJoystickSource
    {
        private readonly object _gate = new();
        private JoystickSnapshot _snapshot = Snapshot();
        private IReadOnlyList<JoystickEvent> _events = [];

        public DirectInputDeviceInfo? ConnectedDevice { get; private set; }

        public IReadOnlyList<JoystickEvent> LatestBufferedButtonEvents { get; private set; } = [];

        public Task Connected => ConnectedSource.Task;

        public Task Disposed => DisposedSource.Task;

        public int DisconnectCalls { get; private set; }

        private TaskCompletionSource ConnectedSource { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource DisposedSource { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryConnect(DeviceSelector selector, out string message)
        {
            ConnectedDevice = devices.Single(device =>
                Guid.TryParse(selector.InstanceGuid, out var expected)
                && device.InstanceGuid == expected);
            ConnectedSource.TrySetResult();
            message = $"Connected to {ConnectedDevice.ProductName}.";
            return true;
        }

        public bool TryRead(out JoystickSnapshot? snapshot, out string? error)
        {
            lock (_gate)
            {
                snapshot = _snapshot;
                LatestBufferedButtonEvents = _events;
                _events = [];
            }
            error = null;
            return true;
        }

        public void SetSnapshot(
            JoystickSnapshot snapshot,
            IReadOnlyList<JoystickEvent> events)
        {
            lock (_gate)
            {
                _snapshot = snapshot;
                _events = events;
            }
        }

        public void Disconnect()
        {
            DisconnectCalls++;
            ConnectedDevice = null;
        }

        public void Dispose()
        {
            Disconnect();
            DisposedSource.TrySetResult();
        }
    }
}
