using Joydex.App;
using Joydex.Contracts;
using Joydex.Core.TaskAlerts;
using Joydex.Core.Voice;
using Joydex.Windows.TaskAlerts;
using Joydex.Windows.Voice;

namespace Joydex.RuntimeHost.Production;

internal sealed partial class WindowsProductionRuntimeOwnerFactory
{
    private async Task<RuntimeCommandResult> ExecuteCommandAsync(
        RuntimeCommandRequest request,
        SettingsBundle activeSettings,
        CancellationToken cancellationToken)
    {
        try
        {
            return request.Kind switch
            {
                RuntimeCommandKind.ListVoiceProjects => await ListVoiceProjectsAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                RuntimeCommandKind.ProvisionVoiceWorkspace => await ProvisionVoiceWorkspaceAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                RuntimeCommandKind.ListDesktopTasks => await ListDesktopTasksAsync(request, activeSettings, cancellationToken)
                    .ConfigureAwait(false),
                RuntimeCommandKind.NavigateToDesktopTask => await NavigateToDesktopTaskAsync(request, activeSettings, cancellationToken)
                    .ConfigureAwait(false),
                RuntimeCommandKind.ReadVoiceWakeTuning => await ReadVoiceWakeTuningAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                RuntimeCommandKind.WriteVoiceWakeTuning => await WriteVoiceWakeTuningAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                RuntimeCommandKind.InspectDesktopBridge => InspectDesktopBridge(request),
                RuntimeCommandKind.InstallDesktopBridge => ChangeDesktopBridge(request, install: true),
                RuntimeCommandKind.RemoveDesktopBridge => ChangeDesktopBridge(request, install: false),
                RuntimeCommandKind.ReadPebbleIndexAccess => ReadPebbleIndexAccess(request, activeSettings),
                RuntimeCommandKind.DeleteLegacyVoiceCaptures => DeleteLegacyVoiceCaptures(request),
                RuntimeCommandKind.ReadVoiceOutboxDelivery => ReadVoiceOutboxDelivery(request, activeSettings),
                RuntimeCommandKind.RetryVoiceOutboxDelivery => await RetryOutboxAsync(request, activeSettings, cancellationToken)
                    .ConfigureAwait(false),
                RuntimeCommandKind.RetargetVoiceOutboxDelivery => await RetargetOutboxAsync(request, activeSettings, cancellationToken)
                    .ConfigureAwait(false),
                RuntimeCommandKind.DiscardVoiceOutboxDelivery => DiscardOutbox(request, activeSettings),
                RuntimeCommandKind.SetJoydexStartAtLogin => SetJoydexStartAtLogin(request),
                RuntimeCommandKind.SetLinkToolStartAtLogin => SetLinkToolStartAtLogin(request),
                RuntimeCommandKind.InspectTaskAlertHooks => InspectTaskAlertHooksCommand(request),
                RuntimeCommandKind.InstallTaskAlertHooks => ChangeTaskAlertHooks(request, install: true),
                RuntimeCommandKind.RemoveTaskAlertHooks => ChangeTaskAlertHooks(request, install: false),
                _ => Rejected(request, $"The {request.Kind} command is unavailable in the production adapter."),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new RuntimeCommandResult(
                request.OperationId,
                request.Kind,
                RuntimeCommandStatus.Failed,
                exception.Message);
        }
    }

    private async Task<RuntimeCommandResult> ListVoiceProjectsAsync(
        RuntimeCommandRequest request,
        CancellationToken cancellationToken)
    {
        var service = new CodexVoiceWorkspaceService(request.Arguments?.CodexAppServerPath, WriteLog);
        var catalog = await service.ListProjectRootsAsync(cancellationToken).ConfigureAwait(false);
        return Completed(
            request,
            catalog.Warning,
            new RuntimeCommandPayload(
                VoiceProjects: catalog.Roots.Select(project => new RuntimeVoiceProject(
                    project.ProjectId,
                    project.ProjectName,
                    project.RootPath)).ToArray()));
    }

    private async Task<RuntimeCommandResult> ProvisionVoiceWorkspaceAsync(
        RuntimeCommandRequest request,
        CancellationToken cancellationToken)
    {
        var argument = request.Arguments?.VoiceWorkspace
            ?? throw new InvalidDataException("Voice workspace arguments are required.");
        var service = new CodexVoiceWorkspaceService(request.Arguments?.CodexAppServerPath, WriteLog);
        var result = await service.ProvisionAsync(
                new CodexVoiceWorkspaceProvisioningRequest(
                    argument.WorkspacePath,
                    argument.ProjectId,
                    argument.ProjectLabel,
                    argument.RegisterProject,
                    argument.TaskName),
                cancellationToken)
            .ConfigureAwait(false);
        return Completed(
            request,
            result.Warning,
            new RuntimeCommandPayload(
                VoiceWorkspace: new RuntimeVoiceWorkspaceResult(
                    result.WorkspacePath,
                    result.ProjectId,
                    result.ProjectLabel,
                    result.TaskId,
                    result.Warning)));
    }

    private async Task<RuntimeCommandResult> ListDesktopTasksAsync(
        RuntimeCommandRequest request,
        SettingsBundle activeSettings,
        CancellationToken cancellationToken)
    {
        var source = ResolveDesktopTaskListSource(request, activeSettings);
        await using var lease = await _desktopBroker.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var catalog = await new DesktopTaskBridgeClient(lease.PipeName)
            .ListTasksAsync(source.SourceTaskId, source.ExcludedTaskId, cancellationToken)
            .ConfigureAwait(false);
        PublishDesktopTasks(catalog.Tasks);
        return Completed(
            request,
            payload: new RuntimeCommandPayload(
                DesktopTasks: catalog.Tasks.Select(ToRuntimeTask).ToArray()));
    }

    internal static (string SourceTaskId, string? ExcludedTaskId) ResolveDesktopTaskListSource(
        RuntimeCommandRequest request,
        SettingsBundle activeSettings)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(activeSettings);
        if (request.Arguments?.Task is { } requestedSource)
        {
            if (!string.Equals(requestedSource.HostId, "local", StringComparison.OrdinalIgnoreCase)
                || !CodexTaskReference.TryParse(requestedSource.TaskId, out var taskId))
            {
                throw new InvalidDataException("A valid local Desktop task source is required.");
            }
            return (taskId, null);
        }

        var voiceSourceTaskId = RequireVoiceSourceTaskId(activeSettings.Voice);
        return (voiceSourceTaskId, voiceSourceTaskId);
    }

    private async Task<RuntimeCommandResult> NavigateToDesktopTaskAsync(
        RuntimeCommandRequest request,
        SettingsBundle activeSettings,
        CancellationToken cancellationToken)
    {
        var navigation = CreateDesktopTaskNavigationRequest(request);
        var navigator = new TaskDeepLinkNavigator(activeSettings.Companion.Safety, WriteLog);
        if (!await navigator.NavigateAsync(navigation, cancellationToken)
            .ConfigureAwait(false))
        {
            return Rejected(request, "The Desktop task could not be opened under the active safety settings.");
        }
        return Completed(request);
    }

    internal static TaskAlertNavigationRequest CreateDesktopTaskNavigationRequest(
        RuntimeCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requested = request.Arguments?.Task;
        if (requested is null
            || !string.Equals(requested.HostId, "local", StringComparison.OrdinalIgnoreCase)
            || !CodexTaskReference.TryParse(requested.TaskId, out var taskId))
        {
            throw new InvalidDataException("A valid local Desktop task target is required.");
        }

        return new TaskAlertNavigationRequest(0, 0, 0, taskId);
    }

    private static async Task<RuntimeCommandResult> ReadVoiceWakeTuningAsync(
        RuntimeCommandRequest request,
        CancellationToken cancellationToken)
    {
        using var client = new EspHomeVoicePeTuningClient(
            request.Arguments?.VoiceEndpoint
            ?? throw new InvalidDataException("A Voice PE endpoint is required."));
        var tuning = await client.GetAsync(cancellationToken).ConfigureAwait(false);
        return Completed(request, payload: new RuntimeCommandPayload(VoiceWakeTuning: tuning));
    }

    private static async Task<RuntimeCommandResult> WriteVoiceWakeTuningAsync(
        RuntimeCommandRequest request,
        CancellationToken cancellationToken)
    {
        using var client = new EspHomeVoicePeTuningClient(
            request.Arguments?.VoiceEndpoint
            ?? throw new InvalidDataException("A Voice PE endpoint is required."));
        var tuning = await client.SetAsync(
                request.Arguments?.VoiceWakeTuning
                ?? throw new InvalidDataException("Voice wake tuning is required."),
                cancellationToken)
            .ConfigureAwait(false);
        return Completed(request, payload: new RuntimeCommandPayload(VoiceWakeTuning: tuning));
    }

    private RuntimeCommandResult InspectDesktopBridge(RuntimeCommandRequest request)
    {
        var status = CreateDesktopBridgeConfiguration().Inspect(_paths.DesktopBridgeHost);
        return Completed(
            request,
            payload: new RuntimeCommandPayload(DesktopBridge: ToRuntimeBridgeStatus(status)));
    }

    private RuntimeCommandResult ChangeDesktopBridge(RuntimeCommandRequest request, bool install)
    {
        var manager = CreateDesktopBridgeConfiguration();
        if (install)
        {
            manager.InstallOrRepair(_paths.DesktopBridgeHost);
        }
        else
        {
            manager.Remove();
        }
        var status = manager.Inspect(_paths.DesktopBridgeHost);
        return Completed(
            request,
            payload: new RuntimeCommandPayload(DesktopBridge: ToRuntimeBridgeStatus(status)));
    }

    private RuntimeCommandResult ReadPebbleIndexAccess(
        RuntimeCommandRequest request,
        SettingsBundle activeSettings)
    {
        var preferences = activeSettings.PebbleIndex.Normalize();
        var secret = PebbleIndexSecretStore.LoadOrCreate(_paths.PebbleIndexSecret);
        return Completed(
            request,
            payload: new RuntimeCommandPayload(
                PebbleIndexAccess: new RuntimePebbleIndexAccess(
                    $"http://127.0.0.1:{preferences.Port}/pebble-index",
                    "Bearer " + secret)));
    }

    private static RuntimeCommandResult DeleteLegacyVoiceCaptures(RuntimeCommandRequest request)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Joydex",
            "voice-diagnostics");
        var deleted = 0;
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.wav", SearchOption.TopDirectoryOnly))
            {
                File.Delete(path);
                deleted++;
            }
        }
        return Completed(request, payload: new RuntimeCommandPayload(DeletedFileCount: deleted));
    }

    private async Task<RuntimeCommandResult> RetryOutboxAsync(
        RuntimeCommandRequest request,
        SettingsBundle activeSettings,
        CancellationToken cancellationToken)
    {
        var (outbox, draft) = FindOutboxDraft(request, activeSettings.Voice);
        var sourceTaskId = RequireVoiceSourceTaskId(activeSettings.Voice);
        var draftRemoved = false;
        var deliveryConfirmed = false;
        try
        {
            await using var lease = await _desktopBroker.AcquireAsync(cancellationToken).ConfigureAwait(false);
            var client = new DesktopTaskBridgeClient(lease.PipeName);
            var target = await client.ResolveTaskAsync(
                    sourceTaskId,
                    draft.TargetTaskId,
                    draft.TargetHostId,
                    cancellationToken)
                .ConfigureAwait(false);
            outbox.Remove(draft.Id);
            draftRemoved = true;
            var result = await client.SendMessageAsync(
                    sourceTaskId,
                    target,
                    draft.Message,
                    cancellationToken)
                .ConfigureAwait(false);
            deliveryConfirmed = true;
            RefreshVoiceMessaging(activeSettings.Voice);
            return Completed(request, result.Queued ? "Queued to the running task." : "Delivered.");
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            if (draftRemoved)
            {
                RecordUnconfirmedOutboxFailure(outbox, draft, deliveryConfirmed, exception);
                RefreshVoiceMessaging(activeSettings.Voice);
            }
            throw;
        }
        catch (Exception exception)
        {
            RecordUnconfirmedOutboxFailure(outbox, draft, deliveryConfirmed, exception);
            RefreshVoiceMessaging(activeSettings.Voice);
            throw;
        }
    }

    internal static void RecordUnconfirmedOutboxFailure(
        VoiceTaskOutbox outbox,
        VoiceTaskOutboxDraft draft,
        bool deliveryConfirmed,
        Exception exception)
    {
        if (!deliveryConfirmed)
        {
            outbox.RecordFailedAttempt(draft, exception.Message);
        }
    }

    private static RuntimeCommandResult ReadVoiceOutboxDelivery(
        RuntimeCommandRequest request,
        SettingsBundle activeSettings)
    {
        var (_, draft) = FindOutboxDraft(request, activeSettings.Voice);
        return Completed(
            request,
            payload: new RuntimeCommandPayload(
                VoiceOutboxDelivery: new RuntimeVoiceOutboxDelivery(
                    draft.Id,
                    new RuntimeTaskReference(draft.TargetTaskId, draft.TargetHostId),
                    draft.TargetTitle,
                    draft.Message,
                    draft.CreatedAt,
                    draft.Attempts,
                    draft.LatestError)));
    }

    private async Task<RuntimeCommandResult> RetargetOutboxAsync(
        RuntimeCommandRequest request,
        SettingsBundle activeSettings,
        CancellationToken cancellationToken)
    {
        var requested = request.Arguments?.Task
            ?? throw new InvalidDataException("A new outbox target is required.");
        var (outbox, draft) = FindOutboxDraft(request, activeSettings.Voice);
        var sourceTaskId = RequireVoiceSourceTaskId(activeSettings.Voice);
        await using var lease = await _desktopBroker.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var target = await new DesktopTaskBridgeClient(lease.PipeName)
            .ResolveTaskAsync(sourceTaskId, requested.TaskId, requested.HostId, cancellationToken)
            .ConfigureAwait(false);
        outbox.Retarget(draft, target);
        RefreshVoiceMessaging(activeSettings.Voice);
        return Completed(request);
    }

    private RuntimeCommandResult DiscardOutbox(
        RuntimeCommandRequest request,
        SettingsBundle activeSettings)
    {
        var (outbox, draft) = FindOutboxDraft(request, activeSettings.Voice);
        outbox.Remove(draft.Id);
        RefreshVoiceMessaging(activeSettings.Voice);
        return Completed(request);
    }

    private RuntimeCommandResult SetJoydexStartAtLogin(RuntimeCommandRequest request)
    {
        var enabled = request.Arguments?.Enabled == true;
        var available = File.Exists(_paths.JoydexApplication);
        var registration = new LoginStartupRegistration(
            _paths.JoydexApplication,
            "Joydex",
            ["--config", _paths.CompanionConfiguration]);
        registration.SetEnabled(enabled && available);
        return Completed(
            request,
            enabled && !available ? "Joydex.App.exe is unavailable, so login startup was not enabled." : null,
            payload: new RuntimeCommandPayload(
                StartupRegistration: new RuntimeStartupRegistrationStatus(
                    registration.IsEnabled,
                    available,
                    available ? null : "Joydex.App.exe is unavailable.")));
    }

    private static RuntimeCommandResult SetLinkToolStartAtLogin(RuntimeCommandRequest request)
    {
        var executable = VirpilLinkToolLocator.FindInstalledPath();
        if (executable is null)
        {
            return Completed(
                request,
                "VIRPIL LinkTool is not installed in a standard location.",
                new RuntimeCommandPayload(
                    StartupRegistration: new RuntimeStartupRegistrationStatus(false, false, "VIRPIL LinkTool was not found.")));
        }

        var registration = new LoginStartupRegistration(executable, "Joydex.VirpilLinkTool");
        registration.SetEnabled(request.Arguments?.Enabled == true);
        return Completed(
            request,
            payload: new RuntimeCommandPayload(
                StartupRegistration: new RuntimeStartupRegistrationStatus(registration.IsEnabled, true)));
    }

    private RuntimeCommandResult InspectTaskAlertHooksCommand(RuntimeCommandRequest request) =>
        Completed(
            request,
            payload: new RuntimeCommandPayload(TaskAlertHooks: InspectTaskAlertHooks()));

    private RuntimeCommandResult ChangeTaskAlertHooks(RuntimeCommandRequest request, bool install)
    {
        var manager = CreateTaskAlertHookManager();
        if (install)
        {
            manager.InstallOrRepair(TaskAlertHookRelayPath());
        }
        else
        {
            manager.Remove();
        }

        var status = InspectTaskAlertHooks();
        TaskAlertSnapshot? snapshot;
        lock (_stateGate)
        {
            snapshot = _taskAlerts?.Snapshot;
        }
        if (snapshot is not null)
        {
            _ui.PublishTaskAlerts(snapshot, status);
        }
        return Completed(
            request,
            payload: new RuntimeCommandPayload(TaskAlertHooks: status));
    }

    private DesktopBridgeConfigurationManager CreateDesktopBridgeConfiguration() =>
        new(DesktopBridgeConfigurationManager.DefaultConfigPath());

    private static CodexHookManager CreateTaskAlertHookManager() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex",
        "hooks.json"));

    private static string TaskAlertHookRelayPath() =>
        Path.Combine(AppContext.BaseDirectory, "Joydex.HookRelay.exe");

    private static (VoiceTaskOutbox Outbox, VoiceTaskOutboxDraft Draft) FindOutboxDraft(
        RuntimeCommandRequest request,
        VoicePePreferences preferences)
    {
        var normalized = preferences.Normalize();
        if (string.IsNullOrWhiteSpace(normalized.AgentWorkspacePath))
        {
            throw new InvalidOperationException("The Voice Agent Workspace is unavailable.");
        }
        var id = request.Arguments?.DeliveryId
            ?? throw new InvalidDataException("An outbox delivery ID is required.");
        var outbox = new VoiceTaskOutbox(normalized.AgentWorkspacePath);
        var draft = outbox.Load().SingleOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("The Voice outbox delivery no longer exists.", id);
        return (outbox, draft);
    }

    private static string RequireVoiceSourceTaskId(VoicePePreferences preferences)
    {
        if (!CodexTaskReference.TryParse(preferences.Normalize().DedicatedTaskId, out var taskId))
        {
            throw new InvalidOperationException("A valid Dedicated Voice Task is required for Desktop task messaging.");
        }
        return taskId;
    }

    private static RuntimeDesktopTask ToRuntimeTask(DesktopTaskSummary task) => new(
        task.Id,
        task.HostId,
        task.Title,
        task.Status,
        task.ProjectId,
        task.WorkingDirectory,
        task.UpdatedAt,
        task.Pinned);

    private static RuntimeDesktopBridgeStatus ToRuntimeBridgeStatus(DesktopBridgeConfigurationStatus status) => new(
        status.State switch
        {
            DesktopBridgeConfigurationState.NotInstalled => RuntimeDesktopBridgeState.NotInstalled,
            DesktopBridgeConfigurationState.Installed => RuntimeDesktopBridgeState.Installed,
            DesktopBridgeConfigurationState.RepairNeeded => RuntimeDesktopBridgeState.RepairNeeded,
            DesktopBridgeConfigurationState.Conflict => RuntimeDesktopBridgeState.Conflict,
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        },
        status.Message);

    private static RuntimeCommandResult Completed(
        RuntimeCommandRequest request,
        string? detail = null,
        RuntimeCommandPayload? payload = null) =>
        new(request.OperationId, request.Kind, RuntimeCommandStatus.Completed, detail, payload);

    private static RuntimeCommandResult Rejected(RuntimeCommandRequest request, string detail) =>
        new(request.OperationId, request.Kind, RuntimeCommandStatus.Rejected, detail);
}
