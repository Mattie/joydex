namespace Joydex.Core.Input;

public sealed class InputEventDetector(int axisThreshold)
{
    private JoystickSnapshot? _previous;

    public IReadOnlyList<JoystickEvent> Detect(JoystickSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (_previous is null || ShapeChanged(_previous, current))
        {
            _previous = current;
            return [];
        }

        var events = new List<JoystickEvent>();

        for (var index = 0; index < current.Buttons.Length; index++)
        {
            if (current.Buttons[index] == _previous.Buttons[index])
            {
                continue;
            }

            events.Add(new JoystickEvent(
                current.Buttons[index] ? JoystickEventKind.ButtonPressed : JoystickEventKind.ButtonReleased,
                index,
                current.Buttons[index] ? 1 : 0));
        }

        for (var index = 0; index < current.PointOfViewControllers.Length; index++)
        {
            if (current.PointOfViewControllers[index] != _previous.PointOfViewControllers[index])
            {
                events.Add(new JoystickEvent(
                    JoystickEventKind.PointOfViewChanged,
                    index,
                    current.PointOfViewControllers[index]));
            }
        }

        for (var index = 0; index < current.Axes.Length; index++)
        {
            if (Math.Abs(current.Axes[index] - _previous.Axes[index]) >= axisThreshold)
            {
                events.Add(new JoystickEvent(JoystickEventKind.AxisChanged, index, current.Axes[index]));
            }
        }

        _previous = current;
        return events;
    }

    public void Reset() => _previous = null;

    private static bool ShapeChanged(JoystickSnapshot previous, JoystickSnapshot current) =>
        previous.Buttons.Length != current.Buttons.Length
        || previous.PointOfViewControllers.Length != current.PointOfViewControllers.Length
        || previous.Axes.Length != current.Axes.Length;
}
