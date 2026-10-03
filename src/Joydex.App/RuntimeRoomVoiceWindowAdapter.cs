using Joydex.Contracts;
using Joydex.Core.Voice;
using Joydex.Windows.Voice;

namespace Joydex.App;

/// <summary>
/// Runs typed commands for one attached runtime connection. The implementation may lose a direct
/// reply after the host accepted a command, so callers can look up that exact operation ID.
/// </summary>
internal interface IRuntimeCommandRunner
{
    Task<RuntimeCommandResult> ExecuteAsync(
        RuntimeCommandRequest request,
        CancellationToken cancellationToken);

    Task<RuntimeCommandOperationResult> GetOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken);
}

internal enum RuntimeVoiceTargetSelectionStatus
{
    Applied,
    SavedPendingIdle,
    Conflict,
    Failed,
    Uncertain,
}

internal sealed record RuntimeVoiceTargetSelectionResult(
    RuntimeVoiceTargetSelectionStatus Status,
    string Detail);

/// <summary>
/// Commits the saved Voice Target through the runtime settings authority for one connection.
/// Selection must remain available during an active Voice session and must not restart its owner.
/// </summary>
internal interface IRuntimeVoiceTargetWriter
{
    Task<RuntimeVoiceTargetSelectionResult> SelectAsync(
        RuntimeTaskReference target,
        Guid operationId,
        CancellationToken cancellationToken);

    Task<RuntimeVoiceTargetSelectionResult> RecoverAsync(
        RuntimeTaskReference target,
        Guid operationId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Adapts bounded runtime Voice state and explicit runtime commands to the existing Room Voice
/// window model. It owns no Voice runtime, task bridge, durable outbox, or settings store.
/// </summary>
internal sealed class RuntimeRoomVoiceWindowAdapter : IDisposable
{
    private const int MaximumConversationPages = 256;
    private readonly object _gate = new();
    private readonly RoomVoiceConversationModel _conversation = new();
    private Connection? _connection;
    private PendingVoiceTargetWrite? _pendingTargetWrite;
    private RuntimeVoiceMessagingSnapshot _messaging = EmptyMessaging();
    private long _latestConnectionGeneration = long.MinValue;
    private bool _disposed;

    public RuntimeRoomVoiceWindowAdapter()
    {
        TaskMessaging = new RoomVoiceTaskMessagingCallbacks(
            RefreshTaskMessagingAsync,
            SelectTargetAsync,
            RetryOutboxDeliveryAsync,
            RetargetOutboxDeliveryAsync,
            LoadOutboxDrafts,
            ReadFullOutboxDeliveryAsync,
            DiscardOutboxDeliveryAsync);
    }

    public RoomVoiceConversationModel Conversation => _conversation;

    public RoomVoiceTaskMessagingCallbacks TaskMessaging { get; }

    public RoomVoiceTaskMessagingSnapshot GetTaskMessagingSnapshot()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ToTaskMessagingSnapshot(_messaging);
        }
    }

    /// <summary>
    /// Starts a newer connection generation. Replacing a connection cancels its local waits; the
    /// runtime remains responsible for any command it already accepted.
    /// </summary>
    public bool BeginConnection(
        long connectionGeneration,
        IRuntimeCommandRunner commands,
        IRuntimeVoiceTargetWriter targetWriter,
        RuntimeVoiceUiSnapshot? snapshot = null)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(targetWriter);
        Connection? previous;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (connectionGeneration <= _latestConnectionGeneration)
            {
                return false;
            }

            previous = _connection;
            _latestConnectionGeneration = connectionGeneration;
            _connection = new Connection(connectionGeneration, commands, targetWriter);
            if (snapshot is not null)
            {
                ApplySnapshotCore(snapshot);
            }
        }
        Cancel(previous);
        StartPendingTargetRecovery();
        return true;
    }

    /// <summary>Applies a complete Voice projection only to the matching live connection.</summary>
    public bool ApplySnapshot(long connectionGeneration, RuntimeVoiceUiSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            if (!IsCurrentConnection(connectionGeneration))
            {
                return false;
            }
            ApplySnapshotCore(snapshot);
            return true;
        }
    }

    /// <summary>
    /// Applies an incremental Voice event only to the matching live connection. A timeline reset
    /// marks the current view stale until the client forwards its newly fetched snapshot.
    /// </summary>
    public bool ApplyEvent(long connectionGeneration, RuntimeVoiceEvent runtimeEvent)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        lock (_gate)
        {
            if (!IsCurrentConnection(connectionGeneration))
            {
                return false;
            }

            if (runtimeEvent.Messaging is not null)
            {
                _messaging = runtimeEvent.Messaging;
            }
            if (!runtimeEvent.TimelineReset && runtimeEvent.TimelineEntry is { } entry)
            {
                ApplyTimelineEntry(entry);
            }
            ApplySession(
                runtimeEvent.Session,
                stale: runtimeEvent.Session.Stale || runtimeEvent.TimelineReset);
            return true;
        }
    }

    /// <summary>Marks the matching connection unavailable without stopping any runtime owner.</summary>
    public bool EndConnection(long connectionGeneration)
    {
        Connection? previous;
        lock (_gate)
        {
            if (!IsCurrentConnection(connectionGeneration))
            {
                return false;
            }

            previous = _connection;
            _connection = null;
            var snapshot = _conversation.GetSnapshot();
            _conversation.SetRuntimeState(
                snapshot.SessionState,
                snapshot.OwnerReady,
                snapshot.SessionActive,
                snapshot.Status,
                snapshot.Error,
                stale: true);
        }
        Cancel(previous);
        return true;
    }

    public Task RefreshConversationAsync(CancellationToken cancellationToken = default) =>
        ExecuteWithoutPayloadAsync(RuntimeCommandKind.RefreshRoomVoiceConversation, cancellationToken);

    public Task EndSessionAsync(CancellationToken cancellationToken = default) =>
        ExecuteWithoutPayloadAsync(RuntimeCommandKind.EndRoomVoiceSession, cancellationToken);

    public Task RestartAsync(CancellationToken cancellationToken = default) =>
        ExecuteWithoutPayloadAsync(RuntimeCommandKind.RestartRoomVoice, cancellationToken);

    /// <summary>
    /// Reads the complete versioned conversation through connection-scoped pages. This is the
    /// source for deliberate full-conversation copy or raw-text inspection.
    /// </summary>
    public async Task<IReadOnlyList<RoomVoiceConversationEntry>> ReadFullConversationAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = GetConnection();
        var entries = new List<RoomVoiceConversationEntry>();
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        string? continuationToken = null;
        for (var pageNumber = 0; pageNumber < MaximumConversationPages; pageNumber++)
        {
            var request = new RuntimeCommandRequest(
                Guid.NewGuid(),
                RuntimeCommandKind.ReadVoiceConversationPage,
                continuationToken is null
                    ? null
                    : new RuntimeCommandArguments(ContinuationToken: continuationToken));
            var result = await ExecuteRequiredAsync(connection, request, cancellationToken)
                .ConfigureAwait(false);
            var page = result.Payload?.VoiceConversation
                ?? throw new InvalidDataException("The runtime omitted the Voice conversation page.");
            entries.AddRange(page.Entries.Select(ToConversationEntry));

            continuationToken = page.NextContinuationToken;
            if (continuationToken is null)
            {
                return entries;
            }
            if (!seenTokens.Add(continuationToken))
            {
                throw new InvalidDataException("The runtime repeated a Voice conversation continuation token.");
            }
        }

        throw new InvalidDataException("The Voice conversation exceeded the supported page count.");
    }

    public void Dispose()
    {
        Connection? previous;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            previous = _connection;
            _connection = null;
        }
        Cancel(previous);
    }

    private async Task<RoomVoiceTaskMessagingSnapshot> RefreshTaskMessagingAsync(
        CancellationToken cancellationToken)
    {
        var connection = GetConnection();
        var request = new RuntimeCommandRequest(Guid.NewGuid(), RuntimeCommandKind.ListDesktopTasks);
        var result = await ExecuteRequiredAsync(connection, request, cancellationToken)
            .ConfigureAwait(false);
        var tasks = result.Payload?.DesktopTasks
            ?? throw new InvalidDataException("The runtime omitted the Desktop task catalog.");
        lock (_gate)
        {
            EnsureCurrent(connection);
            _messaging = _messaging with
            {
                Tasks = tasks,
                BridgeAvailable = true,
                Status = "Desktop bridge connected.",
                TasksTruncated = false,
            };
            return ToTaskMessagingSnapshot(_messaging);
        }
    }

    private async Task<RuntimeVoiceTargetSelectionResult> SelectTargetAsync(
        DesktopTaskSummary target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var requestedTarget = new RuntimeTaskReference(target.Id, target.HostId);
        while (true)
        {
            Connection connection;
            PendingVoiceTargetWrite pending;
            Task<RuntimeVoiceTargetSelectionResult>? recovery;
            var submit = false;
            lock (_gate)
            {
                connection = GetConnectionLocked();
                if (_pendingTargetWrite is null)
                {
                    pending = new PendingVoiceTargetWrite(Guid.NewGuid(), requestedTarget);
                    _pendingTargetWrite = pending;
                    submit = true;
                    recovery = null;
                }
                else
                {
                    pending = _pendingTargetWrite;
                    recovery = StartPendingTargetRecoveryLocked(connection, pending);
                }
            }

            RuntimeVoiceTargetSelectionResult result;
            if (submit)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    connection.Cancellation.Token);
                result = await connection.TargetWriter.SelectAsync(
                        pending.Target,
                        pending.OperationId,
                        linked.Token)
                    .ConfigureAwait(false)
                    ?? throw new InvalidDataException(
                        "The settings authority omitted the Voice target result.");
                ApplyTargetResult(connection, pending, result);
            }
            else
            {
                result = await recovery!.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (SameTarget(pending.Target, requestedTarget)
                || result.Status == RuntimeVoiceTargetSelectionStatus.Uncertain)
            {
                return result;
            }
        }
    }

    private void StartPendingTargetRecovery()
    {
        Task<RuntimeVoiceTargetSelectionResult>? recovery;
        lock (_gate)
        {
            recovery = _connection is { } connection && _pendingTargetWrite is { } pending
                ? StartPendingTargetRecoveryLocked(connection, pending)
                : null;
        }
        if (recovery is not null)
        {
            ObserveFailure(recovery);
        }
    }

    private Task<RuntimeVoiceTargetSelectionResult> StartPendingTargetRecoveryLocked(
        Connection connection,
        PendingVoiceTargetWrite pending)
    {
        if (connection.TargetRecovery is not null)
        {
            return connection.TargetRecovery;
        }
        var recovery = RecoverPendingTargetAsync(connection, pending);
        connection.TargetRecovery = recovery;
        _ = recovery.ContinueWith(
            completed => ClearCompletedTargetRecovery(connection, completed),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return recovery;
    }

    private void ClearCompletedTargetRecovery(
        Connection connection,
        Task<RuntimeVoiceTargetSelectionResult> completed)
    {
        lock (_gate)
        {
            if (ReferenceEquals(connection.TargetRecovery, completed))
            {
                connection.TargetRecovery = null;
            }
        }
    }

    private async Task<RuntimeVoiceTargetSelectionResult> RecoverPendingTargetAsync(
        Connection connection,
        PendingVoiceTargetWrite pending)
    {
        var result = await connection.TargetWriter.RecoverAsync(
                pending.Target,
                pending.OperationId,
                connection.Cancellation.Token)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                "The settings authority omitted the recovered Voice target result.");
        ApplyTargetResult(connection, pending, result);
        return result;
    }

    private void ApplyTargetResult(
        Connection connection,
        PendingVoiceTargetWrite pending,
        RuntimeVoiceTargetSelectionResult result)
    {
        lock (_gate)
        {
            // The settings snapshot/event remains authoritative for SelectedTask and SelectedLabel.
            EnsureCurrent(connection);
            if (ReferenceEquals(_pendingTargetWrite, pending)
                && result.Status != RuntimeVoiceTargetSelectionStatus.Uncertain)
            {
                _pendingTargetWrite = null;
            }
            _messaging = _messaging with { Status = result.Detail };
        }
    }

    private async Task<string?> ReadFullOutboxDeliveryAsync(
        string deliveryId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);
        var connection = GetConnection();
        var request = DeliveryRequest(RuntimeCommandKind.ReadVoiceOutboxDelivery, deliveryId);
        var result = await ExecuteRequiredAsync(connection, request, cancellationToken)
            .ConfigureAwait(false);
        var delivery = result.Payload?.VoiceOutboxDelivery
            ?? throw new InvalidDataException("The runtime omitted the Voice outbox delivery.");
        if (!string.Equals(delivery.Id, deliveryId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The runtime returned a different Voice outbox delivery.");
        }
        return delivery.Message;
    }

    private async Task<string> RetryOutboxDeliveryAsync(
        RoomVoiceOutboxDraftSummary draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var connection = GetConnection();
        var result = await ExecuteRequiredAsync(
                connection,
                DeliveryRequest(RuntimeCommandKind.RetryVoiceOutboxDelivery, draft.Id),
                cancellationToken)
            .ConfigureAwait(false);
        RemoveOutboxDraft(connection, draft.Id);
        return result.Detail ?? "Delivered.";
    }

    private async Task RetargetOutboxDeliveryAsync(
        RoomVoiceOutboxDraftSummary draft,
        DesktopTaskSummary target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(target);
        var connection = GetConnection();
        var request = new RuntimeCommandRequest(
            Guid.NewGuid(),
            RuntimeCommandKind.RetargetVoiceOutboxDelivery,
            new RuntimeCommandArguments(
                Task: new RuntimeTaskReference(target.Id, target.HostId),
                DeliveryId: draft.Id));
        _ = await ExecuteRequiredAsync(connection, request, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            EnsureCurrent(connection);
            _messaging = _messaging with
            {
                Drafts = _messaging.Drafts.Select(candidate =>
                    string.Equals(candidate.Id, draft.Id, StringComparison.OrdinalIgnoreCase)
                        ? candidate with
                        {
                            TargetTask = new RuntimeTaskReference(target.Id, target.HostId),
                            TargetTitle = target.Title,
                            LatestError = string.Empty,
                        }
                        : candidate).ToArray(),
            };
        }
    }

    private async Task DiscardOutboxDeliveryAsync(
        string deliveryId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);
        var connection = GetConnection();
        _ = await ExecuteRequiredAsync(
                connection,
                DeliveryRequest(RuntimeCommandKind.DiscardVoiceOutboxDelivery, deliveryId),
                cancellationToken)
            .ConfigureAwait(false);
        RemoveOutboxDraft(connection, deliveryId);
    }

    private IReadOnlyList<RoomVoiceOutboxDraftSummary> LoadOutboxDrafts()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ToOutboxDrafts(_messaging.Drafts);
        }
    }

    private void RemoveOutboxDraft(Connection connection, string deliveryId)
    {
        lock (_gate)
        {
            EnsureCurrent(connection);
            _messaging = _messaging with
            {
                Drafts = _messaging.Drafts.Where(candidate =>
                    !string.Equals(candidate.Id, deliveryId, StringComparison.OrdinalIgnoreCase)).ToArray(),
            };
        }
    }

    private async Task ExecuteWithoutPayloadAsync(
        RuntimeCommandKind kind,
        CancellationToken cancellationToken)
    {
        var connection = GetConnection();
        _ = await ExecuteRequiredAsync(
                connection,
                new RuntimeCommandRequest(Guid.NewGuid(), kind),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<RuntimeCommandResult> ExecuteRequiredAsync(
        Connection connection,
        RuntimeCommandRequest request,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            connection.Cancellation.Token);
        RuntimeCommandResult result;
        try
        {
            result = await connection.Commands.ExecuteAsync(request, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException executeFailure)
            when (!cancellationToken.IsCancellationRequested
                  && !connection.Cancellation.IsCancellationRequested)
        {
            result = await ReconcileAsync(connection, request, executeFailure, linked.Token)
                .ConfigureAwait(false);
        }
        catch (Exception executeFailure) when (executeFailure is IOException
            or TimeoutException
            or InvalidOperationException)
        {
            result = await ReconcileAsync(connection, request, executeFailure, linked.Token)
                .ConfigureAwait(false);
        }

        lock (_gate)
        {
            EnsureCurrent(connection);
        }
        return ValidateCompletedResult(request, result);
    }

    private async Task<RuntimeCommandResult> ReconcileAsync(
        Connection connection,
        RuntimeCommandRequest request,
        Exception executeFailure,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            EnsureCurrent(connection);
        }

        RuntimeCommandOperationResult operation;
        try
        {
            operation = await connection.Commands
                .GetOperationAsync(request.OperationId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception lookupFailure) when (lookupFailure is IOException
            or TimeoutException
            or InvalidOperationException)
        {
            throw CommandUncertain(request, executeFailure, lookupFailure);
        }

        lock (_gate)
        {
            EnsureCurrent(connection);
        }
        if (operation.OperationId != request.OperationId)
        {
            throw new InvalidDataException("The runtime returned a mismatched command operation identity.");
        }
        if (operation.State == RuntimeCommandOperationState.Completed && operation.Result is not null)
        {
            return operation.Result;
        }

        var detail = operation.State == RuntimeCommandOperationState.Running
            ? "was accepted and is still running"
            : "could not be found after its reply was lost";
        throw new InvalidOperationException(
            $"The {request.Kind} operation {request.OperationId:D} {detail}. Its side effect was not resubmitted.",
            executeFailure);
    }

    private static RuntimeCommandResult ValidateCompletedResult(
        RuntimeCommandRequest request,
        RuntimeCommandResult result)
    {
        if (result.OperationId != request.OperationId || result.Kind != request.Kind)
        {
            throw new InvalidDataException("The runtime returned a mismatched command result identity.");
        }
        if (result.Status != RuntimeCommandStatus.Completed)
        {
            throw new InvalidOperationException(
                result.Detail ?? $"The runtime {result.Status.ToString().ToLowerInvariant()} the {request.Kind} command.");
        }
        return result;
    }

    private void ApplySnapshotCore(RuntimeVoiceUiSnapshot snapshot)
    {
        _messaging = snapshot.Messaging;
        _conversation.ReplaceProjectedHistory(
            snapshot.Timeline.Select(ToConversationEntry).ToArray(),
            snapshot.TimelineTruncated);
        ApplySession(snapshot.Session, snapshot.Session.Stale);
    }

    private void ApplyTimelineEntry(RuntimeVoiceTimelineEntry entry) =>
        _conversation.UpsertProjectedEntry(ToConversationEntry(entry));

    private void ApplySession(RuntimeVoiceSnapshot session, bool stale) =>
        _conversation.SetRuntimeState(
            session.SessionState switch
            {
                RuntimeVoiceSessionState.Armed => VoicePeSessionState.Armed,
                RuntimeVoiceSessionState.Starting => VoicePeSessionState.Starting,
                RuntimeVoiceSessionState.Listening => VoicePeSessionState.Listening,
                RuntimeVoiceSessionState.Muted => VoicePeSessionState.Muted,
                _ => VoicePeSessionState.Error,
            },
            session.OwnerReady,
            session.SessionActive,
            session.Status,
            session.Error,
            stale);

    private Connection GetConnection()
    {
        lock (_gate)
        {
            return GetConnectionLocked();
        }
    }

    private Connection GetConnectionLocked()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _connection
            ?? throw new InvalidOperationException("The Room Voice runtime is disconnected.");
    }

    private bool IsCurrentConnection(long connectionGeneration) =>
        !_disposed && _connection?.Generation == connectionGeneration;

    private void EnsureCurrent(Connection connection)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(_connection, connection))
        {
            throw new OperationCanceledException(
                "The Room Voice runtime connection changed before the operation completed.");
        }
    }

    private static RoomVoiceTaskMessagingSnapshot ToTaskMessagingSnapshot(
        RuntimeVoiceMessagingSnapshot messaging) =>
        new(
            messaging.Enabled,
            messaging.BridgeAvailable,
            messaging.Status,
            messaging.Tasks.Select(ToDesktopTask).ToArray(),
            messaging.SelectedTask?.TaskId ?? string.Empty,
            messaging.SelectedTask?.HostId ?? string.Empty,
            messaging.SelectedLabel,
            ToOutboxDrafts(messaging.Drafts),
            messaging.DraftsTruncated);

    private static RoomVoiceOutboxDraftSummary[] ToOutboxDrafts(
        IEnumerable<RuntimeVoiceOutboxDraft> drafts) =>
        drafts.Select(draft => new RoomVoiceOutboxDraftSummary(
            draft.Id,
            draft.TargetTask.TaskId,
            draft.TargetTask.HostId,
            draft.TargetTitle,
            draft.MessagePreview,
            draft.MessageTruncated,
            draft.CreatedAt,
            draft.Attempts,
            draft.LatestError)).ToArray();

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

    private static RoomVoiceConversationEntry ToConversationEntry(
        RuntimeVoiceConversationEntry entry) =>
        new(
            entry.Id,
            entry.Timestamp,
            ToConversationKind(entry.Kind),
            entry.Text,
            entry.IsPartial,
            entry.RawText);

    private static RoomVoiceConversationEntry ToConversationEntry(RuntimeVoiceTimelineEntry entry) =>
        new(
            entry.Id,
            entry.Timestamp,
            ToConversationKind(entry.Kind),
            entry.Text,
            entry.IsPartial);

    private static CodexVoiceConversationKind ToConversationKind(RuntimeVoiceTimelineKind kind) => kind switch
    {
        RuntimeVoiceTimelineKind.User => CodexVoiceConversationKind.User,
        RuntimeVoiceTimelineKind.Assistant => CodexVoiceConversationKind.Assistant,
        _ => CodexVoiceConversationKind.Activity,
    };

    private static RuntimeCommandRequest DeliveryRequest(RuntimeCommandKind kind, string deliveryId) =>
        new(
            Guid.NewGuid(),
            kind,
            new RuntimeCommandArguments(DeliveryId: deliveryId));

    private static bool SameTarget(RuntimeTaskReference left, RuntimeTaskReference right) =>
        string.Equals(left.TaskId, right.TaskId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.HostId, right.HostId, StringComparison.OrdinalIgnoreCase);

    private static void ObserveFailure(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static InvalidOperationException CommandUncertain(
        RuntimeCommandRequest request,
        Exception executeFailure,
        Exception lookupFailure) =>
        new(
            $"The {request.Kind} operation {request.OperationId:D} lost its reply and could not be reconciled. Its side effect was not resubmitted.",
            new AggregateException(executeFailure, lookupFailure));

    private static RuntimeVoiceMessagingSnapshot EmptyMessaging() =>
        new(
            Enabled: false,
            BridgeAvailable: false,
            "Room Voice runtime unavailable.",
            [],
            SelectedTask: null,
            SelectedLabel: string.Empty,
            []);

    private static void Cancel(Connection? connection)
    {
        if (connection is null)
        {
            return;
        }
        try
        {
            connection.Cancellation.Cancel();
        }
        finally
        {
            connection.Cancellation.Dispose();
        }
    }

    private sealed class Connection(
        long generation,
        IRuntimeCommandRunner commands,
        IRuntimeVoiceTargetWriter targetWriter)
    {
        public long Generation { get; } = generation;

        public IRuntimeCommandRunner Commands { get; } = commands;

        public IRuntimeVoiceTargetWriter TargetWriter { get; } = targetWriter;

        public CancellationTokenSource Cancellation { get; } = new();

        public Task<RuntimeVoiceTargetSelectionResult>? TargetRecovery { get; set; }
    }

    private sealed record PendingVoiceTargetWrite(Guid OperationId, RuntimeTaskReference Target);
}
