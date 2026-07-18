using Joydex.Core.Config;
using Joydex.Core.Input;
using Joydex.Core.Mapping;
using Joydex.Core.TaskAlerts;

namespace Joydex.Core.Runtime;

public sealed record EngineResult(
    IReadOnlyList<JoystickEvent> InputEvents,
    IReadOnlyList<ActionRequest> ActionRequests,
    IReadOnlyList<TaskAlertNavigationRequest> TaskAlertNavigationRequests);

public sealed class CompanionEngine
{
    private readonly InputEventDetector _detector;
    private readonly BindingEngine _bindings;
    private readonly TaskAlertInputInterceptor? _taskAlerts;

    public CompanionEngine(CompanionConfig config, TaskAlertInputInterceptor? taskAlerts = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _detector = new InputEventDetector(config.Polling.AxisTraceThreshold);
        _bindings = new BindingEngine(config);
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
        return new EngineResult(events, requests, interception.NavigationRequests);
    }

    public void Reset()
    {
        _detector.Reset();
        _bindings.Reset();
        _taskAlerts?.Reset();
    }
}
