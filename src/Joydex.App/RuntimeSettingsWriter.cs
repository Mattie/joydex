using System.Collections.Concurrent;
using Joydex.Contracts;

namespace Joydex.App;

internal enum RuntimeSettingsWriteOutcome
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

internal sealed record RuntimeSettingsWriteResult(
    RuntimeSettingsWriteOutcome Outcome,
    Guid OperationId,
    SettingsSnapshot Snapshot,
    ApplySettingsResult? ApplyResult,
    string Detail);

/// <summary>
/// Applies one revisioned settings patch. Callers choose the operation ID before the first write so
/// they can persist it with a draft. An ambiguous Apply is reconciled and never resubmitted.
/// </summary>
internal interface IRuntimeSettingsWriter
{
    Task<RuntimeSettingsWriteResult> ApplyAsync(
        long baseRevision,
        SettingsPatch patch,
        Guid operationId,
        CancellationToken cancellationToken);

    Task<RuntimeSettingsWriteResult> RecoverAsync(
        Guid operationId,
        CancellationToken cancellationToken);
}

internal sealed class RuntimeSettingsWriter : IRuntimeSettingsWriter
{
    private static readonly TimeSpan AmbiguousApplyLookupTimeout = TimeSpan.FromSeconds(5);
    private readonly IRuntimeRpcServer _rpc;
    private readonly Guid _engineEpoch;
    private readonly Func<bool> _isCurrentConnection;
    private readonly Func<RuntimeSnapshot> _currentSnapshot;
    private readonly ConcurrentDictionary<Guid, byte> _submittedOperations = new();

    public RuntimeSettingsWriter(
        IRuntimeRpcServer rpc,
        Guid engineEpoch,
        Func<bool> isCurrentConnection,
        Func<RuntimeSnapshot> currentSnapshot)
    {
        _rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
        if (engineEpoch == Guid.Empty)
        {
            throw new ArgumentException("A runtime engine epoch is required.", nameof(engineEpoch));
        }
        _engineEpoch = engineEpoch;
        _isCurrentConnection = isCurrentConnection
            ?? throw new ArgumentNullException(nameof(isCurrentConnection));
        _currentSnapshot = currentSnapshot
            ?? throw new ArgumentNullException(nameof(currentSnapshot));
    }

    public async Task<RuntimeSettingsWriteResult> ApplyAsync(
        long baseRevision,
        SettingsPatch patch,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (baseRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baseRevision));
        }
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A settings operation ID is required.", nameof(operationId));
        }
        EnsureCurrentConnection();

        PrepareSettingsResult prepared;
        try
        {
            prepared = await _rpc.PrepareSettingsAsync(
                    new PrepareSettingsRequest(baseRevision, patch),
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureCurrentConnection();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result(
                RuntimeSettingsWriteOutcome.Failed,
                operationId,
                CurrentSnapshotOrThrow(),
                applyResult: null,
                $"The settings change could not be prepared: {exception.Message}");
        }

        switch (prepared.Status)
        {
            case SettingsPrepareStatus.NoChanges:
                return Result(
                    RuntimeSettingsWriteOutcome.NoChanges,
                    operationId,
                    prepared.Snapshot,
                    applyResult: null,
                    "The requested settings are already saved.");
            case SettingsPrepareStatus.Conflict:
                return Result(
                    RuntimeSettingsWriteOutcome.Conflict,
                    operationId,
                    prepared.Snapshot,
                    applyResult: null,
                    FirstDetail(
                        prepared.Errors,
                        "Settings changed in another window. Refresh before applying this draft."));
            case SettingsPrepareStatus.Rejected:
                return Result(
                    RuntimeSettingsWriteOutcome.Rejected,
                    operationId,
                    prepared.Snapshot,
                    applyResult: null,
                    FirstDetail(prepared.Errors, "The settings change was rejected."));
            case SettingsPrepareStatus.Prepared:
                break;
            default:
                return Result(
                    RuntimeSettingsWriteOutcome.Failed,
                    operationId,
                    prepared.Snapshot,
                    applyResult: null,
                    "The settings authority returned an unknown preparation state.");
        }

        if (string.IsNullOrWhiteSpace(prepared.PreparationToken))
        {
            return Result(
                RuntimeSettingsWriteOutcome.Failed,
                operationId,
                prepared.Snapshot,
                applyResult: null,
                "The settings authority omitted the preparation token.");
        }

        try
        {
            _submittedOperations.TryAdd(operationId, 0);
            var applied = await _rpc.ApplySettingsAsync(
                    new ApplySettingsRequest(operationId, prepared.PreparationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureCurrentConnection();
            return FromApply(applied, operationId);
        }
        catch (Exception applyFailure)
        {
            // A reply from an older connection must never trigger recovery work or update the
            // replacement editor. A caller can still reconcile the operation on its owning
            // connection before that connection is discarded.
            EnsureCurrentConnection();
            using var lookupTimeout = new CancellationTokenSource(AmbiguousApplyLookupTimeout);
            return await RecoverCoreAsync(operationId, applyFailure, lookupTimeout.Token)
                .ConfigureAwait(false);
        }
    }

    public Task<RuntimeSettingsWriteResult> RecoverAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A settings operation ID is required.", nameof(operationId));
        }
        EnsureCurrentConnection();
        return RecoverCoreAsync(operationId, applyFailure: null, cancellationToken);
    }

    private async Task<RuntimeSettingsWriteResult> RecoverCoreAsync(
        Guid operationId,
        Exception? applyFailure,
        CancellationToken cancellationToken)
    {
        SettingsOperationResult operation;
        try
        {
            operation = await _rpc.GetSettingsOperationAsync(operationId, cancellationToken)
                .ConfigureAwait(false);
            EnsureCurrentConnection();
        }
        catch (Exception lookupFailure)
        {
            EnsureCurrentConnection();
            var applyDetail = applyFailure is null ? string.Empty : $" Apply: {applyFailure.Message}";
            return Result(
                RuntimeSettingsWriteOutcome.Uncertain,
                operationId,
                CurrentSnapshotOrThrow(),
                applyResult: null,
                $"Settings operation {operationId:D} could not be reconciled and was not resubmitted."
                + applyDetail
                + $" Lookup: {lookupFailure.Message}");
        }

        if (operation.OperationId != operationId)
        {
            return Result(
                RuntimeSettingsWriteOutcome.Uncertain,
                operationId,
                CurrentSnapshotOrThrow(),
                applyResult: null,
                $"Settings operation {operationId:D} returned a mismatched identity and was not resubmitted.");
        }
        if (operation.State == SettingsOperationState.Completed && operation.Result is not null)
        {
            return FromApply(operation.Result, operationId);
        }
        if (operation.State == SettingsOperationState.Running)
        {
            return Result(
                RuntimeSettingsWriteOutcome.Running,
                operationId,
                CurrentSnapshotOrThrow(),
                applyResult: null,
                $"Settings operation {operationId:D} is still running.");
        }

        if (operation.State == SettingsOperationState.NotFound
            && !_submittedOperations.ContainsKey(operationId))
        {
            return Result(
                RuntimeSettingsWriteOutcome.Failed,
                operationId,
                CurrentSnapshotOrThrow(),
                applyResult: null,
                "The previous connection's settings operation was not found. Your draft is preserved; review the latest settings before applying again.");
        }

        return Result(
            RuntimeSettingsWriteOutcome.Uncertain,
            operationId,
            CurrentSnapshotOrThrow(),
            applyResult: null,
            $"Settings operation {operationId:D} was not found and was not resubmitted.");
    }

    private RuntimeSettingsWriteResult FromApply(ApplySettingsResult apply, Guid operationId)
    {
        if (apply.OperationId != operationId)
        {
            return Result(
                RuntimeSettingsWriteOutcome.Uncertain,
                operationId,
                apply.Snapshot,
                apply,
                $"Settings operation {operationId:D} returned a mismatched Apply identity and was not resubmitted.");
        }
        var outcome = apply.Status switch
        {
            SettingsApplyStatus.Applied => RuntimeSettingsWriteOutcome.Applied,
            SettingsApplyStatus.PendingIdle => RuntimeSettingsWriteOutcome.PendingIdle,
            SettingsApplyStatus.Conflict => RuntimeSettingsWriteOutcome.Conflict,
            SettingsApplyStatus.Rejected => RuntimeSettingsWriteOutcome.Rejected,
            SettingsApplyStatus.FailedRolledBack
                or SettingsApplyStatus.ActivationFailed => RuntimeSettingsWriteOutcome.Failed,
            SettingsApplyStatus.AuthorityStateUnrecorded => RuntimeSettingsWriteOutcome.Uncertain,
            _ => RuntimeSettingsWriteOutcome.Failed,
        };
        return Result(
            outcome,
            operationId,
            apply.Snapshot,
            apply,
            FirstDetail(apply.Errors, DefaultDetail(outcome)));
    }

    private SettingsSnapshot CurrentSnapshotOrThrow()
    {
        EnsureCurrentConnection();
        var snapshot = _currentSnapshot()
            ?? throw new InvalidOperationException("The runtime settings snapshot is unavailable.");
        if (snapshot.EngineEpoch != _engineEpoch)
        {
            throw new OperationCanceledException(
                "The runtime engine changed before the settings operation completed.");
        }
        return snapshot.Settings;
    }

    private void EnsureCurrentConnection()
    {
        if (!_isCurrentConnection())
        {
            throw new OperationCanceledException(
                "The runtime settings connection changed before the operation completed.");
        }
    }

    private static RuntimeSettingsWriteResult Result(
        RuntimeSettingsWriteOutcome outcome,
        Guid operationId,
        SettingsSnapshot snapshot,
        ApplySettingsResult? applyResult,
        string detail) =>
        new(outcome, operationId, snapshot, applyResult, detail);

    private static string FirstDetail(IReadOnlyList<string> errors, string fallback) =>
        errors.FirstOrDefault(error => !string.IsNullOrWhiteSpace(error)) ?? fallback;

    private static string DefaultDetail(RuntimeSettingsWriteOutcome outcome) => outcome switch
    {
        RuntimeSettingsWriteOutcome.Applied => "The settings change is active.",
        RuntimeSettingsWriteOutcome.PendingIdle => "The settings change is saved and pending an activation boundary.",
        RuntimeSettingsWriteOutcome.Conflict => "Settings changed in another window.",
        RuntimeSettingsWriteOutcome.Rejected => "The settings change was rejected.",
        RuntimeSettingsWriteOutcome.Uncertain => "The settings change has an uncertain final state.",
        _ => "The settings change failed.",
    };
}
