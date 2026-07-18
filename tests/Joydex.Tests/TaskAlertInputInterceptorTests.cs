using Joydex.Core.Input;
using Joydex.Core.TaskAlerts;

namespace Joydex.Tests;

public sealed class TaskAlertInputInterceptorTests
{
    public static TheoryData<int> BankButtons => new()
    {
        38,
        56,
        62,
        68,
        74,
    };

    [Theory]
    [MemberData(nameof(BankButtons))]
    public void OccupiedB1ConsumesCleanPressAndReleaseInEveryBank(int logicalButton)
    {
        TaskAlertAssignment[] assignments =
        [
            new(1, "session-a", "turn", TaskAlertState.Approval, DateTimeOffset.UtcNow),
        ];
        var interceptor = new TaskAlertInputInterceptor(() => assignments);
        var pressed = Snapshot(logicalButton, held: true);

        var press = interceptor.Intercept(
            pressed,
            [new JoystickEvent(JoystickEventKind.ButtonPressed, logicalButton - 1, 1)]);
        assignments = [];
        var release = interceptor.Intercept(
            Snapshot(logicalButton, held: false),
            [new JoystickEvent(JoystickEventKind.ButtonReleased, logicalButton - 1, 0)]);

        Assert.Empty(press.RemainingEvents);
        Assert.Equal("session-a", Assert.Single(press.NavigationRequests).SessionId);
        Assert.Empty(release.RemainingEvents);
        Assert.Empty(release.NavigationRequests);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(43)]
    [InlineData(58)]
    [InlineData(61)]
    public void UnoccupiedB3AndB6PassThrough(int logicalButton)
    {
        var interceptor = new TaskAlertInputInterceptor(() => []);
        var inputEvent = new JoystickEvent(JoystickEventKind.ButtonPressed, logicalButton - 1, 1);

        var result = interceptor.Intercept(Snapshot(logicalButton, held: true), [inputEvent]);

        Assert.Same(inputEvent, Assert.Single(result.RemainingEvents));
        Assert.Empty(result.NavigationRequests);
    }

    [Fact]
    public void AssignmentAppearingWhileHeldDoesNotNavigateOnRelease()
    {
        TaskAlertAssignment[] assignments =
        [
            new(1, "session-a", null, TaskAlertState.Running, DateTimeOffset.UtcNow),
        ];
        var interceptor = new TaskAlertInputInterceptor(() => assignments);

        var held = interceptor.Intercept(Snapshot(38, held: true), []);
        var release = interceptor.Intercept(
            Snapshot(38, held: false),
            [new JoystickEvent(JoystickEventKind.ButtonReleased, 37, 0)]);

        Assert.Empty(held.NavigationRequests);
        Assert.Empty(release.RemainingEvents);
        Assert.Empty(release.NavigationRequests);
    }

    private static JoystickSnapshot Snapshot(int logicalButton, bool held)
    {
        var buttons = new bool[80];
        buttons[logicalButton - 1] = held;
        return new JoystickSnapshot(DateTimeOffset.UtcNow, buttons, [], []);
    }
}
