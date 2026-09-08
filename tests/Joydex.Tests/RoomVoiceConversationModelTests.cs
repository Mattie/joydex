using Joydex.App;
using Joydex.Windows.Voice;

namespace Joydex.Tests;

public sealed class RoomVoiceConversationModelTests
{
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
    public void ClearedEntriesStayHiddenAcrossRefreshWhileNewEntriesAppear()
    {
        var model = new RoomVoiceConversationModel();
        var oldEntry = new CodexVoiceConversationEntry(
            "old-item",
            DateTimeOffset.UnixEpoch,
            CodexVoiceConversationKind.User,
            "old");
        model.ReplaceHistory([oldEntry]);

        model.ClearVisibleConversation();
        model.ReplaceHistory([
            oldEntry,
            new CodexVoiceConversationEntry(
                "new-item",
                DateTimeOffset.UnixEpoch.AddMinutes(1),
                CodexVoiceConversationKind.Assistant,
                "new"),
        ]);

        var visible = Assert.Single(model.GetSnapshot().Entries);
        Assert.Equal("new-item", visible.Id);
    }
}
