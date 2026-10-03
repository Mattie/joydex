using Joydex.App;
using Joydex.Contracts;
using Joydex.Core.Voice;
using Joydex.Windows.Voice;

namespace Joydex.Tests;

public sealed class RuntimeRoomVoiceWindowAdapterTests
{
    [Fact]
    public void MatchingSnapshotsAndEventsDriveTheExistingWindowModels()
    {
        using var adapter = new RuntimeRoomVoiceWindowAdapter();
        var runner = new FakeCommandRunner();
        var targets = new FakeVoiceTargetWriter();
        var snapshot = VoiceUi(
            Session(RuntimeVoiceSessionState.Listening, active: true, version: 7),
            [new RuntimeVoiceTimelineEntry(
                "initial",
                DateTimeOffset.UnixEpoch,
                RuntimeVoiceTimelineKind.Assistant,
                "Ready.",
                IsPartial: true)],
            Messaging(
                tasks: [TaskContract("First task")],
                drafts: [Draft("draft-1", "bounded preview", truncated: true)]));

        Assert.True(adapter.BeginConnection(4, runner, targets, snapshot));
        var initial = adapter.Conversation.GetSnapshot();
        Assert.Equal(VoicePeSessionState.Listening, initial.SessionState);
        Assert.True(initial.SessionActive);
        Assert.Equal("Ready.", Assert.Single(initial.Entries).Text);
        Assert.True(Assert.Single(initial.Entries).IsPartial);
        var draft = Assert.Single(adapter.TaskMessaging.LoadDrafts());
        Assert.Equal("bounded preview", draft.MessagePreview);
        Assert.True(draft.MessageTruncated);

        var stale = new RuntimeVoiceEvent(
            Session(RuntimeVoiceSessionState.Error, active: false, version: 8),
            new RuntimeVoiceTimelineEntry(
                "stale",
                DateTimeOffset.UnixEpoch.AddSeconds(1),
                RuntimeVoiceTimelineKind.User,
                "stale"));
        Assert.False(adapter.ApplyEvent(3, stale));

        var current = new RuntimeVoiceEvent(
            Session(RuntimeVoiceSessionState.Muted, active: true, version: 8),
            new RuntimeVoiceTimelineEntry(
                "initial",
                DateTimeOffset.UnixEpoch.AddSeconds(2),
                RuntimeVoiceTimelineKind.Assistant,
                "Ready now",
                IsPartial: false));
        Assert.True(adapter.ApplyEvent(4, current));

        var updated = adapter.Conversation.GetSnapshot();
        Assert.Equal(VoicePeSessionState.Muted, updated.SessionState);
        Assert.Equal("Ready now", Assert.Single(updated.Entries).Text);
        Assert.False(Assert.Single(updated.Entries).IsPartial);

        Assert.True(adapter.ApplyEvent(
            4,
            new RuntimeVoiceEvent(
                Session(RuntimeVoiceSessionState.Armed, active: false, version: 9),
                TimelineReset: true)));
        Assert.True(adapter.Conversation.GetSnapshot().Stale);
        Assert.True(adapter.ApplySnapshot(
            4,
            VoiceUi(
                Session(RuntimeVoiceSessionState.Armed, active: false, version: 9),
                [new RuntimeVoiceTimelineEntry(
                    "canonical",
                    DateTimeOffset.UnixEpoch.AddSeconds(3),
                    RuntimeVoiceTimelineKind.Assistant,
                    "Canonical history")],
                Messaging())));
        var canonical = adapter.Conversation.GetSnapshot();
        Assert.False(canonical.Stale);
        Assert.Equal("Canonical history", Assert.Single(canonical.Entries).Text);
    }

    [Fact]
    public void TimelineDeltasUpsertByStableIdAndKeepDistinctSameKindEntries()
    {
        using var adapter = new RuntimeRoomVoiceWindowAdapter();
        var firstTimestamp = DateTimeOffset.UnixEpoch.AddSeconds(1);
        var revisedTimestamp = DateTimeOffset.UnixEpoch.AddSeconds(2);
        var secondTimestamp = DateTimeOffset.UnixEpoch.AddSeconds(3);
        var first = new RuntimeVoiceTimelineEntry(
            "entry-x",
            firstTimestamp,
            RuntimeVoiceTimelineKind.Assistant,
            "Complete X");
        Assert.True(adapter.BeginConnection(
            2,
            new FakeCommandRunner(),
            new FakeVoiceTargetWriter(),
            VoiceUi(
                Session(RuntimeVoiceSessionState.Listening, active: true, version: 1),
                [first],
                Messaging())));

        Assert.True(adapter.ApplyEvent(
            2,
            new RuntimeVoiceEvent(
                Session(RuntimeVoiceSessionState.Listening, active: true, version: 1),
                first)));
        var replayed = Assert.Single(adapter.Conversation.GetSnapshot().Entries);
        Assert.Equal("entry-x", replayed.Id);
        Assert.Equal(firstTimestamp, replayed.Timestamp);
        Assert.False(replayed.IsPartial);

        Assert.True(adapter.ApplyEvent(
            2,
            new RuntimeVoiceEvent(
                Session(RuntimeVoiceSessionState.Listening, active: true, version: 2),
                new RuntimeVoiceTimelineEntry(
                    "entry-x",
                    revisedTimestamp,
                    RuntimeVoiceTimelineKind.Assistant,
                    "Partial X",
                    IsPartial: true))));
        var partial = Assert.Single(adapter.Conversation.GetSnapshot().Entries);
        Assert.Equal(revisedTimestamp, partial.Timestamp);
        Assert.Equal("Partial X", partial.Text);
        Assert.True(partial.IsPartial);

        Assert.True(adapter.ApplyEvent(
            2,
            new RuntimeVoiceEvent(
                Session(RuntimeVoiceSessionState.Listening, active: true, version: 3),
                new RuntimeVoiceTimelineEntry(
                    "entry-y",
                    secondTimestamp,
                    RuntimeVoiceTimelineKind.Assistant,
                    "Complete Y"))));
        Assert.Collection(
            adapter.Conversation.GetSnapshot().Entries,
            entry => Assert.Equal("entry-x", entry.Id),
            entry => Assert.Equal("entry-y", entry.Id));
    }

    [Fact]
    public async Task CompleteConversationUsesVersionedPagesAndKeepsRawText()
    {
        using var adapter = new RuntimeRoomVoiceWindowAdapter();
        var runner = new FakeCommandRunner
        {
            ExecuteHandler = (request, _) =>
            {
                var continuation = request.Arguments?.ContinuationToken;
                var page = continuation switch
                {
                    null => new RuntimeVoiceConversationPage(
                        [
                            new RuntimeVoiceConversationEntry(
                                "one",
                                DateTimeOffset.UnixEpoch,
                                RuntimeVoiceTimelineKind.User,
                                "First"),
                            new RuntimeVoiceConversationEntry(
                                "two",
                                DateTimeOffset.UnixEpoch.AddSeconds(1),
                                RuntimeVoiceTimelineKind.Activity,
                                "Tool used",
                                RawText: "private raw one"),
                        ],
                        "version-12-offset-2"),
                    "version-12-offset-2" => new RuntimeVoiceConversationPage(
                        [new RuntimeVoiceConversationEntry(
                            "three",
                            DateTimeOffset.UnixEpoch.AddSeconds(2),
                            RuntimeVoiceTimelineKind.Assistant,
                            "Third",
                            RawText: "private raw two")]),
                    _ => throw new InvalidOperationException("Unexpected continuation token."),
                };
                return Task.FromResult(Completed(
                    request,
                    new RuntimeCommandPayload(VoiceConversation: page)));
            },
        };
        Assert.True(adapter.BeginConnection(1, runner, new FakeVoiceTargetWriter()));

        var entries = await adapter.ReadFullConversationAsync();

        Assert.Equal(3, entries.Count);
        Assert.Equal("private raw one", entries[1].RawText);
        Assert.Equal("private raw two", entries[2].RawText);
        var requests = runner.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.All(requests, request =>
        {
            Assert.Equal(RuntimeCommandKind.ReadVoiceConversationPage, request.Kind);
            Assert.NotEqual(Guid.Empty, request.OperationId);
        });
        Assert.NotEqual(requests[0].OperationId, requests[1].OperationId);
        Assert.Null(requests[0].Arguments);
        Assert.Equal("version-12-offset-2", requests[1].Arguments?.ContinuationToken);
    }

    [Fact]
    public async Task ConversationCopyReaderUsesPrivateFullHistoryInsteadOfTruncatedProjection()
    {
        using var adapter = new RuntimeRoomVoiceWindowAdapter();
        var runner = new FakeCommandRunner
        {
            ExecuteHandler = (request, _) => Task.FromResult(Completed(
                request,
                new RuntimeCommandPayload(
                    VoiceConversation: new RuntimeVoiceConversationPage(
                    [
                        new RuntimeVoiceConversationEntry(
                            "full-entry",
                            DateTimeOffset.UnixEpoch.AddMinutes(1),
                            RuntimeVoiceTimelineKind.Assistant,
                            "Full rendered text",
                            RawText: "Full private raw text"),
                    ])))),
        };
        Assert.True(adapter.BeginConnection(
            3,
            runner,
            new FakeVoiceTargetWriter(),
            VoiceUi(
                Session(RuntimeVoiceSessionState.Listening, active: true, version: 1),
                [new RuntimeVoiceTimelineEntry(
                    "bounded-entry",
                    DateTimeOffset.UnixEpoch,
                    RuntimeVoiceTimelineKind.Assistant,
                    "Bounded visible text",
                    TextTruncated: true)],
                Messaging())));
        var copyReader = new RoomVoiceConversationCopyReader(
            adapter.Conversation,
            adapter.ReadFullConversationAsync);

        var copy = await copyReader.ReadAsync(showRaw: true, default);

        Assert.Null(copy.Notice);
        Assert.Contains("Full private raw text", copy.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Bounded visible text", copy.Text, StringComparison.Ordinal);
        var request = Assert.Single(runner.Requests);
        Assert.Equal(RuntimeCommandKind.ReadVoiceConversationPage, request.Kind);
        Assert.NotEqual(Guid.Empty, request.OperationId);
    }

    [Fact]
    public async Task ConversationCopyReaderMarksTruncationWhenNoFullReaderExists()
    {
        var model = new RoomVoiceConversationModel();
        model.ReplaceProjectedHistory(
        [
            new RoomVoiceConversationEntry(
                "bounded-entry",
                DateTimeOffset.UnixEpoch,
                CodexVoiceConversationKind.User,
                "Bounded visible text"),
        ],
        timelineTruncated: true);
        var copyReader = new RoomVoiceConversationCopyReader(model, readFullConversation: null);

        var copy = await copyReader.ReadAsync(showRaw: false, default);

        Assert.Contains("Bounded visible text", copy.Text, StringComparison.Ordinal);
        Assert.Contains("only the visible conversation", copy.Notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConversationCopyReaderMarksTruncatedVisibleFallbackWhenPrivateReadFails()
    {
        using var adapter = new RuntimeRoomVoiceWindowAdapter();
        var runner = new FakeCommandRunner
        {
            ExecuteHandler = (_, _) => Task.FromException<RuntimeCommandResult>(
                new InvalidDataException("page unavailable")),
        };
        Assert.True(adapter.BeginConnection(
            4,
            runner,
            new FakeVoiceTargetWriter(),
            VoiceUi(
                Session(RuntimeVoiceSessionState.Listening, active: true, version: 1),
                [new RuntimeVoiceTimelineEntry(
                    "bounded-entry",
                    DateTimeOffset.UnixEpoch,
                    RuntimeVoiceTimelineKind.User,
                    "Bounded visible text")],
                Messaging(),
                timelineTruncated: true)));
        var copyReader = new RoomVoiceConversationCopyReader(
            adapter.Conversation,
            adapter.ReadFullConversationAsync);

        var copy = await copyReader.ReadAsync(showRaw: false, default);

        Assert.Contains("Bounded visible text", copy.Text, StringComparison.Ordinal);
        Assert.Contains("visible timeline is truncated", copy.Notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MessagingUsesPrivateReadsTypedActionsAndAuthorityOwnedTargetSelection()
    {
        using var adapter = new RuntimeRoomVoiceWindowAdapter();
        var discard = new TaskCompletionSource<RuntimeCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fullMessage = new string('x', RuntimeUiLimits.MaximumVoiceOutboxPreviewCharacters + 20);
        var runner = new FakeCommandRunner
        {
            ExecuteHandler = (request, _) => request.Kind switch
            {
                RuntimeCommandKind.ReadVoiceOutboxDelivery => Task.FromResult(Completed(
                    request,
                    new RuntimeCommandPayload(
                        VoiceOutboxDelivery: new RuntimeVoiceOutboxDelivery(
                            request.Arguments!.DeliveryId!,
                            TargetReference(),
                            "Target",
                            fullMessage,
                            DateTimeOffset.UnixEpoch,
                            2,
                            "offline")))),
                RuntimeCommandKind.RetryVoiceOutboxDelivery => Task.FromResult(Completed(
                    request,
                    detail: "Delivered.")),
                RuntimeCommandKind.RetargetVoiceOutboxDelivery => Task.FromResult(Completed(request)),
                RuntimeCommandKind.DiscardVoiceOutboxDelivery => discard.Task,
                _ => Task.FromResult(Completed(request)),
            },
        };
        var targetCommit = new TaskCompletionSource<RuntimeVoiceTargetSelectionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var targetWriter = new FakeVoiceTargetWriter
        {
            SelectHandler = (_, _, _) => targetCommit.Task,
        };
        var oldTarget = TargetReference();
        var newTaskContract = TaskContract("New target");
        var newTask = ToDesktopTask(newTaskContract);
        var voice = VoiceUi(
            Session(RuntimeVoiceSessionState.Armed, active: false, version: 3),
            [],
            Messaging(
                tasks: [newTaskContract],
                drafts:
                [
                    Draft("read-1", "bounded preview", truncated: true),
                    Draft("retry-1", "retry preview", truncated: true),
                    Draft("retarget-1", "retarget preview", truncated: true),
                    Draft("discard-1", "discard preview", truncated: true),
                ],
                selected: oldTarget));
        Assert.True(adapter.BeginConnection(9, runner, targetWriter, voice));
        var summaries = adapter.TaskMessaging.LoadDrafts();
        var readSummary = Assert.Single(summaries, draft => draft.Id == "read-1");
        var retrySummary = Assert.Single(summaries, draft => draft.Id == "retry-1");
        var retargetSummary = Assert.Single(summaries, draft => draft.Id == "retarget-1");
        var discardSummary = Assert.Single(summaries, draft => draft.Id == "discard-1");

        var read = await adapter.TaskMessaging.ReadFullOutboxDeliveryAsync!(readSummary.Id, default);
        var retryDetail = await adapter.TaskMessaging.Retry(retrySummary, default);
        await adapter.TaskMessaging.Retarget(
            retargetSummary,
            newTask,
            default);
        var selectTask = adapter.TaskMessaging.SelectTarget(newTask, default);
        var discardTask = adapter.TaskMessaging.DiscardOutboxDeliveryAsync!(discardSummary.Id, default);

        Assert.Equal(fullMessage, read);
        Assert.Equal("Delivered.", retryDetail);
        Assert.False(selectTask.IsCompleted);
        Assert.False(discardTask.IsCompleted);
        Assert.Equal(
            new RuntimeTaskReference(newTask.Id, newTask.HostId),
            Assert.Single(targetWriter.Targets));
        Assert.Equal(oldTarget, adapter.GetTaskMessagingSnapshot().SelectedTaskId.Length == 0
            ? null
            : new RuntimeTaskReference(
                adapter.GetTaskMessagingSnapshot().SelectedTaskId,
                adapter.GetTaskMessagingSnapshot().SelectedHostId));
        targetCommit.SetResult(new RuntimeVoiceTargetSelectionResult(
            RuntimeVoiceTargetSelectionStatus.SavedPendingIdle,
            "Saved. Applies after this call."));
        var targetResult = await selectTask;
        Assert.Equal(RuntimeVoiceTargetSelectionStatus.SavedPendingIdle, targetResult.Status);
        Assert.Equal("Saved. Applies after this call.", targetResult.Detail);
        Assert.Equal("Saved. Applies after this call.", adapter.GetTaskMessagingSnapshot().Status);
        Assert.Equal(oldTarget, new RuntimeTaskReference(
            adapter.GetTaskMessagingSnapshot().SelectedTaskId,
            adapter.GetTaskMessagingSnapshot().SelectedHostId));

        var discardRequest = Assert.Single(
            runner.Requests,
            request => request.Kind == RuntimeCommandKind.DiscardVoiceOutboxDelivery);
        Assert.Contains(adapter.TaskMessaging.LoadDrafts(), draft => draft.Id == "discard-1");
        discard.SetResult(Completed(discardRequest));
        await discardTask;

        var remaining = adapter.TaskMessaging.LoadDrafts();
        Assert.DoesNotContain(remaining, draft => draft.Id is "retry-1" or "discard-1");
        var retargeted = Assert.Single(remaining, draft => draft.Id == "retarget-1");
        Assert.Equal(newTask.Id, retargeted.TargetTaskId);
        Assert.Equal(newTask.HostId, retargeted.TargetHostId);
        Assert.Equal(newTask.Title, retargeted.TargetTitle);
        Assert.Equal(string.Empty, retargeted.LatestError);

        Assert.Collection(
            runner.Requests,
            request => AssertDelivery(request, RuntimeCommandKind.ReadVoiceOutboxDelivery, "read-1"),
            request => AssertDelivery(request, RuntimeCommandKind.RetryVoiceOutboxDelivery, "retry-1"),
            request =>
            {
                AssertDelivery(request, RuntimeCommandKind.RetargetVoiceOutboxDelivery, "retarget-1");
                Assert.Equal(
                    new RuntimeTaskReference(newTask.Id, newTask.HostId),
                    request.Arguments?.Task);
            },
            request => AssertDelivery(request, RuntimeCommandKind.DiscardVoiceOutboxDelivery, "discard-1"));
    }

    [Fact]
    public async Task TruncatedOutboxActionsUseOnlyIdAndCopyPerformsANewPrivateRead()
    {
        using var adapter = new RuntimeRoomVoiceWindowAdapter();
        var readCount = 0;
        var runner = new FakeCommandRunner
        {
            ExecuteHandler = (request, _) => request.Kind switch
            {
                RuntimeCommandKind.ReadVoiceOutboxDelivery => Task.FromResult(Completed(
                    request,
                    new RuntimeCommandPayload(
                        VoiceOutboxDelivery: new RuntimeVoiceOutboxDelivery(
                            request.Arguments!.DeliveryId!,
                            TargetReference(),
                            "Target",
                            $"full message {++readCount}",
                            DateTimeOffset.UnixEpoch,
                            1,
                            "offline")))),
                _ => Task.FromResult(Completed(request)),
            },
        };
        var target = ToDesktopTask(TaskContract("New target"));
        Assert.True(adapter.BeginConnection(
            5,
            runner,
            new FakeVoiceTargetWriter(),
            VoiceUi(
                Session(RuntimeVoiceSessionState.Armed, active: false, version: 1),
                [],
                Messaging(
                    tasks: [TaskContract("New target")],
                    drafts:
                    [
                        Draft("copy-1", "bounded copy", truncated: true),
                        Draft("retry-1", "bounded retry", truncated: true),
                        Draft("retarget-1", "bounded retarget", truncated: true),
                    ]))));
        var drafts = adapter.TaskMessaging.LoadDrafts();
        var copyDraft = Assert.Single(drafts, draft => draft.Id == "copy-1");
        var retryDraft = Assert.Single(drafts, draft => draft.Id == "retry-1");
        var retargetDraft = Assert.Single(drafts, draft => draft.Id == "retarget-1");

        var preview = RoomVoiceOutboxInteraction.Preview(copyDraft);
        Assert.Contains("Preview truncated", preview, StringComparison.Ordinal);
        Assert.Equal(0, readCount);
        Assert.Equal(
            "full message 1",
            await RoomVoiceOutboxInteraction.ReadCopyTextAsync(
                adapter.TaskMessaging,
                copyDraft,
                default));
        Assert.Equal(
            "full message 2",
            await RoomVoiceOutboxInteraction.ReadCopyTextAsync(
                adapter.TaskMessaging,
                copyDraft,
                default));

        await adapter.TaskMessaging.Retry(retryDraft, default);
        await adapter.TaskMessaging.Retarget(retargetDraft, target, default);

        Assert.Equal(2, readCount);
        Assert.Equal(
            2,
            runner.Requests.Count(request => request.Kind == RuntimeCommandKind.ReadVoiceOutboxDelivery));
        Assert.Contains(runner.Requests, request =>
            request.Kind == RuntimeCommandKind.RetryVoiceOutboxDelivery
            && request.Arguments?.DeliveryId == "retry-1");
        Assert.Contains(runner.Requests, request =>
            request.Kind == RuntimeCommandKind.RetargetVoiceOutboxDelivery
            && request.Arguments?.DeliveryId == "retarget-1");
    }

    [Fact]
    public async Task RetryAndRetargetRemainAvailableWhenPrivateOutboxReadFails()
    {
        using var adapter = new RuntimeRoomVoiceWindowAdapter();
        var runner = new FakeCommandRunner
        {
            ExecuteHandler = (request, _) => request.Kind == RuntimeCommandKind.ReadVoiceOutboxDelivery
                ? Task.FromException<RuntimeCommandResult>(new InvalidDataException("private read failed"))
                : Task.FromResult(Completed(request)),
        };
        var target = ToDesktopTask(TaskContract("New target"));
        Assert.True(adapter.BeginConnection(6, runner, new FakeVoiceTargetWriter()));
        var retry = Summary("retry-1", "bounded", truncated: true);
        var retarget = Summary("retarget-1", "bounded", truncated: true);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            RoomVoiceOutboxInteraction.ReadCopyTextAsync(adapter.TaskMessaging, retry, default));
        await adapter.TaskMessaging.Retry(retry, default);
        await adapter.TaskMessaging.Retarget(retarget, target, default);

        Assert.Collection(
            runner.Requests,
            request => AssertDelivery(request, RuntimeCommandKind.ReadVoiceOutboxDelivery, "retry-1"),
            request => AssertDelivery(request, RuntimeCommandKind.RetryVoiceOutboxDelivery, "retry-1"),
            request => AssertDelivery(request, RuntimeCommandKind.RetargetVoiceOutboxDelivery, "retarget-1"));
    }

    [Fact]
    public async Task PrivateOutboxReadRejectsAMismatchedDeliveryId()
    {
        using var adapter = new RuntimeRoomVoiceWindowAdapter();
        var runner = new FakeCommandRunner
        {
            ExecuteHandler = (request, _) => Task.FromResult(Completed(
                request,
                new RuntimeCommandPayload(
                    VoiceOutboxDelivery: new RuntimeVoiceOutboxDelivery(
                        "different-id",
                        TargetReference(),
                        "Target",
                        "private body",
                        DateTimeOffset.UnixEpoch,
                        1,
                        "offline")))),
        };
        Assert.True(adapter.BeginConnection(7, runner, new FakeVoiceTargetWriter()));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            adapter.TaskMessaging.ReadFullOutboxDeliveryAsync!("expected-id", default));
    }

    [Fact]
    public async Task LostReplyIsReconciledByOperationIdWithoutResubmittingTheSideEffect()
    {
        using var adapter = new RuntimeRoomVoiceWindowAdapter();
        RuntimeCommandRequest? accepted = null;
        var runner = new FakeCommandRunner
        {
            ExecuteHandler = (request, _) =>
            {
                accepted = request;
                throw new IOException("reply lost");
            },
            LookupHandler = (operationId, _) => Task.FromResult(
                new RuntimeCommandOperationResult(
                    operationId,
                    RuntimeCommandOperationState.Completed,
                    Completed(accepted!, detail: "Delivered after lookup."))),
        };
        Assert.True(adapter.BeginConnection(2, runner, new FakeVoiceTargetWriter()));

        var detail = await adapter.TaskMessaging.Retry(
            Summary("draft-1", "complete", truncated: false),
            default);

        Assert.Equal("Delivered after lookup.", detail);
        Assert.Single(runner.Requests);
        Assert.Equal(accepted!.OperationId, Assert.Single(runner.Lookups));
    }

    [Fact]
    public async Task UnknownLostReplyIsReportedWithoutResubmittingTheSideEffect()
    {
        using var adapter = new RuntimeRoomVoiceWindowAdapter();
        var runner = new FakeCommandRunner
        {
            ExecuteHandler = (_, _) => throw new IOException("reply lost"),
            LookupHandler = (operationId, _) => Task.FromResult(
                new RuntimeCommandOperationResult(
                    operationId,
                    RuntimeCommandOperationState.NotFound)),
        };
        Assert.True(adapter.BeginConnection(2, runner, new FakeVoiceTargetWriter()));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.TaskMessaging.Retry(
                Summary("draft-1", "complete", truncated: false),
                default));

        Assert.Contains("was not resubmitted", exception.Message, StringComparison.Ordinal);
        Assert.Single(runner.Requests);
        Assert.Single(runner.Lookups);
    }

    [Fact]
    public async Task ReconnectRejectsAnOldCompletionAndKeepsTheNewProjection()
    {
        using var adapter = new RuntimeRoomVoiceWindowAdapter();
        var oldCompletion = new TaskCompletionSource<RuntimeCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        RuntimeCommandRequest? oldRequest = null;
        var oldRunner = new FakeCommandRunner
        {
            ExecuteHandler = (request, _) =>
            {
                oldRequest = request;
                return oldCompletion.Task;
            },
        };
        Assert.True(adapter.BeginConnection(
            10,
            oldRunner,
            new FakeVoiceTargetWriter(),
            VoiceUi(
                Session(RuntimeVoiceSessionState.Listening, active: true, version: 1),
                [],
                Messaging(tasks: [TaskContract("Old")]))));
        var oldRefresh = adapter.TaskMessaging.Refresh(default);

        var newTask = TaskContract("New");
        Assert.True(adapter.BeginConnection(
            11,
            new FakeCommandRunner(),
            new FakeVoiceTargetWriter(),
            VoiceUi(
                Session(RuntimeVoiceSessionState.Armed, active: false, version: 2),
                [],
                Messaging(tasks: [newTask]))));
        oldCompletion.SetResult(Completed(
            oldRequest!,
            new RuntimeCommandPayload(DesktopTasks: [TaskContract("Stale result")])));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldRefresh);
        Assert.Equal("New", Assert.Single(adapter.GetTaskMessagingSnapshot().Tasks).Title);
        Assert.Equal(VoicePeSessionState.Armed, adapter.Conversation.GetSnapshot().SessionState);
        Assert.False(adapter.ApplyEvent(
            10,
            new RuntimeVoiceEvent(Session(RuntimeVoiceSessionState.Error, active: false, version: 3))));
    }

    [Fact]
    public async Task ReconnectRecoversAcceptedTargetOperationBeforeAnotherSubmission()
    {
        using var adapter = new RuntimeRoomVoiceWindowAdapter();
        var target = ToDesktopTask(TaskContract("Target"));
        var oldReply = new TaskCompletionSource<RuntimeVoiceTargetSelectionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var oldWriter = new FakeVoiceTargetWriter
        {
            SelectHandler = (_, _, _) => oldReply.Task,
        };
        Assert.True(adapter.BeginConnection(20, new FakeCommandRunner(), oldWriter));
        var selection = adapter.TaskMessaging.SelectTarget(target, default);
        var operationId = Assert.Single(oldWriter.Operations);

        var recoveredReply = new TaskCompletionSource<RuntimeVoiceTargetSelectionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var newWriter = new FakeVoiceTargetWriter
        {
            RecoverHandler = (_, _, _) => recoveredReply.Task,
        };
        Assert.True(adapter.BeginConnection(21, new FakeCommandRunner(), newWriter));
        Assert.Equal([operationId], newWriter.Recoveries);

        oldReply.SetResult(new RuntimeVoiceTargetSelectionResult(
            RuntimeVoiceTargetSelectionStatus.Applied,
            "stale reply"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selection);

        var repeatedSelection = adapter.TaskMessaging.SelectTarget(target, default);
        Assert.Empty(newWriter.Operations);
        recoveredReply.SetResult(new RuntimeVoiceTargetSelectionResult(
            RuntimeVoiceTargetSelectionStatus.Applied,
            "Recovered exact target operation."));

        var recovered = await repeatedSelection;
        Assert.Equal(RuntimeVoiceTargetSelectionStatus.Applied, recovered.Status);
        Assert.Equal("Recovered exact target operation.", adapter.GetTaskMessagingSnapshot().Status);
        Assert.Equal([operationId], newWriter.Recoveries);
        Assert.Empty(newWriter.Operations);
    }

    [Fact]
    public async Task SameConnectionRetriesUncertainTargetRecoveryByExactOperationId()
    {
        using var adapter = new RuntimeRoomVoiceWindowAdapter();
        var target = ToDesktopTask(TaskContract("Target"));
        var oldReply = new TaskCompletionSource<RuntimeVoiceTargetSelectionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var oldWriter = new FakeVoiceTargetWriter
        {
            SelectHandler = (_, _, _) => oldReply.Task,
        };
        Assert.True(adapter.BeginConnection(30, new FakeCommandRunner(), oldWriter));
        var staleSelection = adapter.TaskMessaging.SelectTarget(target, default);
        var operationId = Assert.Single(oldWriter.Operations);
        var recoveryCount = 0;
        var newWriter = new FakeVoiceTargetWriter
        {
            RecoverHandler = (_, _, _) => Task.FromResult(
                Interlocked.Increment(ref recoveryCount) == 1
                    ? new RuntimeVoiceTargetSelectionResult(
                        RuntimeVoiceTargetSelectionStatus.Uncertain,
                        "Still reconciling.")
                    : new RuntimeVoiceTargetSelectionResult(
                        RuntimeVoiceTargetSelectionStatus.Applied,
                        "Recovered on retry.")),
        };

        Assert.True(adapter.BeginConnection(31, new FakeCommandRunner(), newWriter));
        var recovered = await adapter.TaskMessaging.SelectTarget(target, default);

        Assert.Equal(RuntimeVoiceTargetSelectionStatus.Applied, recovered.Status);
        Assert.Equal([operationId, operationId], newWriter.Recoveries);
        Assert.Empty(newWriter.Operations);
        oldReply.SetResult(new RuntimeVoiceTargetSelectionResult(
            RuntimeVoiceTargetSelectionStatus.Applied,
            "stale reply"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => staleSelection);
    }

    [Fact]
    public async Task DisposeCancelsPendingWorkAndRejectsLaterUse()
    {
        var adapter = new RuntimeRoomVoiceWindowAdapter();
        var completion = new TaskCompletionSource<RuntimeCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        RuntimeCommandRequest? request = null;
        var runner = new FakeCommandRunner
        {
            ExecuteHandler = (value, _) =>
            {
                request = value;
                return completion.Task;
            },
        };
        Assert.True(adapter.BeginConnection(1, runner, new FakeVoiceTargetWriter()));
        var pending = adapter.EndSessionAsync();

        adapter.Dispose();
        completion.SetResult(Completed(request!));

        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => adapter.RestartAsync());
        Assert.False(adapter.ApplySnapshot(
            1,
            VoiceUi(
                Session(RuntimeVoiceSessionState.Armed, active: false, version: 1),
                [],
                Messaging())));
    }

    [Fact]
    public async Task LegacyInProcessMessagingCallbacksAdaptFullDraftsWithoutLosingTheirBodies()
    {
        var full = new VoiceTaskOutboxDraft(
            "0123456789abcdef0123456789abcdef",
            TargetReference().TaskId,
            TargetReference().HostId,
            "Target",
            "complete legacy message",
            "session",
            DateTimeOffset.UnixEpoch,
            Attempts: 1,
            LatestError: "offline");
        var drafts = new List<VoiceTaskOutboxDraft> { full };
        VoiceTaskOutboxDraft? retried = null;
        VoiceTaskOutboxDraft? discarded = null;
        var callbacks = new RoomVoiceTaskMessagingCallbacks(
            _ => Task.FromResult(new RoomVoiceTaskMessagingSnapshot(
                true,
                true,
                "ready",
                [],
                full.TargetTaskId,
                full.TargetHostId,
                full.TargetTitle,
                (IReadOnlyList<VoiceTaskOutboxDraft>)drafts)),
            (_, _) => Task.CompletedTask,
            (draft, _) =>
            {
                retried = draft;
                return Task.FromResult("retried");
            },
            (_, _, _) => Task.CompletedTask,
            draft =>
            {
                discarded = draft;
                drafts.Remove(draft);
            },
            () => drafts);

        var summary = Assert.Single(callbacks.LoadDrafts());
        var body = await callbacks.ReadFullOutboxDeliveryAsync!(summary.Id, default);
        var detail = await callbacks.Retry(summary, default);
        await callbacks.DiscardOutboxDeliveryAsync!(summary.Id, default);

        Assert.False(summary.MessageTruncated);
        Assert.Equal(full.Message, summary.MessagePreview);
        Assert.Equal(full.Message, body);
        Assert.Equal("retried", detail);
        Assert.Same(full, retried);
        Assert.Same(full, discarded);
        Assert.Empty(drafts);
    }

    private static RuntimeVoiceUiSnapshot VoiceUi(
        RuntimeVoiceSnapshot session,
        RuntimeVoiceTimelineEntry[] timeline,
        RuntimeVoiceMessagingSnapshot messaging,
        bool timelineTruncated = false) =>
        new(session, timeline, messaging, timelineTruncated);

    private static RuntimeVoiceSnapshot Session(
        RuntimeVoiceSessionState state,
        bool active,
        long version) =>
        new(
            state,
            OwnerReady: true,
            SessionActive: active,
            HistoryAvailable: true,
            Stale: false,
            state.ToString(),
            Error: null,
            version);

    private static RuntimeVoiceMessagingSnapshot Messaging(
        RuntimeDesktopTask[]? tasks = null,
        RuntimeVoiceOutboxDraft[]? drafts = null,
        RuntimeTaskReference? selected = null) =>
        new(
            Enabled: true,
            BridgeAvailable: true,
            "Desktop bridge connected.",
            tasks ?? [],
            selected,
            "Selected",
            drafts ?? []);

    private static RuntimeVoiceOutboxDraft Draft(
        string id,
        string preview,
        bool truncated) =>
        new(
            id,
            TargetReference(),
            "Target",
            preview,
            truncated,
            DateTimeOffset.UnixEpoch,
            Attempts: 2,
            LatestError: "offline");

    private static RoomVoiceOutboxDraftSummary Summary(
        string id,
        string preview,
        bool truncated) =>
        new(
            id,
            TargetReference().TaskId,
            TargetReference().HostId,
            "Target",
            preview,
            truncated,
            DateTimeOffset.UnixEpoch,
            Attempts: 2,
            LatestError: "offline");

    private static DesktopTaskSummary ToDesktopTask(RuntimeDesktopTask task) =>
        new(
            task.TaskId,
            task.HostId,
            task.Title,
            task.Status,
            task.ProjectId,
            task.WorkingDirectory,
            task.UpdatedAt,
            task.Pinned);

    private static RuntimeDesktopTask TaskContract(string title) =>
        new(
            Guid.NewGuid().ToString("D"),
            "local",
            title,
            "idle",
            ProjectId: null,
            WorkingDirectory: null,
            UpdatedAt: 1);

    private static RuntimeTaskReference TargetReference() =>
        new("00000000-0000-0000-0000-000000000001", "local");

    private static RuntimeCommandResult Completed(
        RuntimeCommandRequest request,
        RuntimeCommandPayload? payload = null,
        string? detail = null) =>
        new(
            request.OperationId,
            request.Kind,
            RuntimeCommandStatus.Completed,
            detail,
            payload);

    private static void AssertDelivery(
        RuntimeCommandRequest request,
        RuntimeCommandKind kind,
        string deliveryId)
    {
        Assert.NotEqual(Guid.Empty, request.OperationId);
        Assert.Equal(kind, request.Kind);
        Assert.Equal(deliveryId, request.Arguments?.DeliveryId);
    }

    private sealed class FakeCommandRunner : IRuntimeCommandRunner
    {
        public Func<RuntimeCommandRequest, CancellationToken, Task<RuntimeCommandResult>> ExecuteHandler { get; init; } =
            (request, _) => Task.FromResult(Completed(request));

        public Func<Guid, CancellationToken, Task<RuntimeCommandOperationResult>> LookupHandler { get; init; } =
            (operationId, _) => Task.FromResult(new RuntimeCommandOperationResult(
                operationId,
                RuntimeCommandOperationState.NotFound));

        public List<RuntimeCommandRequest> Requests { get; } = [];

        public List<Guid> Lookups { get; } = [];

        public Task<RuntimeCommandResult> ExecuteAsync(
            RuntimeCommandRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ExecuteHandler(request, cancellationToken);
        }

        public Task<RuntimeCommandOperationResult> GetOperationAsync(
            Guid operationId,
            CancellationToken cancellationToken)
        {
            Lookups.Add(operationId);
            return LookupHandler(operationId, cancellationToken);
        }
    }

    private sealed class FakeVoiceTargetWriter : IRuntimeVoiceTargetWriter
    {
        public Func<RuntimeTaskReference, Guid, CancellationToken, Task<RuntimeVoiceTargetSelectionResult>> SelectHandler { get; init; } =
            (_, _, _) => Task.FromResult(new RuntimeVoiceTargetSelectionResult(
                RuntimeVoiceTargetSelectionStatus.Applied,
                "Applied."));

        public Func<RuntimeTaskReference, Guid, CancellationToken, Task<RuntimeVoiceTargetSelectionResult>> RecoverHandler { get; init; } =
            (_, _, _) => Task.FromResult(new RuntimeVoiceTargetSelectionResult(
                RuntimeVoiceTargetSelectionStatus.Uncertain,
                "Still reconciling."));

        public List<RuntimeTaskReference> Targets { get; } = [];

        public List<Guid> Operations { get; } = [];

        public List<Guid> Recoveries { get; } = [];

        public Task<RuntimeVoiceTargetSelectionResult> SelectAsync(
            RuntimeTaskReference target,
            Guid operationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Targets.Add(target);
            Operations.Add(operationId);
            return SelectHandler(target, operationId, cancellationToken);
        }

        public Task<RuntimeVoiceTargetSelectionResult> RecoverAsync(
            RuntimeTaskReference target,
            Guid operationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Recoveries.Add(operationId);
            return RecoverHandler(target, operationId, cancellationToken);
        }
    }
}
