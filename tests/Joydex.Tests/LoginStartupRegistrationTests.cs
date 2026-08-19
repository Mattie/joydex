using Joydex.App;

namespace Joydex.Tests;

public sealed class LoginStartupRegistrationTests
{
    [Fact]
    public void EnablingStartupWritesExecutableAndSelectedConfig()
    {
        var store = new RecordingLoginStartupStore();
        var registration = new LoginStartupRegistration(
            @"C:\Program Files\Joydex\Joydex.App.exe",
            "Joydex",
            ["--config", @"C:\Users\Sample User\Joydex\flight config.json"],
            store);

        registration.SetEnabled(true);

        Assert.True(registration.IsEnabled);
        Assert.Equal(
            "\"C:\\Program Files\\Joydex\\Joydex.App.exe\" \"--config\" "
                + "\"C:\\Users\\Sample User\\Joydex\\flight config.json\"",
            store.Command);
    }

    [Fact]
    public void DisablingStartupRemovesAnExistingEntry()
    {
        var store = new RecordingLoginStartupStore { Command = "existing command" };
        var registration = new LoginStartupRegistration(
            @"C:\Joydex\Joydex.App.exe",
            "Joydex",
            store: store);

        registration.SetEnabled(false);

        Assert.False(registration.IsEnabled);
        Assert.Null(store.Command);
    }

    [Fact]
    public void ExistingEntryReportsEnabledEvenWhenTheInstallMoved()
    {
        var store = new RecordingLoginStartupStore { Command = "old command" };
        var registration = new LoginStartupRegistration(
            @"D:\New Joydex\Joydex.App.exe",
            "Joydex",
            store: store);

        Assert.True(registration.IsEnabled);
    }

    [Theory]
    [InlineData(@"C:\Joydex\Joydex.App.exe", @"C:\Config\joydex.json")]
    [InlineData(@"C:\Trailing Slash\", @"C:\Config Folder\")]
    public void StartupCommandQuotesEveryPath(string executablePath, string configPath)
    {
        var command = LoginStartupRegistration.BuildCommand(
            executablePath,
            ["--config", configPath]);

        Assert.StartsWith("\"", command, StringComparison.Ordinal);
        Assert.Contains("\" \"--config\" \"", command, StringComparison.Ordinal);
        Assert.EndsWith("\"", command, StringComparison.Ordinal);
    }

    [Fact]
    public void LinkToolStartupNeedsOnlyItsQuotedExecutable()
    {
        var store = new RecordingLoginStartupStore();
        var registration = new LoginStartupRegistration(
            @"C:\Users\Sample User\Programs\VIRPIL Controls LinkTool.exe",
            "Joydex.VirpilLinkTool",
            store: store);

        registration.SetEnabled(true);

        Assert.Equal(
            "\"C:\\Users\\Sample User\\Programs\\VIRPIL Controls LinkTool.exe\"",
            store.Command);
    }

    private sealed class RecordingLoginStartupStore : ILoginStartupStore
    {
        public string? Command { get; set; }

        public string? Read() => Command;

        public void Write(string command) => Command = command;

        public void Delete() => Command = null;
    }
}
