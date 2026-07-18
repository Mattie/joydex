using Joydex.Core.Input;

namespace Joydex.Core.TaskAlerts;

public sealed record TaskAlertNavigationRequest(int Channel, string SessionId);

public sealed record TaskAlertInterception(
    IReadOnlyList<JoystickEvent> RemainingEvents,
    IReadOnlyList<TaskAlertNavigationRequest> NavigationRequests);

public sealed class TaskAlertInputInterceptor(Func<IReadOnlyList<TaskAlertAssignment>> assignmentProvider)
{
    private readonly HashSet<int> _suppressedUntilRelease = [];

    public TaskAlertInterception Intercept(
        JoystickSnapshot snapshot,
        IReadOnlyList<JoystickEvent> events)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(events);

        var byChannel = assignmentProvider().ToDictionary(assignment => assignment.Channel);
        var pressedNow = events
            .Where(inputEvent => inputEvent.Kind == JoystickEventKind.ButtonPressed)
            .Select(inputEvent => inputEvent.DisplayIndex)
            .ToHashSet();

        foreach (var assignment in byChannel.Values)
        {
            foreach (var button in TaskAlertChannels.LogicalButtons[assignment.Channel])
            {
                if (button <= snapshot.Buttons.Length
                    && snapshot.Buttons[button - 1]
                    && !pressedNow.Contains(button))
                {
                    _suppressedUntilRelease.Add(button);
                }
            }
        }

        var remaining = new List<JoystickEvent>(events.Count);
        var navigation = new List<TaskAlertNavigationRequest>();
        foreach (var inputEvent in events)
        {
            var button = inputEvent.DisplayIndex;
            var channel = TaskAlertChannels.FromLogicalButton(button);
            if (inputEvent.Kind == JoystickEventKind.ButtonReleased
                && _suppressedUntilRelease.Remove(button))
            {
                continue;
            }

            if (inputEvent.Kind == JoystickEventKind.ButtonPressed
                && channel is { } occupiedChannel
                && byChannel.TryGetValue(occupiedChannel, out var assignment))
            {
                _suppressedUntilRelease.Add(button);
                navigation.Add(new TaskAlertNavigationRequest(
                    assignment.Channel,
                    assignment.SessionId));
                continue;
            }

            remaining.Add(inputEvent);
        }

        return new TaskAlertInterception(remaining, navigation);
    }

    public void Reset() => _suppressedUntilRelease.Clear();
}
