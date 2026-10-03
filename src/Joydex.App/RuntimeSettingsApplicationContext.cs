using System.Text.Json;
using Joydex.Contracts;
using Joydex.Core.Config;
using Joydex.Core.Voice;
using Joydex.Ipc;
using Joydex.Windows.Voice;

namespace Joydex.App;

/// <summary>
/// Owns one Settings window and its connection-scoped projections. Runtime owners live in
/// RuntimeHost; closing this context releases only the Settings client and its capture leases.
/// </summary>
internal sealed class RuntimeSettingsApplicationContext : ApplicationContext
{
    private readonly string _configurationPath;
    private readonly string _dataRoot;
    private readonly bool _demoMode;
    private readonly SynchronizationContext _ui;
    private readonly RuntimeSettingsProcessClient _client;
    private readonly RuntimeConfigurationInputClient _input = new();
    private readonly SemaphoreSlim _presentationGate = new(1, 1);
    private readonly Dictionary<RuntimeCommandKind, PendingCommand> _pendingCommands = [];
    private RuntimeSettingsDraftController? _draft;
    private RuntimeSettingsWriter? _writer;
    private RuntimeClientConnection? _connection;
    private RuntimeClientState? _state;
    private IRuntimeRpcServer? _rpc;
    private ConfigurationForm? _form;
    private RoomVoiceSettingsControl? _roomVoiceSettings;
    private PebbleIndexSettingsControl? _pebbleIndexSettings;
    private long _connectionGeneration = long.MinValue;
    private int _inputGeneration = int.MinValue;
    private bool _exitStarted;

    public RuntimeSettingsApplicationContext(
        RuntimeSettingsChannelMessage bootstrap,
        Stream channel,
        SynchronizationContext uiContext)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        _ui = uiContext ?? throw new ArgumentNullException(nameof(uiContext));
        _configurationPath = Path.GetFullPath(
            bootstrap.ConfigurationPath
            ?? throw new InvalidDataException("The Settings bootstrap omitted its configuration path."));
        _dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            bootstrap.DataRoot
            ?? throw new InvalidDataException("The Settings bootstrap omitted its data root.")));
        if (!string.Equals(
                Path.GetDirectoryName(_configurationPath),
                _dataRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The Settings bootstrap configuration is outside its selected data root.");
        }
        _demoMode = bootstrap.InstanceKind == RuntimeInstanceKind.Synthetic;

        _client = new RuntimeSettingsProcessClient(bootstrap, channel, _ui);
        _client.ConnectionChanged += OnConnectionChanged;
        _client.ActivateRequested += OnActivateRequested;

        // Initial attach may complete before event subscribers are installed. Render Current
        // explicitly, then let its generation-tagged event replace this provisional projection.
        if (_client.Current is { } current)
        {
            _ = HandleConnectedAsync(long.MinValue + 1, current, current.State, current.Rpc);
        }
        _ = ObserveClientCompletionAsync();
    }

    protected override void ExitThreadCore()
    {
        if (!_exitStarted)
        {
            BeginExit();
            return;
        }
        base.ExitThreadCore();
    }

    private void OnConnectionChanged(
        object? sender,
        RuntimeSettingsProcessConnectionChangedEventArgs eventArgs)
    {
        if (eventArgs.IsConnected
            && eventArgs.Connection is { } connection
            && eventArgs.State is { } state
            && eventArgs.Rpc is { } rpc)
        {
            _ = HandleConnectedAsync(eventArgs.Generation, connection, state, rpc);
            return;
        }
        _ = HandleDisconnectedAsync(eventArgs.Generation, eventArgs.Failure);
    }

    private async Task HandleConnectedAsync(
        long generation,
        RuntimeClientConnection connection,
        RuntimeClientState state,
        IRuntimeRpcServer rpc)
    {
        await _presentationGate.WaitAsync().ConfigureAwait(true);
        var hadPresentedForm = false;
        try
        {
            if (_exitStarted || generation <= _connectionGeneration)
            {
                return;
            }

            hadPresentedForm = _form is { IsDisposed: false };
            await CaptureVisibleDraftAsync().ConfigureAwait(true);
            if (_exitStarted || generation <= _connectionGeneration)
            {
                return;
            }
            if (_state is not null)
            {
                _state.Changed -= OnRuntimeStateChanged;
            }

            _connectionGeneration = generation;
            _inputGeneration = NextInputGeneration(_inputGeneration);
            _connection = connection;
            _state = state;
            _rpc = rpc;
            state.Changed += OnRuntimeStateChanged;

            var snapshot = GetCheckedSnapshot(state);
            var writer = new RuntimeSettingsWriter(
                rpc,
                snapshot.EngineEpoch,
                () => IsCurrentConnection(generation, connection, state, rpc),
                () => GetCheckedSnapshot(state));
            _writer = writer;
            if (_draft is null)
            {
                _draft = new RuntimeSettingsDraftController(writer, snapshot);
            }
            else
            {
                _draft.BeginConnection(writer, snapshot);
            }

            _input.BeginConnection(_inputGeneration, rpc, state);
            EnsureForm(snapshot);
            ApplySnapshot(snapshot);
            _form!.SetRuntimeConnectionAvailable(true, "Connected to the Joydex runtime.");
            _form.ShowDraftState(_draft.Current);
            _form.ShowRuntimeSettingsStatus(snapshot.Settings);
        }
        catch (Exception exception)
        {
            if (hadPresentedForm && _form is { IsDisposed: false } form)
            {
                form.SetRuntimeConnectionAvailable(
                    false,
                    "The Joydex runtime connection could not initialize. Your draft is still open. "
                    + exception.Message);
                return;
            }

            try
            {
                MessageBox.Show(
                    exception.Message,
                    "Joydex settings could not start",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                BeginExit();
            }
        }
        finally
        {
            _presentationGate.Release();
        }
    }

    private async Task HandleDisconnectedAsync(long generation, Exception? failure)
    {
        await _presentationGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_exitStarted || generation < _connectionGeneration)
            {
                return;
            }
            await CaptureVisibleDraftAsync().ConfigureAwait(true);
            if (_exitStarted || generation < _connectionGeneration)
            {
                return;
            }
            if (_state is not null)
            {
                _state.Changed -= OnRuntimeStateChanged;
            }
            _input.EndConnection(_inputGeneration, failure);
            _connection = null;
            _state = null;
            _rpc = null;
            _writer = null;
            _form?.SetRuntimeConnectionAvailable(
                false,
                failure is null
                    ? "The Joydex runtime is reconnecting. Your draft is still open."
                    : "The Joydex runtime is reconnecting. Your draft is still open. "
                        + failure.Message);
        }
        finally
        {
            _presentationGate.Release();
        }
    }

    private async void OnRuntimeStateChanged(object? sender, RuntimeClientStateChange change)
    {
        var state = _state;
        var connection = _connection;
        var rpc = _rpc;
        var generation = _connectionGeneration;
        if (state is null
            || connection is null
            || rpc is null
            || !ReferenceEquals(sender, state))
        {
            return;
        }

        _input.ApplyStateChange(_inputGeneration, change);
        if (change.Kind == RuntimeClientChangeKind.Disconnected)
        {
            return;
        }

        await _presentationGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (!IsCurrentConnection(generation, connection, state, rpc))
            {
                return;
            }
            if (change.Kind == RuntimeClientChangeKind.ResynchronizationRequired)
            {
                var refreshed = await rpc.GetSnapshotAsync(CancellationToken.None)
                    .ConfigureAwait(true);
                if (IsCurrentConnection(generation, connection, state, rpc))
                {
                    state.ApplySnapshot(refreshed);
                }
                return;
            }
            if (change.State.Snapshot is { } snapshot)
            {
                if (_draft is not null
                    && (snapshot.Settings.Revision != _draft.Current.BaseRevision
                        || snapshot.EngineEpoch != _draft.Current.EngineEpoch))
                {
                    await CaptureVisibleDraftAsync().ConfigureAwait(true);
                    if (!IsCurrentConnection(generation, connection, state, rpc))
                    {
                        return;
                    }
                    _draft.BeginConnection(_writer!, snapshot);
                }
                ApplySnapshot(snapshot);
                _form?.ShowDraftState(_draft!.Current);
                _form?.ShowRuntimeSettingsStatus(snapshot.Settings);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _form?.SetRuntimeConnectionAvailable(
                false,
                "Runtime state needs to reconnect before settings can be applied. " + exception.Message);
        }
        catch (OperationCanceledException exception)
        {
            if (!_exitStarted)
            {
                _form?.SetRuntimeConnectionAvailable(
                    false,
                    "The runtime connection changed while settings were refreshing. "
                    + exception.Message);
            }
        }
        finally
        {
            _presentationGate.Release();
        }
    }

    private void EnsureForm(RuntimeSnapshot snapshot)
    {
        if (_form is { IsDisposed: false })
        {
            return;
        }

        var desired = _draft?.Current.DraftSettings ?? snapshot.Settings.Desired;
        var pebbleStatus = snapshot.Ui?.PebbleIndex;
        _roomVoiceSettings = CreateRoomVoiceSettings(desired.Voice);
        _pebbleIndexSettings = new PebbleIndexSettingsControl(
            desired.PebbleIndex,
            Path.Combine(_dataRoot, "pebble-index.secret"),
            pebbleStatus?.InboxPath ?? Path.Combine(_dataRoot, "pebble-index", "inbox"),
            ListDesktopTasksAsync,
            new PebbleIndexReceiverStatus(
                pebbleStatus?.Running == true,
                pebbleStatus?.Message ?? "Pebble Index runtime status is unavailable.",
                OutstandingCount: pebbleStatus?.OutstandingCount ?? 0),
            allowExternalActions: !_demoMode,
            readAccess: ReadPebbleIndexAccessAsync);

        _form = new ConfigurationForm(
            _configurationPath,
            Path.Combine(_dataRoot, "configuration-window.json"),
            IntPtr.Zero,
            roomVoiceSettings: _roomVoiceSettings,
            pebbleIndexSettings: _pebbleIndexSettings,
            initialConfig: desired.Companion,
            demoMode: _demoMode,
            inputSession: _input,
            applyConfiguration: ApplyConfigurationAsync,
            initialSettings: _draft?.Current.BaseSettings ?? snapshot.Settings.Desired);
        _form.ReviewLatestRequested += OnReviewLatestRequested;
        _form.DiscardDraftRequested += OnDiscardDraftRequested;
        _form.FormClosed += OnConfigurationClosed;
        _form.Show();
    }

    private RoomVoiceSettingsControl CreateRoomVoiceSettings(VoicePePreferences preferences) =>
        new(
            preferences,
            TestVoiceTargetAsync,
            ReadVoiceWakeTuningAsync,
            WriteVoiceWakeTuningAsync,
            ListVoiceProjectsAsync,
            ProvisionVoiceWorkspaceAsync,
            InspectDesktopBridgeAsync,
            InstallDesktopBridgeAsync,
            RemoveDesktopBridgeAsync,
            DeleteLegacyVoiceCapturesAsync,
            allowExternalActions: !_demoMode);

    private async Task<RuntimeSettingsWriteResult> ApplyConfigurationAsync(
        CompanionConfig companion,
        VoicePePreferences? voice,
        PebbleIndexPreferences? pebbleIndex,
        CancellationToken cancellationToken)
    {
        var draft = _draft
            ?? throw new InvalidOperationException("The settings draft is unavailable.");
        var candidate = draft.Current.DraftSettings with
        {
            Companion = companion,
            Voice = voice ?? draft.Current.DraftSettings.Voice,
            PebbleIndex = pebbleIndex ?? draft.Current.DraftSettings.PebbleIndex,
        };
        draft.UpdateDraft(candidate);
        var result = await draft.ApplyAsync(candidate, cancellationToken).ConfigureAwait(true);
        _form?.ShowDraftState(draft.Current);
        return result;
    }

    private void OnReviewLatestRequested(object? sender, ConfigurationDraftEventArgs eventArgs)
    {
        var draft = _draft;
        var form = _form;
        if (draft is null || form is null)
        {
            return;
        }
        try
        {
            draft.UpdateDraft(draft.Current.DraftSettings with
            {
                Companion = eventArgs.Companion,
                Voice = eventArgs.Voice ?? draft.Current.DraftSettings.Voice,
                PebbleIndex = eventArgs.PebbleIndex ?? draft.Current.DraftSettings.PebbleIndex,
            });
            draft.RebaseToLatest();
            form.ApplyDraftState(draft.Current);
        }
        catch (Exception exception)
        {
            form.SetRuntimeConnectionAvailable(true, exception.Message);
        }
    }

    private async void OnDiscardDraftRequested(object? sender, EventArgs eventArgs)
    {
        var draft = _draft;
        var form = _form;
        if (draft is null || form is null)
        {
            return;
        }
        try
        {
            await form.QuiesceCapturesAsync().ConfigureAwait(true);
            if (_exitStarted
                || !ReferenceEquals(_draft, draft)
                || !ReferenceEquals(_form, form))
            {
                return;
            }
            draft.DiscardDraft();
            form.ApplyDraftState(draft.Current);
        }
        catch (Exception exception)
        {
            form.SetRuntimeConnectionAvailable(true, exception.Message);
        }
    }

    private async Task CaptureVisibleDraftAsync()
    {
        if (_form is null || _form.IsDisposed || _draft is null)
        {
            return;
        }
        var visible = await _form.ProjectCurrentDraftAsync().ConfigureAwait(true);
        if (visible is not null)
        {
            _draft.UpdateDraft(visible with
            {
                TaskAlerts = _draft.Current.DraftSettings.TaskAlerts,
            });
        }
    }

    private void ApplySnapshot(RuntimeSnapshot snapshot)
    {
        if (snapshot.Ui?.PebbleIndex is { } pebble)
        {
            _pebbleIndexSettings?.ApplyStatus(pebble);
        }
    }

    private async Task<bool> TestVoiceTargetAsync(VoicePePreferences preferences)
    {
        var result = await ExecuteCommandAsync(
                RuntimeCommandKind.NavigateToDesktopTask,
                new RuntimeCommandArguments(
                    Task: new RuntimeTaskReference(preferences.PinnedTaskId, "local")),
                CancellationToken.None)
            .ConfigureAwait(true);
        return result.Status == RuntimeCommandStatus.Completed;
    }

    private async Task<VoicePeWakeTuning> ReadVoiceWakeTuningAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteCommandAsync(
                RuntimeCommandKind.ReadVoiceWakeTuning,
                new RuntimeCommandArguments(VoiceEndpoint: endpoint),
                cancellationToken)
            .ConfigureAwait(true);
        return RequireCompleted(result).Payload?.VoiceWakeTuning
            ?? throw new InvalidDataException("The runtime omitted Voice wake tuning.");
    }

    private async Task<VoicePeWakeTuning> WriteVoiceWakeTuningAsync(
        Uri endpoint,
        VoicePeWakeTuning tuning,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteCommandAsync(
                RuntimeCommandKind.WriteVoiceWakeTuning,
                new RuntimeCommandArguments(VoiceEndpoint: endpoint, VoiceWakeTuning: tuning),
                cancellationToken)
            .ConfigureAwait(true);
        return RequireCompleted(result).Payload?.VoiceWakeTuning
            ?? throw new InvalidDataException("The runtime omitted the applied Voice wake tuning.");
    }

    private async Task<CodexProjectCatalog> ListVoiceProjectsAsync(
        string appServerPath,
        CancellationToken cancellationToken)
    {
        var result = RequireCompleted(await ExecuteCommandAsync(
                RuntimeCommandKind.ListVoiceProjects,
                new RuntimeCommandArguments(CodexAppServerPath: appServerPath),
                cancellationToken)
            .ConfigureAwait(true));
        return new CodexProjectCatalog(
            (result.Payload?.VoiceProjects ?? [])
                .Select(project => new CodexLocalProjectRoot(
                    project.ProjectId,
                    project.ProjectName,
                    project.RootPath))
                .ToArray());
    }

    private async Task<CodexVoiceWorkspaceProvisioningResult> ProvisionVoiceWorkspaceAsync(
        string appServerPath,
        CodexVoiceWorkspaceProvisioningRequest request,
        CancellationToken cancellationToken)
    {
        var result = RequireCompleted(await ExecuteCommandAsync(
                RuntimeCommandKind.ProvisionVoiceWorkspace,
                new RuntimeCommandArguments(
                    VoiceWorkspace: new RuntimeVoiceWorkspaceRequest(
                        request.WorkspacePath,
                        request.ProjectId,
                        request.ProjectLabel,
                        request.RegisterProject,
                        request.TaskName),
                    CodexAppServerPath: appServerPath),
                cancellationToken)
            .ConfigureAwait(true));
        var workspace = result.Payload?.VoiceWorkspace
            ?? throw new InvalidDataException("The runtime omitted the provisioned Voice workspace.");
        return new CodexVoiceWorkspaceProvisioningResult(
            workspace.WorkspacePath,
            workspace.ProjectId,
            workspace.ProjectLabel,
            workspace.TaskId,
            workspace.Warning);
    }

    private async Task<DesktopTaskCatalog> ListDesktopTasksAsync(
        string? candidateSourceTaskId,
        CancellationToken cancellationToken)
    {
        var draft = _draft?.Current.DraftSettings
            ?? throw new InvalidOperationException("The settings draft is unavailable.");
        var sourceTaskId = ResolvePebbleIndexSourceTaskId(
            candidateSourceTaskId,
            draft.PebbleIndex,
            draft.Voice);
        var result = RequireCompleted(await ExecuteCommandAsync(
                RuntimeCommandKind.ListDesktopTasks,
                new RuntimeCommandArguments(
                    Task: new RuntimeTaskReference(sourceTaskId, "local")),
                cancellationToken)
            .ConfigureAwait(true));
        return new DesktopTaskCatalog((result.Payload?.DesktopTasks ?? [])
            .Select(task => new DesktopTaskSummary(
                task.TaskId,
                task.HostId,
                task.Title,
                task.Status,
                task.ProjectId,
                task.WorkingDirectory,
                task.UpdatedAt,
                task.Pinned))
            .ToArray());
    }

    private static string ResolvePebbleIndexSourceTaskId(
        string? candidateSourceTaskId,
        PebbleIndexPreferences pebbleIndex,
        VoicePePreferences voice)
    {
        foreach (var candidate in new[]
        {
            candidateSourceTaskId,
            pebbleIndex.TargetTaskId,
            voice.DedicatedTaskId,
        })
        {
            if (CodexTaskReference.TryParse(candidate, out var taskId))
            {
                return taskId;
            }
        }
        throw new InvalidOperationException(
            "Paste an existing Codex task ID before refreshing the Desktop task list.");
    }

    private Task<RuntimeDesktopBridgeStatus> InspectDesktopBridgeAsync(
        CancellationToken cancellationToken) =>
        RunDesktopBridgeCommandAsync(RuntimeCommandKind.InspectDesktopBridge, cancellationToken);

    private Task<RuntimeDesktopBridgeStatus> InstallDesktopBridgeAsync(
        CancellationToken cancellationToken) =>
        RunDesktopBridgeCommandAsync(RuntimeCommandKind.InstallDesktopBridge, cancellationToken);

    private Task<RuntimeDesktopBridgeStatus> RemoveDesktopBridgeAsync(
        CancellationToken cancellationToken) =>
        RunDesktopBridgeCommandAsync(RuntimeCommandKind.RemoveDesktopBridge, cancellationToken);

    private async Task<RuntimeDesktopBridgeStatus> RunDesktopBridgeCommandAsync(
        RuntimeCommandKind kind,
        CancellationToken cancellationToken)
    {
        var result = RequireCompleted(await ExecuteCommandAsync(
                kind,
                arguments: null,
                cancellationToken)
            .ConfigureAwait(true));
        return result.Payload?.DesktopBridge
            ?? throw new InvalidDataException("The runtime omitted Desktop Bridge status.");
    }

    private async Task<int> DeleteLegacyVoiceCapturesAsync(
        CancellationToken cancellationToken)
    {
        var result = RequireCompleted(await ExecuteCommandAsync(
                RuntimeCommandKind.DeleteLegacyVoiceCaptures,
                arguments: null,
                cancellationToken)
            .ConfigureAwait(true));
        return result.Payload?.DeletedFileCount
            ?? throw new InvalidDataException("The runtime omitted the deleted capture count.");
    }

    private async Task<RuntimePebbleIndexAccess> ReadPebbleIndexAccessAsync(
        CancellationToken cancellationToken)
    {
        // Access values are deliberately excluded from retained operation results, so a failed
        // read uses a fresh identity on the next explicit click.
        var connection = _connection
            ?? throw new InvalidOperationException("The runtime is reconnecting.");
        var state = _state
            ?? throw new InvalidOperationException("The runtime is reconnecting.");
        var rpc = _rpc
            ?? throw new InvalidOperationException("The runtime is reconnecting.");
        var generation = _connectionGeneration;
        EnsureCurrentConnection(connection, state, rpc, generation);
        var request = new RuntimeCommandRequest(
            Guid.NewGuid(),
            RuntimeCommandKind.ReadPebbleIndexAccess);
        var commandResult = await rpc.ExecuteCommandAsync(request, cancellationToken)
            .ConfigureAwait(true);
        EnsureCurrentConnection(connection, state, rpc, generation);
        var result = RequireCompleted(commandResult);
        return result.Payload?.PebbleIndexAccess
            ?? throw new InvalidDataException("The runtime omitted Pebble Index access details.");
    }

    private async Task<RuntimeCommandResult> ExecuteCommandAsync(
        RuntimeCommandKind kind,
        RuntimeCommandArguments? arguments,
        CancellationToken cancellationToken)
    {
        var connection = _connection
            ?? throw new InvalidOperationException("The runtime is reconnecting.");
        var state = _state
            ?? throw new InvalidOperationException("The runtime is reconnecting.");
        var rpc = _rpc
            ?? throw new InvalidOperationException("The runtime is reconnecting.");
        var generation = _connectionGeneration;
        EnsureCurrentConnection(connection, state, rpc, generation);
        var fingerprint = CommandFingerprint(arguments);
        if (_pendingCommands.TryGetValue(kind, out var pending))
        {
            var sameRequest = string.Equals(
                pending.Fingerprint,
                fingerprint,
                StringComparison.Ordinal);
            var recovered = await rpc.GetCommandOperationAsync(
                    pending.OperationId,
                    cancellationToken)
                .ConfigureAwait(true);
            EnsureCurrentConnection(connection, state, rpc, generation);
            if (recovered.State == RuntimeCommandOperationState.Completed
                && recovered.Result is { } completed)
            {
                ValidateCommandResult(completed, pending.OperationId, kind);
                RemovePendingCommand(kind, pending.OperationId);
                if (!sameRequest)
                {
                    throw new InvalidOperationException(
                        $"The earlier {kind} action was reconciled. Choose the action again to run the current request.");
                }
                return completed;
            }
            throw new InvalidOperationException(
                recovered.State == RuntimeCommandOperationState.Running
                    ? sameRequest
                        ? $"The {kind} action is still running."
                        : $"An earlier {kind} action is still running. Wait for it before running the changed request."
                    : $"The {kind} action could not be reconciled and was not resubmitted.");
        }

        var operationId = Guid.NewGuid();
        _pendingCommands[kind] = new PendingCommand(operationId, fingerprint);
        try
        {
            var result = await rpc.ExecuteCommandAsync(
                    new RuntimeCommandRequest(operationId, kind, arguments),
                    cancellationToken)
                .ConfigureAwait(true);
            EnsureCurrentConnection(connection, state, rpc, generation);
            ValidateCommandResult(result, operationId, kind);
            RemovePendingCommand(kind, operationId);
            return result;
        }
        catch
        {
            // The request may have reached the runtime. The next explicit action checks this
            // exact identity before another side effect is admitted.
            throw;
        }
    }

    private void EnsureCurrentConnection(
        RuntimeClientConnection connection,
        RuntimeClientState state,
        IRuntimeRpcServer rpc,
        long generation)
    {
        if (!IsCurrentConnection(generation, connection, state, rpc))
        {
            throw new OperationCanceledException(
                "The runtime connection changed before the command completed.");
        }
    }

    private void RemovePendingCommand(RuntimeCommandKind kind, Guid operationId)
    {
        if (_pendingCommands.TryGetValue(kind, out var pending)
            && pending.OperationId == operationId)
        {
            _pendingCommands.Remove(kind);
        }
    }

    private static string CommandFingerprint(RuntimeCommandArguments? arguments) =>
        JsonSerializer.Serialize(arguments);

    private static RuntimeCommandResult RequireCompleted(RuntimeCommandResult result)
    {
        if (result.Status != RuntimeCommandStatus.Completed)
        {
            throw new InvalidOperationException(result.Detail ?? "The runtime command failed.");
        }
        return result;
    }

    private static void ValidateCommandResult(
        RuntimeCommandResult result,
        Guid operationId,
        RuntimeCommandKind kind)
    {
        if (result.OperationId != operationId || result.Kind != kind)
        {
            throw new InvalidDataException("The runtime returned a result for a different command.");
        }
    }

    private bool IsCurrentConnection(
        long generation,
        RuntimeClientConnection connection,
        RuntimeClientState state,
        IRuntimeRpcServer rpc)
    {
        if (_exitStarted
            || generation != _connectionGeneration
            || !ReferenceEquals(_connection, connection)
            || !ReferenceEquals(_state, state)
            || !ReferenceEquals(_rpc, rpc))
        {
            return false;
        }
        var current = state.Current;
        return current.IsInitialized
            && !current.IsDisconnected
            && !current.ResynchronizationRequired
            && current.Snapshot is not null;
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
                "The runtime settings connection is unavailable or needs to refresh.");
        }
        return current.Snapshot;
    }

    private void OnActivateRequested(object? sender, EventArgs eventArgs)
    {
        if (_form is not { IsDisposed: false } form)
        {
            return;
        }
        if (form.WindowState == FormWindowState.Minimized)
        {
            form.WindowState = FormWindowState.Normal;
        }
        form.Show();
        form.BringToFront();
        form.Activate();
    }

    private void OnConfigurationClosed(object? sender, FormClosedEventArgs eventArgs)
    {
        if (_form is { } form)
        {
            form.ReviewLatestRequested -= OnReviewLatestRequested;
            form.DiscardDraftRequested -= OnDiscardDraftRequested;
            form.FormClosed -= OnConfigurationClosed;
        }
        _form = null;
        _roomVoiceSettings = null;
        _pebbleIndexSettings = null;
        BeginExit();
    }

    private async Task ObserveClientCompletionAsync()
    {
        try
        {
            await _client.Completion.ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            if (!_exitStarted && _form is { IsDisposed: false } form)
            {
                form.SetRuntimeConnectionAvailable(false, exception.Message);
            }
        }
        finally
        {
            BeginExit();
        }
    }

    private void BeginExit()
    {
        if (_exitStarted)
        {
            return;
        }
        _exitStarted = true;
        _ = ShutdownAndExitAsync();
    }

    private async Task ShutdownAndExitAsync()
    {
        await _presentationGate.WaitAsync().ConfigureAwait(true);
        try
        {
            _client.ConnectionChanged -= OnConnectionChanged;
            _client.ActivateRequested -= OnActivateRequested;
            if (_state is not null)
            {
                _state.Changed -= OnRuntimeStateChanged;
            }
            if (_form is { IsDisposed: false } form)
            {
                form.FormClosed -= OnConfigurationClosed;
                await form.QuiesceCapturesAsync().ConfigureAwait(true);
                form.Close();
                form.Dispose();
            }
            await _input.DisposeAsync().ConfigureAwait(true);
            await _client.DisposeAsync().ConfigureAwait(true);
        }
        finally
        {
            _form = null;
            _presentationGate.Release();
            ExitThread();
        }
    }

    private static int NextInputGeneration(int generation) => generation == int.MaxValue
        ? int.MinValue + 1
        : generation + 1;

    private sealed record PendingCommand(Guid OperationId, string Fingerprint);
}
