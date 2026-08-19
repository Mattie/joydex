using Joydex.Core.Config;
using Joydex.Core.Input;
using Joydex.Core.Mapping;
using Joydex.Core.Runtime;

namespace Joydex.Tests;

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

    [Fact]
    public void BufferedStartupReassertionDoesNotTriggerHeldVoiceSwitch()
    {
        var engine = VoiceSwitchEngine();
        var now = DateTimeOffset.UtcNow;
        var held = SnapshotWithButton(now, displayIndex: 35, pressed: true);
        var pressed = new JoystickEvent(JoystickEventKind.ButtonPressed, ControlIndex: 34, Value: 1);
        var released = new JoystickEvent(JoystickEventKind.ButtonReleased, ControlIndex: 34, Value: 0);

        var baseline = engine.Process(held);
        var reasserted = engine.Process(held with { Timestamp = now.AddMilliseconds(16) }, [pressed]);
        var reassertedAgain = engine.Process(held with { Timestamp = now.AddMilliseconds(24) }, [pressed]);
        var actualRelease = engine.Process(
            SnapshotWithButton(now.AddMilliseconds(32), displayIndex: 35, pressed: false),
            [released]);
        var actualPress = engine.Process(
            SnapshotWithButton(now.AddMilliseconds(48), displayIndex: 35, pressed: true),
            [pressed]);

        Assert.Empty(baseline.ActionRequests);
        Assert.Empty(reasserted.InputEvents);
        Assert.Empty(reasserted.ActionRequests);
        Assert.Empty(reassertedAgain.InputEvents);
        Assert.Empty(reassertedAgain.ActionRequests);
        Assert.Equal("voice-off", Assert.Single(actualRelease.ActionRequests).BindingName);
        Assert.Equal("voice-on", Assert.Single(actualPress.ActionRequests).BindingName);
    }

    [Fact]
    public void BufferedVoiceSwitchCycleBetweenSnapshotsIsPreserved()
    {
        var engine = VoiceSwitchEngine();
        var now = DateTimeOffset.UtcNow;
        var held = SnapshotWithButton(now, displayIndex: 35, pressed: true);
        var released = new JoystickEvent(JoystickEventKind.ButtonReleased, ControlIndex: 34, Value: 0);
        var pressed = new JoystickEvent(JoystickEventKind.ButtonPressed, ControlIndex: 34, Value: 1);
        engine.Process(held);

        var cycle = engine.Process(
            held with { Timestamp = now.AddMilliseconds(16) },
            [released, pressed]);

        Assert.Collection(
            cycle.InputEvents,
            input => Assert.Equal(JoystickEventKind.ButtonReleased, input.Kind),
            input => Assert.Equal(JoystickEventKind.ButtonPressed, input.Kind));
        Assert.Collection(
            cycle.ActionRequests,
            request => Assert.Equal("voice-off", request.BindingName),
            request => Assert.Equal("voice-on", request.BindingName));
    }

    private static JoystickSnapshot Snapshot(DateTimeOffset timestamp, bool bankHeld, bool actionHeld)
    {
        var buttons = new bool[5];
        buttons[1] = actionHeld;
        buttons[4] = bankHeld;
        return new JoystickSnapshot(timestamp, buttons, [-1], [0]);
    }

    private static JoystickSnapshot SnapshotWithButton(
        DateTimeOffset timestamp,
        int displayIndex,
        bool pressed)
    {
        var buttons = new bool[80];
        buttons[displayIndex - 1] = pressed;
        return new JoystickSnapshot(timestamp, buttons, [-1], [0]);
    }

    private static CompanionEngine VoiceSwitchEngine() => new(new CompanionConfig
    {
        Bindings =
        [
            new ButtonBinding
            {
                Name = "voice-on",
                Bank = CompanionConfig.AlwaysBank,
                Button = 35,
                Trigger = "press",
                Action = "voice-chat",
            },
            new ButtonBinding
            {
                Name = "voice-off",
                Bank = CompanionConfig.AlwaysBank,
                Button = 35,
                Trigger = "release",
                Action = "end-voice-chat",
            },
        ],
    });
}
