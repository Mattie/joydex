using Joydex.App;
using Joydex.Core.TaskAlerts;
using Joydex.RuntimeHost.Production;

namespace Joydex.RuntimeHost.Tests;

public sealed class VirpilSettingsTransitionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartupRegistrationRestoresExactCommandUnlessCommitted(bool commit)
    {
        var startup = new MemoryStartup { Command = "original custom startup arguments" };
        var validations = 0;
        using (var transition = VirpilSettingsTransition.Prepare(
            TaskAlertLedOptions.CreateDefault(), Direct(), () => validations++, startup, "unused"))
        {
            Assert.Equal(1, validations);
            Assert.Null(startup.Command);
            if (commit) { transition!.Commit(); }
        }
        Assert.Equal(commit ? null : "original custom startup arguments", startup.Command);
    }

    [Fact]
    public void FailedHardwareValidationDoesNotChangeStartup()
    {
        var startup = new MemoryStartup { Command = "original" };
        Assert.Throws<InvalidOperationException>(() => VirpilSettingsTransition.Prepare(
            TaskAlertLedOptions.CreateDefault(), Direct(),
            () => throw new InvalidOperationException("missing hardware"), startup, "unused"));
        Assert.Equal("original", startup.Command);
        Assert.Equal(0, startup.Mutations);
    }

    [Fact]
    public void UnchangedBackendDoesNotRequireConnectedHardwareOrTouchStartup()
    {
        var startup = new MemoryStartup { Command = "original" };
        Assert.Null(VirpilSettingsTransition.Prepare(Direct(), Direct(),
            () => throw new InvalidOperationException("must not probe"), startup, "unused"));
        Assert.Equal(0, startup.Mutations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedProfilePreparationRestoresExactFileOrAbsence(bool existed)
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-led-transition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "profile.xml");
        byte[] original = [0xef, 0xbb, 0xbf, 0x41, 0x0d, 0x0a];
        try
        {
            if (existed) { File.WriteAllBytes(path, original); }
            Assert.Throws<IOException>(() => VirpilSettingsTransition.Prepare(
                Direct(), TaskAlertLedOptions.CreateDefault(), () => { }, new MemoryStartup(), path,
                (file, _) => { File.WriteAllText(file, "partial"); throw new IOException("write failed"); }));
            Assert.Equal(existed, File.Exists(path));
            if (existed) { Assert.Equal(original, File.ReadAllBytes(path)); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static TaskAlertLedOptions Direct() => TaskAlertLedOptions.CreateDefault() with
    {
        Mode = TaskAlertLedOutputMode.DirectHid,
    };

    internal sealed class MemoryStartup : ILoginStartupStore
    {
        public string? Command { get; set; }
        public int Mutations { get; private set; }
        public string? Read() => Command;
        public void Write(string command) { Mutations++; Command = command; }
        public void Delete() { Mutations++; Command = null; }
    }
}
