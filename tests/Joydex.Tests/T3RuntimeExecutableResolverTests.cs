using System.Text.Json;
using Joydex.Windows.Actions;

namespace Joydex.Tests;

public sealed class T3RuntimeExecutableResolverTests
{
    private static readonly DateTimeOffset Started = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private const string Executable = @"C:\Apps\T3\T3 Code (Beta).exe";
    private static T3RuntimeProcessIdentity Process => new(123, 1, "synthetic-user", Started.AddSeconds(-3), Executable, false);

    private static string? Resolve(T3RuntimeProcessIdentity? process, string origin = "http://127.0.0.1:3773", bool exists = true, string product = "T3 Code (Beta)", int pid = 123)
    {
        using var descriptor = JsonDocument.Parse(JsonSerializer.Serialize(new { version = 999, serverVersion = "future-compatible", pid, startedAt = Started, origin }));
        return T3RuntimeExecutableResolver.Verify(descriptor.RootElement, 1, "synthetic-user", Started.AddMinutes(1), _ => process, _ => exists, _ => product);
    }

    [Fact]
    public void CompatibleLiveDescriptorResolvesExactPidWithoutVersionPinning()
        => Assert.Equal(Executable, Resolve(Process));

    [Fact]
    public void MissingExitedReusedPidAndWrongOwnerSessionOrProductFailClosed()
    {
        Assert.Null(Resolve(null));
        Assert.Null(Resolve(Process with { Exited = true }));
        Assert.Null(Resolve(Process with { StartedAt = Started.AddSeconds(1) }));
        Assert.Null(Resolve(Process with { ProcessId = 124 }));
        Assert.Null(Resolve(Process with { SessionId = 2 }));
        Assert.Null(Resolve(Process with { UserSid = "other-synthetic-user" }));
        Assert.Null(Resolve(Process, product: "Unrelated application"));
        Assert.Null(Resolve(Process, exists: false));
        Assert.Null(Resolve(Process with { ExecutablePath = "relative.exe" }));
    }

    [Theory]
    [InlineData("https://127.0.0.1:3773")]
    [InlineData("http://example.test:3773")]
    [InlineData("http://user@127.0.0.1:3773")]
    [InlineData("http://127.0.0.1:3773/path")]
    public void RuntimeOriginUsesTheSameLoopbackConstraintAsMetadataReads(string origin)
        => Assert.Null(Resolve(Process, origin));

    [Theory]
    [InlineData("LocalCliRegistrationInvalid")]
    [InlineData("LocalCliExecutableMissing")]
    [InlineData("LocalCliProductMismatch")]
    public void RuntimeAlternativeCannotBypassPresentInvalidRegistration(string failure)
    {
        var called = false;
        Assert.Null(InstalledT3ForegroundResolver.ResolveRuntimeWhenRegistrationMissing(failure, () => { called = true; return Executable; }));
        Assert.False(called);
    }
}
