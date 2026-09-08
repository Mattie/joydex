using Joydex.App;
using Joydex.Windows.Voice;

namespace Joydex.Tests;

public sealed class RoomVoiceTranscriptViewTests
{
    [Fact]
    public void LiveTranscriptUpdatesReuseExistingMessageControls()
    {
        using var view = new RoomVoiceTranscriptView();
        var timestamp = DateTimeOffset.Now;
        var partial = new RoomVoiceConversationEntry(
            "live-1",
            timestamp,
            CodexVoiceConversationKind.Assistant,
            "The capital",
            IsPartial: true);

        Assert.True(view.Render([partial], showRaw: false));
        var originalRow = Assert.Single(view.RenderedMessageRows);

        Assert.True(view.Render(
            [partial with { Text = "The capital is Augusta.", IsPartial = false }],
            showRaw: false));

        Assert.Same(originalRow, Assert.Single(view.RenderedMessageRows));
    }

    [Fact]
    public void UnchangedSnapshotDoesNoTranscriptWork()
    {
        using var view = new RoomVoiceTranscriptView();
        var entry = new RoomVoiceConversationEntry(
            "live-1",
            DateTimeOffset.Now,
            CodexVoiceConversationKind.Assistant,
            "No change");
        view.Render([entry], showRaw: false);
        var originalRow = Assert.Single(view.RenderedMessageRows);

        Assert.False(view.Render([entry], showRaw: false));

        Assert.Same(originalRow, Assert.Single(view.RenderedMessageRows));
    }

    [Fact]
    public void AppendingMessagePreservesExistingRows()
    {
        using var view = new RoomVoiceTranscriptView();
        var timestamp = DateTimeOffset.Now;
        var first = new RoomVoiceConversationEntry(
            "live-1",
            timestamp,
            CodexVoiceConversationKind.User,
            "Hello");
        view.Render([first], showRaw: false);
        var originalRow = Assert.Single(view.RenderedMessageRows);

        Assert.True(view.Render(
            [
                first,
                new RoomVoiceConversationEntry(
                    "live-2",
                    timestamp.AddSeconds(1),
                    CodexVoiceConversationKind.Assistant,
                    "Hi"),
            ],
            showRaw: false));

        Assert.Equal(2, view.RenderedMessageRows.Count);
        Assert.Same(originalRow, view.RenderedMessageRows[0]);
    }

    [Fact]
    public void RawModeUpdatesTextWithoutRebuildingRows()
    {
        using var view = new RoomVoiceTranscriptView();
        var entry = new RoomVoiceConversationEntry(
            "live-1",
            DateTimeOffset.Now,
            CodexVoiceConversationKind.User,
            "Readable text",
            RawText: "<input>Readable text</input>");
        view.Render([entry], showRaw: false);
        var originalRow = Assert.Single(view.RenderedMessageRows);

        Assert.True(view.Render([entry], showRaw: true));

        Assert.Same(originalRow, Assert.Single(view.RenderedMessageRows));
        Assert.Equal("<input>Readable text</input>", Assert.Single(view.RenderedMessageTexts));
    }

    [Fact]
    public void MessageTextUsesASelectableReadOnlyControl()
    {
        using var view = new RoomVoiceTranscriptView();
        view.Render(
            [new RoomVoiceConversationEntry(
                "live-1",
                DateTimeOffset.Now,
                CodexVoiceConversationKind.User,
                "Copy me")],
            showRaw: false);

        var message = Assert.Single(view.RenderedMessageTextControls);

        Assert.True(message.ReadOnly);
        Assert.True(message.Multiline);
        Assert.True(message.ShortcutsEnabled);
        Assert.Equal("Copy me", message.Text);
        message.Select(0, 4);
        Assert.Equal(4, message.SelectionLength);
    }

    [Fact]
    public void LiveUpdatePreservesAnExistingTextSelection()
    {
        using var view = new RoomVoiceTranscriptView();
        var entry = new RoomVoiceConversationEntry(
            "live-1",
            DateTimeOffset.Now,
            CodexVoiceConversationKind.Assistant,
            "Copy this partial response",
            IsPartial: true);
        view.Render([entry], showRaw: false);
        var message = Assert.Single(view.RenderedMessageTextControls);
        message.Select(5, 4);

        view.Render(
            [entry with { Text = "Copy this completed response", IsPartial = false }],
            showRaw: false);

        Assert.Equal(5, message.SelectionStart);
        Assert.Equal(4, message.SelectionLength);
    }

    [Theory]
    [InlineData(0, 0, 100, 20, false, true)]
    [InlineData(81, 0, 100, 20, true, true)]
    [InlineData(73, 0, 100, 20, true, true)]
    [InlineData(72, 0, 100, 20, true, false)]
    [InlineData(40, 0, 100, 20, true, false)]
    public void FollowNewestUsesTheReachableScrollbarEnd(
        int value,
        int minimum,
        int maximum,
        int largeChange,
        bool visible,
        bool expected)
    {
        Assert.Equal(
            expected,
            RoomVoiceTranscriptView.IsNearNewest(
                value,
                minimum,
                maximum,
                largeChange,
                visible));
    }

    [Fact]
    public void UserCanLeaveAndReenterFollowMode()
    {
        using var view = new RoomVoiceTranscriptView();
        view.Render(
            [new RoomVoiceConversationEntry(
                "live-1",
                DateTimeOffset.Now,
                CodexVoiceConversationKind.User,
                "Message")],
            showRaw: false);

        view.ObserveUserScrollForTest(20, 0, 100, 20, visible: true);

        Assert.False(view.FollowNewestEnabled);
        Assert.True(view.LatestButtonVisible);

        view.ObserveUserScrollForTest(81, 0, 100, 20, visible: true);

        Assert.True(view.FollowNewestEnabled);
        Assert.False(view.LatestButtonVisible);
    }

    [Fact]
    public void LatestActionReentersFollowMode()
    {
        using var view = new RoomVoiceTranscriptView();
        view.Render(
            [new RoomVoiceConversationEntry(
                "live-1",
                DateTimeOffset.Now,
                CodexVoiceConversationKind.User,
                "Message")],
            showRaw: false);
        view.ObserveUserScrollForTest(20, 0, 100, 20, visible: true);

        view.FollowNewestForTest();

        Assert.True(view.FollowNewestEnabled);
        Assert.False(view.LatestButtonVisible);
    }

    [Theory]
    [InlineData(true, 40, 0, 100, 20, 81)]
    [InlineData(false, 40, 0, 100, 20, 40)]
    [InlineData(false, 95, 0, 100, 20, 81)]
    public void ScrollRestoreFollowsOrPreservesTheReadingPosition(
        bool follow,
        int previousScroll,
        int minimum,
        int maximum,
        int largeChange,
        int expected)
    {
        Assert.Equal(
            expected,
            RoomVoiceTranscriptView.ResolveScrollTarget(
                follow,
                previousScroll,
                minimum,
                maximum,
                largeChange));
    }

    [Fact]
    public void PartialUpdateInFullHistoryPreservesTheControlTree()
    {
        using var view = new RoomVoiceTranscriptView();
        var timestamp = DateTimeOffset.Now;
        var entries = Enumerable.Range(0, RoomVoiceConversationModel.MaximumVisibleEntries)
            .Select(index => new RoomVoiceConversationEntry(
                $"live-{index}",
                timestamp.AddSeconds(index),
                index % 2 == 0
                    ? CodexVoiceConversationKind.User
                    : CodexVoiceConversationKind.Assistant,
                $"message {index}",
                IsPartial: index == RoomVoiceConversationModel.MaximumVisibleEntries - 1))
            .ToArray();
        view.Render(entries, showRaw: false);
        var originalRows = view.RenderedMessageRows.ToArray();
        entries[^1] = entries[^1] with { Text = "completed message", IsPartial = false };

        Assert.True(view.Render(entries, showRaw: false));

        Assert.Equal(originalRows.Length, view.RenderedMessageRows.Count);
        Assert.All(
            originalRows.Select((row, index) => (row, index)),
            pair => Assert.Same(pair.row, view.RenderedMessageRows[pair.index]));
    }

    [Fact]
    public void ReplacedHistoryDisposesOldRowsAndStartsFromTheNewStructure()
    {
        using var view = new RoomVoiceTranscriptView();
        var timestamp = DateTimeOffset.Now;
        view.Render(
            [new RoomVoiceConversationEntry(
                "live-1",
                timestamp,
                CodexVoiceConversationKind.User,
                "Hello")],
            showRaw: false);
        var oldRow = Assert.Single(view.RenderedMessageRows);

        view.Render(
            [new RoomVoiceConversationEntry(
                "history-1",
                timestamp,
                CodexVoiceConversationKind.Assistant,
                "Hi there")],
            showRaw: false);

        Assert.True(oldRow.IsDisposed);
        Assert.NotSame(oldRow, Assert.Single(view.RenderedMessageRows));
    }
}
