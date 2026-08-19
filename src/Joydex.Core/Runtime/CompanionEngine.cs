using Joydex.Core.Config;
using Joydex.Core.Input;
using Joydex.Core.Mapping;
using Joydex.Core.TaskAlerts;

namespace Joydex.Core.Runtime;

public enum PromptPickerGesture
{
    Up,
    Down,
    Insert,
    Dismiss,
}

public sealed record PromptPickerRequest(string PickerId, PromptPickerGesture Gesture, string DeviceId, int Button);

public sealed record ButtonMapVisibilityRequest(string DeviceId, bool Visible);

public sealed record EngineResult(
    IReadOnlyList<JoystickEvent> InputEvents,
    IReadOnlyList<ActionRequest> ActionRequests,
    IReadOnlyList<TaskAlertNavigationRequest> TaskAlertNavigationRequests,
    IReadOnlyList<PromptPickerRequest> PromptPickerRequests,
    IReadOnlyList<ButtonMapVisibilityRequest> ButtonMapVisibilityRequests);

public sealed class CompanionEngine
{
    private readonly InputEventDetector _detector;
    private readonly BindingEngine _bindings;
    private readonly TaskAlertInputInterceptor? _taskAlerts;
    private readonly CompanionConfig _config;
    private readonly string _deviceId;
    private bool[]? _previousButtons;
    private bool[]? _bufferedButtonsInitialized;
    private HashSet<int>? _buttonsAwaitingRelease;

    public CompanionEngine(
        CompanionConfig config,
        TaskAlertInputInterceptor? taskAlerts = null,
        string? deviceId = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = CompanionConfigNormalizer.Normalize(config);
        _deviceId = deviceId ?? _config.Devices[0].Id;
        _ = _config.Devices.First(device =>
            string.Equals(device.Id, _deviceId, StringComparison.OrdinalIgnoreCase));
        _detector = new InputEventDetector(config.Polling.AxisTraceThreshold);
        _bindings = new BindingEngine(_config, _deviceId);
        _taskAlerts = taskAlerts;
    }

    public EngineResult Process(
        JoystickSnapshot snapshot,
        IReadOnlyList<JoystickEvent>? bufferedButtonEvents = null)
    {
        var detectedEvents = _detector.Detect(snapshot);
        IReadOnlyList<JoystickEvent> bufferedEvents;
        if (_previousButtons is null
            || _bufferedButtonsInitialized is null
            || _previousButtons.Length != snapshot.Buttons.Length
            || _bufferedButtonsInitialized.Length != snapshot.Buttons.Length)
        {
            bufferedEvents = [];
            _bufferedButtonsInitialized = new bool[snapshot.Buttons.Length];
            _buttonsAwaitingRelease = snapshot.Buttons
                .Select((pressed, index) => (pressed, index))
                .Where(button => button.pressed)
                .Select(button => button.index)
                .ToHashSet();
        }
        else
        {
            foreach (var detected in detectedEvents.Where(input =>
                         input.Kind is JoystickEventKind.ButtonPressed or JoystickEventKind.ButtonReleased))
            {
                _bufferedButtonsInitialized[detected.ControlIndex] = true;
            }

            bufferedEvents = FilterBufferedButtonEvents(bufferedButtonEvents ?? []);
        }

        _previousButtons = [.. snapshot.Buttons];
        var events = FilterStartupHeldButtonPresses(bufferedEvents
            .Concat(detectedEvents.Where(detected => !bufferedEvents.Any(buffered => buffered == detected)))
            .ToArray());
        var interception = _taskAlerts?.Intercept(snapshot, events)
            ?? new TaskAlertInterception(events, []);
        var requests = _bindings.Resolve(snapshot, interception.RemainingEvents, snapshot.Timestamp);
        var pickerRequests = ResolvePromptPickerRequests(snapshot, events);
        var mapRequests = ResolveButtonMapRequests(snapshot, events);
        return new EngineResult(events, requests, interception.NavigationRequests, pickerRequests, mapRequests);
    }

    public void Reset()
    {
        _detector.Reset();
        _previousButtons = null;
        _bufferedButtonsInitialized = null;
        _buttonsAwaitingRelease = null;
        _bindings.Reset();
        _taskAlerts?.Reset();
    }

    private IReadOnlyList<JoystickEvent> FilterStartupHeldButtonPresses(
        IReadOnlyList<JoystickEvent> events)
    {
        if (_buttonsAwaitingRelease is null || _buttonsAwaitingRelease.Count == 0)
        {
            return events;
        }

        var filtered = new List<JoystickEvent>(events.Count);
        foreach (var inputEvent in events)
        {
            if (inputEvent.Kind == JoystickEventKind.ButtonReleased)
            {
                _buttonsAwaitingRelease.Remove(inputEvent.ControlIndex);
            }
            else if (inputEvent.Kind == JoystickEventKind.ButtonPressed
                && _buttonsAwaitingRelease.Contains(inputEvent.ControlIndex))
            {
                continue;
            }

            filtered.Add(inputEvent);
        }

        return filtered;
    }

    private IReadOnlyList<JoystickEvent> FilterBufferedButtonEvents(
        IReadOnlyList<JoystickEvent> bufferedEvents)
    {
        var previousButtons = _previousButtons!;
        var initialized = _bufferedButtonsInitialized!;
        var filtered = new List<JoystickEvent>(bufferedEvents.Count);
        foreach (var inputEvent in bufferedEvents)
        {
            if (inputEvent.Kind is not (JoystickEventKind.ButtonPressed or JoystickEventKind.ButtonReleased)
                || inputEvent.ControlIndex < 0
                || inputEvent.ControlIndex >= previousButtons.Length)
            {
                filtered.Add(inputEvent);
                continue;
            }

            var index = inputEvent.ControlIndex;
            var reportsPreviousState = inputEvent.Kind == (previousButtons[index]
                ? JoystickEventKind.ButtonPressed
                : JoystickEventKind.ButtonReleased);
            // DirectInput can re-report a maintained switch immediately after acquisition.
            // Only the first buffered update is ambiguous; later updates are real edges or pulses.
            if (!initialized[index])
            {
                initialized[index] = true;
                if (reportsPreviousState)
                {
                    continue;
                }
            }

            filtered.Add(inputEvent);
        }

        return filtered;
    }

    private IReadOnlyList<PromptPickerRequest> ResolvePromptPickerRequests(
        JoystickSnapshot snapshot,
        IReadOnlyList<JoystickEvent> events)
    {
        var activeBank = _bindings.ResolveActiveBank(snapshot);
        var requests = new List<PromptPickerRequest>();
        var sawNonPickerPress = false;

        foreach (var inputEvent in events.Where(input => input.Kind == JoystickEventKind.ButtonPressed))
        {
            var matched = false;
            foreach (var picker in _config.PromptPickers)
            {
                matched |= AddIfMatches(requests, picker, PromptPickerGesture.Up, picker.Controls.Up, inputEvent, activeBank);
                matched |= AddIfMatches(requests, picker, PromptPickerGesture.Down, picker.Controls.Down, inputEvent, activeBank);
                matched |= AddIfMatches(requests, picker, PromptPickerGesture.Insert, picker.Controls.Insert, inputEvent, activeBank);
            }

            sawNonPickerPress |= !matched;
        }

        if (sawNonPickerPress)
        {
            requests.Add(new PromptPickerRequest(string.Empty, PromptPickerGesture.Dismiss, _deviceId, 0));
        }

        return requests;
    }

    private bool AddIfMatches(
        List<PromptPickerRequest> requests,
        PromptPickerConfig picker,
        PromptPickerGesture gesture,
        DeviceControlReference control,
        JoystickEvent inputEvent,
        string? activeBank)
    {
        if (!string.Equals(control.DeviceId, _deviceId, StringComparison.OrdinalIgnoreCase)
            || control.Button != inputEvent.DisplayIndex
            || !string.Equals(control.Bank, CompanionConfig.AlwaysBank, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(control.Bank, activeBank, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        requests.Add(new PromptPickerRequest(picker.Id, gesture, _deviceId, control.Button));
        return true;
    }

    private IReadOnlyList<ButtonMapVisibilityRequest> ResolveButtonMapRequests(
        JoystickSnapshot snapshot,
        IReadOnlyList<JoystickEvent> events)
    {
        var activeBank = _bindings.ResolveActiveBank(snapshot);
        var requests = new List<ButtonMapVisibilityRequest>();
        foreach (var target in _config.Devices)
        {
            var control = target.ButtonMapHoldControl;
            if (control is null
                || !string.Equals(control.DeviceId, _deviceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var input in events.Where(input => input.DisplayIndex == control.Button
                         && input.Kind is JoystickEventKind.ButtonPressed or JoystickEventKind.ButtonReleased))
            {
                var bankMatches = string.Equals(control.Bank, CompanionConfig.AlwaysBank, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(control.Bank, activeBank, StringComparison.OrdinalIgnoreCase);
                if (input.Kind == JoystickEventKind.ButtonPressed && !bankMatches)
                {
                    continue;
                }

                requests.Add(new ButtonMapVisibilityRequest(
                    target.Id,
                    input.Kind == JoystickEventKind.ButtonPressed));
            }
        }

        return requests;
    }
}
