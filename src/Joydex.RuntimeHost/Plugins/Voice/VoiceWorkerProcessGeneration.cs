using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using Joydex.Contracts;
using Joydex.Core.Config;
using Joydex.Core.Mapping;
using Joydex.Core.Voice;
using Joydex.Ipc;
using Joydex.RuntimeHost.Plugins;
using Joydex.RuntimeHost.Production;
using Joydex.Windows.Actions;
using Joydex.Windows.Voice;
using StreamJsonRpc;

namespace Joydex.RuntimeHost.Plugins.Voice;

internal sealed record VoiceWorkerGenerationConfiguration(
    long Generation,
    VoicePePreferences Preferences,
    SafetyOptions Safety,
    string WebViewData,
    string ActivePreferences,
    string DesktopBridgeHost,
    string DesktopBridgePipeName);

internal interface IVoiceWorkerHostCallbacks
{
    void PublishSnapshot(VoiceWorkerSnapshot snapshot);

    void VoiceBecameIdle(long generation, long sequence);

    Task<bool> NavigateAsync(long generation, string taskId, CancellationToken cancellationToken);

    Task<ActionExecutionResult> ExecuteActionAsync(
        long generation,
        ActionRequest request,
        CancellationToken cancellationToken);

    void WriteLog(long generation, string message);
}

internal interface IVoiceWorkerGeneration : IAsyncDisposable
{
    long Generation { get; }

    VoiceWorkerSnapshot Snapshot { get; }

    Task Completion { get; }

    VoiceWorkerSnapshot ActivateCallbacks();

    void DeactivateCallbacks();

    Task EndSessionAsync(CancellationToken cancellationToken);

    Task RefreshConversationAsync(CancellationToken cancellationToken);

    Task<RuntimeVoiceConversationPage> GetConversationPageAsync(
        string? continuationToken,
        CancellationToken cancellationToken);
}

internal interface IVoiceWorkerGenerationFactory
{
    Task<IVoiceWorkerGeneration> StartAsync(
        VoiceWorkerGenerationConfiguration configuration,
        IVoiceWorkerHostCallbacks callbacks,
        CancellationToken cancellationToken);
}

internal sealed class VoiceWorkerProcessGenerationFactory(
    string executablePath,
    IRuntimeSettingsProcessFactory processFactory,
    Func<IWorkerProcessJob>? jobFactory = null) : IVoiceWorkerGenerationFactory
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private readonly string _executablePath = Path.GetFullPath(
        executablePath ?? throw new ArgumentNullException(nameof(executablePath)));
    private readonly IRuntimeSettingsProcessFactory _processFactory =
        processFactory ?? throw new ArgumentNullException(nameof(processFactory));
    private readonly Func<IWorkerProcessJob> _jobFactory = jobFactory ?? WorkerProcessJob.Create;

    public async Task<IVoiceWorkerGeneration> StartAsync(
        VoiceWorkerGenerationConfiguration configuration,
        IVoiceWorkerHostCallbacks callbacks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(callbacks);
        cancellationToken.ThrowIfCancellationRequested();
        if (configuration.Generation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(configuration));
        }

        var pipeName = "Joydex.VoiceWorker." + Guid.NewGuid().ToString("N");
        var capability = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        NamedPipeServerStream? pipe = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        IWorkerProcessJob? job = null;
        IRuntimeSettingsProcess? process = null;
        JsonRpc? rpc = null;
        BoundedMessageStream? bounded = null;
        try
        {
            job = _jobFactory();
            process = _processFactory.Start(CreateStartInfo());
            if (process is not IRuntimeWorkerProcess nativeProcess)
            {
                throw new InvalidOperationException(
                    "The production Voice worker requires a native process owner.");
            }
            job.Assign(nativeProcess);
            var processStartTicks = nativeProcess.ProcessStartTimeUtcTicks;
            using var hostProcess = Process.GetCurrentProcess();
            var ticket = new VoiceWorkerLaunchTicket(
                pipeName,
                capability,
                configuration.Generation,
                VoiceWorkerProtocol.MajorVersion,
                VoiceWorkerProtocol.MinorVersion,
                hostProcess.Id,
                hostProcess.StartTime.ToUniversalTime().Ticks,
                hostProcess.SessionId);
            await JsonSerializer.SerializeAsync(process.StandardInput, ticket, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await process.StandardInput.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            process.CloseInput();

            using var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCancellation.CancelAfter(ConnectTimeout);
            await pipe.WaitForConnectionAsync(connectCancellation.Token).ConfigureAwait(false);
            _ = WindowsPipePeerVerifier.VerifyClient(
                pipe,
                hostProcess.SessionId,
                process.Id,
                processStartTicks);

            (rpc, bounded) = RuntimeJsonRpc.Create(pipe);
            var callbackTarget = new VoiceWorkerHostRpcTarget(configuration.Generation, callbacks);
            rpc.AddLocalRpcTarget(callbackTarget);
            rpc.StartListening();
            var response = await rpc.InvokeWithCancellationAsync<VoiceWorkerStartResponse>(
                    VoiceWorkerProtocol.Start,
                    [CreateStartRequest(configuration, capability)],
                    connectCancellation.Token)
                .ConfigureAwait(false);
            ValidateStartResponse(configuration.Generation, response);
            if (response.Failure is { } failure)
            {
                throw new VoiceWorkerStartupException(failure.Kind, failure.Detail);
            }
            callbackTarget.SetStartSnapshot(response.Snapshot!);
            var generation = new VoiceWorkerProcessGeneration(
                configuration.Generation,
                process,
                job,
                pipe,
                rpc,
                bounded,
                callbackTarget,
                StopTimeout);
            process = null;
            job = null;
            rpc = null;
            bounded = null;
            pipe = null;
            return generation;
        }
        catch (Exception startupFailure)
        {
            var cleanupFailure = await CleanupFailedStartAsync(
                    process,
                    job,
                    rpc,
                    bounded,
                    pipe)
                .ConfigureAwait(false);
            if (cleanupFailure is not null)
            {
                throw new VoiceOwnershipCleanupException(
                    "Voice worker startup failed and generation cleanup was not confirmed.",
                    [startupFailure, cleanupFailure]);
            }
            throw;
        }
    }

    private ProcessStartInfo CreateStartInfo() => new()
    {
        FileName = _executablePath,
        Arguments = "--voice-worker",
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };

    private static VoiceWorkerStartRequest CreateStartRequest(
        VoiceWorkerGenerationConfiguration configuration,
        string capability)
    {
        var value = configuration.Preferences.Normalize();
        return new VoiceWorkerStartRequest(
            capability,
            configuration.Generation,
            VoiceWorkerProtocol.MajorVersion,
            VoiceWorkerProtocol.MinorVersion,
            new VoiceWorkerPreferences(
                value.SchemaVersion,
                value.Enabled,
                value.DeviceEndpoint,
                value.PinnedTaskId,
                value.PinnedTaskLabel,
                (int)value.SessionMode,
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
                value.VoiceTargetTaskLabel),
            new VoiceWorkerSafety(
                configuration.Safety.DryRun,
                configuration.Safety.RequireCodexForeground,
                configuration.Safety.CodexProcessNames,
                configuration.Safety.SimulatorProcessNames),
            new VoiceWorkerPaths(
                configuration.WebViewData,
                configuration.ActivePreferences,
                configuration.DesktopBridgeHost,
                configuration.DesktopBridgePipeName));
    }

    private static void ValidateStartResponse(long generation, VoiceWorkerStartResponse response)
    {
        if (response.ProtocolMajor != VoiceWorkerProtocol.MajorVersion
            || response.ProtocolMinor is < 0 or > VoiceWorkerProtocol.MinorVersion
            || (response.Snapshot is null) == (response.Failure is null)
            || response.Failure is { } failure
                && (!Enum.IsDefined(failure.Kind)
                    || string.IsNullOrWhiteSpace(failure.Detail)
                    || failure.Detail.Length > RuntimeUiLimits.MaximumStatusCharacters))
        {
            throw new InvalidDataException("The Voice worker returned an invalid Start response.");
        }
        if (response.Snapshot is not null)
        {
            VoiceWorkerSnapshotValidator.Validate(response.Snapshot, generation);
        }
    }

    private static async Task<Exception?> CleanupFailedStartAsync(
        IRuntimeSettingsProcess? process,
        IWorkerProcessJob? job,
        JsonRpc? rpc,
        BoundedMessageStream? bounded,
        NamedPipeServerStream? pipe)
    {
        List<Exception>? confirmationFailures = null;
        try { rpc?.Dispose(); } catch { }
        try { bounded?.Dispose(); } catch { }
        try { pipe?.Dispose(); } catch { }
        if (job is not null)
        {
            try { job.Terminate(); } catch { }
        }
        if (process is not null)
        {
            try { process.Kill(); } catch { }
            try
            {
                using var timeout = new CancellationTokenSource(StopTimeout);
                await process.Completion.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) { (confirmationFailures ??= []).Add(exception); }
        }
        if (job is not null)
        {
            try
            {
                await WorkerProcessJob.WaitForEmptyAsync(job, StopTimeout, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) { (confirmationFailures ??= []).Add(exception); }
            job.Dispose();
        }
        if (process is not null)
        {
            try { await process.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        return confirmationFailures is null ? null : new AggregateException(confirmationFailures);
    }

}

internal sealed class VoiceWorkerProcessGeneration : IVoiceWorkerGeneration
{
    private readonly IRuntimeSettingsProcess _process;
    private readonly IWorkerProcessJob _job;
    private readonly NamedPipeServerStream _pipe;
    private readonly JsonRpc _rpc;
    private readonly BoundedMessageStream _bounded;
    private readonly VoiceWorkerHostRpcTarget _callbacks;
    private readonly TimeSpan _stopTimeout;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;

    public VoiceWorkerProcessGeneration(
        long generation,
        IRuntimeSettingsProcess process,
        IWorkerProcessJob job,
        NamedPipeServerStream pipe,
        JsonRpc rpc,
        BoundedMessageStream bounded,
        VoiceWorkerHostRpcTarget callbacks,
        TimeSpan stopTimeout)
    {
        Generation = generation;
        _process = process;
        _job = job;
        _pipe = pipe;
        _rpc = rpc;
        _bounded = bounded;
        _callbacks = callbacks;
        _stopTimeout = stopTimeout;
        Completion = Task.WhenAny(process.Completion, rpc.Completion).Unwrap();
    }

    public long Generation { get; }

    public VoiceWorkerSnapshot Snapshot => _callbacks.CurrentSnapshot;

    public Task Completion { get; }

    public VoiceWorkerSnapshot ActivateCallbacks() => _callbacks.EnableAndGetSnapshot();

    public void DeactivateCallbacks() => _callbacks.Disable();

    public Task EndSessionAsync(CancellationToken cancellationToken) =>
        InvokeAsync(VoiceWorkerProtocol.EndSession, cancellationToken);

    public Task RefreshConversationAsync(CancellationToken cancellationToken) =>
        InvokeAsync(VoiceWorkerProtocol.RefreshConversation, cancellationToken);

    public async Task<RuntimeVoiceConversationPage> GetConversationPageAsync(
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        var page = await _rpc.InvokeWithCancellationAsync<RuntimeVoiceConversationPage>(
                VoiceWorkerProtocol.ReadConversationPage,
                [new VoiceWorkerConversationPageRequest(Generation, continuationToken)],
                cancellationToken)
            .ConfigureAwait(false);
        VoiceWorkerSnapshotValidator.ValidatePage(page);
        return page;
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
        _callbacks.Disable();
        try
        {
            using var graceful = new CancellationTokenSource(_stopTimeout);
            await InvokeAsync(VoiceWorkerProtocol.Stop, graceful.Token).ConfigureAwait(false);
        }
        catch
        {
            // A failed graceful stop is recovered by terminating and confirming the job below.
        }
        List<Exception>? confirmationFailures = null;
        try { _rpc.Dispose(); } catch { }
        try { _bounded.Dispose(); } catch { }
        try { _pipe.Dispose(); } catch { }
        try
        {
            if (_job.ActiveProcessCount != 0)
            {
                _job.Terminate();
            }
        }
        catch { }
        try
        {
            using var cleanup = new CancellationTokenSource(_stopTimeout);
            await Task.WhenAll(
                    _process.Completion.WaitAsync(cleanup.Token),
                    WorkerProcessJob.WaitForEmptyAsync(
                        _job,
                        _stopTimeout,
                        cleanup.Token))
                .ConfigureAwait(false);
        }
        catch (Exception exception) { (confirmationFailures ??= []).Add(exception); }
        _job.Dispose();
        try { await _process.DisposeAsync().ConfigureAwait(false); } catch { }
        if (confirmationFailures is not null)
        {
            throw new VoiceOwnershipCleanupException(
                "Voice worker generation cleanup was not confirmed.",
                confirmationFailures);
        }
    }

    private Task InvokeAsync(string method, CancellationToken cancellationToken) =>
        _rpc.InvokeWithCancellationAsync(
            method,
            [new VoiceWorkerGenerationMessage(Generation)],
            cancellationToken);

}

internal sealed class VoiceWorkerStartupException(
    VoiceWorkerStartFailureKind kind,
    string message) : Exception(message)
{
    public VoiceWorkerStartFailureKind Kind { get; } = kind;
}

internal sealed class VoiceWorkerHostRpcTarget(
    long generation,
    IVoiceWorkerHostCallbacks callbacks)
{
    private readonly object _gate = new();
    private VoiceWorkerSnapshot? _current;
    private bool _enabled;
    private bool _disabled;

    public VoiceWorkerSnapshot CurrentSnapshot
    {
        get
        {
            lock (_gate)
            {
                return _current
                    ?? throw new InvalidOperationException("The Voice worker has no initial snapshot.");
            }
        }
    }

    public void SetStartSnapshot(VoiceWorkerSnapshot snapshot)
    {
        VoiceWorkerSnapshotValidator.Validate(snapshot, generation);
        lock (_gate)
        {
            if (_disabled)
            {
                return;
            }
            if (_current is null || snapshot.Sequence > _current.Sequence)
            {
                _current = snapshot;
            }
        }
    }

    public VoiceWorkerSnapshot EnableAndGetSnapshot()
    {
        lock (_gate)
        {
            if (_disabled)
            {
                throw new ObjectDisposedException(nameof(VoiceWorkerHostRpcTarget));
            }
            _enabled = true;
            return _current
                ?? throw new InvalidOperationException("The Voice worker has no initial snapshot.");
        }
    }

    public void Disable()
    {
        lock (_gate)
        {
            _disabled = true;
            _enabled = false;
        }
    }

    [JsonRpcMethod(VoiceWorkerProtocol.PublishSnapshot)]
    public Task PublishSnapshotAsync(VoiceWorkerSnapshot snapshot)
    {
        if (snapshot.Generation != generation)
        {
            return Task.CompletedTask;
        }
        VoiceWorkerSnapshotValidator.Validate(snapshot, generation);
        var publish = false;
        lock (_gate)
        {
            if (!_disabled && (_current is null || snapshot.Sequence > _current.Sequence))
            {
                _current = snapshot;
                publish = _enabled;
            }
        }
        if (publish)
        {
            callbacks.PublishSnapshot(snapshot);
        }
        return Task.CompletedTask;
    }

    [JsonRpcMethod(VoiceWorkerProtocol.BecameIdle)]
    public Task BecameIdleAsync(VoiceWorkerIdleMessage message)
    {
        if (IsEnabled(message.Generation))
        {
            callbacks.VoiceBecameIdle(generation, message.Sequence);
        }
        return Task.CompletedTask;
    }

    [JsonRpcMethod(VoiceWorkerProtocol.Navigate)]
    public async Task<bool> NavigateAsync(
        VoiceWorkerNavigateRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled(request.Generation))
        {
            return false;
        }
        var navigated = await callbacks.NavigateAsync(
                generation,
                request.TaskId,
                cancellationToken)
            .ConfigureAwait(false);
        return IsEnabled(request.Generation) && navigated;
    }

    [JsonRpcMethod(VoiceWorkerProtocol.ExecuteAction)]
    public async Task<VoiceWorkerActionResult> ExecuteActionAsync(
        VoiceWorkerActionRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled(request.Generation) || request.Action != "start-voice-chat")
        {
            return new VoiceWorkerActionResult(false, false, "The Voice worker action was rejected.");
        }
        var result = await callbacks.ExecuteActionAsync(
                generation,
                new ActionRequest(
                    "Voice PE wake",
                    CompanionConfig.AlwaysBank,
                    0,
                    "wake",
                    CodexAction.StartVoiceChat,
                    DateTimeOffset.UtcNow,
                    DeviceId: "voice-pe"),
                cancellationToken)
            .ConfigureAwait(false);
        if (!IsEnabled(request.Generation))
        {
            return new VoiceWorkerActionResult(false, false, "The Voice worker action was rejected.");
        }
        return new VoiceWorkerActionResult(result.Executed, result.DryRun, result.Message);
    }

    [JsonRpcMethod(VoiceWorkerProtocol.Log)]
    public Task LogAsync(VoiceWorkerLogMessage message)
    {
        if (IsEnabled(message.Generation)
            && message.Message is not null
            && message.Message.Length <= RuntimeUiLimits.MaximumStatusCharacters)
        {
            callbacks.WriteLog(generation, message.Message);
        }
        return Task.CompletedTask;
    }

    private bool IsEnabled(long messageGeneration)
    {
        lock (_gate)
        {
            return !_disabled && _enabled && messageGeneration == generation;
        }
    }
}

internal static class VoiceWorkerSnapshotValidator
{
    public static void Validate(VoiceWorkerSnapshot snapshot, long expectedGeneration)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Voice);
        ArgumentNullException.ThrowIfNull(snapshot.Timeline);
        if (snapshot.Generation != expectedGeneration
            || snapshot.Sequence <= 0
            || snapshot.Timeline.Length > RuntimeUiLimits.MaximumVoiceTimelineEntries
            || !Enum.IsDefined(snapshot.Voice.SessionState)
            || snapshot.Voice.ConversationVersion < 0
            || InvalidStatus(snapshot.Voice.Status)
            || snapshot.Voice.Error is not null && InvalidStatus(snapshot.Voice.Error)
            || snapshot.Timeline.Any(InvalidTimelineEntry))
        {
            throw new InvalidDataException("The Voice worker returned an invalid bounded snapshot.");
        }
    }

    public static void ValidatePage(RuntimeVoiceConversationPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(page.Entries);
        if (page.Entries.Length > VoiceWorkerProtocol.MaximumConversationPageEntries
            || page.NextContinuationToken?.Length > 256
            || page.Entries.Any(entry => entry is null
                || string.IsNullOrEmpty(entry.Id)
                || entry.Id.Length > VoiceWorkerProtocol.MaximumConversationEntryIdCharacters
                || !Enum.IsDefined(entry.Kind)
                || entry.Text is null
                || entry.Text.Length > VoiceWorkerProtocol.MaximumConversationEntryTextCharacters
                || entry.RawText?.Length > VoiceWorkerProtocol.MaximumConversationEntryTextCharacters))
        {
            throw new InvalidDataException("The Voice worker returned an invalid conversation page.");
        }
    }

    private static bool InvalidStatus(string? value) =>
        value is null || value.Length > RuntimeUiLimits.MaximumStatusCharacters;

    private static bool InvalidTimelineEntry(RuntimeVoiceTimelineEntry entry) =>
        entry is null
        || string.IsNullOrEmpty(entry.Id)
        || entry.Id.Length > 256
        || !Enum.IsDefined(entry.Kind)
        || entry.Text is null
        || entry.Text.Length > RuntimeUiLimits.MaximumVoiceTimelineTextCharacters;
}
