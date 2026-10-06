using Joydex.Core.Config;
using Joydex.Core.Voice;
using Joydex.Windows.Actions;
using Joydex.Windows.Voice;

namespace Joydex.App;

/// <summary>
/// Owns the dedicated App Server voice route or device control for unavailable/simulated native fallback.
/// </summary>
internal sealed class VoicePeBridgeRuntime : IAsyncDisposable
{
    private readonly VoicePeControlAdapter _adapter;
    private readonly DedicatedVoiceCoordinator? _dedicatedCoordinator;
    private readonly VoiceMediaStaHost? _mediaSta;
    private readonly VoiceRuntimeAsyncGate? _publicationGate;
    private readonly VoiceSessionArchiveState? _sessionArchiveState;
    private readonly CodexVoiceConversationReader? _conversationReader;
    private readonly RoomVoiceConversationModel _conversation;
    private readonly VoicePeSessionMode _mode;
    private readonly bool _fallbackSimulation;
    private readonly Action<string> _log;
    private readonly Task _ownerCompletion;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;

    private VoicePeBridgeRuntime(
        VoicePeControlAdapter adapter,
        DedicatedVoiceCoordinator? dedicatedCoordinator,
        VoiceMediaStaHost? mediaSta,
        VoiceRuntimeAsyncGate? publicationGate,
        VoiceSessionArchiveState? sessionArchiveState,
        CodexVoiceConversationReader? conversationReader,
        RoomVoiceConversationModel conversation,
        Action<string> log,
        VoicePeSessionMode mode,
        Task ownerCompletion,
        bool fallbackSimulation = false)
    {
        _adapter = adapter;
        _dedicatedCoordinator = dedicatedCoordinator;
        _mediaSta = mediaSta;
        _publicationGate = publicationGate;
        _sessionArchiveState = sessionArchiveState;
        _conversationReader = conversationReader;
        _conversation = conversation;
        _mode = mode;
        _fallbackSimulation = fallbackSimulation;
        _log = log;
        _ownerCompletion = ownerCompletion ?? throw new ArgumentNullException(nameof(ownerCompletion));
    }

    public VoicePeSessionMode Mode => _mode;

    public bool OwnerReady => _mode == VoicePeSessionMode.JoydexOwner
        && _mediaSta?.Completion.IsCompleted == false;

    public bool IsSessionActive => _dedicatedCoordinator?.IsSessionActive == true;

    public RoomVoiceConversationModel Conversation => _conversation;

    public Task OwnerCompletion => _ownerCompletion;

    public static async Task<VoicePeBridgeRuntime> StartAsync(
        VoicePePreferences preferences,
        SafetyOptions safety,
        PinnedVoiceCoordinator fallbackCoordinator,
        SynchronizationContext uiContext,
        string webViewDataDirectory,
        RoomVoiceConversationModel conversation,
        Action<string> log,
        string preferencesPath,
        string voiceToolHostPath,
        string desktopTaskBridgePipeName,
        CancellationToken cancellationToken = default)
    {
        // Retained temporarily for the in-process Tray caller. Voice media no longer stores or
        // dispatches through this context; RuntimeHost composition uses the context-free overload.
        ArgumentNullException.ThrowIfNull(uiContext);
        return await StartAsync(
                preferences,
                safety,
                fallbackCoordinator,
                webViewDataDirectory,
                conversation,
                log,
                preferencesPath,
                voiceToolHostPath,
                desktopTaskBridgePipeName,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<VoicePeBridgeRuntime> StartAsync(
        VoicePePreferences preferences,
        SafetyOptions safety,
        PinnedVoiceCoordinator fallbackCoordinator,
        string webViewDataDirectory,
        RoomVoiceConversationModel conversation,
        Action<string> log,
        string preferencesPath,
        string voiceToolHostPath,
        string desktopTaskBridgePipeName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(safety);
        ArgumentNullException.ThrowIfNull(fallbackCoordinator);
        ArgumentException.ThrowIfNullOrWhiteSpace(webViewDataDirectory);
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(log);
        var runtimeLog = CreateBestEffortLog(log);

        var normalized = preferences.Normalize();
        var errors = normalized.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidDataException(
                "The Voice PE settings are invalid:" + Environment.NewLine + "- "
                + string.Join(Environment.NewLine + "- ", errors));
        }

        if (!normalized.Enabled)
        {
            throw new InvalidOperationException("The Voice PE bridge is disabled.");
        }

        if (!VoicePeEndpoint.TryParse(normalized.DeviceEndpoint, out var endpoint))
        {
            throw new InvalidDataException("The enabled Voice PE bridge has no valid endpoint.");
        }

        return normalized.SessionMode switch
        {
            VoicePeSessionMode.LastVoiceFallback => StartFallback(
                normalized,
                fallbackCoordinator,
                endpoint,
                conversation,
                runtimeLog,
                simulate: safety.DryRun),
            VoicePeSessionMode.JoydexOwner => await StartOwnerAsync(
                    normalized,
                    safety,
                    fallbackCoordinator,
                    endpoint,
                    webViewDataDirectory,
                    conversation,
                    runtimeLog,
                    preferencesPath,
                    voiceToolHostPath,
                    desktopTaskBridgePipeName,
                    cancellationToken)
                .ConfigureAwait(false),
            _ => throw new InvalidDataException($"Unsupported Voice PE session mode {normalized.SessionMode}."),
        };
    }

    internal static Action<string> CreateBestEffortLog(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        return message =>
        {
            try
            {
                log(message);
            }
            catch
            {
                // Diagnostics must not interrupt Voice ownership or cleanup.
            }
        };
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        List<Exception>? ownershipFailures = null;

        if (_publicationGate is not null)
        {
            try
            {
                await _publicationGate.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                TryLog($"Could not quiesce Voice runtime publications: {exception.Message}");
                (ownershipFailures ??= []).Add(exception);
            }
        }

        if (_dedicatedCoordinator is not null)
        {
            try
            {
                // Quiesce endpoint signal intake before canceling the coordinator. Otherwise a
                // fresh wake can race ordered media disposal and construct work against an owner
                // generation that is already shutting down.
                await _adapter.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                TryLog($"Could not stop the Voice PE control adapter: {exception.Message}");
                (ownershipFailures ??= []).Add(exception);
            }

            try
            {
                await _dedicatedCoordinator.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                TryLog($"Could not stop the dedicated Voice Session coordinator: {exception.Message}");
                (ownershipFailures ??= []).Add(exception);
            }
        }

        try
        {
            _sessionArchiveState?.Complete("stopped", "Joydex stopped Room Voice.");
        }
        catch (Exception exception)
        {
            TryLog($"Could not complete the active Voice Session archive: {exception.Message}");
        }

        if (_dedicatedCoordinator is null)
        {
            try
            {
                await _adapter.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                TryLog($"Could not stop the Voice PE control adapter: {exception.Message}");
            }
        }

        if (_mediaSta is not null)
        {
            try
            {
                // Media sessions are disposed by the coordinator above. The pump is stopped last
                // so every WebView2 unsubscribe and COM disposal can execute on its owning STA.
                await _mediaSta.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                TryLog($"Could not stop the Voice media STA: {exception.Message}");
                (ownershipFailures ??= []).Add(exception);
            }
        }

        GC.SuppressFinalize(this);
        if (ownershipFailures is not null)
        {
            throw new VoiceOwnershipCleanupException(
                "Room Voice ownership could not be released completely; replacement is unsafe.",
                ownershipFailures);
        }
    }

    private void TryLog(string message)
    {
        try
        {
            _log(message);
        }
        catch
        {
            // A failing diagnostic sink cannot interrupt ownership cleanup.
        }
    }

    public Task StopSessionAsync(CancellationToken cancellationToken = default) =>
        _dedicatedCoordinator?.StopActiveAsync(cancellationToken) ?? Task.CompletedTask;

    public async Task RefreshConversationAsync(CancellationToken cancellationToken = default)
    {
        if (_conversationReader is null)
        {
            _conversation.SetFallbackState(enabled: true, dryRun: _fallbackSimulation);
            return;
        }

        var entries = await _conversationReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        _conversation.ReplaceHistory(entries);
    }

    private static VoicePeBridgeRuntime StartFallback(
        VoicePePreferences preferences,
        PinnedVoiceCoordinator coordinator,
        Uri endpoint,
        RoomVoiceConversationModel conversation,
        Action<string> log,
        bool simulate)
    {
        var transport = new EspHomeVoicePeTransport(endpoint, log);
        var adapter = new VoicePeControlAdapter(
            transport,
            async cancellationToken => MapFallbackResult(
                await coordinator.StartAsync(preferences, cancellationToken).ConfigureAwait(false)),
            log);
        adapter.Start();
        conversation.SetFallbackState(enabled: true, dryRun: simulate);
        log(simulate
            ? $"Voice PE dry-run simulation started for {endpoint.Host}:{endpoint.Port}."
            : PinnedVoiceCoordinator.UnavailableMessage);
        return new VoicePeBridgeRuntime(
            adapter,
            null,
            null,
            null,
            null,
            null,
            conversation,
            log,
            VoicePeSessionMode.LastVoiceFallback,
            Task.CompletedTask,
            fallbackSimulation: simulate);
    }

    private static async Task<VoicePeBridgeRuntime> StartOwnerAsync(
        VoicePePreferences preferences,
        SafetyOptions safety,
        PinnedVoiceCoordinator fallbackCoordinator,
        Uri endpoint,
        string webViewDataDirectory,
        RoomVoiceConversationModel conversation,
        Action<string> log,
        string preferencesPath,
        string voiceToolHostPath,
        string desktopTaskBridgePipeName,
        CancellationToken cancellationToken)
    {
        if (safety.DryRun)
        {
            // Dry run retains the existing native coordinator's simulation path and avoids taking a writer lock.
            return StartFallback(
                preferences with { SessionMode = VoicePeSessionMode.LastVoiceFallback },
                fallbackCoordinator,
                endpoint,
                conversation,
                log,
                simulate: true);
        }

        using (var device = new EspHomeVoicePeTuningClient(endpoint))
        {
            if (!await device.GetBargeInAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "Joydex Audio Barge In is off on the Voice PE. Turn it on in the device web UI before starting Room Voice.");
            }
        }

        var voiceTools = preferences.DesktopTaskMessagingEnabled
            ? new CodexVoiceToolConfiguration(
                voiceToolHostPath,
                preferencesPath,
                preferences.DedicatedTaskId,
                desktopTaskBridgePipeName)
            : null;
        var conversationReader = new CodexVoiceConversationReader(
            preferences.DedicatedTaskId,
            preferences.CodexAppServerPath,
            preferences.AgentWorkspacePath,
            log);
        var desktopTaskBridge = new DesktopTaskBridgeClient(desktopTaskBridgePipeName);
        var sessionArchiveState = string.IsNullOrWhiteSpace(preferences.AgentWorkspacePath)
            ? null
            : new VoiceSessionArchiveState(preferences, log);
        VoiceMediaStaHost? mediaSta = null;
        VoiceRuntimeAsyncGate? publicationGate = null;
        VoicePeControlAdapter? adapter = null;
        DedicatedVoiceCoordinator? coordinator = null;
        IVoicePeControlTransport? unownedControlTransport = null;
        var ownershipCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            mediaSta = await VoiceMediaStaHost.StartAsync(cancellationToken).ConfigureAwait(false);
            publicationGate = new VoiceRuntimeAsyncGate();
            conversation.SetRuntimeState(
                VoicePeSessionState.Armed,
                ownerReady: true,
                sessionActive: false,
                "Room Voice is armed; the Codex chat is available in Desktop.");
            try
            {
                conversation.ReplaceHistory(await conversationReader.ReadAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception)
            {
                conversation.SetRuntimeState(
                    VoicePeSessionState.Armed,
                    ownerReady: true,
                    sessionActive: false,
                    "Room Voice is armed; the Codex chat is available in Desktop, but its history could not be loaded.",
                    exception.Message);
            }
            coordinator = new DedicatedVoiceCoordinator(
                mediaSessionFactory: async token =>
                {
                    var archive = sessionArchiveState?.Current;
                    CodexDedicatedVoiceOwner CreateOwner() => new(
                        preferences.DedicatedTaskId,
                        preferences.CodexAppServerPath,
                        preferences.AgentWorkspacePath,
                        preferences.RealtimeVoice,
                        log,
                        voiceTools);

                    CodexDedicatedVoiceOwner? owner = null;
                    OwnedVoiceDuplexAudioSession? ownedMedia = null;
                    try
                    {
                        owner = await AcquireWithIdleDesktopHandoffAsync(
                                async acquireToken =>
                                {
                                    var candidate = CreateOwner();
                                    try
                                    {
                                        await candidate.StartAsync(acquireToken).ConfigureAwait(false);
                                        return candidate;
                                    }
                                    catch
                                    {
                                        await candidate.DisposeAsync().ConfigureAwait(false);
                                        throw;
                                    }
                                },
                                async releaseToken =>
                                {
                                    var desktopTask = await desktopTaskBridge.ResolveTaskAsync(
                                            preferences.DedicatedTaskId,
                                            preferences.DedicatedTaskId,
                                            "local",
                                            releaseToken)
                                        .ConfigureAwait(false);
                                    if (!desktopTask.Status.Equals("idle", StringComparison.OrdinalIgnoreCase))
                                    {
                                        throw new InvalidOperationException(
                                            $"Codex reports the Room Voice chat as {desktopTask.Status}, so Joydex will not release it.");
                                    }

                                    await desktopTaskBridge.ReleaseTaskAsync(
                                            preferences.DedicatedTaskId,
                                            desktopTask,
                                            releaseToken)
                                        .ConfigureAwait(false);
                                    log(
                                        $"Joydex released Codex Desktop's idle writer for Dedicated Voice Task "
                                        + $"{preferences.DedicatedTaskId} and restored the chat to the task list.");
                                },
                                token)
                            .ConfigureAwait(false);
                        conversation.ReplaceHistory(await owner.ReadThreadAsync(token).ConfigureAwait(false));
                        var realtime = owner.CreateRealtimeSession();
                        var media = new WebView2VoiceDuplexAudioSession(
                            mediaSta,
                            webViewDataDirectory,
                            realtime.StartAsync,
                            realtime.MarkMediaConnected,
                            realtime.Ready,
                            realtime.Completion,
                            realtime.StopAsync,
                            realtime.DisposeAsync,
                            preferences.ConversationSpeakerGain,
                            preferences.PreserveAssistantAudioDiagnostics,
                            (kind, text, final) =>
                            {
                                conversation.UpdateLiveTranscript(kind, text, final);
                                archive?.UpdateTranscript(kind, text, final);
                            },
                            conversation.AddActivity,
                            log,
                            archive?.AudioDirectory);
                        ownedMedia = new OwnedVoiceDuplexAudioSession(
                            media,
                            owner,
                            failure => ownershipCompletion.TrySetException(failure));
                        await media.StartAsync(token).ConfigureAwait(false);
                        log($"Joydex acquired Dedicated Voice Task {preferences.DedicatedTaskId} for this Voice Session.");
                        return ownedMedia;
                    }
                    catch
                    {
                        if (ownedMedia is not null)
                        {
                            await ownedMedia.DisposeAsync().ConfigureAwait(false);
                        }
                        else
                        {
                            try
                            {
                                if (owner is not null)
                                {
                                    await owner.DisposeAsync().ConfigureAwait(false);
                                }
                            }
                            catch (Exception cleanupFailure)
                            {
                                var ownershipFailure = new VoiceOwnershipCleanupException(
                                    "Room Voice startup failed, and Joydex could not confirm release of its Codex task writer.",
                                    [cleanupFailure]);
                                ownershipCompletion.TrySetException(ownershipFailure);
                                throw ownershipFailure;
                            }
                        }
                        throw;
                    }
                },
                deviceTransportFactory: () => new RecoveringVoicePeDuplexAudioTransport(
                    () => new VoicePeSendspinAudioTransport(endpoint, log),
                    log),
                log,
                sessionListening: async token =>
                {
                    await (adapter
                            ?? throw new InvalidOperationException("The Voice PE control adapter is not ready."))
                        .ConfirmSessionStartedAsync(token)
                        .ConfigureAwait(false);
                    sessionArchiveState?.MarkConnected();
                    conversation.SetRuntimeState(
                        VoicePeSessionState.Listening,
                        ownerReady: true,
                        sessionActive: true,
                        "Listening");
                });

            unownedControlTransport = new EspHomeVoicePeTransport(endpoint, log);
            adapter = new VoicePeControlAdapter(
                unownedControlTransport,
                async token =>
                {
                    var previousState = conversation.GetSnapshot();
                    var archiveCreated = false;
                    var archive = sessionArchiveState?.Begin(out archiveCreated);
                    conversation.SetRuntimeState(
                        VoicePeSessionState.Starting,
                        ownerReady: true,
                        sessionActive: true,
                        "Connecting the voice session…");
                    var result = await coordinator.StartAsync(token).ConfigureAwait(false);
                    if (!result.Accepted && result.Status != VoiceSessionStartStatus.SessionActive)
                    {
                        if (archiveCreated)
                        {
                            sessionArchiveState?.CompleteIfCurrent(
                                archive,
                                result.Status switch
                                {
                                    VoiceSessionStartStatus.Canceled => "canceled",
                                    VoiceSessionStartStatus.OwnershipConflict => "ownership-conflict",
                                    _ => "rejected",
                                },
                                result.Message);
                        }
                    }
                    ApplyStartResult(conversation, previousState, result);
                    return result;
                },
                log,
                startVoiceSetsListeningState: true,
                stopVoice: coordinator.StopActiveAsync,
                toggleMicrophoneMute: () =>
                {
                    var muted = coordinator.ToggleMicrophoneMute();
                    if (muted.HasValue)
                    {
                        conversation.SetRuntimeState(
                            muted.Value ? VoicePeSessionState.Muted : VoicePeSessionState.Listening,
                            ownerReady: true,
                            sessionActive: true,
                            muted.Value ? "Microphone muted" : "Listening");
                    }

                    return muted;
                });
            // VoicePeControlAdapter owns the transport from this point onward.
            unownedControlTransport = null;
            coordinator.SessionEnded += () =>
            {
                sessionArchiveState?.Complete("ended");
                publicationGate.TryRun(token =>
                    RearmAfterOwnerSessionAsync(
                        adapter,
                        conversationReader,
                        conversation,
                        publicationGate,
                        log,
                        token));
            };
            // A process crash can close media without publishing the final Armed state.
            // Runtime startup has no live Voice Session, so restore the wakeable baseline.
            await adapter.ConfirmSessionEndedAsync(cancellationToken).ConfigureAwait(false);
            adapter.Start();

            log(
                $"Joydex will acquire Dedicated Voice Task {preferences.DedicatedTaskId} only during a Voice Session; "
                + $"Voice PE control={endpoint.Host}:{endpoint.Port}, "
                + $"microphone={endpoint.Host}:8765, speaker={endpoint.Host}:8927/Sendspin; "
                + $"voice={(preferences.RealtimeVoice.Length == 0 ? "default" : preferences.RealtimeVoice)}, "
                + $"speakerGain={preferences.ConversationSpeakerGain}x.");
            return new VoicePeBridgeRuntime(
                adapter,
                coordinator,
                mediaSta,
                publicationGate,
                sessionArchiveState,
                conversationReader,
                conversation,
                log,
                VoicePeSessionMode.JoydexOwner,
                ObserveFirstCompletionAsync(ownershipCompletion.Task, mediaSta.Completion));
        }
        catch (Exception startupException)
        {
            var cleanupFailures = await VoiceRuntimeStartupRollback.DisposeAsync(
                    publicationGate,
                    (IAsyncDisposable?)adapter ?? unownedControlTransport,
                    coordinator,
                    mediaSta)
                .ConfigureAwait(false);

            if (cleanupFailures.Count > 0)
            {
                throw VoiceRuntimeStartupRollback.ClassifyStartupFailure(
                    startupException,
                    cleanupFailures);
            }

            throw;
        }
    }

    private static async Task RearmAfterOwnerSessionAsync(
        VoicePeControlAdapter adapter,
        CodexVoiceConversationReader conversationReader,
        RoomVoiceConversationModel conversation,
        VoiceRuntimeAsyncGate publicationGate,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        try
        {
            await adapter.ConfirmSessionEndedAsync(cancellationToken).ConfigureAwait(false);
            if (!publicationGate.TryPublish(
                    cancellationToken,
                    () => conversation.SetRuntimeState(
                        VoicePeSessionState.Armed,
                        ownerReady: true,
                        sessionActive: false,
                        "Room Voice is armed; the Codex chat is available in Desktop.",
                        stale: true)))
            {
                return;
            }
            try
            {
                var entries = await conversationReader.ReadAsync(cancellationToken).ConfigureAwait(false);
                publicationGate.TryPublish(
                    cancellationToken,
                    () => conversation.ReplaceHistory(entries));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                log($"Room Voice conversation refresh failed: {exception.Message}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            log($"Voice PE could not rearm after the dedicated Voice Session: {exception.Message}");
        }
    }

    private static VoiceSessionStartResult MapFallbackResult(PinnedVoiceStartResult result) =>
        new(
            result.Status switch
            {
                PinnedVoiceStartStatus.Requested => VoiceSessionStartStatus.Requested,
                PinnedVoiceStartStatus.Simulated => VoiceSessionStartStatus.Simulated,
                PinnedVoiceStartStatus.Busy or PinnedVoiceStartStatus.SessionActive =>
                    VoiceSessionStartStatus.SessionActive,
                _ => VoiceSessionStartStatus.Rejected,
            },
            result.Message);

    private static async Task ObserveFirstCompletionAsync(Task ownershipCompletion, Task mediaCompletion)
    {
        var completed = await Task.WhenAny(ownershipCompletion, mediaCompletion).ConfigureAwait(false);
        await completed.ConfigureAwait(false);
    }

    internal static void ApplyStartResult(
        RoomVoiceConversationModel conversation,
        RoomVoiceConversationSnapshot previousState,
        VoiceSessionStartResult result)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(previousState);
        ArgumentNullException.ThrowIfNull(result);

        if (result.Status == VoiceSessionStartStatus.SessionActive)
        {
            if (previousState.SessionActive)
            {
                conversation.SetRuntimeState(
                    previousState.SessionState,
                    previousState.OwnerReady,
                    sessionActive: true,
                    previousState.Status,
                    previousState.Error,
                    previousState.Stale);
            }
            return;
        }

        if (result.Status == VoiceSessionStartStatus.Canceled)
        {
            conversation.SetRuntimeState(
                VoicePeSessionState.Armed,
                ownerReady: true,
                sessionActive: false,
                "Room Voice is armed; the Codex chat is available in Desktop.");
            return;
        }

        if (result.Status == VoiceSessionStartStatus.OwnershipConflict)
        {
            conversation.SetRuntimeState(
                VoicePeSessionState.Armed,
                ownerReady: true,
                sessionActive: false,
                "Codex kept the voice chat loaded and the automatic handoff failed. Restart Codex, then wake Room Voice again.");
            return;
        }

        if (!result.Accepted)
        {
            conversation.SetRuntimeState(
                VoicePeSessionState.Error,
                ownerReady: true,
                sessionActive: false,
                "The voice session could not start.",
                result.Message);
        }
    }

    internal static async Task<T> AcquireWithIdleDesktopHandoffAsync<T>(
        Func<CancellationToken, Task<T>> acquire,
        Func<CancellationToken, Task> releaseIdleDesktopTask,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acquire);
        ArgumentNullException.ThrowIfNull(releaseIdleDesktopTask);
        try
        {
            return await acquire(cancellationToken).ConfigureAwait(false);
        }
        catch (CodexDedicatedVoiceOwnershipException ownershipConflict)
        {
            try
            {
                await releaseIdleDesktopTask(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception releaseFailure)
            {
                throw new CodexDedicatedVoiceOwnershipException(
                    "Codex Desktop still owns the Dedicated Voice Task, and Joydex could not complete the idle-chat handoff.",
                    new AggregateException(ownershipConflict, releaseFailure));
            }

            return await acquire(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class VoiceSessionArchiveState(VoicePePreferences preferences, Action<string> log)
    {
        private readonly object _gate = new();
        private VoiceSessionArchive? _current;

        public VoiceSessionArchive? Current
        {
            get
            {
                lock (_gate)
                {
                    return _current;
                }
            }
        }

        public VoiceSessionArchive Begin(out bool created)
        {
            lock (_gate)
            {
                if (_current is not null)
                {
                    created = false;
                    return _current;
                }

                created = true;
                _current = VoiceSessionArchive.Create(
                    preferences.AgentWorkspacePath,
                    preferences.DedicatedTaskId,
                    preferences.AgentProjectId,
                    preferences.AgentProjectLabel,
                    log);
                UpdateActiveSession(_current.SessionId);
                return _current;
            }
        }

        public void MarkConnected() => Current?.MarkConnected();

        public void Complete(string outcome, string? reason = null)
        {
            VoiceSessionArchive? archive;
            lock (_gate)
            {
                archive = _current;
                _current = null;
            }

            archive?.Complete(outcome, reason);
            if (archive is not null)
            {
                UpdateActiveSession(null);
            }
        }

        public void CompleteIfCurrent(
            VoiceSessionArchive? expected,
            string outcome,
            string? reason = null)
        {
            if (expected is null)
            {
                return;
            }

            VoiceSessionArchive? archive = null;
            lock (_gate)
            {
                if (ReferenceEquals(_current, expected))
                {
                    archive = _current;
                    _current = null;
                }
            }

            archive?.Complete(outcome, reason);
            if (archive is not null)
            {
                UpdateActiveSession(null);
            }
        }

        private void UpdateActiveSession(string? sessionId)
        {
            try
            {
                var directory = Path.Combine(preferences.AgentWorkspacePath, ".joydex");
                var path = Path.Combine(directory, "active-voice-session.txt");
                if (sessionId is null)
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                    return;
                }

                Directory.CreateDirectory(directory);
                var temporary = Path.Combine(directory, $".active-voice-session.{Guid.NewGuid():N}.tmp");
                File.WriteAllText(temporary, sessionId + Environment.NewLine);
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or PathTooLongException)
            {
                log($"Could not update the active Voice Session pointer: {exception.Message}");
            }
        }
    }
}

/// <summary>
/// Couples one Realtime media session to the private App Server that owns its Codex task.
/// Disposing the media boundary always releases the task writer.
/// </summary>
internal sealed class OwnedVoiceDuplexAudioSession : IVoiceDuplexAudioSession
{
    private readonly IVoiceDuplexAudioSession _media;
    private readonly IAsyncDisposable _owner;
    private readonly Action<VoiceOwnershipCleanupException>? _ownershipCleanupFailed;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;

    public OwnedVoiceDuplexAudioSession(
        IVoiceDuplexAudioSession media,
        IAsyncDisposable owner,
        Action<VoiceOwnershipCleanupException>? ownershipCleanupFailed = null)
    {
        _media = media ?? throw new ArgumentNullException(nameof(media));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _ownershipCleanupFailed = ownershipCleanupFailed;
    }

    public VoicePcmFormat MicrophoneInputFormat => _media.MicrophoneInputFormat;

    public VoicePcmFormat SpeakerOutputFormat => _media.SpeakerOutputFormat;

    public Task Completion => _media.Completion;

    public ValueTask SendMicrophoneFrameAsync(
        VoicePcmFrame frame,
        CancellationToken cancellationToken = default) =>
        _media.SendMicrophoneFrameAsync(frame, cancellationToken);

    public IAsyncEnumerable<VoiceSpeakerOutput> ReadSpeakerOutputAsync(
        CancellationToken cancellationToken = default) =>
        _media.ReadSpeakerOutputAsync(cancellationToken);

    public ValueTask StopAsync(CancellationToken cancellationToken = default) =>
        _media.StopAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        Exception? mediaFailure = null;
        try
        {
            await _media.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            mediaFailure = exception;
        }

        try
        {
            await _owner.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ownerFailure)
        {
            var ownershipFailure = new VoiceOwnershipCleanupException(
                "The Voice Session ended, but Joydex could not confirm release of its Codex task writer.",
                mediaFailure is null ? [ownerFailure] : [mediaFailure, ownerFailure]);
            _ownershipCleanupFailed?.Invoke(ownershipFailure);
            throw ownershipFailure;
        }

        if (mediaFailure is not null)
        {
            throw mediaFailure;
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Cancels and drains generation-scoped asynchronous publications before a Voice runtime is
/// disposed, preventing an old session tail from updating the next runtime's shared model.
/// </summary>
internal sealed class VoiceRuntimeAsyncGate : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _tasks = [];
    private Task? _disposeTask;
    private bool _stopping;

    public bool TryRun(Func<CancellationToken, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            if (_stopping)
            {
                return false;
            }

            var task = RunAsync(action, _lifetime.Token);
            _tasks.Add(task);
            _ = RemoveWhenCompleteAsync(task);
            return true;
        }
    }

    public bool TryPublish(CancellationToken cancellationToken, Action publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        lock (_gate)
        {
            if (_stopping || cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            publication();
            return true;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        Task[] tasks;
        lock (_gate)
        {
            _stopping = true;
            tasks = [.. _tasks];
        }
        _lifetime.Cancel();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _lifetime.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private async Task RemoveWhenCompleteAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // The owner callback records its own failures. This observer only retires the task.
        }
        finally
        {
            lock (_gate)
            {
                _tasks.Remove(task);
            }
        }
    }

    private static async Task RunAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken) =>
        await action(cancellationToken).ConfigureAwait(false);
}

internal static class VoiceRuntimeStartupRollback
{
    public static Exception ClassifyStartupFailure(
        Exception startupFailure,
        IReadOnlyCollection<Exception> cleanupFailures)
    {
        ArgumentNullException.ThrowIfNull(startupFailure);
        ArgumentNullException.ThrowIfNull(cleanupFailures);
        return cleanupFailures.Count == 0
            ? startupFailure
            : new VoiceOwnershipCleanupException(
                "Room Voice startup failed and its ownership cleanup was incomplete.",
                [startupFailure, .. cleanupFailures]);
    }

    public static async Task<List<Exception>> DisposeAsync(params IAsyncDisposable?[] resources)
    {
        var failures = new List<Exception>();
        foreach (var resource in resources)
        {
            if (resource is null)
            {
                continue;
            }

            try
            {
                await resource.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        return failures;
    }
}
