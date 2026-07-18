using Joydex.Core.Input;
using Joydex.Core.TaskAlerts;

namespace Joydex.Tests;

public sealed class TaskAlertInputInterceptorTests
{
    public static TheoryData<int> PrimaryB1Buttons => new()
    {
        56,
        62,
        68,
    };

    [Theory]
    [MemberData(nameof(PrimaryB1Buttons))]
    public void PrimarySlotConsumesCleanPressAndReleaseOnM2ThroughM4(int logicalButton)
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
        var request = Assert.Single(press.NavigationRequests);
        Assert.Equal(1, request.Slot);
        Assert.Equal(TaskAlertSlots.BankFromLogicalButton(logicalButton), request.Bank);
        Assert.Equal(1, request.Button);
        Assert.Equal("session-a", request.SessionId);
        Assert.Empty(release.RemainingEvents);
        Assert.Empty(release.NavigationRequests);
    }

    [Fact]
    public void OverflowSlotConsumesOnlyItsM1Button()
    {
        TaskAlertAssignment[] assignments =
        [
            new(5, "overflow-a", "turn", TaskAlertState.Approval, DateTimeOffset.UtcNow),
        ];
        var interceptor = new TaskAlertInputInterceptor(() => assignments);

        var m1Press = interceptor.Intercept(
            Snapshot(38, held: true),
            [new JoystickEvent(JoystickEventKind.ButtonPressed, 37, 1)]);
        var request = Assert.Single(m1Press.NavigationRequests);
        Assert.Equal(5, request.Slot);
        Assert.Equal(1, request.Bank);
        Assert.Equal(1, request.Button);

        var m2Press = new JoystickEvent(JoystickEventKind.ButtonPressed, 55, 1);
        var m2 = interceptor.Intercept(Snapshot(56, held: true), [m2Press]);
        Assert.Same(m2Press, Assert.Single(m2.RemainingEvents));
        Assert.Empty(m2.NavigationRequests);
    }

    [Fact]
    public void PrimaryAssignmentDoesNotInterceptM1()
    {
        TaskAlertAssignment[] assignments =
        [
            new(1, "primary", null, TaskAlertState.Running, DateTimeOffset.UtcNow),
        ];
        var interceptor = new TaskAlertInputInterceptor(() => assignments);
        var inputEvent = new JoystickEvent(JoystickEventKind.ButtonPressed, 37, 1);

        var result = interceptor.Intercept(Snapshot(38, held: true), [inputEvent]);

        Assert.Same(inputEvent, Assert.Single(result.RemainingEvents));
        Assert.Empty(result.NavigationRequests);
    }

    [Theory]
    [InlineData(7, 40, 3)]
    [InlineData(10, 43, 6)]
    public void M1B3AndB6AreAvailableToOverflow(int slot, int logicalButton, int button)
    {
        TaskAlertAssignment[] assignments =
        [
            new(slot, "overflow", null, TaskAlertState.Running, DateTimeOffset.UtcNow),
        ];
        var interceptor = new TaskAlertInputInterceptor(() => assignments);

        var result = interceptor.Intercept(
            Snapshot(logicalButton, held: true),
            [new JoystickEvent(JoystickEventKind.ButtonPressed, logicalButton - 1, 1)]);

        var request = Assert.Single(result.NavigationRequests);
        Assert.Equal(slot, request.Slot);
        Assert.Equal(1, request.Bank);
        Assert.Equal(button, request.Button);
        Assert.Empty(result.RemainingEvents);
    }

    [Theory]
    [InlineData(58)]
    [InlineData(61)]
    [InlineData(64)]
    [InlineData(67)]
    [InlineData(70)]
    [InlineData(73)]
    public void PrimaryPageB3AndB6PassThrough(int logicalButton)
    {
        var interceptor = new TaskAlertInputInterceptor(() => []);
        var inputEvent = new JoystickEvent(JoystickEventKind.ButtonPressed, logicalButton - 1, 1);

        var result = interceptor.Intercept(Snapshot(logicalButton, held: true), [inputEvent]);

        Assert.Same(inputEvent, Assert.Single(result.RemainingEvents));
        Assert.Empty(result.NavigationRequests);
    }

    [Theory]
    [InlineData(74)]
    [InlineData(75)]
    [InlineData(76)]
    [InlineData(77)]
    [InlineData(78)]
    [InlineData(79)]
    public void M5AlwaysPassesThroughWithTenAssignments(int logicalButton)
    {
        var assignments = Enumerable.Range(1, 10)
            .Select(slot => new TaskAlertAssignment(
                slot,
                $"session-{slot}",
                null,
                TaskAlertState.Running,
                DateTimeOffset.UtcNow))
            .ToArray();
        var interceptor = new TaskAlertInputInterceptor(() => assignments);
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

        var held = interceptor.Intercept(Snapshot(56, held: true), []);
        var release = interceptor.Intercept(
            Snapshot(56, held: false),
            [new JoystickEvent(JoystickEventKind.ButtonReleased, 55, 0)]);

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
