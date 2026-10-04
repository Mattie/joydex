using System.Security.Cryptography;
using System.Text.Json;
using Joydex.Contracts;

namespace Joydex.RuntimeHost.Settings;

public sealed class RuntimeSettingsCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan PreparationLifetime = TimeSpan.FromMinutes(2);
    private static readonly SettingsAggregateId[] StartupActivationOrder =
    [
        SettingsAggregateId.TaskAlerts,
        SettingsAggregateId.Companion,
        SettingsAggregateId.Voice,
        SettingsAggregateId.PebbleIndex,
    ];

    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly string _selectionIdentity;
    private readonly SettingsDocumentStore _documents;
    private readonly SettingsRevisionJournalStore _journalStore;
    private readonly ISettingsImpactPlanner _impactPlanner;
    private readonly ISettingsActivator _activator;
    private readonly ISettingsTransactionObserver _transactionObserver;
    private readonly RuntimeEventHub _events;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationToken _runtimeCancellationToken;
    private readonly Dictionary<string, PreparedSettingsChange> _preparations = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, CompletedSettingsOperation> _completedOperations = [];
    private readonly Dictionary<SettingsAggregateId, SettingsAggregateState> _aggregateStates = [];
    private readonly Dictionary<SettingsAggregateId, SettingsFileFingerprint> _knownFingerprints = [];
    private readonly Dictionary<SettingsAggregateId, ExternalSettingsCandidate> _externalCandidates = [];
    private readonly HashSet<string> _connections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RuntimeEventSubscription> _subscriptions = new(StringComparer.Ordinal);
    private SettingsBundle _desired;
    private SettingsBundle _active;
    private long _revision;
    private long _lastCompletionSequence;
    private string? _terminalAuthorityFailure;
    private bool _disposed;

    private RuntimeSettingsCoordinator(
        string selectionIdentity,
        SettingsDocumentStore documents,
        SettingsRevisionJournalStore journalStore,
        ISettingsImpactPlanner impactPlanner,
        ISettingsActivator activator,
        ISettingsTransactionObserver transactionObserver,
        RuntimeEventHub events,
        TimeProvider timeProvider,
        CancellationToken runtimeCancellationToken,
        InitializedSettingsState initialized)
    {
        _selectionIdentity = selectionIdentity;
        _documents = documents;
        _journalStore = journalStore;
        _impactPlanner = impactPlanner;
        _activator = activator;
        _transactionObserver = transactionObserver;
        _events = events;
        _timeProvider = timeProvider;
        _runtimeCancellationToken = runtimeCancellationToken;
        _revision = initialized.Revision;
        _lastCompletionSequence = initialized.LastCompletionSequence;
        _desired = initialized.Desired;
        _active = initialized.Active;
        foreach (var state in initialized.Aggregates)
        {
            _aggregateStates.Add(state.Aggregate, state);
        }
        foreach (var fingerprint in initialized.Fingerprints)
        {
            _knownFingerprints.Add(fingerprint.Key, fingerprint.Value);
        }
        foreach (var operation in initialized.CompletedOperations)
        {
            _completedOperations[operation.OperationId] = operation;
        }
    }

    public Guid EngineEpoch => _events.EngineEpoch;

    public static RuntimeSettingsCoordinator Create(
        RuntimeSettingsPaths paths,
        ISettingsImpactPlanner? impactPlanner = null,
        ISettingsActivator? activator = null,
        TimeProvider? timeProvider = null,
        CancellationToken runtimeCancellationToken = default)
    {
        return Create(
            paths,
            impactPlanner ?? new DefaultSettingsImpactPlanner(),
            activator ?? new ImmediateSettingsActivator(),
            timeProvider ?? TimeProvider.System,
            runtimeCancellationToken,
            new SettingsDocumentIo(),
            SettingsTransactionObserver.Instance,
            new RuntimeEventHub(Guid.NewGuid()));
    }

    internal static RuntimeSettingsCoordinator Create(
        RuntimeSettingsPaths paths,
        ISettingsImpactPlanner impactPlanner,
        ISettingsActivator activator,
        TimeProvider timeProvider,
        CancellationToken runtimeCancellationToken,
        ISettingsDocumentIo io,
        ISettingsTransactionObserver transactionObserver,
        RuntimeEventHub events)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(impactPlanner);
        ArgumentNullException.ThrowIfNull(activator);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(io);
        ArgumentNullException.ThrowIfNull(transactionObserver);
        ArgumentNullException.ThrowIfNull(events);

        var normalizedPaths = paths.Normalize();
        var selectionIdentity = normalizedPaths.SelectionIdentity();
        var documents = new SettingsDocumentStore(normalizedPaths, io);
        var journalStore = new SettingsRevisionJournalStore(normalizedPaths.Journal, io);
        var initialized = Initialize(documents, journalStore, selectionIdentity);
        var coordinator = new RuntimeSettingsCoordinator(
            selectionIdentity,
            documents,
            journalStore,
            impactPlanner,
            activator,
            transactionObserver,
            events,
            timeProvider,
            runtimeCancellationToken,
            initialized);
        coordinator.CompleteInterruptedOperation(initialized.InterruptedCommit);
        coordinator.SaveJournal(pendingCommit: null);
        return coordinator;
    }

    internal async Task<SettingsAttachState> AttachAsync(
        string connectionId,
        Guid? previousEpoch,
        long? afterSequence,
        Action<RuntimeEvent> eventSink,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(eventSink);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!_connections.Add(connectionId))
            {
                throw new InvalidOperationException($"Connection '{connectionId}' is already attached.");
            }

            DetectExternalChangesLocked();
            RuntimeEventSubscription? subscription = null;
            try
            {
                subscription = _events.Subscribe(connectionId, previousEpoch, afterSequence, eventSink);
                _subscriptions.Add(connectionId, subscription);
                return new SettingsAttachState(
                    BuildSnapshotLocked(),
                    subscription.Cursor,
                    subscription.ResynchronizationRequired,
                    subscription.Replay,
                    subscription);
            }
            catch
            {
                subscription?.Dispose();
                _subscriptions.Remove(connectionId);
                _connections.Remove(connectionId);
                throw;
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    internal async Task DisconnectAsync(string connectionId)
    {
        await _mutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }
            _connections.Remove(connectionId);
            if (_subscriptions.Remove(connectionId, out var subscription))
            {
                subscription.Dispose();
            }
            foreach (var token in _preparations
                         .Where(item => string.Equals(item.Value.ConnectionId, connectionId, StringComparison.Ordinal)
                                        && item.Value.ConsumedBy is null)
                         .Select(item => item.Key)
                         .ToArray())
            {
                _preparations.Remove(token);
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    internal async Task<SettingsSnapshot> GetSnapshotAsync(
        string connectionId,
        CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfNotConnected(connectionId);
            DetectExternalChangesLocked();
            return BuildSnapshotLocked();
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    internal async Task<PrepareSettingsResult> PrepareAsync(
        string connectionId,
        PrepareSettingsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Patch);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfNotConnected(connectionId);
            RemoveExpiredPreparationsLocked();
            DetectExternalChangesLocked();
            if (_terminalAuthorityFailure is not null)
            {
                return PrepareFailure(SettingsPrepareStatus.Rejected, _terminalAuthorityFailure);
            }
            if (request.BaseRevision != _revision)
            {
                return PrepareFailure(
                    SettingsPrepareStatus.Conflict,
                    $"Settings revision {request.BaseRevision} is stale; the current revision is {_revision}.");
            }

            SettingsBundle candidate;
            try
            {
                candidate = SettingsCanonicalizer.Apply(_desired, request.Patch);
            }
            catch (Exception exception) when (exception is ArgumentException
                                                   or InvalidDataException
                                                   or NullReferenceException
                                                   or OverflowException)
            {
                return PrepareFailure(SettingsPrepareStatus.Rejected, exception.Message);
            }

            var changed = SettingsCanonicalizer.Changed(_desired, candidate);
            if (changed.Length == 0)
            {
                return new PrepareSettingsResult(
                    SettingsPrepareStatus.NoChanges,
                    null,
                    null,
                    null,
                    [],
                    [],
                    BuildSnapshotLocked());
            }

            var errors = SettingsCanonicalizer.Validate(candidate, changed);
            if (errors.Length > 0)
            {
                return new PrepareSettingsResult(
                    SettingsPrepareStatus.Rejected,
                    null,
                    null,
                    null,
                    [],
                    errors,
                    BuildSnapshotLocked());
            }

            var effects = _impactPlanner.Plan(_active, candidate, changed);
            if (effects.Select(effect => effect.Aggregate).Distinct().Count() != changed.Length
                || changed.Any(aggregate => effects.Count(effect => effect.Aggregate == aggregate) != 1))
            {
                return PrepareFailure(
                    SettingsPrepareStatus.Rejected,
                    "The runtime did not produce exactly one effect for every changed settings aggregate.");
            }

            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var payloadHash = SettingsCanonicalizer.HashToken(
                $"{_events.EngineEpoch:D}:{_revision}:{SettingsCanonicalizer.HashBundle(candidate)}:"
                + string.Join(',', changed.Order()));
            var expiresAt = _timeProvider.GetUtcNow() + PreparationLifetime;
            _preparations.Add(token, new PreparedSettingsChange(
                connectionId,
                _events.EngineEpoch,
                _revision,
                candidate,
                changed,
                effects,
                payloadHash,
                expiresAt));

            return new PrepareSettingsResult(
                SettingsPrepareStatus.Prepared,
                token,
                payloadHash,
                expiresAt,
                effects,
                [],
                BuildSnapshotLocked());
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    internal async Task<ApplySettingsResult> ApplyAsync(
        string connectionId,
        ApplySettingsRequest request,
        CancellationToken callerCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var operation = ApplyCoreAsync(connectionId, request);
        _ = operation.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return await operation.WaitAsync(callerCancellationToken).ConfigureAwait(false);
    }

    internal async Task<SettingsOperationResult> GetOperationAsync(
        string connectionId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfNotConnected(connectionId);
            DetectExternalChangesLocked();
            return _completedOperations.TryGetValue(operationId, out var operation)
                ? new SettingsOperationResult(
                    operationId,
                    SettingsOperationState.Completed,
                    operation.Materialize(BuildSnapshotLocked()))
                : new SettingsOperationResult(operationId, SettingsOperationState.NotFound);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    internal async Task<ExternalSettingsAdoptionResult> AdoptExternalAsync(
        string connectionId,
        IReadOnlyCollection<SettingsAggregateId>? requestedAggregates,
        CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfNotConnected(connectionId);
            DetectExternalChangesLocked();
            if (_terminalAuthorityFailure is not null)
            {
                return ExternalAdoptionFailure(_terminalAuthorityFailure);
            }
            var aggregates = (requestedAggregates ?? _externalCandidates.Keys.ToArray())
                .Distinct()
                .Order()
                .ToArray();
            if (aggregates.Length == 0)
            {
                return new ExternalSettingsAdoptionResult(
                    RuntimeCommandStatus.Completed,
                    "No external settings changes are pending.",
                    BuildSnapshotLocked());
            }

            foreach (var aggregate in aggregates)
            {
                if (!_externalCandidates.TryGetValue(aggregate, out var external))
                {
                    return RejectExternalAdoption(
                        $"No external {aggregate} settings change is pending.");
                }
                if (!external.IsValid)
                {
                    return RejectExternalAdoption(
                        $"The external {aggregate} settings are invalid: {external.Detail}");
                }
            }

            ExternalSettingsRead externalRead;
            try
            {
                externalRead = _documents.ReadExternal(_desired, aggregates);
            }
            catch (Exception exception) when (exception is IOException
                                                   or UnauthorizedAccessException
                                                   or ArgumentException
                                                   or InvalidDataException
                                                   or JsonException)
            {
                DetectExternalChangesLocked();
                if (_terminalAuthorityFailure is not null)
                {
                    return ExternalAdoptionFailure(_terminalAuthorityFailure);
                }
                return RejectExternalAdoption(
                    "The external settings could not be loaded: " + exception.Message);
            }

            if (aggregates.Any(aggregate =>
                    !externalRead.Fingerprints.TryGetValue(aggregate, out var current)
                    || !_externalCandidates.TryGetValue(aggregate, out var external)
                    || !string.Equals(
                        current.ContentHash,
                        external.ContentHash,
                        StringComparison.Ordinal)))
            {
                DetectExternalChangesLocked();
                return RejectExternalAdoption(
                    "An external settings file changed while it was being accepted; retry with the new candidate.");
            }

            var candidate = externalRead.Bundle;
            var changed = SettingsCanonicalizer.Changed(_desired, candidate)
                .Where(aggregates.Contains)
                .ToArray();
            var errors = SettingsCanonicalizer.Validate(candidate, changed);
            if (errors.Length > 0)
            {
                return RejectExternalAdoption(string.Join(" ", errors));
            }

            var effects = _impactPlanner.Plan(_active, candidate, changed);
            if (effects.Select(effect => effect.Aggregate).Distinct().Count() != changed.Length
                || changed.Any(aggregate => effects.Count(effect => effect.Aggregate == aggregate) != 1))
            {
                return RejectExternalAdoption(
                    "The runtime did not produce exactly one effect for every changed external aggregate.");
            }

            // Re-read immediately before the durable commit. The parsed candidate remains bound to
            // the exact bytes captured above, while this check detects replacement during parsing.
            var fingerprints = _documents.Fingerprints();
            if (!FingerprintsEqual(fingerprints, _knownFingerprints)
                || aggregates.Any(aggregate =>
                    !fingerprints.TryGetValue(aggregate, out var current)
                    || !externalRead.Fingerprints.TryGetValue(aggregate, out var captured)
                    || current != captured))
            {
                DetectExternalChangesLocked();
                return RejectExternalAdoption(
                    "An external settings file changed while it was being accepted; retry with the new candidate.");
            }

            var previousRevision = _revision;
            var previousDesired = _desired;
            var previousActive = _active;
            var previousStates = _aggregateStates.ToDictionary(item => item.Key, item => item.Value);
            var previousFingerprints = _knownFingerprints.ToDictionary(item => item.Key, item => item.Value);
            var previousExternalCandidates = _externalCandidates.ToDictionary(item => item.Key, item => item.Value);
            var targetRevision = checked(_revision + 1);
            _revision = targetRevision;
            _desired = candidate;
            foreach (var aggregate in aggregates)
            {
                _externalCandidates.Remove(aggregate);
            }
            foreach (var aggregate in changed)
            {
                _aggregateStates[aggregate] = _aggregateStates[aggregate] with
                {
                    DesiredRevision = targetRevision,
                    Activation = SettingsActivationState.PendingIdle,
                    Detail = "The external settings are durable and awaiting activation.",
                };
            }
            try
            {
                SaveJournal(pendingCommit: null);
            }
            catch (Exception exception) when (IsJournalPersistenceFailure(exception))
            {
                RestoreInMemoryState(
                    previousRevision,
                    previousDesired,
                    previousActive,
                    previousStates,
                    previousFingerprints,
                    previousExternalCandidates);
                var detail = LatchAuthorityFailureLocked(
                    "The external settings were not accepted because the revision journal could not be saved: "
                    + exception.Message);
                return ExternalAdoptionFailure(detail);
            }

            foreach (var aggregate in changed)
            {
                var effect = effects.Single(item => item.Aggregate == aggregate);
                if (effect.Kind is SettingsEffectKind.PendingIdle or SettingsEffectKind.RequiresExplicitStop)
                {
                    _aggregateStates[aggregate] = _aggregateStates[aggregate] with
                    {
                        Activation = SettingsActivationState.PendingIdle,
                        Detail = effect.Detail,
                    };
                    continue;
                }
                _ = await ActivateAggregateAsync(aggregate, targetRevision).ConfigureAwait(false);
            }

            try
            {
                SaveJournal(pendingCommit: null);
            }
            catch (Exception exception) when (IsJournalPersistenceFailure(exception))
            {
                var detail = LatchAuthorityFailureLocked(
                    "The external settings were accepted and runtime activation ran, but the resulting state "
                    + "could not be recorded. Settings mutations are disabled until the runtime restarts: "
                    + exception.Message);
                var failedSnapshot = BuildSnapshotLocked();
                _events.Publish(RuntimeEventKind.SettingsChanged, settings: failedSnapshot);
                return new ExternalSettingsAdoptionResult(
                    RuntimeCommandStatus.Failed,
                    detail,
                    failedSnapshot);
            }
            var snapshot = BuildSnapshotLocked();
            _events.Publish(RuntimeEventKind.SettingsChanged, settings: snapshot);
            return new ExternalSettingsAdoptionResult(RuntimeCommandStatus.Completed, null, snapshot);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Retries one durable desired aggregate at a host-controlled safe boundary. The activation
    /// candidate starts from authoritative active state so other pending aggregates stay pending.
    /// </summary>
    public async Task<SettingsActivationResult> ActivateDesiredAsync(
        SettingsAggregateId aggregate,
        CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_terminalAuthorityFailure is not null)
            {
                return new SettingsActivationResult(
                    SettingsActivationState.Failed,
                    _terminalAuthorityFailure);
            }
            if (!_aggregateStates.TryGetValue(aggregate, out var state))
            {
                throw new ArgumentOutOfRangeException(nameof(aggregate));
            }
            if (state.Activation == SettingsActivationState.Applied
                && state.ActiveRevision == state.DesiredRevision)
            {
                return new SettingsActivationResult(SettingsActivationState.Applied, state.Detail);
            }

            var activated = await ActivateAggregateAsync(
                aggregate,
                state.DesiredRevision).ConfigureAwait(false);
            try
            {
                SaveJournal(pendingCommit: null);
                _events.Publish(RuntimeEventKind.SettingsChanged, settings: BuildSnapshotLocked());
                return activated;
            }
            catch (Exception exception) when (IsJournalPersistenceFailure(exception))
            {
                var detail = LatchAuthorityFailureLocked(
                    "The runtime activation finished, but its state could not be recorded. Settings mutations "
                    + "are disabled until the runtime restarts: " + exception.Message);
                _events.Publish(RuntimeEventKind.SettingsChanged, settings: BuildSnapshotLocked());
                return activated with
                {
                    Detail = string.IsNullOrWhiteSpace(activated.Detail)
                        ? detail
                        : activated.Detail + " " + detail,
                };
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Rebuilds every runtime owner from durable desired state during fresh host startup. Recorded
    /// active revisions describe the previous engine generation and never prove that this process
    /// owns those resources. A prior PendingIdle state is therefore retried when no old call exists.
    /// </summary>
    internal async Task<SettingsSnapshot> ReconcileStartupAsync(CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            foreach (var aggregate in StartupActivationOrder)
            {
                var desiredRevision = _aggregateStates[aggregate].DesiredRevision;
                _ = await ActivateAggregateAsync(aggregate, desiredRevision).ConfigureAwait(false);
            }

            SaveJournal(pendingCommit: null);
            var snapshot = BuildSnapshotLocked();
            _events.Publish(RuntimeEventKind.SettingsChanged, settings: snapshot);
            return snapshot;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _mutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var subscription in _subscriptions.Values)
            {
                subscription.Dispose();
            }
            _subscriptions.Clear();
            _connections.Clear();
            _preparations.Clear();
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<ApplySettingsResult> ApplyCoreAsync(
        string connectionId,
        ApplySettingsRequest request)
    {
        await _mutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfNotConnected(connectionId);
            if (request.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(request.PreparationToken))
            {
                return ApplyFailure(
                    request.OperationId,
                    SettingsApplyStatus.Rejected,
                    "A non-empty operation ID and preparation token are required.");
            }

            var tokenHash = SettingsCanonicalizer.HashToken(request.PreparationToken);
            if (_completedOperations.TryGetValue(request.OperationId, out var completed))
            {
                if (!string.Equals(completed.TokenHash, tokenHash, StringComparison.Ordinal))
                {
                    return ApplyFailure(
                        request.OperationId,
                        SettingsApplyStatus.Rejected,
                        "The operation ID is already bound to a different prepared payload.");
                }
                DetectExternalChangesLocked();
                return completed.Materialize(BuildSnapshotLocked());
            }

            if (_completedOperations.Values.Any(operation =>
                    string.Equals(operation.TokenHash, tokenHash, StringComparison.Ordinal)))
            {
                return ApplyFailure(
                    request.OperationId,
                    SettingsApplyStatus.Rejected,
                    "The preparation token was already consumed by another operation.");
            }

            if (_terminalAuthorityFailure is not null)
            {
                return ApplyFailure(
                    request.OperationId,
                    SettingsApplyStatus.Rejected,
                    _terminalAuthorityFailure);
            }

            RemoveExpiredPreparationsLocked();
            if (!_preparations.TryGetValue(request.PreparationToken, out var prepared)
                || !string.Equals(prepared.ConnectionId, connectionId, StringComparison.Ordinal)
                || prepared.EngineEpoch != _events.EngineEpoch)
            {
                return ApplyFailure(
                    request.OperationId,
                    SettingsApplyStatus.Rejected,
                    "The preparation token is unavailable for this connection.");
            }
            if (prepared.ConsumedBy is { } consumedBy && consumedBy != request.OperationId)
            {
                return ApplyFailure(
                    request.OperationId,
                    SettingsApplyStatus.Rejected,
                    "The preparation token was already consumed by another operation.");
            }

            prepared = prepared with { ConsumedBy = request.OperationId };
            _preparations[request.PreparationToken] = prepared;
            DetectExternalChangesLocked();
            if (_terminalAuthorityFailure is not null)
            {
                return ApplyFailure(
                    request.OperationId,
                    SettingsApplyStatus.Rejected,
                    _terminalAuthorityFailure);
            }
            if (prepared.BaseRevision != _revision)
            {
                var conflict = ApplyForPrepared(
                    request.OperationId,
                    prepared,
                    SettingsApplyStatus.Conflict,
                    desiredCommitted: false,
                    SettingsPersistenceStatus.NotAttempted,
                    "The prepared base revision is stale; no settings were written.");
                return CompleteApplyLocked(
                    tokenHash,
                    prepared.PayloadHash,
                    conflict,
                    settingsChanged: false);
            }

            IReadOnlyDictionary<SettingsAggregateId, byte[]> targetDocuments;
            try
            {
                targetDocuments = _documents.Serialize(prepared.Candidate, prepared.ChangedAggregates);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                   or ArgumentException or InvalidDataException)
            {
                var failed = ApplyForPrepared(
                    request.OperationId,
                    prepared,
                    SettingsApplyStatus.FailedRolledBack,
                    desiredCommitted: false,
                    SettingsPersistenceStatus.RolledBack,
                    "The desired settings could not be staged: " + exception.Message);
                return CompleteApplyLocked(
                    tokenHash,
                    prepared.PayloadHash,
                    failed,
                    settingsChanged: false);
            }

            // Staging can take long enough for an out-of-band editor to race the first check.
            // Recheck immediately before recording commit intent or touching an authoritative file.
            DetectExternalChangesLocked();
            if (prepared.BaseRevision != _revision)
            {
                var conflict = ApplyForPrepared(
                    request.OperationId,
                    prepared,
                    SettingsApplyStatus.Conflict,
                    desiredCommitted: false,
                    SettingsPersistenceStatus.NotAttempted,
                    "A settings file changed after preparation; no settings were written.");
                return CompleteApplyLocked(
                    tokenHash,
                    prepared.PayloadHash,
                    conflict,
                    settingsChanged: false);
            }

            var targetRevision = checked(_revision + 1);
            var backups = prepared.ChangedAggregates.Select(aggregate =>
            {
                var previous = _documents.ReadExact(aggregate);
                var previousHash = previous is null
                    ? "MISSING"
                    : Convert.ToHexString(SHA256.HashData(previous));
                var targetHash = Convert.ToHexString(SHA256.HashData(targetDocuments[aggregate]));
                return new SettingsDocumentBackup(
                    aggregate,
                    previous is not null,
                    previous is null ? null : Convert.ToBase64String(previous),
                    previousHash,
                    targetHash);
            }).ToArray();
            if (backups.Any(backup =>
                    !_knownFingerprints.TryGetValue(backup.Aggregate, out var known)
                    || known.Exists != backup.Existed
                    || !string.Equals(known.ContentHash, backup.PreviousHash, StringComparison.Ordinal)))
            {
                DetectExternalChangesLocked();
                var conflict = ApplyForPrepared(
                    request.OperationId,
                    prepared,
                    SettingsApplyStatus.Conflict,
                    desiredCommitted: false,
                    SettingsPersistenceStatus.NotAttempted,
                    "A settings file changed while Apply captured its commit snapshot; no settings were written.");
                return CompleteApplyLocked(
                    tokenHash,
                    prepared.PayloadHash,
                    conflict,
                    settingsChanged: false);
            }
            var pending = new PendingSettingsCommit(
                SettingsCommitPhase.WritingDocuments,
                request.OperationId,
                tokenHash,
                prepared.PayloadHash,
                _revision,
                targetRevision,
                prepared.Candidate,
                prepared.ChangedAggregates,
                prepared.Effects,
                backups);

            var previousRevision = _revision;
            var previousDesired = _desired;
            var previousActive = _active;
            var previousStates = _aggregateStates.ToDictionary(item => item.Key, item => item.Value);
            var previousFingerprints = _knownFingerprints.ToDictionary(item => item.Key, item => item.Value);
            var previousExternalCandidates = _externalCandidates.ToDictionary(item => item.Key, item => item.Value);

            try
            {
                SaveJournal(pending);
                _transactionObserver.OnCheckpoint(SettingsTransactionCheckpoint.IntentPersisted);
                foreach (var aggregate in prepared.ChangedAggregates)
                {
                    _documents.WriteExact(aggregate, targetDocuments[aggregate]);
                    _transactionObserver.OnCheckpoint(SettingsTransactionCheckpoint.DocumentWritten, aggregate);
                }

                _revision = targetRevision;
                _desired = prepared.Candidate;
                foreach (var aggregate in prepared.ChangedAggregates)
                {
                    var prior = _aggregateStates[aggregate];
                    _aggregateStates[aggregate] = prior with
                    {
                        DesiredRevision = targetRevision,
                        Activation = SettingsActivationState.PendingIdle,
                        Detail = "The desired settings are durable and awaiting activation.",
                    };
                    _externalCandidates.Remove(aggregate);
                }
                ReplaceKnownFingerprints(_documents.Fingerprints());
                pending = pending with { Phase = SettingsCommitPhase.DesiredCommitted };
                SaveJournal(pending);
                _transactionObserver.OnCheckpoint(SettingsTransactionCheckpoint.DesiredCommitted);
            }
            catch (SettingsTransactionInterruptedException)
            {
                throw;
            }
            catch (Exception persistenceException) when (persistenceException is IOException
                                                              or UnauthorizedAccessException
                                                              or ArgumentException
                                                              or InvalidDataException)
            {
                try
                {
                    RestoreBackups(backups);
                    RestoreInMemoryState(
                        previousRevision,
                        previousDesired,
                        previousActive,
                        previousStates,
                        previousFingerprints,
                        previousExternalCandidates);
                }
                catch (Exception rollbackException)
                {
                    _ = LatchAuthorityFailureLocked(
                        "The settings save and exact-byte rollback failed. Settings mutations are disabled "
                        + "until the runtime restarts and recovers its journal: " + rollbackException.Message);
                    throw new AggregateException(
                        "The settings save failed and exact-byte rollback also failed; journal recovery is required.",
                        persistenceException,
                        rollbackException);
                }

                var failed = ApplyForPrepared(
                    request.OperationId,
                    prepared,
                    SettingsApplyStatus.FailedRolledBack,
                    desiredCommitted: false,
                    SettingsPersistenceStatus.RolledBack,
                    "The settings save failed and prior bytes were restored: " + persistenceException.Message);
                return CompleteApplyLocked(
                    tokenHash,
                    prepared.PayloadHash,
                    failed,
                    settingsChanged: false);
            }

            foreach (var aggregate in prepared.ChangedAggregates)
            {
                var effect = prepared.Effects.Single(item => item.Aggregate == aggregate);
                if (effect.Kind is SettingsEffectKind.PendingIdle or SettingsEffectKind.RequiresExplicitStop)
                {
                    _aggregateStates[aggregate] = _aggregateStates[aggregate] with
                    {
                        Activation = SettingsActivationState.PendingIdle,
                        Detail = effect.Detail,
                    };
                    continue;
                }

                _ = await ActivateAggregateAsync(aggregate, targetRevision).ConfigureAwait(false);
            }

            var result = BuildCommittedResult(request.OperationId, prepared);
            return CompleteApplyLocked(
                tokenHash,
                prepared.PayloadHash,
                result,
                settingsChanged: true);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private PrepareSettingsResult PrepareFailure(SettingsPrepareStatus status, string error) => new(
        status,
        null,
        null,
        null,
        [],
        [error],
        BuildSnapshotLocked());

    private ExternalSettingsAdoptionResult RejectExternalAdoption(string detail) => new(
        RuntimeCommandStatus.Rejected,
        detail,
        BuildSnapshotLocked());

    private ExternalSettingsAdoptionResult ExternalAdoptionFailure(string detail) => new(
        RuntimeCommandStatus.Failed,
        detail,
        BuildSnapshotLocked());

    private ApplySettingsResult ApplyFailure(Guid operationId, SettingsApplyStatus status, string error) => new(
        operationId,
        status,
        false,
        false,
        [],
        [error],
        BuildSnapshotLocked());

    private ApplySettingsResult ApplyForPrepared(
        Guid operationId,
        PreparedSettingsChange prepared,
        SettingsApplyStatus status,
        bool desiredCommitted,
        SettingsPersistenceStatus persistence,
        string detail)
    {
        var aggregates = prepared.ChangedAggregates.Select(aggregate =>
        {
            var state = _aggregateStates[aggregate];
            return new SettingsAggregateApplyResult(
                aggregate,
                persistence,
                state.Activation,
                state.DesiredRevision,
                state.ActiveRevision,
                detail);
        }).ToArray();
        return new ApplySettingsResult(
            operationId,
            status,
            desiredCommitted,
            false,
            aggregates,
            [detail],
            BuildSnapshotLocked());
    }

    private ApplySettingsResult BuildCommittedResult(Guid operationId, PreparedSettingsChange prepared)
    {
        var aggregates = prepared.ChangedAggregates.Select(aggregate =>
        {
            var state = _aggregateStates[aggregate];
            return new SettingsAggregateApplyResult(
                aggregate,
                SettingsPersistenceStatus.Committed,
                state.Activation,
                state.DesiredRevision,
                state.ActiveRevision,
                state.Detail);
        }).ToArray();
        var hasFailure = aggregates.Any(result => result.Activation == SettingsActivationState.Failed);
        var hasPending = aggregates.Any(result => result.Activation == SettingsActivationState.PendingIdle);
        var status = hasFailure
            ? SettingsApplyStatus.ActivationFailed
            : hasPending
                ? SettingsApplyStatus.PendingIdle
                : SettingsApplyStatus.Applied;
        var canClose = !hasFailure && aggregates.All(result =>
            result.Persistence == SettingsPersistenceStatus.Committed
            && result.Activation is SettingsActivationState.Applied or SettingsActivationState.PendingIdle);
        return new ApplySettingsResult(
            operationId,
            status,
            true,
            canClose,
            aggregates,
            hasFailure ? ["The desired settings were saved, but one or more aggregates failed to activate."] : [],
            BuildSnapshotLocked());
    }

    private void DetectExternalChangesLocked()
    {
        if (_terminalAuthorityFailure is not null)
        {
            return;
        }

        var inspected = Enum.GetValues<SettingsAggregateId>()
            .ToDictionary(aggregate => aggregate, _documents.InspectExternal);
        var changed = inspected
            .Where(item => !_knownFingerprints.TryGetValue(item.Key, out var known)
                           || known != item.Value.Fingerprint)
            .Select(item => item.Key)
            .ToArray();
        if (changed.Length == 0)
        {
            return;
        }

        var previousRevision = _revision;
        var previousDesired = _desired;
        var previousActive = _active;
        var previousStates = _aggregateStates.ToDictionary(item => item.Key, item => item.Value);
        var previousFingerprints = _knownFingerprints.ToDictionary(item => item.Key, item => item.Value);
        var previousExternalCandidates = _externalCandidates.ToDictionary(item => item.Key, item => item.Value);
        _revision = checked(_revision + 1);
        foreach (var aggregate in changed)
        {
            var inspection = inspected[aggregate];
            _externalCandidates[aggregate] = new ExternalSettingsCandidate(
                aggregate,
                inspection.Fingerprint.ContentHash,
                inspection.IsValid,
                inspection.IsValid
                    ? "An external file edit is pending explicit acceptance."
                    : inspection.Detail);
        }
        ReplaceKnownFingerprints(inspected.ToDictionary(item => item.Key, item => item.Value.Fingerprint));
        try
        {
            SaveJournal(pendingCommit: null);
        }
        catch (Exception exception) when (IsJournalPersistenceFailure(exception))
        {
            RestoreInMemoryState(
                previousRevision,
                previousDesired,
                previousActive,
                previousStates,
                previousFingerprints,
                previousExternalCandidates);
            _ = LatchAuthorityFailureLocked(
                "External settings changes could not be recorded in the revision journal. "
                + "Settings mutations are disabled until the runtime restarts: "
                + exception.Message);
            return;
        }
        _events.Publish(RuntimeEventKind.SettingsChanged, settings: BuildSnapshotLocked());
    }

    private string LatchAuthorityFailureLocked(string detail)
    {
        _terminalAuthorityFailure ??= detail;
        _preparations.Clear();
        return _terminalAuthorityFailure;
    }

    private static bool IsJournalPersistenceFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidDataException;

    private SettingsSnapshot BuildSnapshotLocked() => new(
        _revision,
        SettingsCanonicalizer.Clone(_desired),
        SettingsCanonicalizer.Clone(_active),
        _aggregateStates.Values.OrderBy(state => state.Aggregate).ToArray(),
        _externalCandidates.Values.OrderBy(candidate => candidate.Aggregate).ToArray());

    private void PublishOperationLocked(ApplySettingsResult result, bool settingsChanged)
    {
        if (settingsChanged)
        {
            _events.Publish(RuntimeEventKind.SettingsChanged, settings: result.Snapshot);
        }
        _events.Publish(RuntimeEventKind.OperationCompleted, operation: result);
    }

    private ApplySettingsResult CompleteApplyLocked(
        string tokenHash,
        string payloadHash,
        ApplySettingsResult result,
        bool settingsChanged)
    {
        try
        {
            RecordCompletedLocked(tokenHash, payloadHash, result, pendingCommit: null);
            PublishOperationLocked(result, settingsChanged);
            return result;
        }
        catch (Exception exception) when (IsJournalPersistenceFailure(exception))
        {
            var detail = LatchAuthorityFailureLocked(
                "The settings operation finished, but its final authority state could not be recorded. "
                + "Settings mutations are disabled until the runtime restarts: "
                + exception.Message);
            var failed = result with
            {
                Status = SettingsApplyStatus.AuthorityStateUnrecorded,
                CanCloseSettings = false,
                Errors = [.. result.Errors, detail],
                Snapshot = BuildSnapshotLocked(),
            };
            var completionSequence = _completedOperations[failed.OperationId].CompletionSequence;
            _completedOperations[failed.OperationId] = CompletedSettingsOperation.From(
                tokenHash,
                payloadHash,
                failed,
                completionSequence);
            TrimCompletedOperationsLocked();
            PublishOperationLocked(failed, settingsChanged);
            return failed;
        }
    }

    private void RecordCompletedLocked(
        string tokenHash,
        string payloadHash,
        ApplySettingsResult result,
        PendingSettingsCommit? pendingCommit)
    {
        _completedOperations[result.OperationId] = CompletedSettingsOperation.From(
            tokenHash,
            payloadHash,
            result,
            NextCompletionSequenceLocked());
        TrimCompletedOperationsLocked();
        SaveJournal(pendingCommit);
    }

    private void SaveJournal(PendingSettingsCommit? pendingCommit)
    {
        _journalStore.Save(new SettingsRevisionJournal(
            SettingsRevisionJournal.CurrentSchemaVersion,
            _selectionIdentity,
            _revision,
            _desired,
            _active,
            _aggregateStates.Values.OrderBy(state => state.Aggregate).ToArray(),
            _knownFingerprints.ToDictionary(
                item => item.Key,
                item => JournalFileFingerprint.From(item.Value)),
            pendingCommit,
            _completedOperations.Values
                .OrderByDescending(item => item.CompletionSequence)
                .Take(SettingsRevisionJournal.MaximumCompletedOperations)
                .ToArray(),
            _lastCompletionSequence));
    }

    private void RestoreBackups(IEnumerable<SettingsDocumentBackup> backups)
    {
        foreach (var backup in backups)
        {
            _documents.RestoreExact(
                backup.Aggregate,
                backup.Existed
                    ? Convert.FromBase64String(backup.PreviousContentBase64
                        ?? throw new InvalidDataException("A settings journal backup is incomplete."))
                    : null);
        }
    }

    private void ReplaceKnownFingerprints(
        IReadOnlyDictionary<SettingsAggregateId, SettingsFileFingerprint> fingerprints)
    {
        _knownFingerprints.Clear();
        foreach (var item in fingerprints)
        {
            _knownFingerprints.Add(item.Key, item.Value);
        }
    }

    private async Task<SettingsActivationResult> ActivateAggregateAsync(
        SettingsAggregateId aggregate,
        long desiredRevision)
    {
        SettingsActivationResult activated;
        try
        {
            var activationCandidate = SettingsCanonicalizer.ReplaceAggregate(_active, _desired, aggregate);
            activated = await _activator.ActivateAsync(
                aggregate,
                activationCandidate,
                desiredRevision,
                _runtimeCancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                           || !_runtimeCancellationToken.IsCancellationRequested)
        {
            activated = new SettingsActivationResult(
                SettingsActivationState.Failed,
                exception.Message);
        }

        if (activated.State == SettingsActivationState.Applied)
        {
            _active = SettingsCanonicalizer.ReplaceAggregate(_active, _desired, aggregate);
            _aggregateStates[aggregate] = _aggregateStates[aggregate] with
            {
                ActiveRevision = desiredRevision,
                Activation = SettingsActivationState.Applied,
                Detail = activated.Detail,
            };
        }
        else
        {
            _aggregateStates[aggregate] = _aggregateStates[aggregate] with
            {
                Activation = activated.State,
                Detail = activated.Detail,
            };
        }

        return activated;
    }

    private void RestoreInMemoryState(
        long revision,
        SettingsBundle desired,
        SettingsBundle active,
        IReadOnlyDictionary<SettingsAggregateId, SettingsAggregateState> aggregateStates,
        IReadOnlyDictionary<SettingsAggregateId, SettingsFileFingerprint> fingerprints,
        IReadOnlyDictionary<SettingsAggregateId, ExternalSettingsCandidate> externalCandidates)
    {
        _revision = revision;
        _desired = desired;
        _active = active;
        _aggregateStates.Clear();
        foreach (var item in aggregateStates)
        {
            _aggregateStates.Add(item.Key, item.Value);
        }
        ReplaceKnownFingerprints(fingerprints);
        _externalCandidates.Clear();
        foreach (var item in externalCandidates)
        {
            _externalCandidates.Add(item.Key, item.Value);
        }
    }

    private void RemoveExpiredPreparationsLocked()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var token in _preparations
                     .Where(item => item.Value.ExpiresAt <= now && item.Value.ConsumedBy is null)
                     .Select(item => item.Key)
                     .ToArray())
        {
            _preparations.Remove(token);
        }
    }

    private void TrimCompletedOperationsLocked()
    {
        if (_completedOperations.Count <= SettingsRevisionJournal.MaximumCompletedOperations)
        {
            return;
        }
        foreach (var operationId in _completedOperations.Values
                     .OrderBy(item => item.CompletionSequence)
                     .Take(_completedOperations.Count - SettingsRevisionJournal.MaximumCompletedOperations)
                     .Select(item => item.OperationId)
                     .ToArray())
        {
            _completedOperations.Remove(operationId);
        }
    }

    private long NextCompletionSequenceLocked() => checked(++_lastCompletionSequence);

    private void ThrowIfNotConnected(string connectionId)
    {
        ThrowIfDisposed();
        if (!_connections.Contains(connectionId))
        {
            throw new InvalidOperationException("The settings client is not attached.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static InitializedSettingsState Initialize(
        SettingsDocumentStore documents,
        SettingsRevisionJournalStore journalStore,
        string selectionIdentity)
    {
        var journal = journalStore.Load();
        var selectionMatches = journal is not null
            && string.Equals(
                journal.SelectionIdentity,
                selectionIdentity,
                StringComparison.Ordinal);
        if (journal?.PendingCommit is not null && !selectionMatches)
        {
            throw new InvalidDataException(
                "The settings revision journal contains an interrupted commit for another "
                + "companion configuration selection. Select that exact configuration to recover it.");
        }

        PendingSettingsCommit? interrupted = null;
        if (selectionMatches
            && journal?.PendingCommit is { Phase: SettingsCommitPhase.WritingDocuments } pendingWriting)
        {
            RecoverWritingDocuments(documents, pendingWriting);
            interrupted = pendingWriting;
            journal = journal with { PendingCommit = null };
        }

        var loaded = documents.LoadOrCreate();
        var fingerprints = documents.Fingerprints();
        if (journal is null)
        {
            return NewState(1, loaded, fingerprints, [], lastCompletionSequence: 0, interrupted);
        }

        if (!selectionMatches)
        {
            return NewState(
                checked(journal.CurrentRevision + 1),
                loaded,
                fingerprints,
                [],
                lastCompletionSequence: 0,
                interrupted: null);
        }

        var journalFingerprints = journal.Fingerprints.ToDictionary(
            item => item.Key,
            item => item.Value.ToRuntime());
        var filesMatch = FingerprintsEqual(fingerprints, journalFingerprints);
        if (journal.PendingCommit is { Phase: SettingsCommitPhase.DesiredCommitted } desiredCommitted
            && filesMatch)
        {
            return new InitializedSettingsState(
                journal.CurrentRevision,
                loaded,
                SettingsCanonicalizer.Normalize(journal.Active),
                journal.Aggregates,
                fingerprints,
                journal.CompletedOperations,
                journal.LastCompletionSequence,
                desiredCommitted);
        }

        if (filesMatch
            && string.Equals(
                SettingsCanonicalizer.HashBundle(loaded),
                SettingsCanonicalizer.HashBundle(journal.Desired),
                StringComparison.Ordinal))
        {
            return new InitializedSettingsState(
                journal.CurrentRevision,
                loaded,
                SettingsCanonicalizer.Normalize(journal.Active),
                journal.Aggregates,
                fingerprints,
                journal.CompletedOperations,
                journal.LastCompletionSequence,
                interrupted);
        }

        // A valid edit made while the host was stopped becomes the startup configuration.
        return NewState(
            checked(journal.CurrentRevision + 1),
            loaded,
            fingerprints,
            journal.CompletedOperations,
            journal.LastCompletionSequence,
            journal.PendingCommit ?? interrupted);
    }

    private static InitializedSettingsState NewState(
        long revision,
        SettingsBundle loaded,
        IReadOnlyDictionary<SettingsAggregateId, SettingsFileFingerprint> fingerprints,
        IReadOnlyList<CompletedSettingsOperation> operations,
        long lastCompletionSequence,
        PendingSettingsCommit? interrupted) => new(
        revision,
        loaded,
        loaded,
        Enum.GetValues<SettingsAggregateId>().Select(aggregate => new SettingsAggregateState(
            aggregate,
            revision,
            revision,
            SettingsActivationState.Applied)).ToArray(),
        fingerprints,
        operations,
        lastCompletionSequence,
        interrupted);

    private static void RecoverWritingDocuments(
        SettingsDocumentStore documents,
        PendingSettingsCommit pending)
    {
        foreach (var backup in pending.Documents)
        {
            var current = documents.ReadExact(backup.Aggregate);
            var currentHash = current is null
                ? "MISSING"
                : Convert.ToHexString(SHA256.HashData(current));
            if (!string.Equals(currentHash, backup.PreviousHash, StringComparison.Ordinal)
                && !string.Equals(currentHash, backup.TargetHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Cannot recover {backup.Aggregate} settings because the file changed after an interrupted commit.");
            }
        }

        foreach (var backup in pending.Documents)
        {
            documents.RestoreExact(
                backup.Aggregate,
                backup.Existed
                    ? Convert.FromBase64String(backup.PreviousContentBase64
                        ?? throw new InvalidDataException("A settings journal backup is incomplete."))
                    : null);
        }
    }

    private void CompleteInterruptedOperation(PendingSettingsCommit? interrupted)
    {
        if (interrupted is null || _completedOperations.ContainsKey(interrupted.OperationId))
        {
            return;
        }

        ApplySettingsResult result;
        if (interrupted.Phase == SettingsCommitPhase.DesiredCommitted)
        {
            var aggregates = interrupted.ChangedAggregates.Select(aggregate =>
            {
                var state = _aggregateStates[aggregate];
                return new SettingsAggregateApplyResult(
                    aggregate,
                    SettingsPersistenceStatus.Committed,
                    SettingsActivationState.PendingIdle,
                    state.DesiredRevision,
                    state.ActiveRevision,
                    "The desired settings survived host restart and await activation recovery.");
            }).ToArray();
            result = new ApplySettingsResult(
                interrupted.OperationId,
                SettingsApplyStatus.PendingIdle,
                true,
                true,
                aggregates,
                [],
                BuildSnapshotLocked());
        }
        else
        {
            var aggregates = interrupted.ChangedAggregates.Select(aggregate =>
            {
                var state = _aggregateStates[aggregate];
                return new SettingsAggregateApplyResult(
                    aggregate,
                    SettingsPersistenceStatus.RolledBack,
                    state.Activation,
                    state.DesiredRevision,
                    state.ActiveRevision,
                    "An interrupted commit was restored from exact prior bytes.");
            }).ToArray();
            result = new ApplySettingsResult(
                interrupted.OperationId,
                SettingsApplyStatus.FailedRolledBack,
                false,
                false,
                aggregates,
                ["An interrupted settings commit was restored from exact prior bytes."],
                BuildSnapshotLocked());
        }

        _completedOperations[result.OperationId] = CompletedSettingsOperation.From(
            interrupted.TokenHash,
            interrupted.PayloadHash,
            result,
            NextCompletionSequenceLocked());
        TrimCompletedOperationsLocked();
    }

    private static bool FingerprintsEqual(
        IReadOnlyDictionary<SettingsAggregateId, SettingsFileFingerprint> left,
        IReadOnlyDictionary<SettingsAggregateId, SettingsFileFingerprint> right) =>
        Enum.GetValues<SettingsAggregateId>().All(aggregate =>
            left.TryGetValue(aggregate, out var leftValue)
            && right.TryGetValue(aggregate, out var rightValue)
            && leftValue == rightValue);

    private sealed record PreparedSettingsChange(
        string ConnectionId,
        Guid EngineEpoch,
        long BaseRevision,
        SettingsBundle Candidate,
        SettingsAggregateId[] ChangedAggregates,
        SettingsEffect[] Effects,
        string PayloadHash,
        DateTimeOffset ExpiresAt,
        Guid? ConsumedBy = null);

    private sealed record InitializedSettingsState(
        long Revision,
        SettingsBundle Desired,
        SettingsBundle Active,
        SettingsAggregateState[] Aggregates,
        IReadOnlyDictionary<SettingsAggregateId, SettingsFileFingerprint> Fingerprints,
        IReadOnlyList<CompletedSettingsOperation> CompletedOperations,
        long LastCompletionSequence,
        PendingSettingsCommit? InterruptedCommit);
}

internal sealed record SettingsAttachState(
    SettingsSnapshot Snapshot,
    long EventCursor,
    bool ResynchronizationRequired,
    RuntimeEvent[] Replay,
    IDisposable Subscription);

internal sealed record ExternalSettingsAdoptionResult(
    RuntimeCommandStatus Status,
    string? Detail,
    SettingsSnapshot Snapshot);
