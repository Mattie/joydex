using Joydex.App;
using Joydex.Contracts;
using Joydex.Core.Voice;
using Joydex.Windows.Voice;

namespace Joydex.RuntimeHost.Production;

internal sealed class VoiceProductionOwner : IProductionVoiceOwner
{
    private readonly WindowsProductionRuntimeOwnerFactory _factory;
    private readonly VoicePeBridgeRuntime _runtime;
    private readonly RoomVoiceConversationModel _conversation;
    private readonly ProductionDesktopBrokerLease? _desktopBrokerLease;
    private readonly TaskCompletionSource _fallbackCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _completion;
    private bool _wasSessionActive;
    private int _disposed;

    private VoiceProductionOwner(
        WindowsProductionRuntimeOwnerFactory factory,
        VoicePeBridgeRuntime runtime,
        RoomVoiceConversationModel conversation,
        ProductionDesktopBrokerLease? desktopBrokerLease)
    {
        _factory = factory;
        _runtime = runtime;
        _conversation = conversation;
        _desktopBrokerLease = desktopBrokerLease;
        var runtimeCompletion = runtime.Mode == VoicePeSessionMode.JoydexOwner
            ? runtime.OwnerCompletion
            : _fallbackCompletion.Task;
        _completion = desktopBrokerLease is null
            ? runtimeCompletion
            : Task.WhenAny(runtimeCompletion, desktopBrokerLease.Completion).Unwrap();
        _wasSessionActive = runtime.IsSessionActive;
        _conversation.RuntimeStateChanged += OnRuntimeStateChanged;
        _conversation.Changed += OnConversationChanged;
    }

    public SettingsAggregateId Aggregate => SettingsAggregateId.Voice;

    public Task Completion => _completion;

    public bool IsSessionActive => _runtime.IsSessionActive;

    public static async Task<VoiceProductionOwner> StartAsync(
        WindowsProductionRuntimeOwnerFactory factory,
        ProductionRuntimePaths paths,
        ProductionDesktopBrokerManager desktopBroker,
        SettingsBundle activeSettings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(desktopBroker);
        ArgumentNullException.ThrowIfNull(activeSettings);
        var preferences = activeSettings.Voice.Normalize();
        ProductionDesktopBrokerLease? brokerLease = null;
        try
        {
            if (preferences.DesktopTaskMessagingEnabled)
            {
                brokerLease = await desktopBroker.AcquireAsync(cancellationToken).ConfigureAwait(false);
            }

            var voiceExecutor = factory.CreateActionExecutor(activeSettings.Companion);
            var coordinator = new PinnedVoiceCoordinator(
                activeSettings.Companion.Safety,
                factory.WriteLog,
                new PinnedVoiceTargetNavigator(
                    activeSettings.Companion.Safety,
                    factory.WriteLog),
                voiceExecutor.ExecuteAsync);
            var conversation = new RoomVoiceConversationModel();
            var runtime = await VoicePeBridgeRuntime.StartAsync(
                    preferences,
                    activeSettings.Companion.Safety,
                    coordinator,
                    paths.VoiceWebViewData,
                    conversation,
                    factory.WriteLog,
                    paths.VoiceActivePreferences,
                    paths.DesktopBridgeHost,
                    brokerLease?.PipeName ?? "Joydex.DesktopBridge.unavailable",
                    cancellationToken)
                .ConfigureAwait(false);
            var owner = new VoiceProductionOwner(
                factory,
                runtime,
                conversation,
                brokerLease);
            factory.PublishVoice(owner.GetState(), reset: true);
            return owner;
        }
        catch (Exception startupFailure)
        {
            if (brokerLease is not null)
            {
                try
                {
                    await brokerLease.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupFailure)
                {
                    throw new VoiceOwnershipCleanupException(
                        "Room Voice startup failed and its Desktop bridge lease cleanup was incomplete.",
                        [startupFailure, cleanupFailure]);
                }
            }
            throw;
        }
    }

    public Task EndSessionAsync(CancellationToken cancellationToken) =>
        _runtime.IsSessionActive
            ? _runtime.StopSessionAsync(cancellationToken)
            : Task.CompletedTask;

    public Task RefreshConversationAsync(CancellationToken cancellationToken) =>
        _runtime.RefreshConversationAsync(cancellationToken);

    public ProductionVoiceState GetState()
    {
        var snapshot = _conversation.GetSnapshot();
        return new ProductionVoiceState(
            new RuntimeVoiceSnapshot(
                MapState(snapshot.SessionState),
                snapshot.OwnerReady,
                snapshot.SessionActive,
                snapshot.HistoryAvailable,
                snapshot.Stale,
                snapshot.Status,
                snapshot.Error,
                snapshot.ConversationVersion),
            snapshot.Entries.Select(entry => new RuntimeVoiceTimelineEntry(
                entry.Id,
                entry.Timestamp,
                entry.Kind switch
                {
                    CodexVoiceConversationKind.User => RuntimeVoiceTimelineKind.User,
                    CodexVoiceConversationKind.Assistant => RuntimeVoiceTimelineKind.Assistant,
                    _ => RuntimeVoiceTimelineKind.Activity,
                },
                entry.Text,
                entry.IsPartial)).ToArray());
    }

    public RuntimeVoiceConversationPage GetConversationPage(string? continuationToken)
    {
        var snapshot = _conversation.GetSnapshot();
        var offset = DecodeContinuationToken(continuationToken, snapshot.ConversationVersion);
        var entries = snapshot.Entries;
        if (offset > entries.Count)
        {
            throw new InvalidDataException("The Voice conversation continuation token is stale.");
        }

        const int pageSize = 25;
        var page = entries.Skip(offset).Take(pageSize).Select(entry => new RuntimeVoiceConversationEntry(
            entry.Id,
            entry.Timestamp,
            entry.Kind switch
            {
                CodexVoiceConversationKind.User => RuntimeVoiceTimelineKind.User,
                CodexVoiceConversationKind.Assistant => RuntimeVoiceTimelineKind.Assistant,
                _ => RuntimeVoiceTimelineKind.Activity,
            },
            entry.Text,
            entry.IsPartial,
            entry.RawText)).ToArray();
        var nextOffset = offset + page.Length;
        return new RuntimeVoiceConversationPage(
            page,
            nextOffset < entries.Count
                ? EncodeContinuationToken(snapshot.ConversationVersion, nextOffset)
                : null);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _conversation.RuntimeStateChanged -= OnRuntimeStateChanged;
        _conversation.Changed -= OnConversationChanged;
        var failures = new List<Exception>();
        try
        {
            await _runtime.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        if (_desktopBrokerLease is not null)
        {
            try
            {
                await _desktopBrokerLease.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
        _fallbackCompletion.TrySetResult();
        if (failures.Count > 0)
        {
            throw new AggregateException("Room Voice cleanup did not complete.", failures);
        }
    }

    private void OnRuntimeStateChanged(object? sender, EventArgs eventArgs)
    {
        var active = _runtime.IsSessionActive;
        if (_wasSessionActive && !active)
        {
            _factory.PublishVoiceBecameIdle();
        }
        _wasSessionActive = active;
    }

    private void OnConversationChanged(object? sender, EventArgs eventArgs) =>
        _factory.PublishVoice(GetState());

    internal static int DecodeContinuationToken(string? token, long conversationVersion)
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

    internal static string EncodeContinuationToken(long conversationVersion, int offset) =>
        Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{conversationVersion}:{offset}"));

    private static RuntimeVoiceSessionState MapState(VoicePeSessionState state) => state switch
    {
        VoicePeSessionState.Armed => RuntimeVoiceSessionState.Armed,
        VoicePeSessionState.Starting => RuntimeVoiceSessionState.Starting,
        VoicePeSessionState.Listening => RuntimeVoiceSessionState.Listening,
        VoicePeSessionState.Muted => RuntimeVoiceSessionState.Muted,
        _ => RuntimeVoiceSessionState.Error,
    };
}
