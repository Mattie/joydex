using Joydex.Core.Config;
using Joydex.Core.Voice;
using Joydex.Windows.Actions;
using Joydex.Windows.Voice;

namespace Joydex.App;

/// <summary>
/// Owns either the dedicated App Server voice route or the explicit native LASTVOICE fallback.
/// </summary>
internal sealed class VoicePeBridgeRuntime : IAsyncDisposable
{
    private readonly VoicePeControlAdapter _adapter;
    private readonly CodexVoiceSessionObserver? _fallbackObserver;
    private readonly DedicatedVoiceCoordinator? _dedicatedCoordinator;
    private readonly CodexDedicatedVoiceOwner? _owner;
    private readonly VoiceSessionArchiveState? _sessionArchiveState;
    private readonly RoomVoiceConversationModel _conversation;
    private readonly Action<string> _log;
    private int _disposed;

    private VoicePeBridgeRuntime(
        VoicePeControlAdapter adapter,
        CodexVoiceSessionObserver? fallbackObserver,
        DedicatedVoiceCoordinator? dedicatedCoordinator,
        CodexDedicatedVoiceOwner? owner,
        VoiceSessionArchiveState? sessionArchiveState,
        RoomVoiceConversationModel conversation,
        Action<string> log)
    {
        _adapter = adapter;
        _fallbackObserver = fallbackObserver;
        _dedicatedCoordinator = dedicatedCoordinator;
        _owner = owner;
        _sessionArchiveState = sessionArchiveState;
        _conversation = conversation;
        _log = log;
    }

    public VoicePeSessionMode Mode => _owner is null
        ? VoicePeSessionMode.LastVoiceFallback
        : VoicePeSessionMode.JoydexOwner;

    public bool OwnerReady => _owner?.IsReady == true;

    public bool IsSessionActive => _dedicatedCoordinator?.IsSessionActive == true;

    public RoomVoiceConversationModel Conversation => _conversation;

    public Task OwnerCompletion => _owner?.Completion ?? Task.CompletedTask;

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
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(safety);
        ArgumentNullException.ThrowIfNull(fallbackCoordinator);
        ArgumentNullException.ThrowIfNull(uiContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(webViewDataDirectory);
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(log);

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
                log),
            VoicePeSessionMode.JoydexOwner => await StartOwnerAsync(
                    normalized,
                    safety,
                    fallbackCoordinator,
                    endpoint,
                    uiContext,
                    webViewDataDirectory,
                    conversation,
                    log,
                    preferencesPath,
                    voiceToolHostPath,
                    desktopTaskBridgePipeName,
                    cancellationToken)
                .ConfigureAwait(false),
            _ => throw new InvalidDataException($"Unsupported Voice PE session mode {normalized.SessionMode}."),
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_dedicatedCoordinator is not null)
        {
            try
            {
                await _dedicatedCoordinator.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _log($"Could not stop the dedicated Voice Session coordinator: {exception.Message}");
            }
        }

        _sessionArchiveState?.Complete("stopped", "Joydex stopped Room Voice.");

        if (_fallbackObserver is not null)
        {
            try
            {
                await _fallbackObserver.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _log($"Could not stop the Codex Voice session observer: {exception.Message}");
            }
        }

        try
        {
            await _adapter.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log($"Could not stop the Voice PE control adapter: {exception.Message}");
        }

        if (_owner is not null)
        {
            try
            {
                await _owner.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _log($"Could not release the Dedicated Voice Task owner: {exception.Message}");
            }
        }

        GC.SuppressFinalize(this);
    }

    public Task StopSessionAsync(CancellationToken cancellationToken = default) =>
        _dedicatedCoordinator?.StopActiveAsync(cancellationToken) ?? Task.CompletedTask;

    public async Task RefreshConversationAsync(CancellationToken cancellationToken = default)
    {
        if (_owner is null)
        {
            _conversation.SetFallbackState(enabled: true);
            return;
        }

        var entries = await _owner.ReadThreadAsync(cancellationToken).ConfigureAwait(false);
        _conversation.ReplaceHistory(entries);
    }

    private static VoicePeBridgeRuntime StartFallback(
        VoicePePreferences preferences,
        PinnedVoiceCoordinator coordinator,
        Uri endpoint,
        RoomVoiceConversationModel conversation,
        Action<string> log)
    {
        var transport = new EspHomeVoicePeTransport(endpoint, log);
        var adapter = new VoicePeControlAdapter(
            transport,
            async cancellationToken => MapFallbackResult(
                await coordinator.StartAsync(preferences, cancellationToken).ConfigureAwait(false)),
            log);
        adapter.Start();
        conversation.SetFallbackState(enabled: true);

        CodexVoiceSessionObserver? observer = null;
        var logRoot = CodexVoiceSessionObserver.FindDefaultLogRoot();
        if (logRoot is null)
        {
            log("Codex Voice log directory was not found; native LASTVOICE will use the bounded start-confirmation timeout.");
        }
        else
        {
            observer = new CodexVoiceSessionObserver(
                logRoot,
                async (marker, cancellationToken) =>
                {
                    if (marker == CodexVoiceLogMarker.Stopped && coordinator.ConfirmSessionEnded())
                    {
                        await adapter.ConfirmSessionEndedAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else if (marker == CodexVoiceLogMarker.Started && coordinator.ConfirmSessionStarted())
                    {
                        await adapter.ConfirmSessionStartedAsync(cancellationToken).ConfigureAwait(false);
                    }
                },
                log);
            observer.Start();
            log("Codex Voice session observer started at end-of-log for native LASTVOICE.");
        }

        log($"Voice PE native LASTVOICE fallback started for {endpoint.Host}:{endpoint.Port}.");
        return new VoicePeBridgeRuntime(adapter, observer, null, null, null, conversation, log);
    }

    private static async Task<VoicePeBridgeRuntime> StartOwnerAsync(
        VoicePePreferences preferences,
        SafetyOptions safety,
        PinnedVoiceCoordinator fallbackCoordinator,
        Uri endpoint,
        SynchronizationContext uiContext,
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
                log);
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
        var owner = new CodexDedicatedVoiceOwner(
            preferences.DedicatedTaskId,
            preferences.CodexAppServerPath,
            preferences.AgentWorkspacePath,
            preferences.RealtimeVoice,
            log,
            voiceTools);
        var sessionArchiveState = string.IsNullOrWhiteSpace(preferences.AgentWorkspacePath)
            ? null
            : new VoiceSessionArchiveState(preferences, log);
        try
        {
            await owner.StartAsync(cancellationToken).ConfigureAwait(false);
            conversation.SetRuntimeState(
                VoicePeSessionState.Armed,
                ownerReady: true,
                sessionActive: false,
                "Room Voice is armed.");
            try
            {
                conversation.ReplaceHistory(await owner.ReadThreadAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception)
            {
                conversation.SetRuntimeState(
                    VoicePeSessionState.Armed,
                    ownerReady: true,
                    sessionActive: false,
                    "Room Voice is armed; conversation history could not be loaded.",
                    exception.Message);
            }
            VoicePeControlAdapter? adapter = null;
            var coordinator = new DedicatedVoiceCoordinator(
                mediaSessionFactory: async token =>
                {
                    var archive = sessionArchiveState?.Current;
                    var realtime = owner.CreateRealtimeSession();
                    var media = new WebView2VoiceDuplexAudioSession(
                        uiContext,
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
                    try
                    {
                        await media.StartAsync(token).ConfigureAwait(false);
                        return media;
                    }
                    catch
                    {
                        await media.DisposeAsync().ConfigureAwait(false);
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

            var controlTransport = new EspHomeVoicePeTransport(endpoint, log);
            adapter = new VoicePeControlAdapter(
                controlTransport,
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
                            sessionArchiveState?.CompleteIfCurrent(archive, "rejected", result.Message);
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
            coordinator.SessionEnded += () =>
            {
                sessionArchiveState?.Complete("ended");
                _ = RearmAfterOwnerSessionAsync(adapter, owner, conversation, log);
            };
            // A process crash can close media without publishing the final Armed state.
            // Owner startup has no live Voice Session, so restore the wakeable baseline.
            await adapter.ConfirmSessionEndedAsync(cancellationToken).ConfigureAwait(false);
            adapter.Start();

            log(
                $"Joydex owns Dedicated Voice Task {preferences.DedicatedTaskId}; "
                + $"Voice PE control={endpoint.Host}:{endpoint.Port}, "
                + $"microphone={endpoint.Host}:8765, speaker={endpoint.Host}:8927/Sendspin; "
                + $"voice={(preferences.RealtimeVoice.Length == 0 ? "default" : preferences.RealtimeVoice)}, "
                + $"speakerGain={preferences.ConversationSpeakerGain}x.");
            return new VoicePeBridgeRuntime(
                adapter,
                null,
                coordinator,
                owner,
                sessionArchiveState,
                conversation,
                log);
        }
        catch
        {
            await owner.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task RearmAfterOwnerSessionAsync(
        VoicePeControlAdapter adapter,
        CodexDedicatedVoiceOwner owner,
        RoomVoiceConversationModel conversation,
        Action<string> log)
    {
        try
        {
            await adapter.ConfirmSessionEndedAsync(CancellationToken.None).ConfigureAwait(false);
            conversation.SetRuntimeState(
                VoicePeSessionState.Armed,
                ownerReady: true,
                sessionActive: false,
                "Room Voice is armed.",
                stale: true);
            try
            {
                conversation.ReplaceHistory(await owner.ReadThreadAsync(CancellationToken.None).ConfigureAwait(false));
            }
            catch (Exception exception)
            {
                log($"Room Voice conversation refresh failed: {exception.Message}");
            }
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
