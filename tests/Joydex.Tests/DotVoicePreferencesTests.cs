using System.Text.Json;
using Joydex.Core.Voice;
using Joydex.Windows.Voice;
using Joydex.App;

namespace Joydex.Tests;

public sealed class DotVoicePreferencesTests
{
    private static readonly DotVoiceTarget Target = new("account-123", "user-123", "account-123~dot-123",
        "01000000-0000-0000-0000-000000000000", "Example dot");

    [Fact]
    public void NativeDotNeedsAnExactTargetButNoDedicatedTaskOrWorkspace()
    {
        var preferences = new VoicePePreferences(Enabled: true, DeviceEndpoint: "http://192.0.2.1/",
            SessionMode: VoicePeSessionMode.NativeDot);
        Assert.Contains(preferences.Validate(), error => error.Contains("Select a dot"));
        Assert.Empty((preferences with { DotTarget = Target }).Validate());
    }

    [Theory]
    [InlineData("", "user-123", "dot-123")]
    [InlineData("account-123", "", "dot-123")]
    [InlineData("account-123", "user-123", "dot/123")]
    public void DotIdentityCannotBePartialOrContainUrlSyntax(string account, string user, string dot)
    {
        Assert.False((Target with { AccountId = account, UserId = user, DotId = dot }).IsValid);
    }

    [Fact]
    public void VersionSixMigratesWithoutChangingTheSelectedRouteAndKeepsABackup()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "voice-pe.json");
        const string original = """{"schemaVersion":6,"enabled":false,"sessionMode":"joydexOwner","realtimeVoice":"sol"}""";
        try
        {
            File.WriteAllText(path, original);
            var migrated = VoicePePreferencesStore.LoadOrCreate(path);
            Assert.Equal(7, migrated.SchemaVersion);
            Assert.Equal(VoicePeSessionMode.JoydexOwner, migrated.SessionMode);
            Assert.Equal("sol", migrated.RealtimeVoice);
            Assert.Null(migrated.DotTarget);
            Assert.Contains(Directory.GetFiles(directory), file => file != path && File.ReadAllText(file) == original);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(VoicePeSessionMode.NativeDot)]
    [InlineData(VoicePeSessionMode.JoydexOwner)]
    public void EngineSelectionAndBothTargetsRoundTripWithoutTokensOrDerivedFields(VoicePeSessionMode mode)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "voice-pe.json");
        try
        {
            var preferences = new VoicePePreferences(SessionMode: mode, DotTarget: Target,
                DedicatedTaskId: "02000000-0000-0000-0000-000000000000",
                AgentWorkspacePath: directory, RealtimeVoice: "sol", DesktopTaskMessagingEnabled: true);
            VoicePePreferencesStore.Save(path, preferences);
            var restored = VoicePePreferencesStore.LoadExisting(path);
            Assert.Equal(preferences.Normalize(), restored);
            Assert.DoesNotContain("isValid", File.ReadAllText(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task UnfamiliarRuntimeProducesACompatibilityWarning()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "a newly updated runtime");
            var warnings = new List<string>();
            await DotVoiceSession.WarnIfUnfamiliarRuntimeAsync(path, path, warnings.Add, CancellationToken.None);
            Assert.Contains("trying an unfamiliar Codex runtime", Assert.Single(warnings));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task NativeDotDetachesLocalMediaBeforeStoppingTheAllocation()
    {
        var calls = new List<string>();
        await WebView2VoiceDuplexAudioSession.StopInOrderAsync(
            () => { calls.Add("local"); return Task.CompletedTask; },
            () => { calls.Add("server"); return Task.CompletedTask; });
        Assert.Equal(["local", "server"], calls);
    }

    [Fact]
    public async Task LocalStopFailureStillReleasesTheServerAllocation()
    {
        var serverStopped = false;
        await Assert.ThrowsAsync<IOException>(() => WebView2VoiceDuplexAudioSession.StopInOrderAsync(
            () => Task.FromException(new IOException("local failed")),
            () => { serverStopped = true; return Task.CompletedTask; }));
        Assert.True(serverStopped);
    }

    [Fact]
    public void UnconfirmedRemoteAllocationSurvivesAWorkerReplacement()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "native-dot-call.pending");
        try
        {
            var original = new DotCallAllocationGuard(path);
            original.Acquire();
            Assert.Throws<InvalidOperationException>(() => DotCallAllocationGuard.RequireClear(path));
            Assert.Throws<IOException>(() => new DotCallAllocationGuard(path).Acquire());
            new DotCallAllocationGuard(path).ConfirmStopped();
            Assert.True(File.Exists(path)); // Replacement cannot release the original allocation.
            original.ConfirmStopped();
            DotCallAllocationGuard.RequireClear(path);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
