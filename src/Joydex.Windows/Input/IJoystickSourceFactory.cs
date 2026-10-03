namespace Joydex.Windows.Input;

/// <summary>Enumerates DirectInput controllers and creates runtime-owned acquisition sources.</summary>
public interface IJoystickSourceFactory
{
    IReadOnlyList<DirectInputDeviceInfo> EnumerateDevices();

    IJoystickSource Create();
}

public sealed class DirectInputJoystickSourceFactory(IntPtr cooperativeWindowHandle) : IJoystickSourceFactory
{
    public IReadOnlyList<DirectInputDeviceInfo> EnumerateDevices()
    {
        using var source = Create();
        return ((DirectInputJoystickSource)source).EnumerateDevices();
    }

    public IJoystickSource Create() => new DirectInputJoystickSource(cooperativeWindowHandle);
}
