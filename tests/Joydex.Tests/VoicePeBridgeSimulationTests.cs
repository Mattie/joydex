using Joydex.App;
using Joydex.Core.Voice;
using Joydex.Windows.Voice;

namespace Joydex.Tests;

public sealed class VoicePeBridgeSimulationTests
{
    [Fact]
    public async Task DotOnlyDryRunSimulatesRepeatedWakesAndKeepsTheEndpointArmed()
    {
        var preferences = new VoicePePreferences(Enabled: true, DeviceEndpoint: "http://192.0.2.1/",
            SessionMode: VoicePeSessionMode.NativeDot,
            DotTarget: new DotVoiceTarget("account-123", "user-123", "account-123~dot-123",
                "01000000-0000-0000-0000-000000000000", "Example dot"));
        Assert.Empty(preferences.Validate());
        Assert.Empty(preferences.PinnedTaskId);
        Assert.Empty(preferences.DedicatedTaskId);
        var transport = new FakeControlTransport();
        var conversation = new RoomVoiceConversationModel();
        var logs = new List<string>();
        await using var runtime = VoicePeBridgeRuntime.StartDotSimulation(
            preferences.DotTarget!, transport, conversation, logs.Add);

        await transport.WakeAsync();
        await transport.WakeAsync();

        Assert.Equal(VoicePeSessionMode.NativeDot, runtime.Mode);
        Assert.Equal([VoicePeSessionState.Armed, VoicePeSessionState.Armed], transport.SessionStates);
        Assert.Equal(2, logs.Count(message => message.StartsWith("DRY RUN Voice PE wake; dot=Example dot;")));
        Assert.DoesNotContain(logs, message => message.Contains("BLOCKED", StringComparison.Ordinal));
        Assert.False(runtime.OwnerReady);
        Assert.False(runtime.IsSessionActive);
        Assert.True(runtime.OwnerCompletion.IsCompletedSuccessfully);
        Assert.Equal(VoicePeSessionState.Armed, conversation.GetSnapshot().SessionState);

        await runtime.DisposeAsync();
        Assert.True(transport.Disposed);
    }

    private sealed class FakeControlTransport : IVoicePeControlTransport
    {
        private Func<VoicePeControlSignal, CancellationToken, ValueTask>? _onSignal;
        public List<VoicePeSessionState> SessionStates { get; } = [];
        public bool Disposed { get; private set; }

        public Task RunAsync(Func<VoicePeControlSignal, CancellationToken, ValueTask> onSignal,
            CancellationToken cancellationToken = default)
        {
            _onSignal = onSignal;
            return Task.CompletedTask;
        }

        public Task WakeAsync() => _onSignal!(VoicePeControlSignal.Wake, CancellationToken.None).AsTask();

        public Task SetSessionStateAsync(VoicePeSessionState state, CancellationToken cancellationToken = default)
        {
            SessionStates.Add(state);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
