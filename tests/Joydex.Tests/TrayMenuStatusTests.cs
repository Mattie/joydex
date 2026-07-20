using Joydex.App;

namespace Joydex.Tests;

public sealed class TrayMenuStatusTests
{
    [Theory]
    [InlineData("Connected: LEFT VPC MongoosT-50CM3", "Connected")]
    [InlineData("Connected (dry run): RIGHT VPC Stick WarBRD", "Connected (dry run)")]
    [InlineData("Controller disconnected", "Controller disconnected")]
    [InlineData("Waiting for controller", "Waiting for controller")]
    [InlineData(null, "Starting...")]
    public void DeviceStatusRemovesConnectedProductNamesOnly(string? status, string expected)
    {
        Assert.Equal(expected, TrayApplicationContext.SummarizeDeviceStatus(status));
    }

    [Fact]
    public void ControllerItemMarksOnlyDevicesWithoutMaps()
    {
        Assert.Equal(
            "LEFT VPC MongoosT-50CM3: Connected",
            TrayApplicationContext.FormatControllerItem(
                "LEFT VPC MongoosT-50CM3",
                "Connected: LEFT VPC MongoosT-50CM3",
                hasMap: true));
        Assert.Equal(
            "Pedals: Waiting for controller (No map)",
            TrayApplicationContext.FormatControllerItem(
                "Pedals",
                "Waiting for controller",
                hasMap: false));
    }

    [Fact]
    public void ControllerSummaryCountsConnectedDevicesAcrossAllConfiguredDevices()
    {
        var statuses = new[]
        {
            "Connected: LEFT VPC MongoosT-50CM3",
            "Connected (dry run): RIGHT VPC Stick WarBRD",
            "Controller disconnected",
        };

        Assert.Equal(
            "Controllers: 2/3 Connected",
            TrayApplicationContext.FormatControllerSummary(total: 3, statuses));
    }
}
