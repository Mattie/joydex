using Joydex.Contracts;
using Joydex.RuntimeHost.Plugins;

namespace Joydex.RuntimeHost.Tests;

public sealed class DevicePluginCommandTests
{
    [Theory]
    [InlineData(RuntimePluginIds.DirectInput, RuntimeCommandKind.RestartPlugin)]
    [InlineData(RuntimePluginIds.DirectInput, RuntimeCommandKind.ReloadPluginConfiguration)]
    [InlineData(RuntimePluginIds.Virpil, RuntimeCommandKind.RestartPlugin)]
    [InlineData(RuntimePluginIds.Virpil, RuntimeCommandKind.ReloadPluginConfiguration)]
    public async Task DeviceManagementCannotBypassSettingsTransaction(string id, RuntimeCommandKind kind)
    {
        var host = new PadPluginTests.FakeHost();
        var loads = 0;
        await using var pad = new PadPlugin(host, () => { loads++; return null; },
            EspHomePadPluginInstanceFactory.Instance, (_, _) => Task.CompletedTask, default);
        var commands = new PadPluginCommandHandler(pad, host.WritePadLog);
        var request = new RuntimeCommandRequest(Guid.NewGuid(), kind,
            new RuntimeCommandArguments(PluginId: id));

        var result = await commands.ExecuteAsync(request, default);

        Assert.Equal(request.OperationId, result.OperationId);
        Assert.Equal(RuntimeCommandStatus.Rejected, result.Status);
        Assert.Contains("settings Apply", result.Detail, StringComparison.Ordinal);
        Assert.Equal(0, loads);
        var health = Assert.Single(result.Payload!.Plugins!.Health, item => item.PluginId == id);
        Assert.False(health.CanRestart);
        Assert.False(health.CanReload);
    }

    [Fact]
    public async Task InspectionReadsCurrentDeviceGenerationAndCleanupFailure()
    {
        var host = new PadPluginTests.FakeHost();
        await using var pad = new PadPlugin(host, () => null,
            EspHomePadPluginInstanceFactory.Instance, (_, _) => Task.CompletedTask, default);
        var input = new BundledPluginHealth(RuntimePluginIds.DirectInput,
            BundledPluginLifecycleState.Ready, 7, "Controller acquisition is running.", false, false);
        var virpil = new BundledPluginHealth(RuntimePluginIds.Virpil,
            BundledPluginLifecycleState.Ready, 8, "VIRPIL hardware ownership is running.", false, false);
        var commands = new PadPluginCommandHandler(pad, host.WritePadLog,
            directInputHealth: () => input, virpilHealth: () => virpil);
        var request = new RuntimeCommandRequest(Guid.NewGuid(), RuntimeCommandKind.InspectPlugins);
        var first = await commands.ExecuteAsync(request, default);
        Assert.Equal(7, first.Payload!.Plugins!.Health.Single(item => item.PluginId == input.PluginId).Generation);

        input = input with { Generation = 9 };
        virpil = virpil with { State = BundledPluginLifecycleState.Blocked,
            Detail = "VIRPIL cleanup could not be confirmed." };
        var second = await commands.ExecuteAsync(request with { OperationId = Guid.NewGuid() }, default);

        Assert.Equal(9, second.Payload!.Plugins!.Health.Single(item => item.PluginId == input.PluginId).Generation);
        var reported = second.Payload.Plugins.Health.Single(item => item.PluginId == virpil.PluginId);
        Assert.Equal(RuntimePluginState.Blocked, reported.State);
        Assert.Equal(8, reported.Generation);
        Assert.Equal(virpil.Detail, reported.Detail);
    }
}
