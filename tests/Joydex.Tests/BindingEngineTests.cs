using Joydex.Core.Config;
using Joydex.Core.Input;
using Joydex.Core.Mapping;

namespace Joydex.Tests;

public sealed class BindingEngineTests
{
    [Fact]
    public void ResolvesBindingWhenExactlyOneBankIsHeld()
    {
        var engine = new BindingEngine(CreateConfig());
        var now = DateTimeOffset.UtcNow;

        var request = Assert.Single(engine.Resolve(
            Snapshot(now, workBank: true, navigateBank: false),
            [new JoystickEvent(JoystickEventKind.ButtonPressed, ControlIndex: 2, Value: 1)],
            now));

        Assert.Equal("work-new-task", request.BindingName);
        Assert.Equal("work", request.Bank);
        Assert.Equal(3, request.Button);
        Assert.Equal(CodexAction.NewTask, request.Action);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void SuppressesBindingsWhenBankStateIsNotUnique(bool workBank, bool navigateBank)
    {
        var engine = new BindingEngine(CreateConfig());
        var now = DateTimeOffset.UtcNow;

        var requests = engine.Resolve(
            Snapshot(now, workBank, navigateBank),
            [new JoystickEvent(JoystickEventKind.ButtonPressed, ControlIndex: 2, Value: 1)],
            now);

        Assert.Empty(requests);
    }

    [Fact]
    public void IgnoresReleaseEvents()
    {
        var engine = new BindingEngine(CreateConfig());
        var now = DateTimeOffset.UtcNow;

        var requests = engine.Resolve(
            Snapshot(now, workBank: true, navigateBank: false),
            [new JoystickEvent(JoystickEventKind.ButtonReleased, ControlIndex: 2, Value: 0)],
            now);

        Assert.Empty(requests);
    }

    [Fact]
    public void ResolvesPushToTalkReleaseWithoutAnActiveBank()
    {
        var engine = new BindingEngine(new CompanionConfig
        {
            Bindings =
            [
                new ButtonBinding
                {
                    Name = "microphone-release",
                    Bank = CompanionConfig.AlwaysBank,
                    Button = 65,
                    Trigger = "release",
                    Action = "push-to-talk",
                },
            ],
        });
        var now = DateTimeOffset.UtcNow;

        var request = Assert.Single(engine.Resolve(
            new JoystickSnapshot(now, new bool[80], [-1], [0]),
            [new JoystickEvent(JoystickEventKind.ButtonReleased, ControlIndex: 64, Value: 0)],
            now));

        Assert.Equal("release", request.Trigger);
        Assert.Equal(CodexAction.PushToTalk, request.Action);
    }

    [Fact]
    public void ReasoningEncoderPulsesAreNotDebounced()
    {
        var engine = new BindingEngine(new CompanionConfig
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
        var snapshot = new JoystickSnapshot(now, new bool[80], [-1], [0]);
        var pulse = new[] { new JoystickEvent(JoystickEventKind.ButtonPressed, ControlIndex: 49, Value: 1) };

        Assert.Single(engine.Resolve(snapshot, pulse, now));
        Assert.Single(engine.Resolve(snapshot, pulse, now.AddMilliseconds(1)));
    }

    [Theory]
    [InlineData("scroll-up")]
    [InlineData("scroll-down")]
    public void ScrollEncoderPulsesAreNotDebounced(string action)
    {
        var engine = new BindingEngine(new CompanionConfig
        {
            Bindings =
            [
                new ButtonBinding
                {
                    Name = action,
                    Bank = CompanionConfig.AlwaysBank,
                    Button = 54,
                    Action = action,
                    WheelNotches = 4,
                },
            ],
        });
        var now = DateTimeOffset.UtcNow;
        var snapshot = new JoystickSnapshot(now, new bool[80], [-1], [0]);
        var pulse = new[] { new JoystickEvent(JoystickEventKind.ButtonPressed, ControlIndex: 53, Value: 1) };

        var first = Assert.Single(engine.Resolve(snapshot, pulse, now));
        var second = Assert.Single(engine.Resolve(snapshot, pulse, now.AddMilliseconds(1)));
        Assert.Equal(4, first.WheelNotches);
        Assert.Equal(4, second.WheelNotches);
    }

    [Fact]
    public void AppliesCooldownPerBinding()
    {
        var config = CreateConfig();
        var engine = new BindingEngine(config);
        var first = DateTimeOffset.UtcNow;
        var input = new[] { new JoystickEvent(JoystickEventKind.ButtonPressed, ControlIndex: 2, Value: 1) };

        Assert.Single(engine.Resolve(Snapshot(first, true, false), input, first));
        Assert.Empty(engine.Resolve(Snapshot(first, true, false), input, first.AddMilliseconds(249)));
        Assert.Single(engine.Resolve(Snapshot(first, true, false), input, first.AddMilliseconds(250)));
    }

    private static CompanionConfig CreateConfig() => new()
    {
        BankSelectors = new Dictionary<string, int>
        {
            ["work"] = 10,
            ["navigate"] = 11,
        },
        Bindings =
        [
            new ButtonBinding
            {
                Name = "work-new-task",
                Bank = "work",
                Button = 3,
                Action = "new-task",
            },
        ],
    };

    private static JoystickSnapshot Snapshot(
        DateTimeOffset timestamp,
        bool workBank,
        bool navigateBank)
    {
        var buttons = new bool[12];
        buttons[9] = workBank;
        buttons[10] = navigateBank;
        return new JoystickSnapshot(timestamp, buttons, [-1], [0]);
    }
}
