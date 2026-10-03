using Joydex.Core.Input;

namespace Joydex.Windows.Actions;

public interface IInjectedKeyStateLifecycle
{
    void ClearInjectedKeyState();

    void ReleaseHeldKeys(InputSourceSession source);

    void ReleaseAllHeldKeys();
}
