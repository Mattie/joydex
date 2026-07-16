using VirpilCodexPad.Core.Input;

namespace VirpilCodexPad.Core.Tests;

public sealed class InputEventDetectorTests
{
    [Fact]
    public void FirstSnapshotIsBaselineAndProducesNoEvents()
    {
        var detector = new InputEventDetector(axisThreshold: 100);

        var events = detector.Detect(Snapshot(buttons: [true], povs: [-1], axes: [32_767]));

        Assert.Empty(events);
    }

    [Fact]
    public void ReportsButtonEdgesWithOneBasedDisplayNumbers()
    {
        var detector = new InputEventDetector(axisThreshold: 100);
        detector.Detect(Snapshot(buttons: [false, false], povs: [-1], axes: [0]));

        var pressed = Assert.Single(detector.Detect(
            Snapshot(buttons: [false, true], povs: [-1], axes: [0])));
        var released = Assert.Single(detector.Detect(
            Snapshot(buttons: [false, false], povs: [-1], axes: [0])));

        Assert.Equal(JoystickEventKind.ButtonPressed, pressed.Kind);
        Assert.Equal(2, pressed.DisplayIndex);
        Assert.Equal(JoystickEventKind.ButtonReleased, released.Kind);
        Assert.Equal(2, released.DisplayIndex);
    }

    [Fact]
    public void ShapeChangeCreatesANewBaseline()
    {
        var detector = new InputEventDetector(axisThreshold: 100);
        detector.Detect(Snapshot(buttons: [false], povs: [-1], axes: [0]));

        var shapeChange = detector.Detect(Snapshot(buttons: [true, false], povs: [-1], axes: [0]));
        var nextChange = detector.Detect(Snapshot(buttons: [true, true], povs: [-1], axes: [0]));

        Assert.Empty(shapeChange);
        Assert.Equal(JoystickEventKind.ButtonPressed, Assert.Single(nextChange).Kind);
    }

    [Fact]
    public void AxisEventsRequireConfiguredThreshold()
    {
        var detector = new InputEventDetector(axisThreshold: 100);
        detector.Detect(Snapshot(buttons: [], povs: [], axes: [1_000]));

        Assert.Empty(detector.Detect(Snapshot(buttons: [], povs: [], axes: [1_099])));
        var axisEvent = Assert.Single(detector.Detect(Snapshot(buttons: [], povs: [], axes: [1_199])));

        Assert.Equal(JoystickEventKind.AxisChanged, axisEvent.Kind);
        Assert.Equal(1_199, axisEvent.Value);
    }

    private static JoystickSnapshot Snapshot(bool[] buttons, int[] povs, int[] axes) =>
        new(DateTimeOffset.UtcNow, buttons, povs, axes);
}
