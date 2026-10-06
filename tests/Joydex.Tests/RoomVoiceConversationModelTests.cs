using Joydex.App;
using Joydex.Windows.Voice;

namespace Joydex.Tests;

public sealed class RoomVoiceConversationModelTests
{
    [Theory]
    [InlineData(false, VoicePeSessionState.Error)]
    [InlineData(true, VoicePeSessionState.Armed)]
    public void FallbackReportsUnavailableUnlessItIsASimulation(bool dryRun, VoicePeSessionState state)
    {
        var model = new RoomVoiceConversationModel();
        model.SetFallbackState(enabled: true, dryRun: dryRun);

        var snapshot = model.GetSnapshot();
        Assert.Equal(state, snapshot.SessionState);
        Assert.False(snapshot.OwnerReady);
        Assert.False(snapshot.SessionActive);
        Assert.False(snapshot.HistoryAvailable);
        if (dryRun)
        {
            Assert.Null(snapshot.Error);
            Assert.Contains("dry-run", snapshot.Status, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(PinnedVoiceCoordinator.UnavailableMessage, snapshot.Error);
        }
    }

    [Fact]
    public void DisplayTranscriptMergeDoesNotUseHangupClassifierLengthLimit()
    {
        var first = new string('a', 400);
        var second = new string('b', 400);

        var merged = RoomVoiceConversationModel.MergeTranscript(first, second);

        Assert.Equal(801, merged.Length);
        Assert.StartsWith(first, merged, StringComparison.Ordinal);
        Assert.EndsWith(second, merged, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletedTranscriptReplacesThePartialWithAuthoritativeText()
    {
        var model = new RoomVoiceConversationModel();

        model.UpdateLiveTranscript(CodexVoiceConversationKind.User, "hello wor", final: false);
        model.UpdateLiveTranscript(CodexVoiceConversationKind.User, "Hello, world.", final: true);

        var entry = Assert.Single(model.GetSnapshot().Entries);
        Assert.Equal("Hello, world.", entry.Text);
        Assert.False(entry.IsPartial);
    }

    [Fact]
    public void TranscriptDeltasDoNotRaiseRuntimeStateNotifications()
    {
        var model = new RoomVoiceConversationModel();
        var notifications = 0;
        model.RuntimeStateChanged += (_, _) => notifications++;

        model.UpdateLiveTranscript(CodexVoiceConversationKind.User, "hello", final: false);
        model.SetRuntimeState(
            VoicePeSessionState.Listening,
            ownerReady: true,
            sessionActive: true,
            "Listening");

        Assert.Equal(1, notifications);
    }

    [Fact]
    public void CanonicalHistoryIsChronologicalAndLimitedToNewestEntries()
    {
        var model = new RoomVoiceConversationModel();
        var entries = Enumerable.Range(0, RoomVoiceConversationModel.MaximumVisibleEntries + 5)
            .Reverse()
            .Select(index => new CodexVoiceConversationEntry(
                $"item-{index}",
                DateTimeOffset.UnixEpoch.AddMinutes(index),
                CodexVoiceConversationKind.User,
                $"message {index}"))
            .ToArray();

        model.ReplaceHistory(entries);

        var visible = model.GetSnapshot().Entries;
        Assert.Equal(RoomVoiceConversationModel.MaximumVisibleEntries, visible.Count);
        Assert.Equal("item-5", visible[0].Id);
        Assert.Equal($"item-{RoomVoiceConversationModel.MaximumVisibleEntries + 4}", visible[^1].Id);
        Assert.True(visible.Zip(visible.Skip(1)).All(pair => pair.First.Timestamp <= pair.Second.Timestamp));
    }

    [Fact]
    public void LiveConversationIsLimitedToNewestEntries()
    {
        var model = new RoomVoiceConversationModel();

        for (var index = 0; index < RoomVoiceConversationModel.MaximumVisibleEntries + 5; index++)
        {
            model.UpdateLiveTranscript(
                index % 2 == 0
                    ? CodexVoiceConversationKind.User
                    : CodexVoiceConversationKind.Assistant,
                $"message {index}",
                final: true);
        }

        var visible = model.GetSnapshot().Entries;
        Assert.Equal(RoomVoiceConversationModel.MaximumVisibleEntries, visible.Count);
        Assert.Equal("message 5", visible[0].Text);
        Assert.Equal($"message {RoomVoiceConversationModel.MaximumVisibleEntries + 4}", visible[^1].Text);
    }

    [Fact]
    public void NewestLivePartialSurvivesVisibleHistoryTrimming()
    {
        var model = new RoomVoiceConversationModel();
        model.UpdateLiveTranscript(CodexVoiceConversationKind.User, "still speaking", final: false);
        for (var index = 0; index < RoomVoiceConversationModel.MaximumVisibleEntries; index++)
        {
            model.UpdateLiveTranscript(
                CodexVoiceConversationKind.Assistant,
                $"message {index}",
                final: true);
        }

        var visible = model.GetSnapshot().Entries;
        Assert.Equal(RoomVoiceConversationModel.MaximumVisibleEntries, visible.Count);
        var partial = Assert.Single(visible, entry => entry.IsPartial);
        Assert.Equal("still speaking", partial.Text);
    }

    [Fact]
    public void RuntimeStateChangesDoNotAdvanceConversationVersion()
    {
        var model = new RoomVoiceConversationModel();
        var originalVersion = model.GetSnapshot().ConversationVersion;

        model.SetRuntimeState(
            VoicePeSessionState.Listening,
            ownerReady: true,
            sessionActive: true,
            "Listening");

        Assert.Equal(originalVersion, model.GetSnapshot().ConversationVersion);

        model.UpdateLiveTranscript(CodexVoiceConversationKind.User, "hello", final: true);

        Assert.True(model.GetSnapshot().ConversationVersion > originalVersion);
    }

    [Fact]
    public void DuplicateWakeRestoresTheActiveConversationState()
    {
        var model = new RoomVoiceConversationModel();
        model.SetRuntimeState(
            VoicePeSessionState.Muted,
            ownerReady: true,
            sessionActive: true,
            "Microphone muted");
        var active = model.GetSnapshot();
        model.SetRuntimeState(
            VoicePeSessionState.Starting,
            ownerReady: true,
            sessionActive: true,
            "Connecting the voice session…");

        VoicePeBridgeRuntime.ApplyStartResult(
            model,
            active,
            new VoiceSessionStartResult(VoiceSessionStartStatus.SessionActive, "already active"));

        var restored = model.GetSnapshot();
        Assert.Equal(VoicePeSessionState.Muted, restored.SessionState);
        Assert.True(restored.OwnerReady);
        Assert.True(restored.SessionActive);
        Assert.Equal("Microphone muted", restored.Status);
        Assert.Null(restored.Error);
    }

    [Fact]
    public void DuplicateWakeDuringStartupKeepsTheCurrentStartingState()
    {
        var model = new RoomVoiceConversationModel();
        var armed = model.GetSnapshot();
        model.SetRuntimeState(
            VoicePeSessionState.Starting,
            ownerReady: true,
            sessionActive: true,
            "Connecting the voice session…");

        VoicePeBridgeRuntime.ApplyStartResult(
            model,
            armed,
            new VoiceSessionStartResult(VoiceSessionStartStatus.SessionActive, "start in progress"));

        var current = model.GetSnapshot();
        Assert.Equal(VoicePeSessionState.Starting, current.SessionState);
        Assert.True(current.OwnerReady);
        Assert.True(current.SessionActive);
        Assert.Equal("Connecting the voice session…", current.Status);
        Assert.Null(current.Error);
    }

    [Fact]
    public void CanceledStartupReturnsTheRoomToArmed()
    {
        var model = new RoomVoiceConversationModel();
        var armed = model.GetSnapshot();
        model.SetRuntimeState(
            VoicePeSessionState.Starting,
            ownerReady: true,
            sessionActive: true,
            "Connecting the voice session…");

        VoicePeBridgeRuntime.ApplyStartResult(
            model,
            armed,
            new VoiceSessionStartResult(VoiceSessionStartStatus.Canceled, "canceled"));

        var current = model.GetSnapshot();
        Assert.Equal(VoicePeSessionState.Armed, current.SessionState);
        Assert.True(current.OwnerReady);
        Assert.False(current.SessionActive);
        Assert.Null(current.Error);
    }

    [Fact]
    public void WriterConflictReturnsTheRoomToArmedWithRecoveryInstruction()
    {
        var model = new RoomVoiceConversationModel();
        var armed = model.GetSnapshot();
        model.SetRuntimeState(
            VoicePeSessionState.Starting,
            ownerReady: true,
            sessionActive: true,
            "Connecting the voice session…");

        VoicePeBridgeRuntime.ApplyStartResult(
            model,
            armed,
            new VoiceSessionStartResult(
                VoiceSessionStartStatus.OwnershipConflict,
                "already has an active writer"));

        var current = model.GetSnapshot();
        Assert.Equal(VoicePeSessionState.Armed, current.SessionState);
        Assert.True(current.OwnerReady);
        Assert.False(current.SessionActive);
        Assert.Contains("automatic handoff failed", current.Status, StringComparison.Ordinal);
        Assert.Null(current.Error);
    }

    [Fact]
    public async Task IdleDesktopHandoffReleasesAndRetriesOwnershipOnce()
    {
        var attempts = 0;
        var releases = 0;

        var owner = await VoicePeBridgeRuntime.AcquireWithIdleDesktopHandoffAsync(
            _ => ++attempts == 1
                ? Task.FromException<string>(new CodexDedicatedVoiceOwnershipException("busy"))
                : Task.FromResult("acquired"),
            _ =>
            {
                releases++;
                return Task.CompletedTask;
            });

        Assert.Equal("acquired", owner);
        Assert.Equal(2, attempts);
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task FailedDesktopHandoffRemainsATypedOwnershipConflict()
    {
        var error = await Assert.ThrowsAsync<CodexDedicatedVoiceOwnershipException>(() =>
            VoicePeBridgeRuntime.AcquireWithIdleDesktopHandoffAsync(
                _ => Task.FromException<string>(new CodexDedicatedVoiceOwnershipException("busy")),
                _ => Task.FromException(new IOException("bridge unavailable"))));

        Assert.Contains("could not complete", error.Message, StringComparison.Ordinal);
        Assert.IsType<AggregateException>(error.InnerException);
    }

    [Fact]
    public void CanonicalRefreshPreservesEntriesAddedAfterClear()
    {
        var model = new RoomVoiceConversationModel();
        var oldEntry = new CodexVoiceConversationEntry(
            "old-item",
            DateTimeOffset.UnixEpoch,
            CodexVoiceConversationKind.User,
            "old");
        model.ReplaceHistory([oldEntry]);

        model.ClearVisibleConversation();
        model.UpdateLiveTranscript(CodexVoiceConversationKind.Assistant, "new", final: true);
        model.ReplaceHistory([
            oldEntry,
            new CodexVoiceConversationEntry(
                "new-item",
                DateTimeOffset.UnixEpoch,
                CodexVoiceConversationKind.Assistant,
                "new"),
        ]);

        var visible = Assert.Single(model.GetSnapshot().Entries);
        Assert.StartsWith("live-", visible.Id, StringComparison.Ordinal);
        Assert.Equal("new", visible.Text);
    }
}
