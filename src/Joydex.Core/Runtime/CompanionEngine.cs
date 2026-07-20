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
        var bufferedEvents = bufferedButtonEvents ?? [];
        var events = bufferedEvents
            .Concat(detectedEvents.Where(detected => !bufferedEvents.Any(buffered => buffered == detected)))
            .ToArray();
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
        _bindings.Reset();
        _taskAlerts?.Reset();
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
