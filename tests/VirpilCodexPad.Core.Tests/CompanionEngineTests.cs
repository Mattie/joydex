using VirpilCodexPad.Core.Config;
using VirpilCodexPad.Core.Input;
using VirpilCodexPad.Core.Mapping;
using VirpilCodexPad.Core.Runtime;

namespace VirpilCodexPad.Core.Tests;

public sealed class CompanionEngineTests
{
    [Fact]
    public void HeldButtonAtStartupDoesNotDispatchUntilANewPress()
    {
        var engine = new CompanionEngine(new CompanionConfig
        {
            BankSelectors = new Dictionary<string, int> { ["work"] = 5 },
            Bindings =
            [
                new ButtonBinding
                {
                    Name = "work-new-task",
                    Bank = "work",
                    Button = 2,
                    Action = "new-task",
                },
            ],
        });
        var now = DateTimeOffset.UtcNow;

        var startup = engine.Process(Snapshot(now, bankHeld: true, actionHeld: true));
        var release = engine.Process(Snapshot(now.AddMilliseconds(20), bankHeld: true, actionHeld: false));
        var press = engine.Process(Snapshot(now.AddMilliseconds(40), bankHeld: true, actionHeld: true));

        Assert.Empty(startup.InputEvents);
        Assert.Empty(startup.ActionRequests);
        Assert.Empty(release.ActionRequests);
        Assert.Single(press.ActionRequests);
    }

    [Fact]
    public void BufferedEncoderPulsesSurviveBetweenSnapshots()
    {
        var engine = new CompanionEngine(new CompanionConfig
        {
            Bindings =
            [
                new ButtonBinding
                {
                    Name = "reasoning-clockwise",
                    Bank = CompanionConfig.AlwaysBank,
                    Button = 50,
                    Action = "reasoning-up",
                },
            ],
        });
        var now = DateTimeOffset.UtcNow;
        var idle = new JoystickSnapshot(now, new bool[80], [-1], [0]);
        engine.Process(idle);

        var pulse = new JoystickEvent(JoystickEventKind.ButtonPressed, ControlIndex: 49, Value: 1);
        var result = engine.Process(idle with { Timestamp = now.AddMilliseconds(16) }, [pulse, pulse]);

        Assert.Equal(2, result.InputEvents.Count);
        Assert.Equal(2, result.ActionRequests.Count);
        Assert.All(result.ActionRequests, request => Assert.Equal("reasoning-up", CodexActionCatalog.GetId(request.Action)));
    }

    private static JoystickSnapshot Snapshot(DateTimeOffset timestamp, bool bankHeld, bool actionHeld)
    {
        var buttons = new bool[5];
        buttons[1] = actionHeld;
        buttons[4] = bankHeld;
        return new JoystickSnapshot(timestamp, buttons, [-1], [0]);
    }
}
