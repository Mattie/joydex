using System.Threading.Channels;
using System.Text.Json;
using Joydex.Core.TaskAlerts;
using Joydex.Core.T3;

namespace Joydex.Windows.TaskAlerts;

public sealed record TaskAlertSnapshot(
    bool Enabled,
    IReadOnlyList<TaskAlertAssignment> Assignments,
    long DroppedEventCount,
    int Bank = 2,
    bool BankAutomaticallyDetected = false,
    IReadOnlyList<TaskAlertEventTrace>? RecentEvents = null,
    IReadOnlyList<TaskAlertSuppressionRule>? Suppressions = null,
    TaskAlertLedOptions? LedOutput = null,
    bool ClearQuestionsOnTurnEnd = false,
    T3ReadResult? T3Status = null,
    IReadOnlyList<T3ReadResult>? T3Statuses = null)
{
    public TaskAlertLedOptions EffectiveLedOutput =>
        (LedOutput ?? TaskAlertLedOptions.CreateDefault()).Normalize();
}

public enum TaskAlertEventResult
{
    Assigned,
    Updated,
    StopGrace,
    Dropped,
    Ignored,
    Suppressed,
    Deferred,
}

public sealed record TaskAlertEventTrace(
    DateTimeOffset ReceivedAt,
    CodexLifecycleEvent Event,
    string SessionId,
    string? TurnId,
    int? Slot,
    TaskAlertState? State,
    TaskAlertEventResult Result,
    string? Workspace = null);

/// <summary>Captures the event watermark and tracked tasks before an asynchronous Desktop read.</summary>
public sealed record TaskAlertReconciliationCheckpoint(long Revision, long AuthorityVersion, string[] KnownTaskIds);

public sealed class TaskAlertCoordinator : IAsyncDisposable
{
    private const int MaximumRecentEvents = 100;
    private readonly object _sync = new();
    private readonly string _preferencesPath;
    private readonly string _statePath;
    private readonly Action<string> _log;
    private readonly TaskAlertPool _pool;
    private TaskAlertMetadataStore? _metadata;
    private T3ReadResult? _t3Status;
    private readonly Dictionary<string, T3ReadResult> _t3Statuses = new(StringComparer.Ordinal);
    private readonly HashSet<string> _desktopSources = new(StringComparer.Ordinal);
    private readonly HashSet<string> _desktopCatalogSources = new(StringComparer.Ordinal);
    private T3ReadResult? _discoveryStatus;
    private T3ReadResult[] _desktopDiagnostics = [];
    private readonly Channel<TaskAlertEvent> _events = Channel.CreateUnbounded<TaskAlertEvent>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _eventSignal = new(0);
    private readonly Queue<TaskAlertEventTrace> _recentEvents = new();
    private HashSet<string> _internallySuppressedTaskIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Task _reducerTask;
    private TaskAlertPreferences _preferences;
    private int? _detectedBank;
    private long _reconciliationRevision;
    private long _reconciliationAuthorityVersion;
    private readonly Dictionary<string, long> _sessionRevisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pendingEventCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string? TurnId, DateTimeOffset At)> _acknowledgedTurns = new(StringComparer.Ordinal);

    public TaskAlertCoordinator(
        string preferencesPath,
        string? statePath = null,
        Action<string>? log = null)
        : this(
            preferencesPath,
            TaskAlertPreferencesStore.LoadOrCreate(preferencesPath),
            statePath,
            log)
    {
    }

    /// <summary>
    /// Starts task-alert processing from settings that the runtime has already activated. The
    /// constructor does not read or write the preferences file.
    /// </summary>
    public TaskAlertCoordinator(
        string preferencesPath,
        TaskAlertPreferences activePreferences,
        string? statePath = null,
        Action<string>? log = null)
    {
        _preferencesPath = preferencesPath ?? throw new ArgumentNullException(nameof(preferencesPath));
        ArgumentNullException.ThrowIfNull(activePreferences);
        var preferencesDirectory = Path.GetDirectoryName(Path.GetFullPath(preferencesPath))
            ?? throw new InvalidOperationException("The task-alert settings path has no parent directory.");
        _statePath = statePath ?? Path.Combine(preferencesDirectory, "task-alert-state.json");
        _log = log ?? (_ => { });
        _preferences = activePreferences.Normalize();
        _pool = new TaskAlertPool { ClearQuestionsOnTurnEnd = _preferences.ClearQuestionsOnTurnEnd };
        if (!_preferences.Enabled)
        {
            _pool.SetEnabled(false);
        }
        else
        {
            RestoreStateUnsafe();
        }

        TrySaveStateUnsafe();

        _reducerTask = RunReducerAsync(_cancellation.Token);
    }

    public event EventHandler<TaskAlertSnapshot>? Changed;

    public bool TryPublish(TaskAlertEvent taskEvent)
    {
        ArgumentNullException.ThrowIfNull(taskEvent);
        if (string.IsNullOrWhiteSpace(taskEvent.SessionId)) return false;
        bool written;
        lock (_sync)
        {
            if (!_pool.Enabled)
            {
                return false;
            }
            if (_internallySuppressedTaskIds.Contains(taskEvent.SessionId))
            {
                return false;
            }

            written = _events.Writer.TryWrite(taskEvent);
            if (written)
            {
                _sessionRevisions[taskEvent.SessionId] = ++_reconciliationRevision;
                _pendingEventCounts[taskEvent.SessionId] = _pendingEventCounts.GetValueOrDefault(taskEvent.SessionId) + 1;
            }
        }

        if (!written)
        {
            return false;
        }

        _eventSignal.Release();
        return true;
    }

    public TaskAlertSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return SnapshotUnsafe();
        }
    }

    /// <summary>Publishes positive remote observations without manufacturing local lifecycle hooks.</summary>
    public void ApplySourceSnapshot(TaskAlertSourceSnapshot source, T3ReadResult status)
    {
        TaskAlertSnapshot snapshot;
        lock (_sync)
        {
            var configured = _preferences.T3 ?? new T3ConnectionOptions();
            if (!_pool.Enabled || !IsT3SourceEnabledUnsafe(source.Source)) return;
            var displayName = configured.FollowDesktop ? _t3Statuses.GetValueOrDefault(source.Source.EnvironmentId)?.DisplayName : configured.DisplayName;
            var projectedStatus = status with { Tasks = [], EnvironmentId = source.Source.EnvironmentId,
                DisplayName = displayName, Enabled = true };
            _t3Statuses[source.Source.EnvironmentId] = projectedStatus;
            if (!configured.FollowDesktop) _t3Status = projectedStatus;
            source = source with { Tasks = source.Tasks.Where(task => !IsSuppressedUnsafe(
                TaskAlertIdentity.RemoteT3(source.Source.EnvironmentId, task.NativeThreadId).PoolKey, null)).ToArray() };
            if (_pool.ApplySourceSnapshot(source)) TrySaveStateUnsafe();
            var labelsChanged = false;
            _metadata ??= new TaskAlertMetadataStore(_statePath + ".metadata.json");
            foreach (var task in source.Tasks)
                if (task.Metadata is not null)
                    labelsChanged |= _metadata.Set(TaskAlertIdentity.RemoteT3(source.Source.EnvironmentId, task.NativeThreadId).PoolKey, task.Metadata);
            if (labelsChanged) SaveMetadataUnsafe();
            snapshot = SnapshotUnsafe();
        }
        RaiseChanged(snapshot);
    }

    public void SetSourceUnavailable(TaskAlertSourceIdentity source, T3ReadResult status)
    {
        TaskAlertSnapshot snapshot;
        lock (_sync)
        {
            if (!IsT3SourceEnabledUnsafe(source)) return;
            _pool.SetSourceUnavailable(source);
            var configured = _preferences.T3;
            var previous = _t3Statuses.GetValueOrDefault(source.EnvironmentId);
            var projectedStatus = status with
            {
                Tasks = [], EnvironmentId = source.EnvironmentId,
                DisplayName = configured?.FollowDesktop is true ? previous?.DisplayName : configured?.DisplayName,
                TransportKind = status.TransportKind ?? previous?.TransportKind,
                CapturedAt = status.CapturedAt > DateTimeOffset.UnixEpoch ? status.CapturedAt : previous?.CapturedAt ?? default,
                Enabled = true,
            };
            _t3Statuses[source.EnvironmentId] = projectedStatus;
            if (configured?.FollowDesktop is not true) _t3Status = projectedStatus;
            snapshot = SnapshotUnsafe();
        }
        RaiseChanged(snapshot);
    }

    /// <summary>Positive disables retire their source; only a complete catalog can retire an absent source.</summary>
    public void SetT3DesktopEnvironments(T3DesktopDiscoveryResult catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        TaskAlertSnapshot snapshot;
        lock (_sync)
        {
            if (_preferences.T3 is not { Enabled: true, FollowDesktop: true }) return;
            if (catalog.Environments.Count > 64 || catalog.Success && !catalog.IsComplete
                && _desktopCatalogSources.Union(catalog.Environments.Where(environment => !string.IsNullOrWhiteSpace(environment.EnvironmentId))
                    .Select(environment => environment.EnvironmentId), StringComparer.Ordinal).Count() > 64)
                catalog = new(false, false, [], "The desktop catalog exceeds the supported environment limit; existing sources are retained.");
            var complete = catalog.IsComplete && catalog.Environments.Count <= 64;
            if (!catalog.Success || !complete)
                _discoveryStatus = new() { ErrorClass = catalog.Success ? "PartialCatalog" : "DiscoveryUnavailable",
                    Reason = catalog.Error ?? "The T3 desktop environment catalog is unavailable or incomplete.", TransportKind = "discovery", DisplayName = "T3 desktop discovery" };
            else _discoveryStatus = null;
            if (catalog.Success)
            {
                if (complete) _desktopCatalogSources.Clear();
                var enabled = new HashSet<string>(StringComparer.Ordinal);
                var diagnostics = new List<T3ReadResult>();
                foreach (var environment in catalog.Environments.Take(64))
                {
                    if (string.IsNullOrWhiteSpace(environment.EnvironmentId) || environment.EnvironmentId.Length > 512
                        || environment.EnvironmentId.Any(char.IsControl))
                    {
                        diagnostics.Add(new() { DisplayName = environment.DisplayName, TransportKind = environment.TransportKind,
                            Enabled = environment.Enabled, ErrorClass = "UnsupportedRoute",
                            Reason = environment.UnsupportedReason ?? "The desktop environment identity could not be verified." });
                        continue;
                    }
                    var supported = environment.Enabled && string.IsNullOrWhiteSpace(environment.UnsupportedReason);
                    _desktopCatalogSources.Add(environment.EnvironmentId);
                    if (supported) enabled.Add(environment.EnvironmentId);
                    if (supported || !environment.Enabled || complete || !_t3Statuses.ContainsKey(environment.EnvironmentId))
                    {
                        if (supported) _desktopSources.Add(environment.EnvironmentId);
                        else if (!environment.Enabled || complete)
                        {
                            _desktopSources.Remove(environment.EnvironmentId);
                            _pool.SetSourceUnavailable(new(TaskAlertBackend.RemoteT3, environment.EnvironmentId));
                        }
                        var previous = _t3Statuses.GetValueOrDefault(environment.EnvironmentId);
                        _t3Statuses[environment.EnvironmentId] = supported && previous is not null
                            ? previous with { DisplayName = environment.DisplayName, TransportKind = environment.TransportKind, Enabled = true }
                            : new() { EnvironmentId = environment.EnvironmentId, DisplayName = environment.DisplayName,
                                TransportKind = environment.TransportKind, Enabled = environment.Enabled,
                                ErrorClass = !environment.Enabled ? "Disabled" : supported ? "AwaitingRead" : "UnsupportedRoute",
                                Reason = environment.UnsupportedReason ?? (environment.Enabled ? "Waiting for a metadata read." : "This environment is disabled in T3 desktop."),
                                CapturedAt = previous?.CapturedAt ?? default };
                    }
                }
                var allDiagnostics = (complete ? diagnostics : _desktopDiagnostics.Concat(diagnostics))
                    .DistinctBy(status => (status.DisplayName, status.TransportKind, status.Reason)).ToArray();
                _desktopDiagnostics = allDiagnostics.Take(64).ToArray();
                if (allDiagnostics.Length > 64)
                    _discoveryStatus = new() { ErrorClass = "DiagnosticLimit", TransportKind = "discovery", DisplayName = "T3 desktop discovery",
                        Reason = "Additional unsupported routes exceed the diagnostic limit; previously authorized sources are retained." };
                if (complete)
                    foreach (var missing in _desktopSources.Where(id => !enabled.Contains(id)).ToArray())
                    {
                        _desktopSources.Remove(missing);
                        _pool.SetSourceUnavailable(new(TaskAlertBackend.RemoteT3, missing));
                        if (_t3Statuses.TryGetValue(missing, out var status))
                            _t3Statuses[missing] = status with { Success = false, Enabled = false, ErrorClass = "NotEnabled",
                                Reason = "This environment is no longer enabled in the T3 desktop catalog." };
                    }
            }
            snapshot = SnapshotUnsafe();
        }
        RaiseChanged(snapshot);
    }

    private bool IsT3SourceEnabledUnsafe(TaskAlertSourceIdentity source) => source.Backend == TaskAlertBackend.RemoteT3
        && _preferences.T3 is { Enabled: true } configured
        && (configured.FollowDesktop ? _desktopSources.Contains(source.EnvironmentId) : configured.EnvironmentId == source.EnvironmentId);

    /// <summary>Refreshes labels only; catalog metadata never starts or ends a task.</summary>
    public void SetTaskMetadata(IEnumerable<KeyValuePair<string, TaskAlertMetadata>> labels)
    {
        TaskAlertSnapshot? snapshot = null;
        lock (_sync)
        {
            _metadata ??= new TaskAlertMetadataStore(_statePath + ".metadata.json");
            var changed = false;
            foreach (var pair in labels.Take(2048)) changed |= _metadata.Set(pair.Key, pair.Value);
            if (changed) { SaveMetadataUnsafe(); snapshot = SnapshotUnsafe(); }
        }
        RaiseChanged(snapshot);
    }

    private void SaveMetadataUnsafe()
    {
        try { _metadata?.Save(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _log("Task display metadata could not be saved (" + error.GetType().Name + ")."); }
    }

    public void SetEnabled(bool enabled)
    {
        TaskAlertSnapshot? snapshot = null;
        lock (_sync)
        {
            if (_pool.SetEnabled(enabled))
            {
                _reconciliationAuthorityVersion++;
                if (!enabled)
                {
                    while (_events.Reader.TryRead(out _))
                    {
                    }
                    _pendingEventCounts.Clear();
                }

                _preferences = _preferences with { Enabled = enabled };
                TaskAlertPreferencesStore.Save(_preferencesPath, _preferences);
                TrySaveStateUnsafe();
                snapshot = SnapshotUnsafe();
            }
        }

        RaiseChanged(snapshot);
    }

    /// <summary>Persists the normal turn-end question policy without changing hook registration.</summary>
    public void SetClearQuestionsOnTurnEnd(bool enabled)
    {
        TaskAlertSnapshot snapshot;
        lock (_sync)
        {
            if (_preferences.ClearQuestionsOnTurnEnd == enabled) return;
            var updated = _preferences with { ClearQuestionsOnTurnEnd = enabled };
            TaskAlertPreferencesStore.Save(_preferencesPath, updated);
            _preferences = updated;
            _pool.ClearQuestionsOnTurnEnd = enabled;
            _reconciliationAuthorityVersion++;
            snapshot = SnapshotUnsafe();
        }
        RaiseChanged(snapshot);
    }

    public void SetDetectedBank(int bank)
    {
        if (bank is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(bank), "The throttle bank must be M1 through M5.");
        }

        TaskAlertSnapshot? snapshot = null;
        lock (_sync)
        {
            if (_detectedBank != bank)
            {
                _detectedBank = bank;
                snapshot = SnapshotUnsafe();
            }
        }

        RaiseChanged(snapshot);
    }

    /// <summary>
    /// Excludes application-owned tasks without adding them to the user's ignored-task settings.
    /// </summary>
    public void SetInternallySuppressedTaskIds(IEnumerable<string> taskIds)
    {
        ArgumentNullException.ThrowIfNull(taskIds);
        TaskAlertSnapshot snapshot;
        lock (_sync)
        {
            _reconciliationAuthorityVersion++;
            _internallySuppressedTaskIds = taskIds
                .Where(taskId => !string.IsNullOrWhiteSpace(taskId))
                .Select(taskId => taskId.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _pool.RemoveAssignments(
                assignment => _internallySuppressedTaskIds.Contains(assignment.SessionId),
                DateTimeOffset.UtcNow);
            TrySaveStateUnsafe();
            snapshot = SnapshotUnsafe();
        }

        RaiseChanged(snapshot);
    }

    public TaskAlertLedOptions GetLedOutput()
    {
        lock (_sync)
        {
            return (_preferences.LedOutput ?? TaskAlertLedOptions.CreateDefault()).Normalize();
        }
    }

    public void SetLedOutput(TaskAlertLedOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var normalized = options.Normalize();
        TaskAlertSnapshot snapshot;
        lock (_sync)
        {
            var updatedPreferences = _preferences with { LedOutput = normalized };
            TaskAlertPreferencesStore.Save(_preferencesPath, updatedPreferences);
            _preferences = updatedPreferences;
            snapshot = SnapshotUnsafe();
        }

        RaiseChanged(snapshot);
    }

    public bool AddSuppression(TaskAlertSuppressionScope scope, string value)
    {
        var rule = TaskAlertSuppression.Normalize(new TaskAlertSuppressionRule(scope, value))
            ?? throw new ArgumentException("The task-alert suppression value is invalid.", nameof(value));
        TaskAlertSnapshot? snapshot = null;
        lock (_sync)
        {
            var current = _preferences.Suppressions ?? [];
            if (current.Contains(rule, TaskAlertSuppression.RuleComparer))
            {
                return false;
            }

            if (current.Length >= TaskAlertSuppression.MaximumRules)
            {
                throw new InvalidOperationException(
                    $"Joydex supports up to {TaskAlertSuppression.MaximumRules} ignored tasks and workspaces.");
            }

            var updated = _preferences with { Suppressions = [.. current, rule] };
            TaskAlertPreferencesStore.Save(_preferencesPath, updated);
            _preferences = updated.Normalize();
            _reconciliationAuthorityVersion++;
            _pool.RemoveAssignments(assignment => IsSuppressedUnsafe(
                assignment.SessionId,
                assignment.WorkspaceKey), DateTimeOffset.UtcNow);
            TrySaveStateUnsafe();
            snapshot = SnapshotUnsafe();
        }

        RaiseChanged(snapshot);
        return true;
    }

    public bool RemoveSuppression(TaskAlertSuppressionRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var normalized = TaskAlertSuppression.Normalize(rule);
        if (normalized is null)
        {
            return false;
        }

        TaskAlertSnapshot? snapshot = null;
        lock (_sync)
        {
            var current = _preferences.Suppressions ?? [];
            var updatedRules = current
                .Where(candidate => !TaskAlertSuppression.RuleComparer.Equals(candidate, normalized))
                .ToArray();
            if (updatedRules.Length == current.Length)
            {
                return false;
            }

            var updated = _preferences with { Suppressions = updatedRules };
            TaskAlertPreferencesStore.Save(_preferencesPath, updated);
            _preferences = updated.Normalize();
            _reconciliationAuthorityVersion++;
            snapshot = SnapshotUnsafe();
        }

        RaiseChanged(snapshot);
        return true;
    }

    public bool AcknowledgeTerminal(int slot, string sessionId)
    {
        TaskAlertSnapshot? snapshot = null;
        lock (_sync)
        {
            var acknowledged = _pool.Assignments.FirstOrDefault(item => item.Slot == slot && item.SessionId == sessionId);
            if (_pool.AcknowledgeTerminal(slot, sessionId, DateTimeOffset.UtcNow))
            {
                _acknowledgedTurns[sessionId] = (acknowledged!.TurnId, acknowledged.UpdatedAt);
                _sessionRevisions[sessionId] = ++_reconciliationRevision;
                TrySaveStateUnsafe();
                snapshot = SnapshotUnsafe();
            }
        }

        RaiseChanged(snapshot);
        return snapshot is not null;
    }

    /// <summary>Dismisses the selected questions under runtime authority and persists the result.</summary>
    public bool DismissQuestions(int slot, string sessionId, IReadOnlyCollection<string> questionKeys)
    {
        TaskAlertSnapshot? snapshot = null;
        lock (_sync)
        {
            if (_pool.DismissQuestions(slot, sessionId, questionKeys, DateTimeOffset.UtcNow))
            {
                _sessionRevisions[sessionId] = ++_reconciliationRevision;
                TrySaveStateUnsafe();
                snapshot = SnapshotUnsafe();
            }
        }
        RaiseChanged(snapshot);
        return snapshot is not null;
    }

    /// <summary>Starts a read-only scan; subsequent hook receipts and user actions supersede it.</summary>
    public TaskAlertReconciliationCheckpoint CaptureReconciliationCheckpoint()
    {
        lock (_sync)
            return new(_reconciliationRevision, _reconciliationAuthorityVersion,
                _pool.Assignments.Select(item => item.SessionId)
                    .Concat(_sessionRevisions.OrderByDescending(item => item.Value).Select(item => item.Key))
                    .Where(id => Guid.TryParse(id, out _)).Distinct(StringComparer.Ordinal)
                    .Take(TaskAlertSlots.All.Length).ToArray());
    }

    /// <summary>Applies observations only when neither newer hooks nor user actions changed their task.</summary>
    public int Reconcile(TaskAlertReconciliationCheckpoint checkpoint, TaskAlertDesktopSnapshot desktop)
    {
        TaskAlertSnapshot? snapshot = null;
        var visibleChanges = 0;
        var applied = false;
        lock (_sync)
        {
            if (!_pool.Enabled || checkpoint.AuthorityVersion != _reconciliationAuthorityVersion) return 0;
            foreach (var observation in desktop.Tasks)
            {
                if (_sessionRevisions.GetValueOrDefault(observation.SessionId) > checkpoint.Revision
                    || _pendingEventCounts.GetValueOrDefault(observation.SessionId) > 0
                    || _internallySuppressedTaskIds.Contains(observation.SessionId)) continue;
                var before = _pool.Assignments.FirstOrDefault(item => item.SessionId == observation.SessionId);
                var workspace = TaskAlertSuppression.NormalizeWorkspace(observation.Workspace);
                var workspaceKey = TaskAlertSuppression.CreateWorkspaceKey(workspace)
                    ?? (TaskAlertSuppression.IsWorkspaceKey(observation.WorkspaceKey) ? observation.WorkspaceKey : before?.WorkspaceKey);
                if (IsSuppressedUnsafe(observation.SessionId, workspaceKey))
                {
                    // A legacy hook may have omitted cwd. A fresh Desktop observation can
                    // reveal that its existing light belongs to an ignored workspace.
                    if (before is not null && _pool.Reconcile(observation, DateTimeOffset.UtcNow))
                    {
                        _pool.RemoveAssignments(item => item.SessionId == observation.SessionId, DateTimeOffset.UtcNow);
                        _sessionRevisions[observation.SessionId] = ++_reconciliationRevision;
                        applied = true;
                        visibleChanges++;
                    }
                    continue;
                }
                if (before is null && (workspaceKey is null || _acknowledgedTurns.TryGetValue(observation.SessionId, out var acknowledged)
                        && (acknowledged.TurnId == observation.TurnId
                            || observation.StartedAt.ToUnixTimeSeconds() < acknowledged.At.ToUnixTimeSeconds()))) continue;
                if (!_pool.Reconcile(observation, DateTimeOffset.UtcNow)) continue;
                applied = true;
                _sessionRevisions[observation.SessionId] = ++_reconciliationRevision;
                var after = _pool.Assignments.First(item => item.SessionId == observation.SessionId);
                if (before is null || before.State != after.State || before.TurnId != after.TurnId
                    || before.Workspace != after.Workspace || before.CompleteAfter != after.CompleteAfter)
                    visibleChanges++;
            }
            if (applied) TrySaveStateUnsafe();
            if (visibleChanges > 0) snapshot = SnapshotUnsafe();
        }
        RaiseChanged(snapshot);
        return visibleChanges;
    }

    public async ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        _cancellation.Cancel();
        try
        {
            await _reducerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        lock (_sync)
        {
            TrySaveStateUnsafe();
        }

        _cancellation.Dispose();
        _eventSignal.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task RunReducerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _eventSignal
                .WaitAsync(TimeSpan.FromMilliseconds(250), cancellationToken)
                .ConfigureAwait(false);

            var changed = false;
            while (_events.Reader.TryRead(out var taskEvent))
            {
                lock (_sync)
                {
                    var remaining = _pendingEventCounts.GetValueOrDefault(taskEvent.SessionId) - 1;
                    if (remaining <= 0) _pendingEventCounts.Remove(taskEvent.SessionId);
                    else _pendingEventCounts[taskEvent.SessionId] = remaining;
                    if (_internallySuppressedTaskIds.Contains(taskEvent.SessionId))
                    {
                        continue;
                    }
                    var workspace = TaskAlertSuppression.NormalizeWorkspace(taskEvent.Workspace);
                    var workspaceKey = TaskAlertSuppression.CreateWorkspaceKey(workspace);
                    var taskSuppressed = IsSuppressedUnsafe(
                        TaskAlertSuppressionScope.Task,
                        taskEvent.SessionId,
                        workspaceKey);
                    var workspaceSuppressed = IsSuppressedUnsafe(
                        TaskAlertSuppressionScope.Workspace,
                        taskEvent.SessionId,
                        workspaceKey);
                    if (taskSuppressed || workspaceSuppressed)
                    {
                        var removed = _pool.RemoveAssignments(
                            assignment => string.Equals(
                                    assignment.SessionId,
                                    taskEvent.SessionId,
                                    StringComparison.Ordinal)
                                && taskEvent.ReceivedAt >= assignment.UpdatedAt
                                && (taskSuppressed
                                    || assignment.WorkspaceKey is null
                                    || string.Equals(
                                        assignment.WorkspaceKey,
                                        workspaceKey,
                                        StringComparison.Ordinal)),
                            taskEvent.ReceivedAt);
                        AddRecentEventUnsafe(new TaskAlertEventTrace(
                            taskEvent.ReceivedAt,
                            taskEvent.Event,
                            taskEvent.SessionId,
                            taskEvent.TurnId,
                            null,
                            null,
                            TaskAlertEventResult.Suppressed,
                            workspace));
                        if (removed > 0)
                        {
                            TrySaveStateUnsafe();
                        }
                        changed = true;
                        continue;
                    }

                    var stateChanged = _pool.Advance(taskEvent.ReceivedAt);
                    var existing = _pool.Assignments.FirstOrDefault(assignment =>
                        string.Equals(assignment.SessionId, taskEvent.SessionId, StringComparison.Ordinal));
                    var droppedBefore = _pool.DroppedEventCount;
                    var applied = _pool.Apply(taskEvent);
                    stateChanged |= applied;
                    var dropped = droppedBefore != _pool.DroppedEventCount;
                    var assignment = _pool.Assignments.FirstOrDefault(candidate =>
                        string.Equals(candidate.SessionId, taskEvent.SessionId, StringComparison.Ordinal));
                    AddRecentEventUnsafe(new TaskAlertEventTrace(
                        taskEvent.ReceivedAt,
                        taskEvent.Event,
                        taskEvent.SessionId,
                        taskEvent.TurnId,
                        assignment?.Slot,
                        assignment?.State,
                        dropped
                            ? TaskAlertEventResult.Dropped
                            : !applied
                                ? TaskAlertEventResult.Ignored
                                : taskEvent.Event == CodexLifecycleEvent.AsyncUserInputRequest
                                    && assignment?.TurnId != taskEvent.TurnId
                                    ? TaskAlertEventResult.Deferred
                                : taskEvent.Event == CodexLifecycleEvent.Stop
                                    ? TaskAlertEventResult.StopGrace
                                    : existing is null
                                        ? TaskAlertEventResult.Assigned
                                        : TaskAlertEventResult.Updated,
                        workspace));
                    if (stateChanged)
                    {
                        TrySaveStateUnsafe();
                    }

                    changed = true;
                }
            }

            lock (_sync)
            {
                var advanced = _pool.Advance(DateTimeOffset.UtcNow);
                if (advanced)
                {
                    TrySaveStateUnsafe();
                    changed = true;
                }
            }

            if (changed)
            {
                RaiseChanged(GetSnapshot());
            }
        }
    }

    private TaskAlertSnapshot SnapshotUnsafe()
    {
        if (_preferences.T3?.FollowDesktop is true)
        {
            var retained = _pool.Assignments.Where(task => task.TaskIdentity.Backend == TaskAlertBackend.RemoteT3)
                .Select(task => task.TaskIdentity.EnvironmentId).ToHashSet(StringComparer.Ordinal);
            foreach (var removed in _t3Statuses.Keys.Where(id => !_desktopCatalogSources.Contains(id) && !retained.Contains(id)).ToArray())
                _t3Statuses.Remove(removed);
        }
        return new(
        _pool.Enabled,
        _pool.Assignments.Select(assignment => assignment with
        {
            Metadata = assignment.Metadata ?? (_metadata ??= new TaskAlertMetadataStore(_statePath + ".metadata.json")).Get(assignment.SessionId)
        }).ToArray(),
        _pool.DroppedEventCount,
        _detectedBank ?? _preferences.Bank,
        _detectedBank is not null,
        [.. _recentEvents],
        [.. (_preferences.Suppressions ?? [])],
        (_preferences.LedOutput ?? TaskAlertLedOptions.CreateDefault()).Normalize(),
        _preferences.ClearQuestionsOnTurnEnd,
        _t3Status,
        _t3Statuses.Values.OrderBy(status => status.DisplayName, StringComparer.Ordinal).ThenBy(status => status.EnvironmentId, StringComparer.Ordinal)
            .Concat(_desktopDiagnostics).Concat(_discoveryStatus is null ? [] : new[] { _discoveryStatus }).ToArray());
    }

    private void RestoreStateUnsafe()
    {
        try
        {
            _pool.Restore(TaskAlertStateStore.Load(_statePath));
            foreach (var source in _pool.Assignments.Select(task => task.TaskIdentity.Source)
                .Where(source => source.Backend == TaskAlertBackend.RemoteT3).Distinct().ToArray())
                _pool.SetSourceUnavailable(source);
            _pool.RemoveAssignments(assignment => IsSuppressedUnsafe(
                assignment.SessionId,
                assignment.WorkspaceKey), DateTimeOffset.UtcNow);
            _pool.Advance(DateTimeOffset.UtcNow);
            if (_pool.Assignments.Count > 0)
            {
                _log($"Restored {_pool.Assignments.Count} task-alert assignment(s).");
            }
        }
        catch (Exception exception) when (exception is JsonException
                                               or InvalidDataException
                                               or NotSupportedException)
        {
            try
            {
                var quarantinePath = TaskAlertStateStore.Quarantine(_statePath);
                _log(quarantinePath is null
                    ? $"Task-alert state was invalid; starting empty: {exception.Message}"
                    : $"Task-alert state was invalid and moved to {quarantinePath}: {exception.Message}");
            }
            catch (Exception quarantineException) when (IsFileFailure(quarantineException))
            {
                _log(
                    $"Task-alert state was invalid and could not be moved aside: "
                    + quarantineException.Message);
            }
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            _log($"Could not load task-alert state; starting empty: {exception.Message}");
        }
    }

    private void TrySaveStateUnsafe()
    {
        try
        {
            TaskAlertStateStore.Save(_statePath, _pool.CaptureState());
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            _log($"Could not save task-alert state: {exception.Message}");
        }
    }

    private static bool IsFileFailure(Exception exception) => exception is IOException
        or UnauthorizedAccessException
        or JsonException
        or NotSupportedException;

    private bool IsSuppressedUnsafe(string sessionId, string? workspaceKey) =>
        (_preferences.Suppressions ?? []).Any(rule => TaskAlertSuppression.Matches(
            rule,
            sessionId,
            workspaceKey));

    private bool IsSuppressedUnsafe(
        TaskAlertSuppressionScope scope,
        string sessionId,
        string? workspaceKey) =>
        (_preferences.Suppressions ?? []).Any(rule => rule.Scope == scope
            && TaskAlertSuppression.Matches(rule, sessionId, workspaceKey));

    private void AddRecentEventUnsafe(TaskAlertEventTrace trace)
    {
        _recentEvents.Enqueue(trace);
        while (_recentEvents.Count > MaximumRecentEvents)
        {
            _recentEvents.Dequeue();
        }
    }

    private void RaiseChanged(TaskAlertSnapshot? snapshot)
    {
        if (snapshot is not null)
        {
            Changed?.Invoke(this, snapshot);
        }
    }
}
