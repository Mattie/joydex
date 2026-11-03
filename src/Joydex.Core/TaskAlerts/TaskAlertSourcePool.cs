namespace Joydex.Core.TaskAlerts;

public sealed partial class TaskAlertPool
{
    private const int MaximumSourceAcknowledgements = 2048;
    private readonly List<TaskAlertSourceAcknowledgement> _sourceAcknowledgements = [];
    private readonly Dictionary<TaskAlertSourceIdentity, (long Generation, long Sequence, DateTimeOffset CapturedAt, bool Complete, DateTimeOffset? CatalogObservedAt)> _sourceCursors = [];

    /// <summary>Applies positive native observations directly, without synthesizing Codex hooks.</summary>
    public bool ApplySourceSnapshot(TaskAlertSourceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Enabled || snapshot.Source is null || snapshot.Tasks is null || snapshot.Generation < 0
            || snapshot.Sequence < 0 || snapshot.CapturedAt == default
            || snapshot.CatalogObservedAt is { } catalogAt && (catalogAt <= DateTimeOffset.UnixEpoch || catalogAt > snapshot.CapturedAt)
            || !Enum.IsDefined(snapshot.Source.Backend) || !TaskAlertIdentity.IsNativeId(snapshot.Source.EnvironmentId)) return false;
        if (_sourceCursors.TryGetValue(snapshot.Source, out var cursor)
            && (snapshot.Generation < cursor.Generation
                || snapshot.Generation == cursor.Generation && snapshot.Sequence < cursor.Sequence
                || snapshot.CapturedAt < cursor.CapturedAt
                || snapshot.Generation == cursor.Generation && snapshot.Sequence == cursor.Sequence
                    && snapshot.CapturedAt == cursor.CapturedAt)) return false;
        var complete = snapshot.IsComplete && snapshot.Tasks.All(task => task is { ReadComplete: true });
        // Detail reads can advance the aggregate sequence beyond the shell's.
        // Only ordered shell boundaries establish a completion recovery interval.
        DateTimeOffset? terminalBaseline = cursor.Complete && complete && cursor.Generation == snapshot.Generation
            && snapshot.CatalogObservedAt > cursor.CatalogObservedAt
                ? cursor.CatalogObservedAt : null;
        var catalogBoundary = snapshot.CatalogObservedAt < cursor.CatalogObservedAt ? null : snapshot.CatalogObservedAt;
        _sourceCursors[snapshot.Source] = (snapshot.Generation, snapshot.Sequence, snapshot.CapturedAt, complete, catalogBoundary);
        // A partial catalog cannot certify retained tasks that were not read.
        var changed = snapshot.IsComplete && SetSourceUnavailable(snapshot.Source, false);
        foreach (var observation in snapshot.Tasks)
            if (observation is not null) changed |= ApplySourceObservation(snapshot, observation, terminalBaseline);
        return changed;
    }

    /// <summary>Marks source health without changing lifecycle timestamps or renewing leases.</summary>
    public bool SetSourceUnavailable(TaskAlertSourceIdentity source, bool unavailable = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (unavailable && _sourceCursors.TryGetValue(source, out var cursor))
            _sourceCursors[source] = cursor with { Complete = false };
        var changed = false;
        foreach (var assignment in _assignments.Values.ToArray())
            if (assignment.TaskIdentity.Source == source && assignment.SourceUnavailable != unavailable)
            {
                _assignments[assignment.Slot] = assignment with { SourceUnavailable = unavailable };
                changed = true;
            }
        return changed;
    }

    private bool ApplySourceObservation(TaskAlertSourceSnapshot snapshot, TaskAlertSourceObservation observation, DateTimeOffset? terminalBaseline)
    {
        if (!TaskAlertIdentity.IsNativeId(observation.NativeThreadId)
            || observation.RunId is { } run && !TaskAlertIdentity.IsNativeId(run)
            || observation.State is { } state && !Enum.IsDefined(state)
            || observation.StartedAt == default(DateTimeOffset)
            || observation.CompletedAt == default(DateTimeOffset)
            || observation.LatestHumanUserTurnAt == default(DateTimeOffset)
            || observation.StartedAt > snapshot.CapturedAt.AddSeconds(5)
            || observation.CompletedAt > snapshot.CapturedAt.AddSeconds(5)
            || observation.LatestHumanUserTurnAt > snapshot.CapturedAt.AddSeconds(5)) return false;
        var identity = new TaskAlertIdentity(snapshot.Source.Backend, snapshot.Source.EnvironmentId, observation.NativeThreadId);
        string key;
        try { key = identity.PoolKey; }
        catch (InvalidDataException) { return false; }
        var existing = _assignments.Values.FirstOrDefault(item => item.SessionId == key);
        var active = observation.ReadComplete && observation.State == TaskAlertState.Running
            && observation.StartedAt is not null && observation.RunId is not null;
        var requests = (observation.BlockingRequests ?? []).Concat(observation.AsyncQuestions ?? []).ToArray();
        if (requests.Length > 128 || observation.ResolvedQuestions?.Length > 128
            || requests.Any(request => !ValidSourceRequest(request, snapshot.CapturedAt))
            || (observation.ResolvedQuestions ?? []).Any(request => !ValidSourceRequest(request, snapshot.CapturedAt))) return false;
        var attention = (observation.BlockingRequests ?? []).Length > 0
            || (observation.AsyncQuestions ?? []).Any(request => !IsSourceAcknowledged(identity, request.RunId, request.Key)
            && (observation.LatestHumanUserTurnAt is null || request.CreatedAt is null
                || request.CreatedAt >= observation.LatestHumanUserTurnAt));
        if (IsSourceAcknowledged(identity, observation.RunId, null)) return false;
        if (existing is null)
        {
            var newlyFinished = snapshot.Source.Backend == TaskAlertBackend.RemoteT3 && terminalBaseline is { } baseline
                && observation.ReadComplete && observation.RunId is not null && observation.StartedAt >= baseline
                && observation.CompletedAt >= observation.StartedAt && observation.CompletedAt is not null
                && observation.State is TaskAlertState.Completed or TaskAlertState.Fault;
            if (!active && !attention && !newlyFinished) return false;
            var slot = TaskAlertSlots.All.FirstOrDefault(candidate => !_assignments.ContainsKey(candidate)
                && !_backfillAfter.ContainsKey(candidate));
            if (slot == 0) { DroppedEventCount++; return false; }
            existing = new(slot, key, observation.RunId, TaskAlertState.Running,
                observation.StartedAt ?? snapshot.CapturedAt, Identity: identity);
        }
        var sameRun = existing.TurnId == observation.RunId;
        var establishesRunBoundary = observation.ReadComplete && observation.RunId is not null && observation.StartedAt is not null;
        // Attention can allocate a slot before a read establishes its native run. Its
        // UpdatedAt is then receipt time, not evidence that the eventual run started later.
        if (!sameRun && (!observation.ReadComplete || observation.RunId is null || observation.StartedAt is null
            || existing.TurnBoundaryAt is { } knownBoundary && observation.StartedAt < knownBoundary)) return false;
        // A delayed active observation cannot undo accepted completion of this run.
        if (active && sameRun && (existing.CompleteAfter is not null
            || existing.State is TaskAlertState.Completed or TaskAlertState.Fault)) active = false;

        var pending = GetPendingAttention(key);
        var latestHumanTurn = existing.LatestHumanUserTurnAt;
        if (observation.ReadComplete && observation.LatestHumanUserTurnAt is { } humanAt
            && (latestHumanTurn is null || humanAt > latestHumanTurn))
        {
            foreach (var question in pending.CaptureQuestionOrigins())
                if (question.RequestedAt is null || question.RequestedAt < humanAt)
                    RememberSourceAcknowledgement(existing, question.Key, question.TurnId, snapshot.CapturedAt);
            pending.ClearQuestionsBefore(humanAt, observation.RunId);
            latestHumanTurn = humanAt;
        }
        if (observation.ReadComplete && observation.RequestsComplete) pending.ClearBlocking();
        foreach (var request in observation.BlockingRequests ?? [])
            if (!IsSourceAcknowledged(identity, request.RunId, request.Key)) pending.AddOnce(request.Key);
        foreach (var request in observation.AsyncQuestions ?? [])
        {
            if (IsSourceAcknowledged(identity, request.RunId, request.Key)) continue;
            if (latestHumanTurn is { } newer && request.CreatedAt < newer)
            {
                RememberSourceAcknowledgement(existing, request.Key, request.RunId, snapshot.CapturedAt);
                continue;
            }
            pending.AddAsyncQuestion(request.Key, request.RunId, request.CreatedAt ?? snapshot.CapturedAt);
        }
        if (observation.ReadComplete)
            foreach (var request in observation.ResolvedQuestions ?? [])
                if (pending.CaptureQuestionOrigins().Any(question => question.Key == request.Key && question.TurnId == request.RunId))
                {
                    pending.CompleteAsyncQuestion(request.Key);
                    RememberSourceAcknowledgement(existing, request.Key, request.RunId, snapshot.CapturedAt);
                }

        var terminal = observation.ReadComplete && observation.RunId is not null && observation.StartedAt is { } start
            && observation.CompletedAt is { } end && end >= start
            && observation.State is TaskAlertState.Completed or TaskAlertState.Fault;
        // A verified new run owns its lifecycle even if first observed waiting
        // for permission. The preceding run's completion timer cannot end it.
        var nextState = sameRun ? existing.State : TaskAlertState.Running;
        var completeAfter = sameRun ? existing.CompleteAfter : null;
        var updatedAt = sameRun ? existing.UpdatedAt : observation.StartedAt!.Value;
        if (active)
        {
            nextState = TaskAlertState.Running;
            completeAfter = null;
            if (!sameRun) updatedAt = observation.StartedAt!.Value;
        }
        if (terminal)
        {
            if (observation.State == TaskAlertState.Fault || ClearQuestionsOnTurnEnd)
            {
                foreach (var question in pending.CaptureQuestionOrigins())
                    RememberSourceAcknowledgement(existing, question.Key, question.TurnId, snapshot.CapturedAt);
                pending = new PendingAttention();
                _pendingAttention[key] = pending;
                RememberSourceAcknowledgement(existing, null, observation.RunId, snapshot.CapturedAt);
            }
            else pending.ClearBlocking();
            nextState = observation.State!.Value;
            updatedAt = observation.CompletedAt!.Value > updatedAt ? observation.CompletedAt.Value : updatedAt;
            completeAfter = pending.HasAsyncQuestions ? observation.CompletedAt : null;
        }
        if (pending.Any) nextState = TaskAlertState.Approval;
        else
        {
            _pendingAttention.Remove(key);
            if (nextState == TaskAlertState.Approval)
                nextState = completeAfter is not null ? TaskAlertState.Completed : TaskAlertState.Running;
            if (nextState == TaskAlertState.Completed) completeAfter = null;
        }
        // An exact pending request is fresh positive attention evidence even when the
        // provider is waiting or its run has ended. Retained absence and partial reads
        // do not renew the existing lease, and acknowledged requests cannot renew it.
        var observedAttention = observation.ReadComplete && observation.RequestsComplete && pending.Any
            && ((observation.BlockingRequests ?? []).Any(request => pending.CaptureCorrelated().Any(item => item.Key == request.Key))
                || (observation.AsyncQuestions ?? []).Any(request => pending.CaptureQuestionOrigins().Any(item => item.Key == request.Key && item.TurnId == request.RunId)));
        var updated = existing with
        {
            Identity = identity, TurnId = observation.RunId ?? existing.TurnId, State = nextState,
            UpdatedAt = updatedAt, CompleteAfter = completeAfter,
            ObservedActiveAt = active || observedAttention ? snapshot.CapturedAt : terminal ? null : existing.ObservedActiveAt,
            TurnBoundaryAt = establishesRunBoundary
                ? !sameRun ? observation.StartedAt : existing.TurnBoundaryAt ?? observation.StartedAt
                : existing.TurnBoundaryAt,
            LatestHumanUserTurnAt = latestHumanTurn, Metadata = observation.Metadata ?? existing.Metadata,
            SourceUnavailable = !observation.ReadComplete,
        };
        _assignments[existing.Slot] = updated;
        return true;
    }

    private static bool ValidSourceRequest(TaskAlertSourceRequest? request, DateTimeOffset capturedAt) =>
        request is not null && PendingAttention.IsSha256Hash(request.Key)
        && (request.RunId is null || TaskAlertIdentity.IsNativeId(request.RunId))
        && request.CreatedAt != default(DateTimeOffset) && !(request.CreatedAt > capturedAt.AddSeconds(5));

    private bool IsSourceAcknowledged(TaskAlertIdentity identity, string? runId, string? key) =>
        _sourceAcknowledgements.Any(item => item.Identity == identity && item.RunId == runId && item.Key == key);

    private void RememberSourceAcknowledgement(TaskAlertAssignment assignment, string? key, string? runId, DateTimeOffset at)
    {
        if (assignment.TaskIdentity.Backend != TaskAlertBackend.RemoteT3
            || IsSourceAcknowledged(assignment.TaskIdentity, runId, key)) return;
        _sourceAcknowledgements.Add(new(assignment.TaskIdentity, runId, key, at));
        if (_sourceAcknowledgements.Count > MaximumSourceAcknowledgements) _sourceAcknowledgements.RemoveAt(0);
    }

    private static TaskAlertSourceAcknowledgement[] ValidateSourceAcknowledgements(TaskAlertSourceAcknowledgement[]? values)
    {
        if (values is null) return [];
        if (values.Length > MaximumSourceAcknowledgements) throw new InvalidDataException("Too many source acknowledgements.");
        var unique = new HashSet<(TaskAlertIdentity, string?, string?)>();
        foreach (var value in values)
        {
            if (value?.Identity is null || value.AcknowledgedAt == default
                || value.Key is not null && !PendingAttention.IsSha256Hash(value.Key)
                || value.RunId is not null && !TaskAlertIdentity.IsNativeId(value.RunId)
                || !unique.Add((value.Identity, value.RunId, value.Key)))
                throw new InvalidDataException("Invalid source acknowledgement.");
            value.Identity.Validate();
        }
        return values;
    }
}
