namespace Joydex.Core.Input;

public sealed record JoystickSnapshot(
    DateTimeOffset Timestamp,
    bool[] Buttons,
    int[] PointOfViewControllers,
    int[] Axes);

public enum JoystickEventKind
{
    ButtonPressed,
    ButtonReleased,
    PointOfViewChanged,
    AxisChanged,
}

public sealed record JoystickEvent(JoystickEventKind Kind, int ControlIndex, int Value)
{
    public int DisplayIndex => ControlIndex + 1;
}
