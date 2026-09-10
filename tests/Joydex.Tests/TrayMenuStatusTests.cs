using Joydex.App;
using Joydex.Core.Voice;
using Joydex.Windows.Voice;
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

    [Theory]
    [InlineData(false, VoicePeSessionState.Armed, true, false, "Room Voice — Disabled")]
    [InlineData(true, VoicePeSessionState.Starting, true, false, "Room Voice — Connecting")]
    [InlineData(true, VoicePeSessionState.Listening, true, false, "Room Voice — Listening")]
    [InlineData(true, VoicePeSessionState.Muted, true, false, "Room Voice — Muted")]
    [InlineData(true, VoicePeSessionState.Error, true, false, "Room Voice — Needs attention")]
    [InlineData(true, VoicePeSessionState.Armed, true, false, "Room Voice")]
    public void RoomVoiceMenuUsesCompactProductStatus(
        bool enabled,
        VoicePeSessionState state,
        bool ownerReady,
        bool startupActive,
        string expected)
    {
        var preferences = new VoicePePreferences(
            Enabled: enabled,
            SessionMode: VoicePeSessionMode.JoydexOwner);
        var conversation = new RoomVoiceConversationSnapshot(
            [],
            state,
            ownerReady,
            SessionActive: state is VoicePeSessionState.Starting
                or VoicePeSessionState.Listening
                or VoicePeSessionState.Muted,
            HistoryAvailable: true,
            Stale: false,
            Status: string.Empty,
            Error: null);

        Assert.Equal(
            expected,
            TrayApplicationContext.FormatRoomVoiceMenuText(
                preferences,
                conversation,
                ownerReady,
                startupActive));
    }

    [Fact]
    public void InvalidRoomVoicePreferencesFallBackWithoutBlockingJoydexStartup()
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-voice-preferences-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "voice-pe.json");
            File.WriteAllText(path, "{ this is not valid JSON }");
            var messages = new List<string>();

            var preferences = TrayApplicationContext.LoadRoomVoicePreferences(
                path,
                messages.Add,
                out var error);

            Assert.Equal(VoicePePreferences.Default, preferences);
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.Contains(messages, message => message.Contains("normal Joydex features will continue", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("failed to load configuration: config.toml: invalid type: map, expected a boolean")]
    [InlineData("Failed to load bootstrap configuration")]
    public void CodexConfigurationFailuresDoNotEnterTheRoomVoiceRetryLoop(string message)
    {
        Assert.False(TrayApplicationContext.IsTransientOwnerStartupFailure(
            new InvalidOperationException(message)));
        Assert.True(TrayApplicationContext.IsTransientOwnerStartupFailure(
            new InvalidOperationException("The App Server connection closed unexpectedly.")));
    }

    [Fact]
    public void MissingManagedCodexRuntimeRetriesAfterAnUpdateRace()
    {
        Assert.True(TrayApplicationContext.IsTransientOwnerStartupFailure(
            new CodexManagedRuntimeUnavailableException(
                "The managed runtime is temporarily unavailable.",
                Path.GetTempPath())));
        Assert.False(TrayApplicationContext.IsTransientOwnerStartupFailure(
            new FileNotFoundException("The explicit runtime override is missing.")));
        Assert.False(TrayApplicationContext.IsTransientOwnerStartupFailure(
            new DirectoryNotFoundException("The configured workspace is missing.")));
        Assert.False(TrayApplicationContext.IsTransientOwnerStartupFailure(
            new CodexDedicatedVoiceCompatibilityException("The App Server protocol is incompatible.")));
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);
}
