using Joydex.Core.Config;
using Joydex.Core.Voice;
using Joydex.Windows.Voice;

namespace Joydex.Tests;

public sealed class PinnedVoiceCoordinatorTests
{
    private static readonly VoicePePreferences EnabledPreferences = new(
        Enabled: true,
        DeviceEndpoint: "http://voice-pe.local/",
        PinnedTaskId: "01900000-0000-7000-8000-000000000001",
        PinnedTaskLabel: "Codex Voice Chat");

    [Theory]
    [InlineData(VoicePeSessionMode.LastVoiceFallback)]
    [InlineData(VoicePeSessionMode.JoydexOwner)]
    public async Task RealNativeWakeRejectsWithoutAnyRoutingOrInputDependencies(VoicePeSessionMode mode)
    {
        var coordinator = new PinnedVoiceCoordinator(new SafetyOptions { DryRun = false }, _ => { });

        var result = await coordinator.StartAsync(EnabledPreferences with { SessionMode = mode });

        Assert.Equal(PinnedVoiceStartStatus.ActionBlocked, result.Status);
        Assert.False(result.Accepted);
        Assert.Contains(PinnedVoiceCoordinator.UnavailableMessage, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DryRunDescribesCompositeWithoutAcquiringWriterOrMedia()
    {
        var coordinator = new PinnedVoiceCoordinator(new SafetyOptions { DryRun = true }, _ => { });

        var result = await coordinator.StartAsync(EnabledPreferences with { DeviceEndpoint = "" });

        Assert.Equal(PinnedVoiceStartStatus.Simulated, result.Status);
        Assert.True(result.Accepted);
        Assert.Contains("Codex Voice Chat", result.Message, StringComparison.Ordinal);
        Assert.Contains("real native route unavailable", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedStartsDoNotLeaveALatchThatBlocksLaterWakes()
    {
        var coordinator = new PinnedVoiceCoordinator(new SafetyOptions { DryRun = false }, _ => { });

        Assert.Equal(PinnedVoiceStartStatus.ActionBlocked, (await coordinator.StartAsync(EnabledPreferences)).Status);
        Assert.Equal(PinnedVoiceStartStatus.ActionBlocked, (await coordinator.StartAsync(EnabledPreferences)).Status);
    }

    [Fact]
    public async Task DiagnosticFailureStillReleasesTheStartGate()
    {
        var calls = 0;
        var coordinator = new PinnedVoiceCoordinator(new SafetyOptions { DryRun = false }, _ =>
        {
            if (++calls == 1) throw new InvalidOperationException("diagnostic sink");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync(EnabledPreferences));
        Assert.Equal(PinnedVoiceStartStatus.ActionBlocked, (await coordinator.StartAsync(EnabledPreferences)).Status);
    }

    [Fact]
    public async Task DisabledAndInvalidRoutesStillReportConfigurationErrors()
    {
        var coordinator = new PinnedVoiceCoordinator(new SafetyOptions { DryRun = true }, _ => { });

        Assert.Equal(PinnedVoiceStartStatus.Disabled,
            (await coordinator.StartAsync(EnabledPreferences with { Enabled = false })).Status);
        Assert.Equal(PinnedVoiceStartStatus.InvalidConfiguration,
            (await coordinator.StartAsync(EnabledPreferences with { PinnedTaskId = "" })).Status);
        Assert.Equal(PinnedVoiceStartStatus.Simulated, (await coordinator.StartAsync(EnabledPreferences)).Status);
    }
}
