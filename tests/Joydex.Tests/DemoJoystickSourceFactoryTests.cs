using Joydex.App;
using Joydex.Core.Config;
using Joydex.Core.Input;

namespace Joydex.Tests;

public sealed class DemoJoystickSourceFactoryTests
{
    [Fact]
    public void EnumeratesTwoClearlySimulatedControllers()
    {
        var devices = new DemoJoystickSourceFactory().EnumerateDevices();

        Assert.Collection(
            devices,
            device => Assert.Equal("Simulated Joydex CM3", device.ProductName),
            device => Assert.Equal("Simulated Joydex WarBRD", device.ProductName));
        Assert.Equal(2, devices.Select(device => device.InstanceGuid).Distinct().Count());
    }

    [Fact]
    public void EachCreateReturnsAnIndependentAcquisitionSource()
    {
        var factory = new DemoJoystickSourceFactory();

        using var first = factory.Create();
        using var second = factory.Create();

        Assert.NotSame(first, second);
        Assert.True(first.TryConnect(Selector("Simulated Joydex CM3"), out _));
        Assert.True(second.TryConnect(Selector("Simulated Joydex WarBRD"), out _));
        Assert.NotEqual(first.ConnectedDevice, second.ConnectedDevice);
    }

    [Fact]
    public void Cm3StartsIdleThenEmitsDeterministicPressAndReleaseEdges()
    {
        var now = TimeSpan.Zero;
        var factory = new DemoJoystickSourceFactory(() => now);
        using var source = factory.Create();
        Assert.True(source.TryConnect(Selector("Simulated Joydex CM3"), out _));

        Assert.True(source.TryRead(out var idle, out _));
        Assert.NotNull(idle);
        Assert.Empty(source.LatestBufferedButtonEvents);
        Assert.False(idle.Buttons[11]);

        now = TimeSpan.FromMilliseconds(1500);
        Assert.True(source.TryRead(out var pressed, out _));
        var press = Assert.Single(source.LatestBufferedButtonEvents);
        Assert.Equal(JoystickEventKind.ButtonPressed, press.Kind);
        Assert.Equal(12, press.DisplayIndex);
        Assert.True(pressed!.Buttons[11]);

        Assert.True(source.TryRead(out _, out _));
        Assert.Empty(source.LatestBufferedButtonEvents);

        now = TimeSpan.FromMilliseconds(2000);
        Assert.True(source.TryRead(out var released, out _));
        var release = Assert.Single(source.LatestBufferedButtonEvents);
        Assert.Equal(JoystickEventKind.ButtonReleased, release.Kind);
        Assert.Equal(12, release.DisplayIndex);
        Assert.False(released!.Buttons[11]);
    }

    [Fact]
    public void WarbrdUsesAStaggeredPulseAndReconnectStartsIdle()
    {
        var now = TimeSpan.Zero;
        var factory = new DemoJoystickSourceFactory(() => now);
        using var source = factory.Create();
        Assert.True(source.TryConnect(Selector("Simulated Joydex WarBRD"), out _));

        now = TimeSpan.FromMilliseconds(3500);
        Assert.True(source.TryRead(out var pressed, out _));
        Assert.True(pressed!.Buttons[0]);
        Assert.Equal(JoystickEventKind.ButtonPressed, Assert.Single(source.LatestBufferedButtonEvents).Kind);

        source.Disconnect();
        now = TimeSpan.FromSeconds(20);
        Assert.True(source.TryConnect(Selector("Simulated Joydex WarBRD"), out _));
        Assert.True(source.TryRead(out var reconnected, out _));
        Assert.False(reconnected!.Buttons[0]);
        Assert.Empty(source.LatestBufferedButtonEvents);
    }

    private static DeviceSelector Selector(string name) => new() { ProductNameContains = name };
}
