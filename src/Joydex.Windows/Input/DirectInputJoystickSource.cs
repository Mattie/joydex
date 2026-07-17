using Joydex.Core.Config;
using Joydex.Core.Input;
using Vortice.DirectInput;

namespace Joydex.Windows.Input;

public sealed class DirectInputJoystickSource : IJoystickSource
{
    private readonly IDirectInput8 _directInput = DInput.DirectInput8Create();
    private readonly IntPtr _cooperativeWindowHandle;
    private IDirectInputDevice8? _device;

    public DirectInputJoystickSource(IntPtr cooperativeWindowHandle)
    {
        _cooperativeWindowHandle = cooperativeWindowHandle;
    }

    public DirectInputDeviceInfo? ConnectedDevice { get; private set; }

    public IReadOnlyList<JoystickEvent> LatestBufferedButtonEvents { get; private set; } = [];

    public IReadOnlyList<DirectInputDeviceInfo> EnumerateDevices() =>
        _directInput
            .GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly)
            .Select(instance => new DirectInputDeviceInfo(
                instance.InstanceName,
                instance.ProductName,
                instance.InstanceGuid,
                instance.ProductGuid))
            .OrderBy(device => device.ProductName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public bool TryConnect(DeviceSelector selector, out string message)
    {
        ArgumentNullException.ThrowIfNull(selector);
        Disconnect();

        var candidates = EnumerateDevices();
        var selected = candidates.FirstOrDefault(candidate => Matches(candidate, selector));
        if (selected is null)
        {
            message = $"No matching DirectInput device found among {candidates.Count} attached game controllers.";
            return false;
        }

        try
        {
            if (_cooperativeWindowHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("A valid application window handle is required to acquire a DirectInput device.");
            }

            _device = _directInput.CreateDevice(selected.InstanceGuid);
            _device.SetDataFormat<RawJoystickState>();
            _device.Properties.BufferSize = 256;
            _device.SetCooperativeLevel(
                _cooperativeWindowHandle,
                CooperativeLevel.Background | CooperativeLevel.NonExclusive);
            _device.Acquire();
            ConnectedDevice = selected;
            message = $"Connected to {selected.ProductName} ({selected.InstanceGuid}).";
            return true;
        }
        catch (Exception exception)
        {
            Disconnect();
            message = $"Could not acquire {selected.ProductName}: {exception.Message}";
            return false;
        }
    }

    public bool TryRead(out JoystickSnapshot? snapshot, out string? error)
    {
        snapshot = null;
        error = null;

        if (_device is null)
        {
            return false;
        }

        try
        {
            _device.Poll();
            LatestBufferedButtonEvents = _device
                .GetBufferedJoystickData()
                .Where(update => (int)update.Offset >= (int)JoystickOffset.Buttons0
                    && (int)update.Offset <= (int)JoystickOffset.Buttons127)
                .Select(update =>
                {
                    var pressed = (update.Value & 0x80) != 0;
                    return new JoystickEvent(
                        pressed ? JoystickEventKind.ButtonPressed : JoystickEventKind.ButtonReleased,
                        (int)update.Offset - (int)JoystickOffset.Buttons0,
                        pressed ? 1 : 0);
                })
                .ToArray();
            var state = _device.GetCurrentJoystickState();
            var axes = new[]
            {
                state.X,
                state.Y,
                state.Z,
                state.RotationX,
                state.RotationY,
                state.RotationZ,
                state.Sliders[0],
                state.Sliders[1],
            };

            snapshot = new JoystickSnapshot(
                DateTimeOffset.UtcNow,
                [.. state.Buttons],
                [.. state.PointOfViewControllers],
                axes);
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            Disconnect();
            return false;
        }
    }

    public void Disconnect()
    {
        if (_device is not null)
        {
            try
            {
                _device.Unacquire();
            }
            catch
            {
                // The device may already be gone during hot-unplug.
            }

            _device.Dispose();
            _device = null;
        }

        ConnectedDevice = null;
        LatestBufferedButtonEvents = [];
    }

    public void Dispose()
    {
        Disconnect();
        _directInput.Dispose();
        GC.SuppressFinalize(this);
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
