using Joydex.Contracts;
using Joydex.Core.TaskAlerts;
using Joydex.Windows.TaskAlerts;

namespace Joydex.App;

internal enum RuntimeTaskAlertsActionKind
{
    SetEnabled,
    SetLedOutput,
    AddSuppression,
    RemoveSuppression,
    InspectHooks,
    InstallHooks,
    RemoveHooks,
}

internal enum RuntimeTaskAlertsActionOutcome
{
    NoChanges,
    Applied,
    PendingIdle,
    Conflict,
    Rejected,
    Failed,
    Running,
    Uncertain,
}

internal sealed record RuntimeTaskAlertsActionResult(
    RuntimeTaskAlertsActionKind Kind,
    RuntimeTaskAlertsActionOutcome Outcome,
    Guid OperationId,
    string Detail)
{
    public bool Succeeded => Outcome is RuntimeTaskAlertsActionOutcome.NoChanges
        or RuntimeTaskAlertsActionOutcome.Applied
        or RuntimeTaskAlertsActionOutcome.PendingIdle;
}

internal sealed class RuntimeTaskAlertsPresentationState(
    long Generation,
    bool IsConnected,
    bool IsStale,
    TaskAlertSnapshot Snapshot,
    RuntimeTaskAlertHookStatus Hooks,
    SettingsSnapshot? Settings,
    Exception? Failure,
    RuntimeTaskAlertsActionResult? LastAction) : EventArgs
{
    public long Generation { get; } = Generation;

    public bool IsConnected { get; } = IsConnected;

    public bool IsStale { get; } = IsStale;

    public TaskAlertSnapshot Snapshot { get; } = Snapshot;

    public RuntimeTaskAlertHookStatus Hooks { get; } = Hooks;

    public SettingsSnapshot? Settings { get; } = Settings;

    public Exception? Failure { get; } = Failure;

    public RuntimeTaskAlertsActionResult? LastAction { get; } = LastAction;
}

/// <summary>
/// Projects Task Alerts state from one runtime connection and sends preference and hook changes
/// through the runtime authority. Operation IDs come from the caller and remain pending until a
/// definite result is known, allowing a replacement connection to reconcile without resubmitting.
/// </summary>
internal sealed class RuntimeTaskAlertsConnectionServices : IDisposable
{
    private readonly object _gate = new();
    private readonly SynchronizationContext _notificationContext;
    private readonly Queue<RuntimeTaskAlertsPresentationState> _notifications = new();
    private readonly Dictionary<Guid, PendingSettingsAction> _pendingSettings = [];
    private readonly Dictionary<Guid, PendingCommandAction> _pendingCommands = [];
    private readonly Dictionary<Guid, TaskCompletionSource> _executingOperations = [];
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private Connection? _connection;
    private SettingsSnapshot? _settings;
    private RuntimeTaskAlertSnapshot? _taskAlerts;
    private RuntimeTaskAlertHookStatus _hooks = new(
        RuntimeTaskAlertHookState.NotInstalled,
        "Task-alert hook status is unavailable until the runtime connects.");
    private RuntimeTaskAlertsActionResult? _lastAction;
    private Exception? _failure;
    private long _latestGeneration = long.MinValue;
    private bool _stale = true;
    private bool _notificationScheduled;
    private bool _disposed;

    public RuntimeTaskAlertsConnectionServices(SynchronizationContext notificationContext)
    {
        _notificationContext = notificationContext
            ?? throw new ArgumentNullException(nameof(notificationContext));
    }

    public event EventHandler<RuntimeTaskAlertsPresentationState>? Changed;

    public RuntimeTaskAlertsPresentationState Current
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return CurrentLocked();
            }
        }
    }

    /// <summary>Begins a strictly newer connection and seeds presentation state from its snapshot.</summary>
    public bool BeginConnection(
        long generation,
        IRuntimeSettingsWriter settingsWriter,
        IRuntimeCommandRunner commandRunner,
        RuntimeClientState state)
    {
        ArgumentNullException.ThrowIfNull(settingsWriter);
        ArgumentNullException.ThrowIfNull(commandRunner);
        ArgumentNullException.ThrowIfNull(state);
        Connection? previous;
        var schedule = false;
        lock (_gate)
        {
            if (_disposed || generation <= _latestGeneration)
            {
                return false;
            }

            previous = _connection;
            _latestGeneration = generation;
            _connection = new Connection(generation, settingsWriter, commandRunner);
            _settings = null;
            _taskAlerts = null;
            _hooks = new RuntimeTaskAlertHookStatus(
                RuntimeTaskAlertHookState.NotInstalled,
                "Task-alert hook status is unavailable until the runtime snapshot arrives.");
            ApplyConnectionStateLocked(state.Current);
            _failure = null;
            schedule = EnqueueNotificationLocked();
        }

        Cancel(previous);
        ScheduleNotifications(schedule);
        return true;
    }

    /// <summary>Applies state already attributed to the matching runtime connection.</summary>
    public bool ApplyStateChange(long generation, RuntimeClientStateChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Kind == RuntimeClientChangeKind.Disconnected || change.State.IsDisconnected)
        {
            return EndConnection(generation, change.State.DisconnectFailure);
        }

        var schedule = false;
        lock (_gate)
        {
            if (!TryGetCurrentLocked(generation, out _))
            {
                return false;
            }

            ApplyConnectionStateLocked(change.State);
            schedule = EnqueueNotificationLocked();
        }

        ScheduleNotifications(schedule);
        return true;
    }

    /// <summary>Marks only the matching generation disconnected and keeps its last projection.</summary>
    public bool EndConnection(long generation, Exception? failure)
    {
        Connection connection;
        var schedule = false;
        lock (_gate)
        {
            if (!TryGetCurrentLocked(generation, out connection))
            {
                return false;
            }

            _connection = null;
            _stale = true;
            _failure = failure;
            schedule = EnqueueNotificationLocked();
        }

        Cancel(connection);
        ScheduleNotifications(schedule);
        return true;
    }

    public Task<RuntimeTaskAlertsActionResult> SetEnabledAsync(
        bool enabled,
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        ApplySettingsAsync(
            new SettingsIntent(RuntimeTaskAlertsActionKind.SetEnabled, Enabled: enabled),
            operationId,
            cancellationToken);

    public Task<RuntimeTaskAlertsActionResult> SetLedOutputAsync(
        TaskAlertLedOptions options,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return ApplySettingsAsync(
            new SettingsIntent(
                RuntimeTaskAlertsActionKind.SetLedOutput,
                LedOutput: options.Normalize()),
            operationId,
            cancellationToken);
    }

    public Task<RuntimeTaskAlertsActionResult> AddSuppressionAsync(
        TaskAlertSuppressionScope scope,
        string value,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        var rule = TaskAlertSuppression.Normalize(new TaskAlertSuppressionRule(scope, value))
            ?? throw new ArgumentException("The task-alert suppression value is invalid.", nameof(value));
        return ApplySettingsAsync(
            new SettingsIntent(RuntimeTaskAlertsActionKind.AddSuppression, Rule: rule),
            operationId,
            cancellationToken);
    }

    public Task<RuntimeTaskAlertsActionResult> RemoveSuppressionAsync(
        TaskAlertSuppressionRule rule,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var normalized = TaskAlertSuppression.Normalize(rule)
            ?? throw new ArgumentException("The task-alert suppression rule is invalid.", nameof(rule));
        return ApplySettingsAsync(
            new SettingsIntent(RuntimeTaskAlertsActionKind.RemoveSuppression, Rule: normalized),
            operationId,
            cancellationToken);
    }

    public Task<RuntimeTaskAlertsActionResult> InspectHooksAsync(
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        ExecuteCommandAsync(
            RuntimeTaskAlertsActionKind.InspectHooks,
            RuntimeCommandKind.InspectTaskAlertHooks,
            operationId,
            cancellationToken);

    public Task<RuntimeTaskAlertsActionResult> InstallHooksAsync(
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        ExecuteCommandAsync(
            RuntimeTaskAlertsActionKind.InstallHooks,
            RuntimeCommandKind.InstallTaskAlertHooks,
            operationId,
            cancellationToken);

    public Task<RuntimeTaskAlertsActionResult> RemoveHooksAsync(
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        ExecuteCommandAsync(
            RuntimeTaskAlertsActionKind.RemoveHooks,
            RuntimeCommandKind.RemoveTaskAlertHooks,
            operationId,
            cancellationToken);

    /// <summary>
    /// Reconciles all operations whose result was lost or remained running. Each call starts fresh
    /// lookups; an Uncertain lookup is retained for a later attempt and is never memoized.
    /// </summary>
    public async Task<IReadOnlyList<RuntimeTaskAlertsActionResult>> RecoverPendingOperationsAsync(
        CancellationToken cancellationToken = default)
    {
        await _recoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Guid[] settingsIds;
            Guid[] commandIds;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _ = GetConnectionLocked();
                settingsIds = _pendingSettings.Keys.ToArray();
                commandIds = _pendingCommands.Keys.ToArray();
            }

            var results = new List<RuntimeTaskAlertsActionResult>(settingsIds.Length + commandIds.Length);
            foreach (var operationId in settingsIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WaitForOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
                if (TryGetPendingSettings(operationId, out var pending))
                {
                    results.Add(await RecoverSettingsAsync(pending, cancellationToken).ConfigureAwait(false));
                }
            }
            foreach (var operationId in commandIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WaitForOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
                if (TryGetPendingCommand(operationId, out var pending))
                {
                    results.Add(await RecoverCommandAsync(pending, cancellationToken).ConfigureAwait(false));
                }
            }
            return results;
        }
        finally
        {
            _recoveryGate.Release();
        }
    }

    public void Dispose()
    {
        Connection? connection;
        TaskCompletionSource[] executingOperations;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            connection = _connection;
            _connection = null;
            _pendingSettings.Clear();
            _pendingCommands.Clear();
            executingOperations = _executingOperations.Values.ToArray();
            _executingOperations.Clear();
            _notifications.Clear();
            _notificationScheduled = false;
            Changed = null;
        }
        Cancel(connection);
        foreach (var operation in executingOperations)
        {
            operation.TrySetResult();
        }
    }

    private async Task<RuntimeTaskAlertsActionResult> ApplySettingsAsync(
        SettingsIntent intent,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        ValidateOperationId(operationId);
        Connection connection;
        SettingsSnapshot settings;
        PendingSettingsAction pending;
        TaskAlertPreferences? updatedPreferences = null;
        var recover = false;
        lock (_gate)
        {
            connection = GetConnectionLocked();
            settings = _settings
                ?? throw new InvalidOperationException("Runtime settings are unavailable.");
            if (_executingOperations.ContainsKey(operationId))
            {
                throw new InvalidOperationException("The operation is already being reconciled.");
            }
            if (_pendingCommands.ContainsKey(operationId))
            {
                throw new InvalidOperationException("The operation ID already belongs to a hook command.");
            }
            if (_pendingSettings.TryGetValue(operationId, out pending!))
            {
                EnsureSameIntent(pending.Intent, intent);
                recover = true;
            }
            else
            {
                if (_pendingSettings.Count > 0)
                {
                    throw new InvalidOperationException(
                        "A previous Task Alerts settings change must be reconciled before another is submitted.");
                }
                updatedPreferences = ApplyIntent(settings.Desired.TaskAlerts, intent);
                pending = new PendingSettingsAction(operationId, intent);
                _pendingSettings.Add(operationId, pending);
            }
            BeginOperationLocked(operationId);
        }

        try
        {
            using var linked = Link(connection, cancellationToken);
            var result = recover
                ? await connection.SettingsWriter.RecoverAsync(operationId, linked.Token)
                    .ConfigureAwait(false)
                : await connection.SettingsWriter.ApplyAsync(
                        settings.Revision,
                        new SettingsPatch(TaskAlerts: updatedPreferences!),
                        operationId,
                        linked.Token)
                    .ConfigureAwait(false);
            return FinishSettings(connection, pending, result);
        }
        finally
        {
            EndOperation(operationId);
        }
    }

    private async Task<RuntimeTaskAlertsActionResult> RecoverSettingsAsync(
        PendingSettingsAction pending,
        CancellationToken cancellationToken)
    {
        Connection connection;
        lock (_gate)
        {
            connection = GetConnectionLocked();
            if (!_pendingSettings.ContainsKey(pending.OperationId))
            {
                throw new InvalidOperationException("The settings operation is no longer pending.");
            }
            BeginOperationLocked(pending.OperationId);
        }

        try
        {
            using var linked = Link(connection, cancellationToken);
            var result = await connection.SettingsWriter
                .RecoverAsync(pending.OperationId, linked.Token)
                .ConfigureAwait(false);
            return FinishSettings(connection, pending, result);
        }
        finally
        {
            EndOperation(pending.OperationId);
        }
    }

    private RuntimeTaskAlertsActionResult FinishSettings(
        Connection connection,
        PendingSettingsAction pending,
        RuntimeSettingsWriteResult result)
    {
        if (result.OperationId != pending.OperationId)
        {
            throw new InvalidDataException("The runtime returned a mismatched settings operation identity.");
        }

        var action = new RuntimeTaskAlertsActionResult(
            pending.Intent.Kind,
            ToActionOutcome(result.Outcome),
            result.OperationId,
            result.Detail);
        var schedule = false;
        lock (_gate)
        {
            EnsureCurrentLocked(connection);
            _settings = result.Snapshot;
            _lastAction = action;
            if (IsTerminal(action.Outcome))
            {
                _pendingSettings.Remove(pending.OperationId);
            }
            schedule = EnqueueNotificationLocked();
        }
        ScheduleNotifications(schedule);
        return action;
    }

    private async Task<RuntimeTaskAlertsActionResult> ExecuteCommandAsync(
        RuntimeTaskAlertsActionKind actionKind,
        RuntimeCommandKind commandKind,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        ValidateOperationId(operationId);
        Connection connection;
        PendingCommandAction pending;
        var recover = false;
        lock (_gate)
        {
            connection = GetConnectionLocked();
            if (_executingOperations.ContainsKey(operationId))
            {
                throw new InvalidOperationException("The operation is already being reconciled.");
            }
            if (_pendingSettings.ContainsKey(operationId))
            {
                throw new InvalidOperationException("The operation ID already belongs to a settings change.");
            }
            if (_pendingCommands.TryGetValue(operationId, out pending!))
            {
                if (pending.ActionKind != actionKind || pending.CommandKind != commandKind)
                {
                    throw new InvalidOperationException("The operation ID belongs to a different hook command.");
                }
                recover = true;
            }
            else
            {
                if (_pendingCommands.Count > 0)
                {
                    throw new InvalidOperationException(
                        "A previous Task Alerts hook command must be reconciled before another is submitted.");
                }
                pending = new PendingCommandAction(operationId, actionKind, commandKind);
                _pendingCommands.Add(operationId, pending);
            }
            BeginOperationLocked(operationId);
        }

        try
        {
            using var linked = Link(connection, cancellationToken);
            if (recover)
            {
                return await RecoverCommandCoreAsync(connection, pending, linked.Token)
                    .ConfigureAwait(false);
            }

            try
            {
                var request = new RuntimeCommandRequest(operationId, commandKind);
                var result = await connection.CommandRunner.ExecuteAsync(request, linked.Token)
                    .ConfigureAwait(false);
                return FinishCommand(connection, pending, result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsAmbiguousFailure(exception))
            {
                lock (_gate)
                {
                    EnsureCurrentLocked(connection);
                }
                return await RecoverCommandCoreAsync(connection, pending, linked.Token, exception)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            EndOperation(operationId);
        }
    }

    private async Task<RuntimeTaskAlertsActionResult> RecoverCommandAsync(
        PendingCommandAction pending,
        CancellationToken cancellationToken)
    {
        Connection connection;
        lock (_gate)
        {
            connection = GetConnectionLocked();
            if (!_pendingCommands.ContainsKey(pending.OperationId))
            {
                throw new InvalidOperationException("The hook operation is no longer pending.");
            }
            BeginOperationLocked(pending.OperationId);
        }

        try
        {
            using var linked = Link(connection, cancellationToken);
            return await RecoverCommandCoreAsync(connection, pending, linked.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            EndOperation(pending.OperationId);
        }
    }

    private async Task<RuntimeTaskAlertsActionResult> RecoverCommandCoreAsync(
        Connection connection,
        PendingCommandAction pending,
        CancellationToken cancellationToken,
        Exception? executeFailure = null)
    {
        RuntimeCommandOperationResult operation;
        try
        {
            operation = await connection.CommandRunner
                .GetOperationAsync(pending.OperationId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception lookupFailure) when (IsAmbiguousFailure(lookupFailure))
        {
            var detail = executeFailure is null
                ? $"Hook operation {pending.OperationId:D} could not be reconciled: {lookupFailure.Message}"
                : $"Hook operation {pending.OperationId:D} lost its reply and could not be reconciled: "
                    + $"{executeFailure.Message} Lookup: {lookupFailure.Message}";
            return PublishCommandState(
                connection,
                pending,
                RuntimeTaskAlertsActionOutcome.Uncertain,
                detail);
        }

        lock (_gate)
        {
            EnsureCurrentLocked(connection);
        }
        if (operation.OperationId != pending.OperationId)
        {
            return PublishCommandState(
                connection,
                pending,
                RuntimeTaskAlertsActionOutcome.Uncertain,
                "The runtime returned a mismatched hook operation identity.");
        }
        if (operation.State == RuntimeCommandOperationState.Completed
            && operation.Result is not null)
        {
            return FinishCommand(connection, pending, operation.Result);
        }
        if (operation.State == RuntimeCommandOperationState.Running)
        {
            return PublishCommandState(
                connection,
                pending,
                RuntimeTaskAlertsActionOutcome.Running,
                $"Hook operation {pending.OperationId:D} is still running.");
        }
        return PublishCommandState(
            connection,
            pending,
            RuntimeTaskAlertsActionOutcome.Uncertain,
            $"Hook operation {pending.OperationId:D} was not found and was not resubmitted.");
    }

    private RuntimeTaskAlertsActionResult FinishCommand(
        Connection connection,
        PendingCommandAction pending,
        RuntimeCommandResult result)
    {
        if (result.OperationId != pending.OperationId || result.Kind != pending.CommandKind)
        {
            return PublishCommandState(
                connection,
                pending,
                RuntimeTaskAlertsActionOutcome.Uncertain,
                "The runtime returned a mismatched hook command result identity.");
        }

        var outcome = result.Status switch
        {
            RuntimeCommandStatus.Completed => RuntimeTaskAlertsActionOutcome.Applied,
            RuntimeCommandStatus.Rejected => RuntimeTaskAlertsActionOutcome.Rejected,
            RuntimeCommandStatus.Failed => RuntimeTaskAlertsActionOutcome.Failed,
            _ => RuntimeTaskAlertsActionOutcome.Failed,
        };
        if (outcome == RuntimeTaskAlertsActionOutcome.Applied
            && result.Payload?.TaskAlertHooks is not { } hooks)
        {
            return PublishCommandState(
                connection,
                pending,
                RuntimeTaskAlertsActionOutcome.Uncertain,
                "The runtime omitted the Task Alerts hook status.");
        }

        return PublishCommandState(
            connection,
            pending,
            outcome,
            result.Detail ?? DefaultDetail(pending.ActionKind),
            result.Payload?.TaskAlertHooks);
    }

    private RuntimeTaskAlertsActionResult PublishCommandState(
        Connection connection,
        PendingCommandAction pending,
        RuntimeTaskAlertsActionOutcome outcome,
        string detail,
        RuntimeTaskAlertHookStatus? hooks = null)
    {
        var action = new RuntimeTaskAlertsActionResult(
            pending.ActionKind,
            outcome,
            pending.OperationId,
            detail);
        var schedule = false;
        lock (_gate)
        {
            EnsureCurrentLocked(connection);
            if (hooks is not null)
            {
                _hooks = hooks;
                if (_taskAlerts is not null)
                {
                    _taskAlerts = _taskAlerts with { Hooks = hooks };
                }
            }
            _lastAction = action;
            if (IsTerminal(outcome))
            {
                _pendingCommands.Remove(pending.OperationId);
            }
            schedule = EnqueueNotificationLocked();
        }
        ScheduleNotifications(schedule);
        return action;
    }

    private void ApplyConnectionStateLocked(RuntimeClientConnectionState state)
    {
        if (state.Snapshot is { } snapshot)
        {
            _settings = snapshot.Settings;
            _taskAlerts = snapshot.Ui?.TaskAlerts;
            if (_taskAlerts is { } taskAlerts)
            {
                _hooks = taskAlerts.Hooks;
            }
            else
            {
                _hooks = new RuntimeTaskAlertHookStatus(
                    RuntimeTaskAlertHookState.NotInstalled,
                    "Task Alerts are unavailable in this runtime composition.");
            }
        }
        _stale = state.ResynchronizationRequired || !state.IsInitialized;
        _failure = state.DisconnectFailure;
    }

    private RuntimeTaskAlertsPresentationState CurrentLocked() => new(
        _connection?.Generation ?? _latestGeneration,
        _connection is not null,
        _stale || _connection is null,
        ToPresentationSnapshot(_taskAlerts, _settings),
        _hooks,
        _settings,
        _failure,
        _lastAction);

    private static TaskAlertSnapshot ToPresentationSnapshot(
        RuntimeTaskAlertSnapshot? snapshot,
        SettingsSnapshot? settings)
    {
        var active = settings?.Active.TaskAlerts.Normalize() ?? TaskAlertPreferences.Default;
        var completeSuppressions = (settings?.Desired.TaskAlerts.Normalize().Suppressions
                ?? snapshot?.Suppressions
                ?? [])
            .ToArray();
        if (snapshot is null)
        {
            return new TaskAlertSnapshot(
                active.Enabled,
                [],
                0,
                active.Bank,
                BankAutomaticallyDetected: false,
                RecentEvents: [],
                completeSuppressions,
                active.LedOutput);
        }

        return new TaskAlertSnapshot(
            snapshot.Enabled,
            snapshot.Assignments.ToArray(),
            snapshot.DroppedEventCount,
            snapshot.Bank,
            snapshot.BankAutomaticallyDetected,
            snapshot.RecentEvents.Select(ToEventTrace).ToArray(),
            completeSuppressions,
            snapshot.LedOutput.Normalize());
    }

    private static TaskAlertEventTrace ToEventTrace(RuntimeTaskAlertEventTrace trace) => new(
        trace.ReceivedAt,
        trace.Event,
        trace.SessionId,
        trace.TurnId,
        trace.Slot,
        trace.State,
        trace.Result switch
        {
            RuntimeTaskAlertEventResult.Assigned => TaskAlertEventResult.Assigned,
            RuntimeTaskAlertEventResult.Updated => TaskAlertEventResult.Updated,
            RuntimeTaskAlertEventResult.StopGrace => TaskAlertEventResult.StopGrace,
            RuntimeTaskAlertEventResult.Dropped => TaskAlertEventResult.Dropped,
            RuntimeTaskAlertEventResult.Ignored => TaskAlertEventResult.Ignored,
            RuntimeTaskAlertEventResult.Suppressed => TaskAlertEventResult.Suppressed,
            _ => throw new InvalidDataException("The runtime returned an unknown Task Alerts event result."),
        },
        trace.Workspace);

    private static TaskAlertPreferences ApplyIntent(
        TaskAlertPreferences current,
        SettingsIntent intent)
    {
        var normalized = current.Normalize();
        return intent.Kind switch
        {
            RuntimeTaskAlertsActionKind.SetEnabled => normalized with
            {
                Enabled = intent.Enabled
                    ?? throw new InvalidOperationException("The enabled setting was omitted."),
            },
            RuntimeTaskAlertsActionKind.SetLedOutput => normalized with
            {
                LedOutput = intent.LedOutput
                    ?? throw new InvalidOperationException("The LED setting was omitted."),
            },
            RuntimeTaskAlertsActionKind.AddSuppression => AddSuppression(normalized, intent.Rule),
            RuntimeTaskAlertsActionKind.RemoveSuppression => RemoveSuppression(normalized, intent.Rule),
            _ => throw new InvalidOperationException("The action is not a Task Alerts settings change."),
        };
    }

    private static TaskAlertPreferences AddSuppression(
        TaskAlertPreferences current,
        TaskAlertSuppressionRule? rule)
    {
        if (rule is null)
        {
            throw new InvalidOperationException("The suppression rule was omitted.");
        }
        var suppressions = current.Suppressions ?? [];
        if (suppressions.Contains(rule, TaskAlertSuppression.RuleComparer))
        {
            return current;
        }
        if (suppressions.Length >= TaskAlertSuppression.MaximumRules)
        {
            throw new InvalidOperationException(
                $"Joydex supports up to {TaskAlertSuppression.MaximumRules} ignored tasks and workspaces.");
        }
        return current with { Suppressions = [.. suppressions, rule] };
    }

    private static TaskAlertPreferences RemoveSuppression(
        TaskAlertPreferences current,
        TaskAlertSuppressionRule? rule)
    {
        if (rule is null)
        {
            throw new InvalidOperationException("The suppression rule was omitted.");
        }
        return current with
        {
            Suppressions = (current.Suppressions ?? [])
                .Where(candidate => !TaskAlertSuppression.RuleComparer.Equals(candidate, rule))
                .ToArray(),
        };
    }

    private bool TryGetPendingSettings(Guid operationId, out PendingSettingsAction pending)
    {
        lock (_gate)
        {
            return _pendingSettings.TryGetValue(operationId, out pending!);
        }
    }

    private bool TryGetPendingCommand(Guid operationId, out PendingCommandAction pending)
    {
        lock (_gate)
        {
            return _pendingCommands.TryGetValue(operationId, out pending!);
        }
    }

    private Connection GetConnectionLocked()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _connection
            ?? throw new InvalidOperationException("The Task Alerts runtime is disconnected.");
    }

    private bool TryGetCurrentLocked(long generation, out Connection connection)
    {
        if (_disposed || _connection is null || _connection.Generation != generation)
        {
            connection = null!;
            return false;
        }
        connection = _connection;
        return true;
    }

    private void EnsureCurrentLocked(Connection connection)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(_connection, connection))
        {
            throw new OperationCanceledException(
                "The Task Alerts runtime connection changed before the operation completed.");
        }
    }

    private void BeginOperationLocked(Guid operationId)
    {
        if (!_executingOperations.TryAdd(
                operationId,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)))
        {
            throw new InvalidOperationException("The operation is already being reconciled.");
        }
    }

    private void EndOperation(Guid operationId)
    {
        TaskCompletionSource? completion;
        lock (_gate)
        {
            _executingOperations.Remove(operationId, out completion);
        }
        completion?.TrySetResult();
    }

    private async Task WaitForOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        Task? completion;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            completion = _executingOperations.TryGetValue(operationId, out var source)
                ? source.Task
                : null;
        }
        if (completion is not null)
        {
            await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private bool EnqueueNotificationLocked()
    {
        _notifications.Enqueue(CurrentLocked());
        if (_notificationScheduled)
        {
            return false;
        }
        _notificationScheduled = true;
        return true;
    }

    private void ScheduleNotifications(bool schedule)
    {
        if (schedule)
        {
            _notificationContext.Post(_ => DispatchNotifications(), null);
        }
    }

    private void DispatchNotifications()
    {
        while (true)
        {
            RuntimeTaskAlertsPresentationState state;
            EventHandler<RuntimeTaskAlertsPresentationState>? handlers;
            lock (_gate)
            {
                if (_disposed)
                {
                    _notifications.Clear();
                    _notificationScheduled = false;
                    return;
                }
                if (_notifications.Count == 0)
                {
                    _notificationScheduled = false;
                    return;
                }
                state = _notifications.Dequeue();
                handlers = Changed;
            }

            if (handlers is null)
            {
                continue;
            }
            foreach (EventHandler<RuntimeTaskAlertsPresentationState> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, state);
                }
                catch
                {
                    // One window cannot block later runtime state from reaching other views.
                }
            }
        }
    }

    private static CancellationTokenSource Link(
        Connection connection,
        CancellationToken cancellationToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            connection.Cancellation.Token);

    private static void EnsureSameIntent(SettingsIntent pending, SettingsIntent requested)
    {
        var same = pending.Kind == requested.Kind
            && pending.Enabled == requested.Enabled
            && Equals(pending.LedOutput, requested.LedOutput)
            && RulesEqual(pending.Rule, requested.Rule);
        if (!same)
        {
            throw new InvalidOperationException(
                "The operation ID belongs to a different Task Alerts settings change.");
        }
    }

    private static bool RulesEqual(
        TaskAlertSuppressionRule? left,
        TaskAlertSuppressionRule? right) =>
        left is null
            ? right is null
            : right is not null && TaskAlertSuppression.RuleComparer.Equals(left, right);

    private static RuntimeTaskAlertsActionOutcome ToActionOutcome(RuntimeSettingsWriteOutcome outcome) =>
        outcome switch
        {
            RuntimeSettingsWriteOutcome.NoChanges => RuntimeTaskAlertsActionOutcome.NoChanges,
            RuntimeSettingsWriteOutcome.Applied => RuntimeTaskAlertsActionOutcome.Applied,
            RuntimeSettingsWriteOutcome.PendingIdle => RuntimeTaskAlertsActionOutcome.PendingIdle,
            RuntimeSettingsWriteOutcome.Conflict => RuntimeTaskAlertsActionOutcome.Conflict,
            RuntimeSettingsWriteOutcome.Rejected => RuntimeTaskAlertsActionOutcome.Rejected,
            RuntimeSettingsWriteOutcome.Failed => RuntimeTaskAlertsActionOutcome.Failed,
            RuntimeSettingsWriteOutcome.Running => RuntimeTaskAlertsActionOutcome.Running,
            RuntimeSettingsWriteOutcome.Uncertain => RuntimeTaskAlertsActionOutcome.Uncertain,
            _ => RuntimeTaskAlertsActionOutcome.Failed,
        };

    private static bool IsTerminal(RuntimeTaskAlertsActionOutcome outcome) => outcome is not
        (RuntimeTaskAlertsActionOutcome.Running or RuntimeTaskAlertsActionOutcome.Uncertain);

    private static bool IsAmbiguousFailure(Exception exception) => exception is
        IOException or TimeoutException or InvalidOperationException;

    private static void ValidateOperationId(Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A caller-owned operation ID is required.", nameof(operationId));
        }
    }

    private static string DefaultDetail(RuntimeTaskAlertsActionKind kind) => kind switch
    {
        RuntimeTaskAlertsActionKind.InspectHooks => "Task Alerts hook status refreshed.",
        RuntimeTaskAlertsActionKind.InstallHooks => "Task Alerts hooks installed.",
        RuntimeTaskAlertsActionKind.RemoveHooks => "Task Alerts hooks removed.",
        _ => "The Task Alerts operation completed.",
    };

    private static void Cancel(Connection? connection)
    {
        if (connection is not null)
        {
            connection.Cancellation.Cancel();
        }
    }

    private sealed record Connection(
        long Generation,
        IRuntimeSettingsWriter SettingsWriter,
        IRuntimeCommandRunner CommandRunner)
    {
        public CancellationTokenSource Cancellation { get; } = new();
    }

    private sealed record SettingsIntent(
        RuntimeTaskAlertsActionKind Kind,
        bool? Enabled = null,
        TaskAlertLedOptions? LedOutput = null,
        TaskAlertSuppressionRule? Rule = null);

    private sealed record PendingSettingsAction(Guid OperationId, SettingsIntent Intent);

    private sealed record PendingCommandAction(
        Guid OperationId,
        RuntimeTaskAlertsActionKind ActionKind,
        RuntimeCommandKind CommandKind);
}

/// <summary>
/// Rechecks retained Task Alerts operation IDs a bounded number of times. Opening a new window and
/// repeating an action both use this policy, so neither path replaces uncertain work with a new ID.
/// </summary>
internal static class RuntimeTaskAlertsRecoveryPolicy
{
    private const int AutomaticRecoveryAttemptLimit = 3;

    public static async Task<IReadOnlyList<RuntimeTaskAlertsActionResult>> RecoverUntilSettledAsync(
        RuntimeTaskAlertsConnectionServices runtimeServices,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeServices);
        IReadOnlyList<RuntimeTaskAlertsActionResult> results = [];
        for (var attempt = 0; attempt < AutomaticRecoveryAttemptLimit; attempt++)
        {
            results = await runtimeServices
                .RecoverPendingOperationsAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!results.Any(IsPending))
            {
                break;
            }
        }
        return results;
    }

    public static async Task ReconcileBeforeNewActionAsync(
        RuntimeTaskAlertsConnectionServices runtimeServices,
        CancellationToken cancellationToken = default)
    {
        var results = await RecoverUntilSettledAsync(runtimeServices, cancellationToken)
            .ConfigureAwait(false);
        var unresolved = results.FirstOrDefault(IsPending);
        if (unresolved is not null)
        {
            throw new InvalidOperationException(unresolved.Detail);
        }
    }

    private static bool IsPending(RuntimeTaskAlertsActionResult result) => result.Outcome is
        RuntimeTaskAlertsActionOutcome.Running or RuntimeTaskAlertsActionOutcome.Uncertain;
}
