using System.Text.Json;
using Joydex.Contracts;
using Joydex.Core.Config;

namespace Joydex.App;

internal sealed record RuntimeSettingsDraftState(
    Guid EngineEpoch,
    long BaseRevision,
    SettingsBundle BaseSettings,
    SettingsBundle DraftSettings,
    Guid? PendingOperationId,
    SettingsBundle? PendingSettings = null,
    bool RequiresRebase = false,
    string? Detail = null)
{
    public bool IsDirty => !RuntimeSettingsDraftController.SettingsEqual(
        BaseSettings,
        DraftSettings);
}

/// <summary>
/// Keeps one live Settings editor's draft and submitted operation identity across runtime
/// connections. The Runtime Host owns the durable operation journal; this controller never
/// resubmits an operation whose reply may have been lost.
/// </summary>
internal sealed class RuntimeSettingsDraftController
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private IRuntimeSettingsWriter _writer;
    private RuntimeSnapshot _latestSnapshot;
    private RuntimeSettingsDraftState _state;

    public RuntimeSettingsDraftController(
        IRuntimeSettingsWriter writer,
        RuntimeSnapshot initialSnapshot)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        ArgumentNullException.ThrowIfNull(initialSnapshot);
        ArgumentNullException.ThrowIfNull(initialSnapshot.Settings);
        _latestSnapshot = initialSnapshot;
        _state = FromSnapshot(initialSnapshot);
    }

    public RuntimeSettingsDraftState Current => _state;

    public void UpdateDraft(SettingsBundle draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        _state = _state with { DraftSettings = draft };
    }

    /// <summary>
    /// Replaces the connection-scoped writer. A pending operation keeps its exact identity so it
    /// can be recovered from the host journal, including after an engine restart.
    /// </summary>
    public void BeginConnection(
        IRuntimeSettingsWriter writer,
        RuntimeSnapshot snapshot)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Settings);
        _latestSnapshot = snapshot;

        if (_state.PendingOperationId is not null)
        {
            _state = _state with { EngineEpoch = snapshot.EngineEpoch };
            return;
        }

        if (MatchesBase(snapshot.Settings))
        {
            _state = _state with { EngineEpoch = snapshot.EngineEpoch };
            return;
        }

        _state = _state with
        {
            EngineEpoch = snapshot.EngineEpoch,
            RequiresRebase = true,
            Detail = "Settings changed while this draft was open. Review the latest settings before applying the draft.",
        };
    }

    /// <summary>
    /// Rebases preserved edits onto the latest authoritative revision after the user has reviewed
    /// the conflict. No change is submitted by this method.
    /// </summary>
    public void RebaseToLatest()
    {
        if (_state.PendingOperationId is not null)
        {
            throw new InvalidOperationException(
                "A pending settings operation must be recovered before rebasing the draft.");
        }

        var edits = CreatePatch(_state.BaseSettings, _state.DraftSettings);
        var rebasedDraft = ApplyPatch(_latestSnapshot.Settings.Desired, edits);
        _state = _state with
        {
            EngineEpoch = _latestSnapshot.EngineEpoch,
            BaseRevision = _latestSnapshot.Settings.Revision,
            BaseSettings = _latestSnapshot.Settings.Desired,
            DraftSettings = rebasedDraft,
            RequiresRebase = false,
            Detail = "The draft now uses the latest saved settings as its base.",
        };
    }

    public void DiscardDraft()
    {
        if (_state.PendingOperationId is not null)
        {
            throw new InvalidOperationException(
                "A pending settings operation must be recovered before discarding the draft.");
        }

        _state = FromSnapshot(_latestSnapshot);
    }

    public async Task<RuntimeSettingsWriteResult> ApplyAsync(
        SettingsBundle candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state.PendingOperationId is { } pendingOperationId)
            {
                var sameRequest = _state.PendingSettings is { } pending
                    && SettingsEqual(pending, candidate);
                var recovered = await RecoverCoreAsync(pendingOperationId, cancellationToken)
                    .ConfigureAwait(false);
                if (!sameRequest
                    && recovered.Outcome is not (RuntimeSettingsWriteOutcome.Running
                        or RuntimeSettingsWriteOutcome.Uncertain))
                {
                    _state = _state with { DraftSettings = candidate };
                    throw new InvalidOperationException(
                        "The earlier settings action was reconciled. Review its result, then choose Apply again for the current draft.");
                }
                return recovered;
            }

            _state = _state with { DraftSettings = candidate };
            if (_state.RequiresRebase)
            {
                return new RuntimeSettingsWriteResult(
                    RuntimeSettingsWriteOutcome.Conflict,
                    Guid.Empty,
                    _latestSnapshot.Settings,
                    ApplyResult: null,
                    _state.Detail ?? "Review the latest settings before applying this draft.");
            }

            var patch = CreatePatch(_state.BaseSettings, candidate);
            if (PatchIsEmpty(patch))
            {
                _state = _state with
                {
                    Detail = "The requested settings are already saved.",
                };
                return new RuntimeSettingsWriteResult(
                    RuntimeSettingsWriteOutcome.NoChanges,
                    Guid.Empty,
                    _latestSnapshot.Settings,
                    ApplyResult: null,
                    _state.Detail);
            }

            var operationId = Guid.NewGuid();
            _state = _state with
            {
                PendingOperationId = operationId,
                PendingSettings = candidate,
                Detail = "Applying settings…",
            };

            RuntimeSettingsWriteResult result;
            try
            {
                result = await _writer.ApplyAsync(
                        _state.BaseRevision,
                        patch,
                        operationId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // The operation may have reached the authority. Keep its identity for exact
                // recovery on this connection or its replacement.
                throw;
            }

            ApplyResult(result);
            return result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<RuntimeSettingsWriteResult?> RecoverAsync(
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _state.PendingOperationId is { } operationId
                ? await RecoverCoreAsync(operationId, cancellationToken).ConfigureAwait(false)
                : null;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<RuntimeSettingsWriteResult> RecoverCoreAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var result = await _writer.RecoverAsync(operationId, cancellationToken)
            .ConfigureAwait(false);
        ApplyResult(result);
        return result;
    }

    private void ApplyResult(RuntimeSettingsWriteResult result)
    {
        if (_state.PendingOperationId is not { } expected
            || result.OperationId != expected)
        {
            throw new InvalidDataException(
                "The settings writer returned a result for a different operation.");
        }

        switch (result.Outcome)
        {
            case RuntimeSettingsWriteOutcome.NoChanges:
            case RuntimeSettingsWriteOutcome.Applied:
            case RuntimeSettingsWriteOutcome.PendingIdle:
                _latestSnapshot = _latestSnapshot with { Settings = result.Snapshot };
                _state = new RuntimeSettingsDraftState(
                    _latestSnapshot.EngineEpoch,
                    result.Snapshot.Revision,
                    result.Snapshot.Desired,
                    result.Snapshot.Desired,
                    PendingOperationId: null,
                    PendingSettings: null,
                    Detail: result.Detail);
                break;
            case RuntimeSettingsWriteOutcome.Conflict:
            case RuntimeSettingsWriteOutcome.Rejected:
            case RuntimeSettingsWriteOutcome.Failed:
                UpdateLatest(result.Snapshot);
                _state = _state with
                {
                    PendingOperationId = null,
                    PendingSettings = null,
                    RequiresRebase = !MatchesBase(result.Snapshot),
                    Detail = result.Detail,
                };
                break;
            case RuntimeSettingsWriteOutcome.Running:
            case RuntimeSettingsWriteOutcome.Uncertain:
                UpdateLatest(result.Snapshot);
                _state = _state with { Detail = result.Detail };
                break;
            default:
                throw new InvalidDataException("The settings writer returned an unknown outcome.");
        }
    }

    private void UpdateLatest(SettingsSnapshot settings) =>
        _latestSnapshot = _latestSnapshot with { Settings = settings };

    private bool MatchesBase(SettingsSnapshot settings) =>
        settings.Revision == _state.BaseRevision
        && SettingsEqual(settings.Desired, _state.BaseSettings);

    private static RuntimeSettingsDraftState FromSnapshot(RuntimeSnapshot snapshot) => new(
        snapshot.EngineEpoch,
        snapshot.Settings.Revision,
        snapshot.Settings.Desired,
        snapshot.Settings.Desired,
        PendingOperationId: null,
        PendingSettings: null);

    internal static SettingsPatch CreatePatch(SettingsBundle baseline, SettingsBundle candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        var changes = ConfigurationChangeDetector.Detect(
            baseline.Companion,
            candidate.Companion,
            baseline.Voice,
            candidate.Voice,
            baseline.PebbleIndex,
            candidate.PebbleIndex);
        return new SettingsPatch(
            Companion: changes.CompanionChanged ? candidate.Companion : null,
            Voice: changes.VoicePreferencesChanged ? candidate.Voice : null,
            PebbleIndex: changes.PebbleIndexPreferencesChanged ? candidate.PebbleIndex : null,
            TaskAlerts: Serialize(baseline.TaskAlerts.Normalize())
                != Serialize(candidate.TaskAlerts.Normalize())
                ? candidate.TaskAlerts
                : null);
    }

    internal static bool SettingsEqual(SettingsBundle left, SettingsBundle right) =>
        PatchIsEmpty(CreatePatch(left, right));

    internal static SettingsBundle ApplyPatch(SettingsBundle baseline, SettingsPatch patch) => new(
        patch.Companion ?? baseline.Companion,
        patch.Voice ?? baseline.Voice,
        patch.PebbleIndex ?? baseline.PebbleIndex,
        patch.TaskAlerts ?? baseline.TaskAlerts);

    private static bool PatchIsEmpty(SettingsPatch patch) =>
        patch.Companion is null
        && patch.Voice is null
        && patch.PebbleIndex is null
        && patch.TaskAlerts is null;

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value);
}
