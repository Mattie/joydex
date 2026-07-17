namespace VirpilCodexPad.Windows.Actions;

public interface IInjectedKeyStateLifecycle
{
    void ClearInjectedKeyState();

    void ReleaseHeldKeys();
}
