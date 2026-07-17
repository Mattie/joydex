using Joydex.Core.Config;
using Joydex.Core.Input;

namespace Joydex.Windows.Input;

public interface IJoystickSource : IDisposable
{
    DirectInputDeviceInfo? ConnectedDevice { get; }

    IReadOnlyList<JoystickEvent> LatestBufferedButtonEvents { get; }

    bool TryConnect(DeviceSelector selector, out string message);

    bool TryRead(out JoystickSnapshot? snapshot, out string? error);

    void Disconnect();
}
