using VirpilCodexPad.Core.Config;
using VirpilCodexPad.Core.Input;

namespace VirpilCodexPad.Windows.Input;

public interface IJoystickSource : IDisposable
{
    DirectInputDeviceInfo? ConnectedDevice { get; }

    IReadOnlyList<JoystickEvent> LatestBufferedButtonEvents { get; }

    bool TryConnect(DeviceSelector selector, out string message);

    bool TryRead(out JoystickSnapshot? snapshot, out string? error);

    void Disconnect();
}
