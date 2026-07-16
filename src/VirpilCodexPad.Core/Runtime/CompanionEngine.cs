using VirpilCodexPad.Core.Config;
using VirpilCodexPad.Core.Input;
using VirpilCodexPad.Core.Mapping;

namespace VirpilCodexPad.Core.Runtime;

public sealed record EngineResult(
    IReadOnlyList<JoystickEvent> InputEvents,
    IReadOnlyList<ActionRequest> ActionRequests);

public sealed class CompanionEngine
{
    private readonly InputEventDetector _detector;
    private readonly BindingEngine _bindings;

    public CompanionEngine(CompanionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _detector = new InputEventDetector(config.Polling.AxisTraceThreshold);
        _bindings = new BindingEngine(config);
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
        var requests = _bindings.Resolve(snapshot, events, snapshot.Timestamp);
        return new EngineResult(events, requests);
    }

    public void Reset()
    {
        _detector.Reset();
        _bindings.Reset();
    }
}
