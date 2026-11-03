using Joydex.Windows.Actions;

namespace Joydex.Tests;

public sealed class T3InstalledExecutableVerificationTests
{
    private const string UserPath = @"C:\Apps\User T3\T3 Code (Beta).exe";
    private const string MachinePath = @"C:\Apps\Machine T3\T3 Code.exe";
    private static string Command(string path) => "\"" + path + "\" \"%1\"";

    [Fact]
    public void PresentUserRegistrationWinsAndAbsentUserFallsBackToMergedRegistration()
    {
        var first = InstalledT3ForegroundResolver.VerifyProtocolRegistration(true, Command(UserPath), Command(MachinePath),
            _ => true, _ => "T3 Code (Beta)", out var error);
        Assert.Equal(UserPath, first); Assert.Null(error);
        var fallback = InstalledT3ForegroundResolver.VerifyProtocolRegistration(false, null, Command(MachinePath),
            _ => true, _ => "T3 Code", out error);
        Assert.Equal(MachinePath, fallback); Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unquoted.exe %1")]
    [InlineData("\"C:\\Apps\\T3.exe\" --unsafe \"%1\"")]
    public void InvalidUserOverrideNeverFallsThroughToValidMachineCommand(string? invalid)
    {
        var inspected = false;
        var result = InstalledT3ForegroundResolver.VerifyProtocolRegistration(true, invalid, Command(MachinePath),
            _ => { inspected = true; return true; }, _ => "T3 Code", out var error);
        Assert.Null(result); Assert.False(inspected); Assert.Equal("LocalCliRegistrationInvalid", error);
    }

    [Fact]
    public void MissingRegistrationFileAndProductMismatchHaveSafeDistinctStages()
    {
        Assert.Null(InstalledT3ForegroundResolver.VerifyProtocolRegistration(false, null, null, _ => true, _ => "T3 Code", out var missing));
        Assert.Equal("LocalCliRegistrationMissing", missing);
        Assert.Null(InstalledT3ForegroundResolver.VerifyProtocolRegistration(true, Command(UserPath), null, _ => false, _ => "T3 Code", out var absent));
        Assert.Equal("LocalCliExecutableMissing", absent);
        Assert.Null(InstalledT3ForegroundResolver.VerifyProtocolRegistration(true, Command(UserPath), null, _ => true, _ => "Unrelated executable", out var mismatch));
        Assert.Equal("LocalCliProductMismatch", mismatch);
        Assert.Null(InstalledT3ForegroundResolver.VerifyProtocolRegistration(true, Command(UserPath), null, _ => true,
            _ => throw new IOException("PRIVATE PATH OR ERROR"), out var unreadable));
        Assert.Equal("LocalCliVerificationFailed", unreadable);
        Assert.DoesNotContain("PRIVATE", unreadable);
    }
}
