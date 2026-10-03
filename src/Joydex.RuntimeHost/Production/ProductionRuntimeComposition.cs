using Joydex.App;
using Joydex.Contracts;
using Joydex.Core.Runtime;
using Joydex.Core.Voice;
using Joydex.RuntimeHost.Settings;
using Joydex.Windows.Voice;

namespace Joydex.RuntimeHost.Production;

/// <summary>
/// Owns the production aggregate generations. Replacements are serialized, and an aggregate is
/// never started until the previous generation has confirmed cleanup.
/// </summary>
internal sealed class ProductionRuntimeComposition : IRuntimeComposition
{
    private static readonly SettingsAggregateId[] StartupOrder =
    [
        SettingsAggregateId.TaskAlerts,
        SettingsAggregateId.Companion,
        SettingsAggregateId.Voice,
        SettingsAggregateId.PebbleIndex,
    ];

    private readonly IProductionRuntimeOwnerFactory _factory;
    private readonly CancellationToken _runtimeCancellationToken;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _captureGate = new();
    private readonly Dictionary<SettingsAggregateId, IProductionRuntimeOwner> _owners = [];
    private readonly Dictionary<Guid, CaptureObservation> _captureObservations = [];
    private readonly HashSet<SettingsAggregateId> _initialized = [];
    private readonly HashSet<SettingsAggregateId> _terminal = [];
    private readonly Dictionary<SettingsAggregateId, Exception> _terminalFailures = [];
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SettingsBundle? _activeBundle;
    private bool _disposed;

    internal ProductionRuntimeComposition(
        IProductionRuntimeOwnerFactory factory,
        CancellationToken runtimeCancellationToken)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _runtimeCancellationToken = runtimeCancellationToken;
        _factory.VoiceBecameIdle += OnVoiceBecameIdle;
        _factory.UiChanged += OnUiChanged;
        _ = ObserveFactoryCompletionAsync();
    }

    public event EventHandler? ActivationBoundaryAvailable;

    public event EventHandler<RuntimeUiEvent>? UiChanged;

    public Task Completion => _completion.Task;

    public static ProductionRuntimeComposition Create(
        RuntimeInputHost inputHost,
        string companionConfigurationPath,
        bool existingCompanionInstall,
        CancellationToken runtimeCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(companionConfigurationPath);
        return new ProductionRuntimeComposition(
            new WindowsProductionRuntimeOwnerFactory(
                inputHost,
                ProductionRuntimePaths.FromCompanionConfiguration(companionConfigurationPath),
                existingCompanionInstall,
                runtimeCancellationToken),
            runtimeCancellationToken);
    }

    public bool VoiceSessionActive
    {
        get
        {
            _lifecycle.Wait();
            try
            {
                return !_disposed
                    && _owners.TryGetValue(SettingsAggregateId.Voice, out var owner)
                    && owner is IProductionVoiceOwner { IsSessionActive: true };
            }
            finally
            {
                _lifecycle.Release();
            }
        }
    }

    public RuntimeUiSnapshot GetUiSnapshot() => _factory.GetUiSnapshot();

    public RuntimeInputSource[] Refresh(SettingsBundle activeSettings)
    {
        ArgumentNullException.ThrowIfNull(activeSettings);
        _lifecycle.Wait(_runtimeCancellationToken);
        try
        {
            ThrowIfDisposed();
            _activeBundle ??= activeSettings;
            EnsureStartedAsync(activeSettings).GetAwaiter().GetResult();
            _activeBundle = activeSettings;
            return _owners.TryGetValue(SettingsAggregateId.Companion, out var owner)
                && owner is IProductionInputOwner inputs
                    ? inputs.Refresh(activeSettings)
                    : [];
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public bool ObserveForCapture(Guid captureId, string sourceId)
    {
        if (captureId == Guid.Empty)
        {
            throw new ArgumentException("A capture ID is required.", nameof(captureId));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        _lifecycle.Wait(_runtimeCancellationToken);
        try
        {
            ThrowIfDisposed();
            if (!_owners.TryGetValue(SettingsAggregateId.Companion, out var owner)
                || owner is not IProductionInputOwner inputs)
            {
                return false;
            }

            lock (_captureGate)
            {
                if (_captureObservations.ContainsKey(captureId))
                {
                    throw new InvalidOperationException(
                        $"Capture observation {captureId:D} is already registered.");
                }
                _captureObservations.Add(captureId, new CaptureObservation(sourceId, inputs));
                try
                {
                    if (inputs.ObserveForCapture(sourceId))
                    {
                        return true;
                    }
                }
                catch
                {
                    RollBackCaptureObservation(captureId, inputs);
                    throw;
                }

                RollBackCaptureObservation(captureId, inputs);
                return false;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void ReleaseCaptureObservation(Guid captureId)
    {
        lock (_captureGate)
        {
            if (!_captureObservations.Remove(captureId, out var released))
            {
                return;
            }
            if (_captureObservations.Values.Any(observation =>
                ReferenceEquals(observation.Owner, released.Owner)
                && string.Equals(
                    observation.SourceId,
                    released.SourceId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            try
            {
                released.Owner.ReleaseCaptureObservation(released.SourceId);
            }
            catch (ObjectDisposedException)
            {
                // The retiring generation already released its observer during cleanup.
            }
        }
    }

    public async Task<SettingsActivationResult> ActivateAsync(
        SettingsAggregateId aggregate,
        SettingsBundle activationCandidate,
        long desiredRevision,
        CancellationToken runtimeCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activationCandidate);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _runtimeCancellationToken,
            runtimeCancellationToken);
        await _lifecycle.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (aggregate == SettingsAggregateId.Voice
                && _activeBundle is not null
                && VoiceTargetSettings.TryApplyOnly(
                    _activeBundle,
                    activationCandidate,
                    out var targetUpdatedBundle))
            {
                _factory.RefreshVoiceMessaging(targetUpdatedBundle.Voice);
                _activeBundle = targetUpdatedBundle;
                return new SettingsActivationResult(SettingsActivationState.Applied);
            }
            if (VoiceIsActiveLocked()
                && (aggregate == SettingsAggregateId.Voice
                    || aggregate == SettingsAggregateId.Companion
                    && _activeBundle is not null
                    && VoiceBoundaryChanged(_activeBundle, activationCandidate)))
            {
                return new SettingsActivationResult(
                    SettingsActivationState.PendingIdle,
                    "The active Room Voice call must end before these settings can be activated.");
            }
            if (aggregate == SettingsAggregateId.Companion
                && _activeBundle is not null
                && VoiceBoundaryChanged(_activeBundle, activationCandidate))
            {
                return await ReplaceCompanionAndVoiceAsync(
                        activationCandidate,
                        linkedCancellation.Token)
                    .ConfigureAwait(false);
            }
            return await ReplaceAggregateAsync(
                    aggregate,
                    activationCandidate,
                    desiredRevision,
                    linkedCancellation.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task<RuntimeCommandResult> ExecuteAsync(
        RuntimeCommandRequest request,
        CancellationToken runtimeCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _runtimeCancellationToken,
            runtimeCancellationToken);
        await _lifecycle.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_activeBundle is null)
            {
                return Rejected(request, "The production runtime has not loaded active settings.");
            }

            if (request.Kind == RuntimeCommandKind.RestartRoomVoice)
            {
                var activation = await ReplaceAggregateAsync(
                        SettingsAggregateId.Voice,
                        _activeBundle,
                        desiredRevision: 0,
                        linkedCancellation.Token)
                    .ConfigureAwait(false);
                if (activation.State != SettingsActivationState.Applied)
                {
                    return new RuntimeCommandResult(
                        request.OperationId,
                        request.Kind,
                        RuntimeCommandStatus.Failed,
                        activation.Detail);
                }
            }

            if (request.Kind is RuntimeCommandKind.EndRoomVoiceSession
                or RuntimeCommandKind.RestartRoomVoice
                or RuntimeCommandKind.RefreshRoomVoiceConversation
                or RuntimeCommandKind.ReadVoiceConversationPage)
            {
                return await ExecuteVoiceCommandAsync(request, linkedCancellation.Token)
                    .ConfigureAwait(false);
            }

            if (request.Kind == RuntimeCommandKind.DismissPromptPicker)
            {
                if (_owners.TryGetValue(SettingsAggregateId.Companion, out var companion)
                    && companion is IProductionInputOwner inputs)
                {
                    inputs.DismissPromptPicker();
                }
                return new RuntimeCommandResult(
                    request.OperationId,
                    request.Kind,
                    RuntimeCommandStatus.Completed);
            }

            // RuntimeEngine intercepts settings adoption/reload and shutdown because those commands
            // require its coordinator and Program-owned lifetime, neither of which belongs here.
            if (request.Kind is RuntimeCommandKind.ReloadConfiguration
                or RuntimeCommandKind.AdoptExternalSettings
                or RuntimeCommandKind.ShutdownRuntime)
            {
                return Rejected(request, $"The {request.Kind} command must be handled by RuntimeEngine.");
            }

            return await _factory.ExecuteAsync(
                    request,
                    _activeBundle,
                    linkedCancellation.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _factory.VoiceBecameIdle -= OnVoiceBecameIdle;
            _factory.UiChanged -= OnUiChanged;
            var failures = _terminalFailures.Select(item => new InvalidOperationException(
                $"The {item.Key} owner previously failed to confirm cleanup.",
                item.Value)).Cast<Exception>().ToList();
            foreach (var aggregate in StartupOrder.Reverse())
            {
                if (!_owners.Remove(aggregate, out var owner))
                {
                    continue;
                }

                try
                {
                    RemoveCaptureObservations(owner);
                    await owner.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    MarkTerminal(aggregate, exception);
                    failures.Add(new InvalidOperationException(
                        $"The {aggregate} owner did not confirm cleanup.",
                        exception));
                }
            }

            try
            {
                await _factory.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            if (failures.Count > 0)
            {
                var failure = new AggregateException(
                    "Production runtime cleanup did not complete.",
                    failures);
                _completion.TrySetException(failure);
                throw failure;
            }

            _completion.TrySetResult();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task EnsureStartedAsync(SettingsBundle activeSettings)
    {
        foreach (var aggregate in StartupOrder)
        {
            if (_initialized.Contains(aggregate) || _terminal.Contains(aggregate))
            {
                continue;
            }

            try
            {
                var owner = await _factory.CreateAsync(
                        aggregate,
                        activeSettings,
                        _runtimeCancellationToken)
                    .ConfigureAwait(false);
                _initialized.Add(aggregate);
                if (owner is not null)
                {
                    Install(aggregate, owner);
                }
            }
            catch (OperationCanceledException) when (_runtimeCancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsTerminalStartupFailure(exception))
            {
                MarkTerminal(aggregate, exception);
                _factory.ReportFailure(aggregate, exception);
            }
            catch (Exception exception)
            {
                _factory.ReportFailure(aggregate, exception);
            }
        }
    }

    private async Task<SettingsActivationResult> ReplaceAggregateAsync(
        SettingsAggregateId aggregate,
        SettingsBundle activationCandidate,
        long desiredRevision,
        CancellationToken cancellationToken)
    {
        if (_terminal.Contains(aggregate))
        {
            return new SettingsActivationResult(
                SettingsActivationState.Failed,
                $"The {aggregate} owner is terminal because its previous generation did not confirm cleanup.");
        }

        var previousBundle = _activeBundle;
        var hadInitializedOwner = _initialized.Contains(aggregate);
        _owners.Remove(aggregate, out var previousOwner);
        _initialized.Remove(aggregate);

        if (previousOwner is not null)
        {
            RemoveCaptureObservations(previousOwner);
            try
            {
                await previousOwner.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                MarkTerminal(aggregate, exception);
                _factory.ReportFailure(aggregate, exception);
                return new SettingsActivationResult(
                    SettingsActivationState.Failed,
                    $"The prior {aggregate} generation did not confirm cleanup; no replacement was started. {exception.Message}");
            }
        }

        try
        {
            var replacement = await _factory.CreateAsync(
                    aggregate,
                    activationCandidate,
                    cancellationToken)
                .ConfigureAwait(false);
            _initialized.Add(aggregate);
            if (replacement is not null)
            {
                Install(aggregate, replacement);
            }
            _activeBundle = activationCandidate;
            return new SettingsActivationResult(SettingsActivationState.Applied);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception activationFailure) when (IsTerminalStartupFailure(activationFailure))
        {
            MarkTerminal(aggregate, activationFailure);
            _factory.ReportFailure(aggregate, activationFailure);
            return new SettingsActivationResult(
                SettingsActivationState.Failed,
                $"The {aggregate} startup did not confirm ownership cleanup; no rollback or replacement is safe in this process. "
                + activationFailure.Message);
        }
        catch (Exception activationFailure)
        {
            _factory.ReportFailure(aggregate, activationFailure);
            var rollbackFailure = await TryRestoreAsync(
                    aggregate,
                    previousBundle,
                    hadInitializedOwner,
                    cancellationToken)
                .ConfigureAwait(false);
            return new SettingsActivationResult(
                SettingsActivationState.Failed,
                rollbackFailure is null
                    ? $"The {aggregate} settings could not be activated; the prior active generation was restored. {activationFailure.Message}"
                    : $"The {aggregate} settings could not be activated and its prior generation could not be restored. "
                      + $"Activation: {activationFailure.Message} Rollback: {rollbackFailure.Message}");
        }
    }

    private async Task<SettingsActivationResult> ReplaceCompanionAndVoiceAsync(
        SettingsBundle activationCandidate,
        CancellationToken cancellationToken)
    {
        SettingsAggregateId[] stopOrder =
        [
            SettingsAggregateId.Voice,
            SettingsAggregateId.Companion,
        ];
        SettingsAggregateId[] startOrder =
        [
            SettingsAggregateId.Companion,
            SettingsAggregateId.Voice,
        ];
        foreach (var aggregate in startOrder)
        {
            if (_terminal.Contains(aggregate))
            {
                return new SettingsActivationResult(
                    SettingsActivationState.Failed,
                    $"The {aggregate} owner is terminal because cleanup was not confirmed.");
            }
        }

        var previousBundle = _activeBundle
            ?? throw new InvalidOperationException("The production runtime has no active configuration.");
        var previouslyInitialized = startOrder.ToDictionary(
            aggregate => aggregate,
            aggregate => _initialized.Contains(aggregate));
        var stopped = new List<SettingsAggregateId>();

        foreach (var aggregate in stopOrder)
        {
            _owners.Remove(aggregate, out var owner);
            _initialized.Remove(aggregate);
            if (owner is null)
            {
                stopped.Add(aggregate);
                continue;
            }

            RemoveCaptureObservations(owner);
            try
            {
                await owner.DisposeAsync().ConfigureAwait(false);
                stopped.Add(aggregate);
            }
            catch (Exception exception)
            {
                MarkTerminal(aggregate, exception);
                _factory.ReportFailure(aggregate, exception);
                var rollback = aggregate == SettingsAggregateId.Companion
                    ? Array.Empty<Exception>()
                    : await RestoreAggregatesAsync(
                            stopped.AsEnumerable().Reverse(),
                            previousBundle,
                            previouslyInitialized,
                            cancellationToken)
                        .ConfigureAwait(false);
                return new SettingsActivationResult(
                    SettingsActivationState.Failed,
                    $"The prior {aggregate} generation did not confirm cleanup; dependent activation stopped. "
                    + DescribeRollback(rollback));
            }
        }

        var started = new List<SettingsAggregateId>();
        var startingAggregate = SettingsAggregateId.Companion;
        try
        {
            foreach (var aggregate in startOrder)
            {
                startingAggregate = aggregate;
                var owner = await _factory.CreateAsync(aggregate, activationCandidate, cancellationToken)
                    .ConfigureAwait(false);
                _initialized.Add(aggregate);
                started.Add(aggregate);
                if (owner is not null)
                {
                    Install(aggregate, owner);
                }
            }

            _activeBundle = activationCandidate;
            return new SettingsActivationResult(SettingsActivationState.Applied);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception activationFailure)
        {
            _factory.ReportFailure(
                startingAggregate,
                activationFailure);
            if (IsTerminalStartupFailure(activationFailure))
            {
                MarkTerminal(startingAggregate, activationFailure);
            }

            var cleanupFailures = await CleanupStartedAggregatesAsync(started.AsEnumerable().Reverse())
                .ConfigureAwait(false);
            var rollbackFailures = _terminal.Contains(SettingsAggregateId.Companion)
                ? Array.Empty<Exception>()
                : await RestoreAggregatesAsync(
                        startOrder,
                        previousBundle,
                        previouslyInitialized,
                        cancellationToken)
                    .ConfigureAwait(false);
            return new SettingsActivationResult(
                SettingsActivationState.Failed,
                "The Companion settings and dependent Room Voice owner could not be activated. "
                + activationFailure.Message + " "
                + DescribeRollback(cleanupFailures.Concat(rollbackFailures).ToArray()));
        }
    }

    private async Task<Exception[]> CleanupStartedAggregatesAsync(
        IEnumerable<SettingsAggregateId> aggregates)
    {
        var failures = new List<Exception>();
        foreach (var aggregate in aggregates)
        {
            _initialized.Remove(aggregate);
            if (!_owners.Remove(aggregate, out var owner))
            {
                continue;
            }

            RemoveCaptureObservations(owner);
            try
            {
                await owner.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                MarkTerminal(aggregate, exception);
                _factory.ReportFailure(aggregate, exception);
                failures.Add(exception);
            }
        }
        return failures.ToArray();
    }

    private async Task<Exception[]> RestoreAggregatesAsync(
        IEnumerable<SettingsAggregateId> aggregates,
        SettingsBundle previousBundle,
        IReadOnlyDictionary<SettingsAggregateId, bool> previouslyInitialized,
        CancellationToken cancellationToken)
    {
        var failures = new List<Exception>();
        foreach (var aggregate in aggregates)
        {
            if (_terminal.Contains(aggregate)
                || !previouslyInitialized.TryGetValue(aggregate, out var hadInitialized)
                || !hadInitialized)
            {
                continue;
            }

            var failure = await TryRestoreAsync(
                    aggregate,
                    previousBundle,
                    hadInitialized,
                    cancellationToken)
                .ConfigureAwait(false);
            if (failure is not null)
            {
                failures.Add(failure);
                if (aggregate == SettingsAggregateId.Companion)
                {
                    break;
                }
            }
        }
        return failures.ToArray();
    }

    private static string DescribeRollback(IReadOnlyCollection<Exception> failures) =>
        failures.Count == 0
            ? "The prior active generations were restored."
            : "One or more prior generations could not be restored: "
              + string.Join(" | ", failures.Select(failure => failure.Message));

    private async Task<Exception?> TryRestoreAsync(
        SettingsAggregateId aggregate,
        SettingsBundle? previousBundle,
        bool hadInitializedOwner,
        CancellationToken cancellationToken)
    {
        if (!hadInitializedOwner || previousBundle is null)
        {
            return null;
        }

        try
        {
            var restored = await _factory.CreateAsync(aggregate, previousBundle, cancellationToken)
                .ConfigureAwait(false);
            _initialized.Add(aggregate);
            if (restored is not null)
            {
                Install(aggregate, restored);
            }
            return null;
        }
        catch (Exception exception)
        {
            if (IsTerminalStartupFailure(exception))
            {
                MarkTerminal(aggregate, exception);
            }
            else
            {
                _completion.TrySetException(new InvalidOperationException(
                    $"The prior {aggregate} production owner could not be restored.",
                    exception));
            }
            _factory.ReportFailure(aggregate, exception);
            return exception;
        }
    }

    private async Task<RuntimeCommandResult> ExecuteVoiceCommandAsync(
        RuntimeCommandRequest request,
        CancellationToken cancellationToken)
    {
        if (!_owners.TryGetValue(SettingsAggregateId.Voice, out var owner)
            || owner is not IProductionVoiceOwner voice)
        {
            return Rejected(request, "Room Voice is disabled or unavailable.");
        }

        if (request.Kind == RuntimeCommandKind.EndRoomVoiceSession)
        {
            await voice.EndSessionAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (request.Kind == RuntimeCommandKind.RefreshRoomVoiceConversation)
        {
            await voice.RefreshConversationAsync(cancellationToken).ConfigureAwait(false);
        }

        else if (request.Kind == RuntimeCommandKind.ReadVoiceConversationPage)
        {
            return new RuntimeCommandResult(
                request.OperationId,
                request.Kind,
                RuntimeCommandStatus.Completed,
                Payload: new RuntimeCommandPayload(
                    VoiceConversation: voice.GetConversationPage(
                        request.Arguments?.ContinuationToken)));
        }

        var state = voice.GetState();
        var voiceSnapshot = state.Snapshot with
        {
            Status = LimitUiText(state.Snapshot.Status),
            Error = state.Snapshot.Error is null ? null : LimitUiText(state.Snapshot.Error),
        };
        var timeline = state.Timeline.TakeLast(RuntimeUiLimits.MaximumVoiceTimelineEntries)
            .Select(entry =>
            {
                var text = entry.Text.Length <= RuntimeUiLimits.MaximumVoiceTimelineTextCharacters
                    ? entry.Text
                    : entry.Text[..RuntimeUiLimits.MaximumVoiceTimelineTextCharacters];
                return entry with
                {
                    Text = text,
                    TextTruncated = text.Length != entry.Text.Length,
                };
            })
            .ToArray();
        return new RuntimeCommandResult(
            request.OperationId,
            request.Kind,
            RuntimeCommandStatus.Completed,
            Payload: new RuntimeCommandPayload(
                Voice: voiceSnapshot,
                VoiceTimeline: timeline));
    }

    private void Install(SettingsAggregateId aggregate, IProductionRuntimeOwner owner)
    {
        if (owner.Aggregate != aggregate)
        {
            throw new InvalidOperationException(
                $"The production owner factory returned {owner.Aggregate} for {aggregate}.");
        }

        _owners.Add(aggregate, owner);
        _ = ObserveCompletionAsync(aggregate, owner);
    }

    private async Task ObserveCompletionAsync(
        SettingsAggregateId aggregate,
        IProductionRuntimeOwner owner)
    {
        Exception? completionFailure = null;
        try
        {
            await owner.Completion.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            completionFailure = exception;
        }

        try
        {
            await _lifecycle.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_disposed
                || !_owners.TryGetValue(aggregate, out var current)
                || !ReferenceEquals(current, owner))
            {
                return;
            }

            _owners.Remove(aggregate);
            _initialized.Remove(aggregate);
            RemoveCaptureObservations(owner);
            if (completionFailure is not null)
            {
                _factory.ReportFailure(aggregate, completionFailure);
            }

            var ownerFailure = completionFailure ?? new InvalidOperationException(
                $"The {aggregate} production owner stopped unexpectedly.");
            _completion.TrySetException(ownerFailure);

            try
            {
                await owner.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                // Voice is explicitly terminal after uncertain STA/media cleanup. Applying this
                // rule to every aggregate also prevents duplicate hardware/port ownership.
                MarkTerminal(aggregate, cleanupFailure);
                _factory.ReportFailure(aggregate, cleanupFailure);
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task ObserveFactoryCompletionAsync()
    {
        Exception? completionFailure = null;
        try
        {
            await _factory.Completion.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            completionFailure = exception;
        }

        try
        {
            await _lifecycle.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_disposed)
            {
                return;
            }

            _completion.TrySetException(completionFailure ?? new InvalidOperationException(
                "The production owner factory stopped unexpectedly."));
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private static RuntimeCommandResult Rejected(RuntimeCommandRequest request, string detail) =>
        new(request.OperationId, request.Kind, RuntimeCommandStatus.Rejected, detail);

    private static string LimitUiText(string value) =>
        value.Length <= RuntimeUiLimits.MaximumStatusCharacters
            ? value
            : value[..RuntimeUiLimits.MaximumStatusCharacters];

    private bool VoiceIsActiveLocked() =>
        _owners.TryGetValue(SettingsAggregateId.Voice, out var owner)
        && owner is IProductionVoiceOwner { IsSessionActive: true };

    private static bool VoiceBoundaryChanged(SettingsBundle active, SettingsBundle candidate)
    {
        var left = active.Companion;
        var right = candidate.Companion;
        return left.Safety.DryRun != right.Safety.DryRun
               || left.Safety.RequireCodexForeground != right.Safety.RequireCodexForeground
               || !left.Safety.CodexProcessNames.SequenceEqual(
                   right.Safety.CodexProcessNames,
                   StringComparer.OrdinalIgnoreCase)
               || !left.Safety.SimulatorProcessNames.SequenceEqual(
                   right.Safety.SimulatorProcessNames,
                   StringComparer.OrdinalIgnoreCase)
               || !string.Equals(
                   left.OpenWorkingDirectory.Target,
                   right.OpenWorkingDirectory.Target,
                   StringComparison.OrdinalIgnoreCase);
    }

    private void OnVoiceBecameIdle()
    {
        try
        {
            ActivationBoundaryAvailable?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            _factory.ReportFailure(SettingsAggregateId.Voice, exception);
        }
    }

    private void OnUiChanged(object? sender, RuntimeUiEvent update)
    {
        try
        {
            UiChanged?.Invoke(this, update);
        }
        catch (Exception exception)
        {
            _factory.ReportFailure(SettingsAggregateId.Companion, exception);
        }
    }

    private void RemoveCaptureObservations(IProductionRuntimeOwner owner)
    {
        if (owner is not IProductionInputOwner inputs)
        {
            return;
        }

        lock (_captureGate)
        {
            foreach (var observation in _captureObservations.ToArray())
            {
                if (ReferenceEquals(observation.Value.Owner, inputs))
                {
                    _captureObservations.Remove(observation.Key);
                }
            }
        }
    }

    private void RollBackCaptureObservation(Guid captureId, IProductionInputOwner owner)
    {
        lock (_captureGate)
        {
            if (!_captureObservations.TryGetValue(captureId, out var observation)
                || !ReferenceEquals(observation.Owner, owner))
            {
                return;
            }
            _captureObservations.Remove(captureId);
        }
    }

    private void MarkTerminal(SettingsAggregateId aggregate, Exception failure)
    {
        _terminal.Add(aggregate);
        _terminalFailures.TryAdd(aggregate, failure);
        _completion.TrySetException(new InvalidOperationException(
            $"The {aggregate} production owner entered a terminal state.",
            failure));
    }

    private static bool IsTerminalStartupFailure(Exception failure) =>
        failure is ProductionOwnershipCleanupException
            or VoiceOwnershipCleanupException
            or DesktopTaskBridgeOwnershipCleanupException;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record CaptureObservation(
        string SourceId,
        IProductionInputOwner Owner);
}

internal interface IProductionRuntimeOwner : IAsyncDisposable
{
    SettingsAggregateId Aggregate { get; }

    Task Completion { get; }
}

internal interface IProductionInputOwner : IProductionRuntimeOwner
{
    RuntimeInputSource[] Refresh(SettingsBundle activeSettings);

    bool ObserveForCapture(string sourceId);

    void ReleaseCaptureObservation(string sourceId);

    void DismissPromptPicker();
}

internal interface IProductionVoiceOwner : IProductionRuntimeOwner
{
    bool IsSessionActive { get; }

    Task EndSessionAsync(CancellationToken cancellationToken);

    Task RefreshConversationAsync(CancellationToken cancellationToken);

    ProductionVoiceState GetState();

    RuntimeVoiceConversationPage GetConversationPage(string? continuationToken);
}

internal sealed record ProductionVoiceState(
    RuntimeVoiceSnapshot Snapshot,
    RuntimeVoiceTimelineEntry[] Timeline);

internal interface IProductionRuntimeOwnerFactory : IAsyncDisposable
{
    event Action? VoiceBecameIdle;

    event EventHandler<RuntimeUiEvent>? UiChanged;

    Task Completion { get; }

    RuntimeUiSnapshot GetUiSnapshot();

    void RefreshVoiceMessaging(VoicePePreferences preferences);

    Task<IProductionRuntimeOwner?> CreateAsync(
        SettingsAggregateId aggregate,
        SettingsBundle activeSettings,
        CancellationToken cancellationToken);

    Task<RuntimeCommandResult> ExecuteAsync(
        RuntimeCommandRequest request,
        SettingsBundle activeSettings,
        CancellationToken cancellationToken);

    void ReportFailure(SettingsAggregateId aggregate, Exception exception);
}
