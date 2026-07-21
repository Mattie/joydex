using Joydex.App;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace Joydex.Tests;

public sealed class TrayMenuStatusTests
{
    [Fact]
    public void NativeWindowHiddenBehindWinFormsStateCanBeReshown()
    {
        Exception? failure = null;
        using var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new Form
                {
                    Bounds = new Rectangle(-32000, -32000, 320, 200),
                    FormBorderStyle = FormBorderStyle.None,
                    ShowInTaskbar = false,
                };
                form.Shown += (_, _) => form.BeginInvoke(() =>
                {
                    try
                    {
                        Assert.True(TrayApplicationContext.IsFormNativelyVisible(form));
                        _ = ShowWindow(form.Handle, 0); // SW_HIDE
                        Assert.False(TrayApplicationContext.IsFormNativelyVisible(form));

                        TrayApplicationContext.EnsureNativeWindowVisible(form);

                        Assert.True(TrayApplicationContext.IsFormNativelyVisible(form));
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }
                    finally
                    {
                        form.Close();
                    }
                });
                Application.Run(form);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                completed.Set();
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(completed.Wait(TimeSpan.FromSeconds(5)), "The native-window visibility test did not complete.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);
}
