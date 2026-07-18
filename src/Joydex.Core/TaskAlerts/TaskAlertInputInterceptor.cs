using Joydex.Core.Input;

namespace Joydex.Core.TaskAlerts;

public sealed record TaskAlertNavigationRequest(int Slot, int Bank, int Button, string SessionId);

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

        var bySlot = assignmentProvider().ToDictionary(assignment => assignment.Slot);
        var pressedNow = events
            .Where(inputEvent => inputEvent.Kind == JoystickEventKind.ButtonPressed)
            .Select(inputEvent => inputEvent.DisplayIndex)
            .ToHashSet();

        foreach (var assignment in bySlot.Values)
        {
            foreach (var button in TaskAlertSlots.LogicalButtons(assignment.Slot))
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
            var slot = TaskAlertSlots.FromLogicalButton(button);
            if (inputEvent.Kind == JoystickEventKind.ButtonReleased
                && _suppressedUntilRelease.Remove(button))
            {
                continue;
            }

            if (inputEvent.Kind == JoystickEventKind.ButtonPressed
                && slot is { } occupiedSlot
                && bySlot.TryGetValue(occupiedSlot, out var assignment))
            {
                _suppressedUntilRelease.Add(button);
                navigation.Add(new TaskAlertNavigationRequest(
                    assignment.Slot,
                    TaskAlertSlots.BankFromLogicalButton(button),
                    TaskAlertSlots.Button(assignment.Slot),
                    assignment.SessionId));
                continue;
            }

            remaining.Add(inputEvent);
        }

        return new TaskAlertInterception(remaining, navigation);
    }

    public void Reset() => _suppressedUntilRelease.Clear();
}
