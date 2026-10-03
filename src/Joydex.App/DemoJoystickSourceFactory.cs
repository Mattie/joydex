using System.Diagnostics;
using Joydex.Core.Config;
using Joydex.Core.Input;
using Joydex.Windows.Input;

namespace Joydex.App;

/// <summary>Creates deterministic controller sources for the explicit isolated demo mode.</summary>
internal sealed class DemoJoystickSourceFactory : IJoystickSourceFactory
{
    private static readonly IReadOnlyList<DemoDevice> Devices =
    [
        new(
            new DirectInputDeviceInfo(
                "Simulated Joydex CM3",
                "Simulated Joydex CM3",
                Guid.Parse("8bc01c50-cb2e-4d92-a82f-d2bc130b7681"),
                Guid.Parse("ef3536e1-c46a-46ff-8c78-09455c6a811c")),
            Button: 12,
            PressAt: TimeSpan.FromMilliseconds(1500)),
        new(
            new DirectInputDeviceInfo(
                "Simulated Joydex WarBRD",
                "Simulated Joydex WarBRD",
                Guid.Parse("db488e23-99d6-4374-935d-d0959a22d6b5"),
                Guid.Parse("7822ad6d-07b0-4995-acf2-d56142d9777a")),
            Button: 1,
            PressAt: TimeSpan.FromMilliseconds(3500)),
    ];

    private readonly Func<TimeSpan>? _testElapsed;

    public DemoJoystickSourceFactory()
    {
    }

    internal DemoJoystickSourceFactory(Func<TimeSpan> testElapsed) =>
        _testElapsed = testElapsed ?? throw new ArgumentNullException(nameof(testElapsed));

    public IReadOnlyList<DirectInputDeviceInfo> EnumerateDevices() =>
        Devices.Select(device => device.Info).ToArray();

    public IJoystickSource Create()
    {
        if (_testElapsed is not null)
        {
            return new DemoJoystickSource(Devices, _testElapsed);
        }

        var stopwatch = Stopwatch.StartNew();
        return new DemoJoystickSource(Devices, () => stopwatch.Elapsed);
    }

    private sealed class DemoJoystickSource(
        IReadOnlyList<DemoDevice> devices,
        Func<TimeSpan> elapsed) : IJoystickSource
    {
        private static readonly TimeSpan Cycle = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(500);
        private readonly object _gate = new();
        private DemoDevice? _connected;
        private TimeSpan _connectedAt;
        private bool _pressed;
        private bool _disposed;

        public DirectInputDeviceInfo? ConnectedDevice { get; private set; }

        public IReadOnlyList<JoystickEvent> LatestBufferedButtonEvents { get; private set; } = [];

        public bool TryConnect(DeviceSelector selector, out string message)
        {
            ArgumentNullException.ThrowIfNull(selector);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                DisconnectLocked();
                _connected = devices.FirstOrDefault(device => Matches(device.Info, selector));
                if (_connected is null)
                {
                    message = "No matching simulated Joydex controller was found.";
                    return false;
                }

                ConnectedDevice = _connected.Info;
                _connectedAt = elapsed();
                message = $"Connected to {ConnectedDevice.ProductName}.";
                return true;
            }
        }

        public bool TryRead(out JoystickSnapshot? snapshot, out string? error)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                error = null;
                if (_connected is null)
                {
                    snapshot = null;
                    LatestBufferedButtonEvents = [];
                    return false;
                }

                var sinceConnection = elapsed() - _connectedAt;
                var cyclePosition = TimeSpan.FromTicks(
                    Math.Max(0, sinceConnection.Ticks) % Cycle.Ticks);
                var pressed = cyclePosition >= _connected.PressAt
                    && cyclePosition < _connected.PressAt + Hold;
                LatestBufferedButtonEvents = pressed == _pressed
                    ? []
                    :
                    [
                        new JoystickEvent(
                            pressed ? JoystickEventKind.ButtonPressed : JoystickEventKind.ButtonReleased,
                            _connected.Button - 1,
                            pressed ? 1 : 0),
                    ];
                _pressed = pressed;

                var buttons = new bool[128];
                buttons[_connected.Button - 1] = pressed;
                snapshot = new JoystickSnapshot(
                    DateTimeOffset.UtcNow,
                    buttons,
                    [-1],
                    new int[8]);
                return true;
            }
        }

        public void Disconnect()
        {
            lock (_gate)
            {
                DisconnectLocked();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                DisconnectLocked();
                _disposed = true;
            }

            GC.SuppressFinalize(this);
        }

        private void DisconnectLocked()
        {
            _connected = null;
            ConnectedDevice = null;
            LatestBufferedButtonEvents = [];
            _pressed = false;
        }

        private static bool Matches(DirectInputDeviceInfo candidate, DeviceSelector selector)
        {
            if (Guid.TryParse(selector.InstanceGuid, out var instanceGuid)
                && candidate.InstanceGuid != instanceGuid)
            {
                return false;
            }

            if (Guid.TryParse(selector.ProductGuid, out var productGuid)
                && candidate.ProductGuid != productGuid)
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(selector.ProductNameContains)
                || candidate.ProductName.Contains(selector.ProductNameContains, StringComparison.OrdinalIgnoreCase)
                || candidate.InstanceName.Contains(selector.ProductNameContains, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed record DemoDevice(
        DirectInputDeviceInfo Info,
        int Button,
        TimeSpan PressAt);
}
