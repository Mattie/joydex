using Joydex.App;
using Joydex.Contracts;
using Joydex.Core.Config;
using Joydex.Core.Mapping;
using Joydex.Core.Voice;
using Joydex.Ipc;
using Joydex.Windows.Actions;
using Joydex.Windows.Voice;
using StreamJsonRpc;
using System.Threading.Channels;

namespace Joydex.VoiceWorker;

internal sealed class VoiceWorkerService : IAsyncDisposable
{
    private readonly VoiceWorkerLaunchTicket _ticket;
    private readonly JsonRpc _rpc;
    private readonly SemaphoreSlim _publicationGate = new(1, 1);
    private readonly object _conversationStateGate = new();
    private readonly object _disposeGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<bool> _snapshotSignals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly Channel<string> _logs = Channel.CreateBounded<string>(new BoundedChannelOptions(64)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RoomVoiceConversationModel? _conversation;
    private VoicePeBridgeRuntime? _runtime;
    private long _sequence;
    private int _idlePending;
    private readonly Task _snapshotPublisher;
    private readonly Task _logPublisher;
    private bool _wasSessionActive;
    private int _started;
    private int _disposed;

    public VoiceWorkerService(VoiceWorkerLaunchTicket ticket, JsonRpc rpc)
    {
        _ticket = ticket ?? throw new ArgumentNullException(nameof(ticket));
        _rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
        _snapshotPublisher = RunSnapshotPublisherAsync();
        _logPublisher = RunLogPublisherAsync();
    }

    public Task Completion => _completion.Task;

    public bool StopRequested { get; private set; }

    [JsonRpcMethod(VoiceWorkerProtocol.Start)]
    public async Task<VoiceWorkerStartResponse> StartAsync(
        VoiceWorkerStartRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            throw new InvalidOperationException("The Voice worker has already received Start.");
        }
        ValidateStart(request);

        var preferences = MapPreferences(request.Preferences).Normalize();
        var safety = MapSafety(request.Safety);
        var conversation = new RoomVoiceConversationModel();
        var coordinator = new PinnedVoiceCoordinator(
            safety,
            WriteLog);
        VoicePeBridgeRuntime? runtime = null;
        try
        {
            runtime = await VoicePeBridgeRuntime.StartAsync(
                    preferences,
                    safety,
                    coordinator,
                    request.Paths.WebViewData,
                    conversation,
                    WriteLog,
                    request.Paths.ActivePreferences,
                    request.Paths.DesktopBridgeHost,
                    request.Paths.DesktopBridgePipeName,
                    cancellationToken)
                .ConfigureAwait(false);
            _conversation = conversation;
            _runtime = runtime;
            lock (_conversationStateGate) { _wasSessionActive = runtime.IsSessionActive; }
            conversation.RuntimeStateChanged += OnConversationChanged;
            conversation.Changed += OnConversationChanged;
            if (runtime.Mode == VoicePeSessionMode.JoydexOwner)
            {
                _ = ObserveRuntimeCompletionAsync(runtime);
            }
            var snapshot = CreateSnapshot();
            return new VoiceWorkerStartResponse(
                VoiceWorkerProtocol.MajorVersion,
                Math.Min(request.ProtocolMinor, VoiceWorkerProtocol.MinorVersion),
                snapshot);
        }
        catch (Exception startupFailure)
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().ConfigureAwait(false);
            }
            return new VoiceWorkerStartResponse(
                VoiceWorkerProtocol.MajorVersion,
                Math.Min(request.ProtocolMinor, VoiceWorkerProtocol.MinorVersion),
                Snapshot: null,
                Failure: ClassifyStartFailure(startupFailure));
        }
    }

    [JsonRpcMethod(VoiceWorkerProtocol.Stop)]
    public async Task StopAsync(
        VoiceWorkerGenerationMessage request,
        CancellationToken cancellationToken)
    {
        ValidateGeneration(request.Generation);
        StopRequested = true;
        await DisposeAsync().ConfigureAwait(false);
    }

    [JsonRpcMethod(VoiceWorkerProtocol.EndSession)]
    public Task EndSessionAsync(
        VoiceWorkerGenerationMessage request,
        CancellationToken cancellationToken)
    {
        ValidateGeneration(request.Generation);
        var runtime = RequireRuntime();
        return runtime.IsSessionActive
            ? runtime.StopSessionAsync(cancellationToken)
            : Task.CompletedTask;
    }

    [JsonRpcMethod(VoiceWorkerProtocol.RefreshConversation)]
    public Task RefreshConversationAsync(
        VoiceWorkerGenerationMessage request,
        CancellationToken cancellationToken)
    {
        ValidateGeneration(request.Generation);
        return RequireRuntime().RefreshConversationAsync(cancellationToken);
    }

    [JsonRpcMethod(VoiceWorkerProtocol.ReadConversationPage)]
    public Task<RuntimeVoiceConversationPage> ReadConversationPageAsync(
        VoiceWorkerConversationPageRequest request,
        CancellationToken cancellationToken)
    {
        ValidateGeneration(request.Generation);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = RequireConversation().GetSnapshot();
        var offset = DecodeContinuationToken(request.ContinuationToken, snapshot.ConversationVersion);
        if (offset > snapshot.Entries.Count)
        {
            throw new InvalidDataException("The Voice conversation continuation token is stale.");
        }

        return Task.FromResult(CreateConversationPage(snapshot, offset));
    }

    private Task? _disposeTask;

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var conversation = Interlocked.Exchange(ref _conversation, null);
        if (conversation is not null)
        {
            conversation.RuntimeStateChanged -= OnConversationChanged;
            conversation.Changed -= OnConversationChanged;
        }
        var runtime = Interlocked.Exchange(ref _runtime, null);
        _snapshotSignals.Writer.TryComplete();
        _logs.Writer.TryComplete();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        List<Exception>? failures = null;
        try
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }
        try
        {
            await Task.WhenAll(_snapshotPublisher, _logPublisher).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }
        _lifetime.Dispose();
        if (failures is not null)
        {
            throw new AggregateException("Voice worker cleanup did not complete.", failures);
        }
    }

    private void ValidateStart(VoiceWorkerStartRequest request)
    {
        if (!string.Equals(request.Capability, _ticket.Capability, StringComparison.Ordinal)
            || request.Generation != _ticket.Generation)
        {
            throw new InvalidDataException("The Voice worker capability is invalid.");
        }
        if (request.ProtocolMajor != VoiceWorkerProtocol.MajorVersion)
        {
            throw new InvalidDataException("The Voice worker protocol major version is unsupported.");
        }
        ArgumentNullException.ThrowIfNull(request.Preferences);
        ArgumentNullException.ThrowIfNull(request.Safety);
        ArgumentNullException.ThrowIfNull(request.Paths);
        if (request.ProtocolMinor < 0
            || request.Safety.CodexProcessNames is null
            || request.Safety.SimulatorProcessNames is null
            || request.Safety.CodexProcessNames.Length > 64
            || request.Safety.SimulatorProcessNames.Length > 64
            || request.Safety.CodexProcessNames.Any(string.IsNullOrWhiteSpace)
            || request.Safety.SimulatorProcessNames.Any(string.IsNullOrWhiteSpace)
            || InvalidPath(request.Paths.WebViewData)
            || InvalidPath(request.Paths.ActivePreferences)
            || InvalidPath(request.Paths.DesktopBridgeHost)
            || string.IsNullOrWhiteSpace(request.Paths.DesktopBridgePipeName)
            || request.Paths.DesktopBridgePipeName.Length > 256
            || request.Preferences.DeviceEndpoint is null
            || request.Preferences.PinnedTaskId is null
            || request.Preferences.PinnedTaskLabel is null
            || request.Preferences.DedicatedTaskId is null
            || request.Preferences.DedicatedTaskLabel is null
            || request.Preferences.CodexAppServerPath is null
            || request.Preferences.AgentWorkspacePath is null
            || request.Preferences.AgentProjectId is null
            || request.Preferences.AgentProjectLabel is null
            || request.Preferences.RealtimeVoice is null
            || request.Preferences.VoiceTargetTaskId is null
            || request.Preferences.VoiceTargetHostId is null
            || request.Preferences.VoiceTargetTaskLabel is null)
        {
            throw new InvalidDataException("The Voice worker Start payload is invalid.");
        }
    }

    private async Task ObserveRuntimeCompletionAsync(VoicePeBridgeRuntime runtime)
    {
        Exception? failure = null;
        try
        {
            await runtime.OwnerCompletion.ConfigureAwait(false);
            if (Volatile.Read(ref _disposed) == 0)
            {
                failure = new InvalidOperationException("The Voice owner stopped unexpectedly.");
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (failure is null)
        {
            return;
        }
        _completion.TrySetException(failure);
    }

    private void OnConversationChanged(object? sender, EventArgs eventArgs)
    {
        var runtime = _runtime;
        if (runtime is not null)
        {
            lock (_conversationStateGate)
            {
                var active = runtime.IsSessionActive;
                if (_wasSessionActive && !active)
                {
                    Interlocked.Exchange(ref _idlePending, 1);
                }
                _wasSessionActive = active;
            }
        }
        _snapshotSignals.Writer.TryWrite(true);
    }

    internal static RuntimeVoiceConversationPage CreateConversationPage(
        RoomVoiceConversationSnapshot snapshot,
        int offset)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (offset < 0 || offset > snapshot.Entries.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        var page = new List<RuntimeVoiceConversationEntry>();
        var escapedCharacters = 0;
        foreach (var entry in snapshot.Entries.Skip(offset)
            .Take(VoiceWorkerProtocol.MaximumConversationPageEntries))
        {
            if (string.IsNullOrEmpty(entry.Id)
                || entry.Id.Length > VoiceWorkerProtocol.MaximumConversationEntryIdCharacters
                || entry.Text is null
                || entry.Text.Length > VoiceWorkerProtocol.MaximumConversationEntryTextCharacters
                || entry.RawText?.Length > VoiceWorkerProtocol.MaximumConversationEntryTextCharacters)
            {
                throw new InvalidDataException(
                    "A Voice conversation entry exceeds the worker transport limits.");
            }

            var entryCharacters = entry.Id.Length + entry.Text.Length + (entry.RawText?.Length ?? 0);
            if (page.Count > 0
                && escapedCharacters + entryCharacters
                    > VoiceWorkerProtocol.MaximumConversationPageEscapedCharacters)
            {
                break;
            }
            if (escapedCharacters + entryCharacters
                > VoiceWorkerProtocol.MaximumConversationPageEscapedCharacters)
            {
                throw new InvalidDataException(
                    "A Voice conversation entry exceeds the worker transport limits.");
            }

            page.Add(new RuntimeVoiceConversationEntry(
                entry.Id,
                entry.Timestamp,
                MapKind(entry.Kind),
                entry.Text,
                entry.IsPartial,
                entry.RawText));
            escapedCharacters += entryCharacters;
        }

        var nextOffset = offset + page.Count;
        return new RuntimeVoiceConversationPage(
            [.. page],
            nextOffset < snapshot.Entries.Count
                ? EncodeContinuationToken(snapshot.ConversationVersion, nextOffset)
                : null);
    }

    private async Task RunSnapshotPublisherAsync()
    {
        try
        {
            await foreach (var signal in _snapshotSignals.Reader.ReadAllAsync(_lifetime.Token)
                .ConfigureAwait(false))
            {
                _ = signal;
                while (_snapshotSignals.Reader.TryRead(out _))
                {
                }
                await _publicationGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try
                {
                    if (Volatile.Read(ref _disposed) == 0 && _runtime is not null)
                    {
                        var snapshot = CreateSnapshot();
                        await _rpc.InvokeWithCancellationAsync(
                            VoiceWorkerProtocol.PublishSnapshot,
                            [snapshot],
                            _lifetime.Token).ConfigureAwait(false);
                        if (Interlocked.Exchange(ref _idlePending, 0) != 0)
                        {
                            await _rpc.InvokeWithCancellationAsync(
                                    VoiceWorkerProtocol.BecameIdle,
                                    [new VoiceWorkerIdleMessage(_ticket.Generation, snapshot.Sequence)],
                                    _lifetime.Token)
                                .ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    _publicationGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            _completion.TrySetException(
                new IOException("The Voice worker could not publish its state to the host."));
        }
    }

    private async Task RunLogPublisherAsync()
    {
        try
        {
            await foreach (var message in _logs.Reader.ReadAllAsync(_lifetime.Token)
                .ConfigureAwait(false))
            {
                await _rpc.InvokeWithCancellationAsync(
                        VoiceWorkerProtocol.Log,
                        [new VoiceWorkerLogMessage(_ticket.Generation, message)],
                        _lifetime.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            _completion.TrySetException(
                new IOException("The Voice worker could not publish diagnostics to the host."));
        }
    }

    private VoiceWorkerSnapshot CreateSnapshot()
    {
        var runtime = RequireRuntime();
        var snapshot = RequireConversation().GetSnapshot();
        var timeline = snapshot.Entries
            .TakeLast(RuntimeUiLimits.MaximumVoiceTimelineEntries)
            .Select(entry => new RuntimeVoiceTimelineEntry(
                entry.Id,
                entry.Timestamp,
                MapKind(entry.Kind),
                LimitText(entry.Text),
                entry.IsPartial,
                TextTruncated: entry.Text.Length > RuntimeUiLimits.MaximumVoiceTimelineTextCharacters))
            .ToArray();
        return new VoiceWorkerSnapshot(
            _ticket.Generation,
            Interlocked.Increment(ref _sequence),
            new RuntimeVoiceSnapshot(
                MapState(snapshot.SessionState),
                snapshot.OwnerReady,
                snapshot.SessionActive,
                snapshot.HistoryAvailable,
                snapshot.Stale,
                LimitStatus(snapshot.Status),
                snapshot.Error is null ? null : LimitStatus(snapshot.Error),
                snapshot.ConversationVersion),
            timeline);
    }

    private async Task<ActionExecutionResult> ExecuteActionAsync(
        ActionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _rpc.InvokeWithCancellationAsync<VoiceWorkerActionResult>(
                VoiceWorkerProtocol.ExecuteAction,
                [new VoiceWorkerActionRequest(
                    _ticket.Generation,
                    request.Action == CodexAction.StartVoiceChat ? "start-voice-chat" : "unsupported",
                    request.BindingName,
                    request.Bank,
                    request.Button,
                    request.Trigger,
                    request.RequestedAt,
                    request.DeviceId)],
                cancellationToken)
            .ConfigureAwait(false);
        return new ActionExecutionResult(result.Executed, result.DryRun, result.Message);
    }

    private void WriteLog(string message) => _logs.Writer.TryWrite(LimitStatus(message));

    private VoicePeBridgeRuntime RequireRuntime() => _runtime
        ?? throw new InvalidOperationException("The Voice worker has not started.");

    private RoomVoiceConversationModel RequireConversation() => _conversation
        ?? throw new InvalidOperationException("The Voice worker has not started.");

    private void ValidateGeneration(long generation)
    {
        if (generation != _ticket.Generation)
        {
            throw new InvalidOperationException("The Voice worker generation is stale.");
        }
    }

    private static VoicePePreferences MapPreferences(VoiceWorkerPreferences value) => new(
        value.SchemaVersion,
        value.Enabled,
        value.DeviceEndpoint,
        value.PinnedTaskId,
        value.PinnedTaskLabel,
        (VoicePeSessionMode)value.SessionMode,
        value.DedicatedTaskId,
        value.DedicatedTaskLabel,
        value.CodexAppServerPath,
        value.AgentWorkspacePath,
        value.AgentProjectId,
        value.AgentProjectLabel,
        value.RealtimeVoice,
        value.ConversationSpeakerGain,
        value.PreserveAssistantAudioDiagnostics,
        value.DesktopTaskMessagingEnabled,
        value.VoiceTargetTaskId,
        value.VoiceTargetHostId,
        value.VoiceTargetTaskLabel);

    private static SafetyOptions MapSafety(VoiceWorkerSafety value) => new()
    {
        DryRun = value.DryRun,
        RequireCodexForeground = value.RequireCodexForeground,
        CodexProcessNames = value.CodexProcessNames,
        SimulatorProcessNames = value.SimulatorProcessNames,
    };

    private static RuntimeVoiceSessionState MapState(VoicePeSessionState state) => state switch
    {
        VoicePeSessionState.Armed => RuntimeVoiceSessionState.Armed,
        VoicePeSessionState.Starting => RuntimeVoiceSessionState.Starting,
        VoicePeSessionState.Listening => RuntimeVoiceSessionState.Listening,
        VoicePeSessionState.Muted => RuntimeVoiceSessionState.Muted,
        _ => RuntimeVoiceSessionState.Error,
    };

    private static RuntimeVoiceTimelineKind MapKind(CodexVoiceConversationKind kind) => kind switch
    {
        CodexVoiceConversationKind.User => RuntimeVoiceTimelineKind.User,
        CodexVoiceConversationKind.Assistant => RuntimeVoiceTimelineKind.Assistant,
        _ => RuntimeVoiceTimelineKind.Activity,
    };

    private static string LimitText(string value) =>
        value.Length <= RuntimeUiLimits.MaximumVoiceTimelineTextCharacters
            ? value
            : value[..RuntimeUiLimits.MaximumVoiceTimelineTextCharacters];

    private static string LimitStatus(string value) =>
        value.Length <= RuntimeUiLimits.MaximumStatusCharacters
            ? value
            : value[..RuntimeUiLimits.MaximumStatusCharacters];

    private static bool InvalidPath(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 32_767;

    private static VoiceWorkerStartFailure ClassifyStartFailure(Exception exception) => exception switch
    {
        CodexDedicatedVoiceCompatibilityException => new(
            VoiceWorkerStartFailureKind.Compatibility,
            "The managed Codex Voice runtime is incompatible with this Joydex build."),
        InvalidDataException or ArgumentException or UnauthorizedAccessException
            or FileNotFoundException or DirectoryNotFoundException => new(
                VoiceWorkerStartFailureKind.Configuration,
                "Room Voice configuration or an explicit runtime path is invalid."),
        _ => new(
            VoiceWorkerStartFailureKind.Transient,
            "Room Voice worker initialization failed."),
    };

    private static int DecodeContinuationToken(string? token, long conversationVersion)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return 0;
        }
        try
        {
            var text = System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(token));
            var pieces = text.Split(':', 2);
            if (pieces.Length != 2
                || !long.TryParse(pieces[0], out var version)
                || version != conversationVersion
                || !int.TryParse(pieces[1], out var offset)
                || offset < 0)
            {
                throw new InvalidDataException(
                    "The Voice conversation changed while it was being read; restart from the first page.");
            }
            return offset;
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The Voice conversation continuation token is invalid.", exception);
        }
    }

    private static string EncodeContinuationToken(long conversationVersion, int offset) =>
        Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{conversationVersion}:{offset}"));

    private sealed class HostNavigator(VoiceWorkerService owner) : IPinnedVoiceTargetNavigator
    {
        public Task<bool> NavigateAsync(string taskId, CancellationToken cancellationToken) =>
            owner._rpc.InvokeWithCancellationAsync<bool>(
                VoiceWorkerProtocol.Navigate,
                [new VoiceWorkerNavigateRequest(owner._ticket.Generation, taskId)],
                cancellationToken);
    }
}
