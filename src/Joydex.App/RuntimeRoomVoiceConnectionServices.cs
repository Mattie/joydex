using Joydex.Contracts;
using Joydex.Core.Voice;

namespace Joydex.App;

/// <summary>Runs runtime commands through one attached RPC connection.</summary>
internal sealed class RuntimeRpcCommandRunner(IRuntimeRpcServer rpc) : IRuntimeCommandRunner
{
    private readonly IRuntimeRpcServer _rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));

    public Task<RuntimeCommandResult> ExecuteAsync(
        RuntimeCommandRequest request,
        CancellationToken cancellationToken) =>
        _rpc.ExecuteCommandAsync(request, cancellationToken);

    public Task<RuntimeCommandOperationResult> GetOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken) =>
        _rpc.GetCommandOperationAsync(operationId, cancellationToken);
}

/// <summary>
/// Writes only the saved Room Voice target against the latest checked Desired revision. A lost
/// Apply reply is reconciled by operation ID and is never resubmitted under a new identity.
/// </summary>
internal sealed class RuntimeVoiceTargetWriter : IRuntimeVoiceTargetWriter
{
    private readonly IRuntimeSettingsWriter _writer;
    private readonly RuntimeClientState _state;

    public RuntimeVoiceTargetWriter(
        IRuntimeRpcServer rpc,
        RuntimeClientState state) : this(CreateWriter(rpc, state), state)
    {
    }

    internal RuntimeVoiceTargetWriter(
        IRuntimeSettingsWriter writer,
        RuntimeClientState state)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    public async Task<RuntimeVoiceTargetSelectionResult> SelectAsync(
        RuntimeTaskReference target,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.TaskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.HostId);
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A Voice target operation ID is required.", nameof(operationId));
        }
        var snapshot = GetCurrentSnapshot();
        var desiredVoice = snapshot.Settings.Desired.Voice.Normalize();
        var label = ResolveTargetLabel(snapshot, desiredVoice, target);
        var candidate = desiredVoice with
        {
            VoiceTargetTaskId = target.TaskId.Trim(),
            VoiceTargetHostId = target.HostId.Trim(),
            VoiceTargetTaskLabel = label,
        };

        var result = await _writer.ApplyAsync(
                snapshot.Settings.Revision,
                new SettingsPatch(Voice: candidate),
                operationId,
                cancellationToken)
            .ConfigureAwait(false);
        return MapWrite(result, target);
    }

    public async Task<RuntimeVoiceTargetSelectionResult> RecoverAsync(
        RuntimeTaskReference target,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.TaskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.HostId);
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A Voice target operation ID is required.", nameof(operationId));
        }
        var result = await _writer.RecoverAsync(operationId, cancellationToken)
            .ConfigureAwait(false);
        return MapWrite(result, target);
    }

    private RuntimeSnapshot GetCurrentSnapshot()
    {
        var current = _state.Current;
        if (!current.IsInitialized
            || current.IsDisconnected
            || current.ResynchronizationRequired
            || current.Snapshot is null)
        {
            throw new InvalidOperationException(
                "The Room Voice settings connection is unavailable or needs to refresh.");
        }
        return current.Snapshot;
    }

    private static string ResolveTargetLabel(
        RuntimeSnapshot snapshot,
        VoicePePreferences desiredVoice,
        RuntimeTaskReference target)
    {
        var label = snapshot.Ui?.Voice?.Messaging.Tasks.FirstOrDefault(task =>
            SameTarget(task.TaskId, task.HostId, target))?.Title;
        label = string.IsNullOrWhiteSpace(label)
            ? desiredVoice.VoiceTargetTaskLabel
            : label;
        return label.Trim();
    }

    private static RuntimeVoiceTargetSelectionResult MapWrite(
        RuntimeSettingsWriteResult result,
        RuntimeTaskReference target)
    {
        return result.Outcome switch
        {
            RuntimeSettingsWriteOutcome.NoChanges => TargetIsActive(result.Snapshot.Active.Voice, target)
                ? Applied("The Voice target is already active.")
                : Pending("The Voice target is saved and will apply after the pending Room Voice changes."),
            RuntimeSettingsWriteOutcome.Applied => TargetIsActive(result.Snapshot.Active.Voice, target)
                ? Applied(result.Detail)
                : Pending("The Voice target is saved and will apply after the pending Room Voice changes."),
            RuntimeSettingsWriteOutcome.PendingIdle => Pending(result.Detail),
            RuntimeSettingsWriteOutcome.Conflict => Conflict(result.Detail),
            RuntimeSettingsWriteOutcome.Rejected
                or RuntimeSettingsWriteOutcome.Failed => Failed(result.Detail),
            RuntimeSettingsWriteOutcome.Running
                or RuntimeSettingsWriteOutcome.Uncertain => Uncertain(result.Detail),
            _ => Failed("The settings authority returned an unknown Voice target Apply state."),
        };
    }

    private static IRuntimeSettingsWriter CreateWriter(
        IRuntimeRpcServer rpc,
        RuntimeClientState state)
    {
        ArgumentNullException.ThrowIfNull(rpc);
        ArgumentNullException.ThrowIfNull(state);
        var epoch = GetCheckedSnapshot(state).EngineEpoch;
        return new RuntimeSettingsWriter(
            rpc,
            epoch,
            () => IsCurrent(state, epoch),
            () => GetCheckedSnapshot(state));
    }

    private static bool IsCurrent(RuntimeClientState state, Guid epoch)
    {
        var current = state.Current;
        return current.IsInitialized
            && !current.IsDisconnected
            && !current.ResynchronizationRequired
            && current.Snapshot?.EngineEpoch == epoch;
    }

    private static RuntimeSnapshot GetCheckedSnapshot(RuntimeClientState state)
    {
        var current = state.Current;
        if (!current.IsInitialized
            || current.IsDisconnected
            || current.ResynchronizationRequired
            || current.Snapshot is null)
        {
            throw new InvalidOperationException(
                "The Room Voice settings connection is unavailable or needs to refresh.");
        }
        return current.Snapshot;
    }

    private static bool TargetIsActive(VoicePePreferences preferences, RuntimeTaskReference target) =>
        SameTarget(preferences.VoiceTargetTaskId, preferences.VoiceTargetHostId, target);

    private static bool SameTarget(string taskId, string hostId, RuntimeTaskReference target) =>
        string.Equals(taskId, target.TaskId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(hostId, target.HostId, StringComparison.OrdinalIgnoreCase);

    private static RuntimeVoiceTargetSelectionResult Applied(string detail) =>
        new(RuntimeVoiceTargetSelectionStatus.Applied, detail);

    private static RuntimeVoiceTargetSelectionResult Pending(string detail) =>
        new(RuntimeVoiceTargetSelectionStatus.SavedPendingIdle, detail);

    private static RuntimeVoiceTargetSelectionResult Conflict(string detail) =>
        new(RuntimeVoiceTargetSelectionStatus.Conflict, detail);

    private static RuntimeVoiceTargetSelectionResult Failed(string detail) =>
        new(RuntimeVoiceTargetSelectionStatus.Failed, detail);

    private static RuntimeVoiceTargetSelectionResult Uncertain(string detail) =>
        new(RuntimeVoiceTargetSelectionStatus.Uncertain, detail);
}
