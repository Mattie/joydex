namespace Joydex.Windows.Actions;

public interface IInjectedKeyStateLifecycle
{
    void ClearInjectedKeyState();

    /// <summary>Attempts to release every input hold still owned by the companion.</summary>
    /// <returns><see langword="true"/> when no tracked hold remains; otherwise <see langword="false"/>.</returns>
    bool ReleaseHeldKeys();
}
