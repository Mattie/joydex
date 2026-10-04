using System.Diagnostics;
using System.Text.Json;
using Joydex.Contracts;
using Joydex.Core.Config;
using Joydex.Core.TaskAlerts;
using Joydex.Ipc;
using Joydex.Windows.Actions;

namespace Joydex.App;

/// <summary>
/// Presents tray-owned windows for the persistent RuntimeHost. Every runtime object shown here is
/// a projection; closing this UI does not dispose a controller, Voice, or inbound plugin owner.
/// </summary>
internal sealed class RuntimeTrayApplicationContext : ApplicationContext
{
    private const int MaximumConsecutiveHostExits = 2;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ConfigurationMismatchDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ExitShutdownTimeout = TimeSpan.FromSeconds(5);
    private readonly string _configurationPath;
    private readonly string _dataRoot;
    private readonly string _runtimeHostPath;
    private readonly bool _demoMode;
    private readonly RuntimeIpcEndpoint _endpoint;
    private readonly SynchronizationContext _ui;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Icon _icon;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _connectionItem;
    private readonly ToolStripMenuItem _controllersItem;
    private readonly ToolStripMenuItem _voiceItem;
    private readonly ToolStripMenuItem _configureItem;
    private readonly ToolStripMenuItem _promptPickersItem;
    private readonly ToolStripMenuItem _taskAlertsItem;
    private readonly ToolStripMenuItem _taskAlertsStatusItem;
    private readonly ToolStripMenuItem _modeItem;
    private readonly ToolStripMenuItem _testControlsItem;
    private readonly ToolStripMenuItem _startAtLoginItem;
    private readonly ToolStripMenuItem _startLinkToolAtLoginItem;
    private readonly RuntimeRoomVoiceWindowAdapter _voiceAdapter = new();
    private readonly RuntimeButtonMapWindowAdapter _buttonMaps;
    private readonly RuntimePromptPickerWindowAdapter _promptPicker;
    private readonly RuntimeConfigurationInputClient _input = new();
    private readonly RuntimeTaskAlertsConnectionServices _taskAlerts;
    private readonly ForegroundProcessGuard _foreground = new();
    private readonly LoginStartupRegistration? _joydexLoginStartup;
    private readonly LoginStartupRegistration? _linkToolLoginStartup;
    private readonly Queue<RuntimeActionActivity> _recentActivity = new();
    private readonly Dictionary<RuntimeCommandKind, PendingCommand> _pendingCommands = [];
    private readonly Dictionary<TraySettingsAction, PendingSettingsWrite> _pendingSettingsWrites = [];
    private readonly Task _connectTask;
    private RuntimeClientConnection? _connection;
    private RuntimeSnapshot? _snapshot;
    private RuntimeSettingsWriter? _settingsWriter;
    private RoomVoiceForm? _voiceForm;
    private TaskAlertsForm? _taskAlertsForm;
    private bool _taskAlertsShowPending;
    private DryRunActivityForm? _activityForm;
    private PromptPickerEditorForm? _promptPickerEditor;
    private long? _promptPickerEditorBaseRevision;
    private Process? _startedHost;
    private Guid _activityEpoch;
    private long _lastActivitySequence = -1;
    private long _connectionGeneration;
    private int _inputGeneration = int.MinValue;
    private bool _hostLaunchAttempted;
    private bool _admissionPreviouslyGranted;
    private int _consecutiveHostExits;
    private string? _lastHostExitDetail;
    private DateTimeOffset? _configurationMismatchDetectedAt;
    private bool _demoModeOpenedSettings;
    private bool _exitStarted;

    public RuntimeTrayApplicationContext(
        string configurationPath,
        bool demoMode = false,
        string? demoPipeName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        _configurationPath = Path.GetFullPath(configurationPath.Trim());
        _dataRoot = Path.GetDirectoryName(_configurationPath)
            ?? throw new InvalidOperationException(
                "The companion configuration path has no parent directory.");
        _runtimeHostPath = Path.Combine(AppContext.BaseDirectory, "Joydex.RuntimeHost.exe");
        _demoMode = demoMode;
        _endpoint = demoMode
            ? RuntimeIpcEndpoint.CreateSynthetic(
                _dataRoot,
                _configurationPath,
                string.IsNullOrWhiteSpace(demoPipeName)
                    ? "joydex-demo-" + Guid.NewGuid().ToString("N")
                    : demoPipeName.Trim())
            : RuntimeIpcEndpoint.CreateProduction(_dataRoot, _configurationPath);
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _icon = AppIconFactory.Create();
        _buttonMaps = new RuntimeButtonMapWindowAdapter(_ui, GetButtonMapStatePath);
        _promptPicker = new RuntimePromptPickerWindowAdapter(
            _ui,
            CodexStillForeground,
            SendCommandAsync);
        _taskAlerts = new RuntimeTaskAlertsConnectionServices(_ui);
        _taskAlerts.Changed += OnTaskAlertsChanged;

        _connectionItem = new ToolStripMenuItem("Runtime: Starting…") { Enabled = false };
        _controllersItem = new ToolStripMenuItem("Controllers: Starting…") { Enabled = false };
        _voiceItem = new ToolStripMenuItem("Room Voice", image: null, OnToggleVoice)
        {
            Enabled = false,
        };
        _taskAlertsItem = new ToolStripMenuItem("Task alerts", image: null, OnToggleTaskAlerts)
        {
            CheckOnClick = false,
            Enabled = false,
        };
        _configureItem = new ToolStripMenuItem(
            demoMode ? "Demo inspector…" : "Configure…",
            image: null,
            OnConfigure)
        {
            Enabled = false,
        };
        _promptPickersItem = new ToolStripMenuItem(
            "Prompt pickers…",
            image: null,
            OnPromptPickers)
        {
            Enabled = false,
        };
        _modeItem = new ToolStripMenuItem("Dry run", image: null, OnToggleDryRun)
        {
            CheckOnClick = false,
            Enabled = false,
        };
        _testControlsItem = new ToolStripMenuItem("Test controls…", image: null, OnTestControls)
        {
            Enabled = false,
        };
        _taskAlertsStatusItem = new ToolStripMenuItem(
            "Task alerts / ignored tasks…",
            image: null,
            OnTaskAlertsStatus)
        {
            Enabled = false,
        };

        if (!_demoMode)
        {
            _joydexLoginStartup = new LoginStartupRegistration(
                Environment.ProcessPath ?? Application.ExecutablePath,
                "Joydex",
                ["--config", _configurationPath]);
            var linkToolPath = VirpilLinkToolLocator.FindInstalledPath();
            _linkToolLoginStartup = linkToolPath is null
                ? null
                : new LoginStartupRegistration(linkToolPath, "Joydex.VirpilLinkTool");
        }
        _startAtLoginItem = new ToolStripMenuItem(
            "Start Joydex when I sign in",
            image: null,
            OnToggleStartAtLogin)
        {
            CheckOnClick = false,
            Enabled = !_demoMode && _joydexLoginStartup is not null,
        };
        _startLinkToolAtLoginItem = new ToolStripMenuItem(
            "Start VIRPIL LinkTool when I sign in",
            image: null,
            OnToggleStartLinkToolAtLogin)
        {
            CheckOnClick = false,
            Enabled = !_demoMode && _linkToolLoginStartup is not null,
        };
        InitializeStartupRegistrationItems();

        var reloadItem = new ToolStripMenuItem(
            "Reload configuration",
            image: null,
            OnReloadConfiguration)
        {
            Enabled = !_demoMode,
        };
        var openConfigItem = new ToolStripMenuItem(
            "Open config JSON…",
            image: null,
            (_, _) => OpenPath(_configurationPath))
        {
            Enabled = !_demoMode,
        };
        var openLogItem = new ToolStripMenuItem(
            "Open log",
            image: null,
            (_, _) => OpenPath(Path.Combine(_dataRoot, "joydex.log")))
        {
            Enabled = !_demoMode,
        };
        var advanced = new ToolStripMenuItem("Advanced");
        advanced.DropDownItems.AddRange([
            _modeItem,
            _testControlsItem,
            new ToolStripSeparator(),
            _taskAlertsStatusItem,
            reloadItem,
            openConfigItem,
            openLogItem,
        ]);
        var exitItem = new ToolStripMenuItem("Exit Joydex", image: null, (_, _) => BeginExit());
        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = new ContextMenuStrip
            {
                Items =
                {
                    _connectionItem,
                    _controllersItem,
                    _taskAlertsItem,
                    _voiceItem,
                    new ToolStripSeparator(),
                    _configureItem,
                    _promptPickersItem,
                    _startAtLoginItem,
                    _startLinkToolAtLoginItem,
                    new ToolStripSeparator(),
                    advanced,
                    new ToolStripSeparator(),
                    exitItem,
                },
            },
            Icon = _icon,
            Text = demoMode ? "Joydex — Demo" : "Joydex",
            Visible = true,
        };
        _notifyIcon.DoubleClick += OnConfigure;
        _connectTask = ConnectLoopAsync();
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

    private async Task ConnectLoopAsync()
    {
        await Task.Yield();
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                RuntimeBootstrapRendezvousResult admission;
                try
                {
                    admission = await RequestAdmissionAsync(_lifetime.Token).ConfigureAwait(true);
                }
                catch (Exception exception) when (exception is IOException
                                                  or TimeoutException
                                                  or InvalidOperationException)
                {
                    if (ResetExitedRuntimeHost()
                        && _consecutiveHostExits >= MaximumConsecutiveHostExits)
                    {
                        MessageBox.Show(
                            "Joydex RuntimeHost exited repeatedly before the App could connect. "
                            + (_lastHostExitDetail ?? "No exit details were available."),
                            "Joydex RuntimeHost could not start",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                        BeginExit(shutdownRuntime: false);
                        return;
                    }
                    var anotherProductionHostExists = !_demoMode && ProductionRuntimeOwnerExists();
                    if (!_hostLaunchAttempted && anotherProductionHostExists)
                    {
                        _configurationMismatchDetectedAt ??= DateTimeOffset.UtcNow;
                        if (DateTimeOffset.UtcNow - _configurationMismatchDetectedAt
                            >= ConfigurationMismatchDelay)
                        {
                            MessageBox.Show(
                                "Another production Joydex runtime owns this Windows session, but "
                                + "this App could not connect to it. It may come from another "
                                + "installation or use a different configuration path. Exit the other "
                                + "Joydex instance before starting this configuration.\n\nConfiguration: "
                                + _configurationPath,
                                "Joydex runtime mismatch",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error);
                            BeginExit(shutdownRuntime: false);
                            return;
                        }
                    }
                    else if (!_hostLaunchAttempted)
                    {
                        _configurationMismatchDetectedAt = null;
                        StartRuntimeHost();
                    }
                    SetConnectionStatus("Runtime: Waiting…", exception.Message);
                    await Task.Delay(RetryDelay, _lifetime.Token).ConfigureAwait(true);
                    continue;
                }

                _configurationMismatchDetectedAt = null;
                if (admission.Status == RuntimeBootstrapRendezvousStatus.TrayAlreadyReserved)
                {
                    if (!_admissionPreviouslyGranted)
                    {
                        MessageBox.Show(
                            admission.Detail ?? "Joydex is already running.",
                            "Joydex",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                        BeginExit(shutdownRuntime: false);
                        return;
                    }
                    await Task.Delay(RetryDelay, _lifetime.Token).ConfigureAwait(true);
                    continue;
                }

                _admissionPreviouslyGranted = true;
                var ticket = admission.LaunchTicket
                    ?? throw new InvalidDataException(
                        "The RuntimeHost admitted the tray without a launch ticket.");
                RuntimeClientConnection connection;
                try
                {
                    connection = await RuntimeClientConnection.ConnectAsync(
                            _endpoint,
                            RuntimeClientKind.Tray,
                            ticket.Value,
                            _ui,
                            cancellationToken: _lifetime.Token)
                        .ConfigureAwait(true);
                }
                catch (Exception exception)
                {
                    if (_lifetime.IsCancellationRequested)
                    {
                        break;
                    }
                    SetConnectionStatus("Runtime: Reconnecting…", exception.Message);
                    await Task.Delay(RetryDelay, _lifetime.Token).ConfigureAwait(true);
                    continue;
                }

                if (_exitStarted || _lifetime.IsCancellationRequested)
                {
                    await connection.DisposeAsync().ConfigureAwait(true);
                    break;
                }
                PublishConnection(connection);
                _consecutiveHostExits = 0;
                _lastHostExitDetail = null;
                try
                {
                    await connection.Completion.WaitAsync(_lifetime.Token).ConfigureAwait(true);
                }
                finally
                {
                    await RetireConnectionAsync(connection).ConfigureAwait(true);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetConnectionStatus("Runtime: Unavailable", exception.Message);
            _notifyIcon.ShowBalloonTip(
                5000,
                "Joydex runtime unavailable",
                exception.Message,
                ToolTipIcon.Error);
        }
    }

    private Task<RuntimeBootstrapRendezvousResult> RequestAdmissionAsync(
        CancellationToken cancellationToken)
    {
        var options = new RuntimeBootstrapRendezvousClientOptions
        {
            RequestTimeout = _hostLaunchAttempted || (!_demoMode && ProductionRuntimeOwnerExists())
                ? TimeSpan.FromSeconds(5)
                : TimeSpan.FromMilliseconds(300),
        };
        return _demoMode
            ? RuntimeBootstrapRendezvousClient.RequestDemoTrayAsync(
                _endpoint,
                _runtimeHostPath,
                options,
                cancellationToken)
            : RuntimeBootstrapRendezvousClient.RequestTrayAsync(
                _endpoint,
                _runtimeHostPath,
                options,
                cancellationToken);
    }

    private void StartRuntimeHost()
    {
        if (_hostLaunchAttempted)
        {
            return;
        }
        _hostLaunchAttempted = true;
        if (!File.Exists(_runtimeHostPath))
        {
            throw new FileNotFoundException(
                "This Joydex package does not contain Joydex.RuntimeHost.exe.",
                _runtimeHostPath);
        }

        var start = new ProcessStartInfo(_runtimeHostPath)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        if (_demoMode)
        {
            start.ArgumentList.Add("--demo");
            start.ArgumentList.Add("--config");
            start.ArgumentList.Add(_configurationPath);
            start.ArgumentList.Add("--pipe-name");
            start.ArgumentList.Add(_endpoint.PipeName);
            start.ArgumentList.Add("--instance-name");
            start.ArgumentList.Add(_endpoint.PipeName);
        }
        else
        {
            start.ArgumentList.Add("--config");
            start.ArgumentList.Add(_configurationPath);
        }
        _startedHost = Process.Start(start)
            ?? throw new InvalidOperationException("The Joydex RuntimeHost process did not start.");
    }

    private bool ResetExitedRuntimeHost()
    {
        if (_startedHost is null)
        {
            return false;
        }
        try
        {
            if (!_startedHost.HasExited)
            {
                return false;
            }
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            _lastHostExitDetail = $"The last process exited with code {_startedHost.ExitCode}.";
        }
        catch (InvalidOperationException)
        {
            _lastHostExitDetail = "The last process ended before its exit code was available.";
        }
        _startedHost.Dispose();
        _startedHost = null;
        _hostLaunchAttempted = false;
        _consecutiveHostExits++;
        return true;
    }

    private static bool ProductionRuntimeOwnerExists()
    {
        try
        {
            if (!Mutex.TryOpenExisting(@"Local\Joydex", out var ownership))
            {
                return false;
            }
            ownership.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // An inaccessible production ownership mutex is still an existing owner.
            return true;
        }
    }

    private void PublishConnection(RuntimeClientConnection connection)
    {
        _connection = connection;
        var generation = checked(++_connectionGeneration);
        _inputGeneration = NextInputGeneration(_inputGeneration);
        connection.State.Changed += OnRuntimeStateChanged;
        var snapshot = connection.State.Current.Snapshot
            ?? throw new InvalidDataException("The RuntimeHost omitted its initial snapshot.");
        _snapshot = snapshot;
        _settingsWriter = CreateSettingsWriter(connection, generation, snapshot.EngineEpoch);
        _input.BeginConnection(_inputGeneration, connection.Rpc, connection.State);
        _taskAlerts.BeginConnection(
            generation,
            _settingsWriter,
            new RuntimeRpcCommandRunner(connection.Rpc),
            connection.State);
        _voiceAdapter.BeginConnection(
            generation,
            new RuntimeRpcCommandRunner(connection.Rpc),
            new RuntimeVoiceTargetWriter(connection.Rpc, connection.State),
            snapshot.Ui?.Voice,
            snapshot.EngineEpoch);
        ApplySnapshot(snapshot);
        SetConnectionStatus("Runtime: Connected", null);
        _configureItem.Enabled = true;
        _promptPickersItem.Enabled = true;
        if (_demoMode && !_demoModeOpenedSettings)
        {
            _demoModeOpenedSettings = true;
            OnConfigure(this, EventArgs.Empty);
        }
    }

    private RuntimeSettingsWriter CreateSettingsWriter(
        RuntimeClientConnection connection,
        long generation,
        Guid engineEpoch) => new(
        connection.Rpc,
        engineEpoch,
        () => ReferenceEquals(_connection, connection)
            && _connectionGeneration == generation
            && IsUsable(connection.State),
        () => GetCheckedSnapshot(connection.State));

    private async Task RetireConnectionAsync(RuntimeClientConnection connection)
    {
        connection.State.Changed -= OnRuntimeStateChanged;
        if (!ReferenceEquals(_connection, connection))
        {
            await connection.DisposeAsync().ConfigureAwait(true);
            return;
        }

        _input.EndConnection(_inputGeneration, connection.State.Current.DisconnectFailure);
        _taskAlerts.EndConnection(_connectionGeneration, connection.State.Current.DisconnectFailure);
        _voiceAdapter.EndConnection(_connectionGeneration);
        _connection = null;
        _snapshot = null;
        _settingsWriter = null;
        _configureItem.Enabled = false;
        _promptPickersItem.Enabled = false;
        _voiceItem.Enabled = false;
        _controllersItem.Enabled = false;
        _taskAlertsItem.Enabled = false;
        _taskAlertsStatusItem.Enabled = false;
        _modeItem.Enabled = false;
        _testControlsItem.Enabled = false;
        _activityForm?.SetConnectionStatus("Controllers: reconnecting…");
        _promptPicker.Apply(null, CompanionConfig.CreateSafeDefault());
        SetConnectionStatus("Runtime: Reconnecting…", null);
        await connection.DisposeAsync().ConfigureAwait(true);
    }

    private async void OnRuntimeStateChanged(object? sender, RuntimeClientStateChange change)
    {
        var connection = _connection;
        if (connection is null || !ReferenceEquals(sender, connection.State))
        {
            return;
        }
        _input.ApplyStateChange(_inputGeneration, change);
        _taskAlerts.ApplyStateChange(_connectionGeneration, change);
        if (change.Kind == RuntimeClientChangeKind.ResynchronizationRequired)
        {
            try
            {
                var refreshedSnapshot = await connection.Rpc.GetSnapshotAsync(_lifetime.Token)
                    .ConfigureAwait(true);
                if (ReferenceEquals(_connection, connection))
                {
                    connection.State.ApplySnapshot(refreshedSnapshot);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                SetConnectionStatus("Runtime: Refresh needed", exception.Message);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException exception)
            {
                SetConnectionStatus("Runtime: Refresh needed", exception.Message);
            }
            return;
        }
        if (change.Kind == RuntimeClientChangeKind.Disconnected)
        {
            return;
        }
        if (change.State.Snapshot is { } snapshot)
        {
            ApplySnapshot(snapshot);
        }
    }

    private void ApplySnapshot(RuntimeSnapshot snapshot)
    {
        if (_activityEpoch != snapshot.EngineEpoch)
        {
            _activityEpoch = snapshot.EngineEpoch;
            _lastActivitySequence = -1;
            _recentActivity.Clear();
            _activityForm?.Close();
            _activityForm = null;
        }

        _snapshot = snapshot;
        var ui = snapshot.Ui ?? new RuntimeUiSnapshot();
        var active = snapshot.Settings.Active.Companion;
        _promptPicker.Apply(ui.PromptPicker, active);
        _buttonMaps.ApplySnapshot(
            active,
            ui.TaskAlerts,
            ui.Controllers,
            ui.ButtonMaps);
        _voiceAdapter.ApplySnapshot(_connectionGeneration, ui.Voice);
        ConfigureControllerMenu(active, ui.Controllers);
        ApplyActivity(ui.RecentActivity);
        _voiceItem.Enabled = ui.Voice is not null;
        _modeItem.Checked = active.Safety.DryRun;
        _modeItem.Enabled = !_demoMode && _connection is not null;
        _testControlsItem.Enabled = _connection is not null;
        _taskAlertsItem.Enabled = !_demoMode && ui.TaskAlerts is not null;
        _taskAlertsStatusItem.Enabled = !_demoMode && ui.TaskAlerts is not null;
    }

    private void ApplyActivity(IEnumerable<RuntimeActionActivity>? activity)
    {
        foreach (var item in (activity ?? []).OrderBy(item => item.Sequence))
        {
            if (item.Sequence <= _lastActivitySequence)
            {
                continue;
            }
            _lastActivitySequence = item.Sequence;
            _recentActivity.Enqueue(item);
            while (_recentActivity.Count > RuntimeUiLimits.MaximumActionActivityEntries)
            {
                _recentActivity.Dequeue();
            }
            _activityForm?.Append(item.Message);
        }
    }

    private void ConfigureControllerMenu(
        CompanionConfig config,
        RuntimeControllerStatus[]? controllers)
    {
        var statuses = (controllers ?? [])
            .GroupBy(item => item.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        _controllersItem.DropDownItems.Clear();
        foreach (var device in config.Devices.Where(device =>
                     !string.IsNullOrWhiteSpace(device.ButtonMapTemplate)))
        {
            var label = statuses.TryGetValue(device.Id, out var status)
                ? $"{device.DisplayName} — {status.Status}"
                : device.DisplayName;
            var item = new ToolStripMenuItem(label)
            {
                Enabled = !statuses.TryGetValue(device.Id, out var current) || current.HasButtonMap,
            };
            item.Click += (_, _) => _buttonMaps.ShowManual(device.Id);
            _controllersItem.DropDownItems.Add(item);
        }
        _controllersItem.Text = ControllerStatusText(controllers);
        _controllersItem.Enabled = _controllersItem.DropDownItems.Count > 0;
        _activityForm?.SetConnectionStatus(_controllersItem.Text);
    }

    private static string ControllerStatusText(RuntimeControllerStatus[]? controllers)
    {
        var current = controllers ?? [];
        if (current.Length == 0)
        {
            return "Controllers: None";
        }
        var connected = current.Count(item =>
            string.Equals(item.Status, "Connected", StringComparison.OrdinalIgnoreCase));
        return $"Controllers: {connected}/{current.Length} connected";
    }

    private async Task<RuntimeCommandResult> SendCommandAsync(
        RuntimeCommandRequest request,
        CancellationToken cancellationToken)
    {
        var connection = _connection
            ?? throw new InvalidOperationException("The Joydex runtime is reconnecting.");
        var generation = _connectionGeneration;
        EnsureCurrentConnection(connection, generation);
        var result = await connection.Rpc.ExecuteCommandAsync(request, cancellationToken)
            .ConfigureAwait(false);
        EnsureCurrentConnection(connection, generation);
        return result;
    }

    private async Task<RuntimeCommandResult> ExecuteRecoverableCommandAsync(
        RuntimeCommandKind kind,
        RuntimeCommandArguments? arguments,
        CancellationToken cancellationToken)
    {
        var connection = _connection
            ?? throw new InvalidOperationException("The Joydex runtime is reconnecting.");
        var generation = _connectionGeneration;
        EnsureCurrentConnection(connection, generation);
        var fingerprint = CommandFingerprint(arguments);
        var engineEpoch = connection.State.Current.Snapshot!.EngineEpoch;
        if (_pendingCommands.TryGetValue(kind, out var pending))
        {
            var sameRequest = string.Equals(
                pending.Fingerprint,
                fingerprint,
                StringComparison.Ordinal);
            var recovered = await connection.Rpc
                .GetCommandOperationAsync(pending.OperationId, cancellationToken)
                .ConfigureAwait(true);
            EnsureCurrentConnection(connection, generation);
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
            if (recovered.OperationId == pending.OperationId
                && recovered.State == RuntimeCommandOperationState.NotFound
                && pending.EngineEpoch != engineEpoch)
            {
                RemovePendingCommand(kind, pending.OperationId);
                throw new InvalidOperationException(
                    $"The runtime restarted and the earlier {kind} outcome is unavailable. Check its effect before choosing the action again.");
            }
            throw new InvalidOperationException(
                recovered.State == RuntimeCommandOperationState.Running
                    ? sameRequest
                        ? $"The {kind} action is still running."
                        : $"An earlier {kind} action is still running. Wait for it before running the changed request."
                    : $"The {kind} action could not be reconciled and was not resubmitted.");
        }

        var operationId = Guid.NewGuid();
        _pendingCommands[kind] = new PendingCommand(operationId, fingerprint, engineEpoch);
        try
        {
            var result = await connection.Rpc.ExecuteCommandAsync(
                    new RuntimeCommandRequest(operationId, kind, arguments),
                    cancellationToken)
                .ConfigureAwait(true);
            EnsureCurrentConnection(connection, generation);
            ValidateCommandResult(result, operationId, kind);
            RemovePendingCommand(kind, operationId);
            return result;
        }
        catch
        {
            // A later click checks this exact operation before another side effect is admitted.
            throw;
        }
    }

    private void EnsureCurrentConnection(RuntimeClientConnection connection, long generation)
    {
        if (!ReferenceEquals(_connection, connection)
            || _connectionGeneration != generation
            || !IsUsable(connection.State))
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

    private async void OnConfigure(object? sender, EventArgs eventArgs)
    {
        if (_connection is null)
        {
            return;
        }
        _configureItem.Enabled = false;
        try
        {
            var result = await ExecuteRecoverableCommandAsync(
                    RuntimeCommandKind.OpenSettings,
                    arguments: null,
                    _lifetime.Token)
                .ConfigureAwait(true);
            RequireCompleted(result, "The Settings process could not be opened.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowFailure("Joydex settings unavailable", exception);
        }
        finally
        {
            if (!_exitStarted)
            {
                _configureItem.Enabled = _connection is not null;
            }
        }
    }

    private void OnPromptPickers(object? sender, EventArgs eventArgs)
    {
        if (_connection is null || _snapshot is null)
        {
            return;
        }
        if (_promptPickerEditor is { IsDisposed: false } existing)
        {
            ShowAndActivate(existing);
            return;
        }

        var snapshot = _snapshot;
        var editor = new PromptPickerEditorForm(
            _configurationPath,
            IntPtr.Zero,
            initialConfig: snapshot.Settings.Desired.Companion,
            inputSession: _input,
            applyConfiguration: ApplyPromptPickerConfigurationAsync);
        editor.FormClosed += OnPromptPickerEditorClosed;
        _promptPickerEditorBaseRevision = snapshot.Settings.Revision;
        _promptPickerEditor = editor;
        ShowAndActivate(editor);
    }

    private async Task<RuntimeSettingsWriteResult> ApplyPromptPickerConfigurationAsync(
        CompanionConfig candidate,
        CancellationToken cancellationToken)
    {
        var baseRevision = _promptPickerEditorBaseRevision
            ?? throw new InvalidOperationException("The prompt-picker editor has no settings base revision.");
        var result = await ExecuteRecoverableSettingsWriteAsync(
                TraySettingsAction.PromptEditor,
                baseRevision,
                new SettingsPatch(Companion: candidate),
                cancellationToken)
            .ConfigureAwait(true);
        var reconciled = PromptPickerCandidateAfter(candidate, result);
        if (!ReferenceEquals(reconciled, candidate)
            && _promptPickerEditor is { IsDisposed: false } editor)
        {
            editor.ReplaceConfiguration(reconciled);
        }
        _promptPickerEditorBaseRevision = PromptPickerBaseRevisionAfter(baseRevision, result);
        return result;
    }

    internal static CompanionConfig PromptPickerCandidateAfter(
        CompanionConfig candidate,
        RuntimeSettingsWriteResult result) =>
        IsTerminal(result.Outcome)
            ? RebasePromptPickerCandidate(candidate, result.Snapshot.Desired.Companion)
            : candidate;

    private static CompanionConfig RebasePromptPickerCandidate(
        CompanionConfig candidate,
        CompanionConfig authoritative) => new()
    {
        Device = candidate.Device,
        Devices = candidate.Devices,
        Polling = authoritative.Polling,
        Safety = authoritative.Safety,
        OpenWorkingDirectory = authoritative.OpenWorkingDirectory,
        BankSelectors = candidate.BankSelectors,
        Bindings = authoritative.Bindings,
        PromptPickers = candidate.PromptPickers,
    };

    internal static long PromptPickerBaseRevisionAfter(
        long baseRevision,
        RuntimeSettingsWriteResult result) =>
        IsTerminal(result.Outcome) ? result.Snapshot.Revision : baseRevision;

    private async Task<RuntimeSettingsWriteResult> ExecuteRecoverableSettingsWriteAsync(
        TraySettingsAction action,
        long baseRevision,
        SettingsPatch patch,
        CancellationToken cancellationToken)
    {
        var connection = _connection
            ?? throw new InvalidOperationException("The runtime is reconnecting.");
        var writer = _settingsWriter
            ?? throw new InvalidOperationException("The runtime is reconnecting.");
        var generation = _connectionGeneration;
        EnsureCurrentConnection(connection, generation);
        var fingerprint = baseRevision + ":" + JsonSerializer.Serialize(patch);
        RuntimeSettingsWriteResult result;
        if (_pendingSettingsWrites.TryGetValue(action, out var pending))
        {
            var sameRequest = string.Equals(
                pending.Fingerprint,
                fingerprint,
                StringComparison.Ordinal);
            result = await writer.RecoverAsync(pending.OperationId, cancellationToken)
                .ConfigureAwait(true);
            EnsureCurrentConnection(connection, generation);
            if (IsTerminal(result.Outcome))
            {
                RemovePendingSettingsWrite(action, pending.OperationId);
                if (!sameRequest)
                {
                    throw new InvalidOperationException(
                        "The earlier settings action was reconciled. Choose the action again to apply the current values.");
                }
            }
            return result;
        }

        var operationId = Guid.NewGuid();
        _pendingSettingsWrites[action] = new PendingSettingsWrite(operationId, fingerprint);
        result = await writer.ApplyAsync(
                baseRevision,
                patch,
                operationId,
                cancellationToken)
            .ConfigureAwait(true);
        EnsureCurrentConnection(connection, generation);
        if (IsTerminal(result.Outcome))
        {
            RemovePendingSettingsWrite(action, operationId);
        }
        return result;
    }

    private void RemovePendingSettingsWrite(TraySettingsAction action, Guid operationId)
    {
        if (_pendingSettingsWrites.TryGetValue(action, out var pending)
            && pending.OperationId == operationId)
        {
            _pendingSettingsWrites.Remove(action);
        }
    }

    private static bool IsTerminal(RuntimeSettingsWriteOutcome outcome) =>
        outcome is not (RuntimeSettingsWriteOutcome.Running
            or RuntimeSettingsWriteOutcome.Uncertain);

    private void OnPromptPickerEditorClosed(object? sender, FormClosedEventArgs eventArgs)
    {
        if (sender is PromptPickerEditorForm editor)
        {
            editor.FormClosed -= OnPromptPickerEditorClosed;
            if (ReferenceEquals(_promptPickerEditor, editor))
            {
                _promptPickerEditor = null;
                _promptPickerEditorBaseRevision = null;
            }
        }
    }

    private async void OnToggleDryRun(object? sender, EventArgs eventArgs)
    {
        if (_demoMode || _snapshot is null || _settingsWriter is null)
        {
            return;
        }
        _modeItem.Enabled = false;
        try
        {
            var current = _snapshot.Settings.Desired.Companion;
            var candidate = new CompanionConfig
            {
                Device = current.Device,
                Devices = current.Devices,
                Polling = current.Polling,
                Safety = new SafetyOptions
                {
                    DryRun = !current.Safety.DryRun,
                    RequireCodexForeground = current.Safety.RequireCodexForeground,
                    CodexProcessNames = current.Safety.CodexProcessNames,
                    SimulatorProcessNames = current.Safety.SimulatorProcessNames,
                },
                OpenWorkingDirectory = current.OpenWorkingDirectory,
                BankSelectors = current.BankSelectors,
                Bindings = current.Bindings,
                PromptPickers = current.PromptPickers,
            };
            var result = await ExecuteRecoverableSettingsWriteAsync(
                    TraySettingsAction.DryRun,
                    _snapshot.Settings.Revision,
                    new SettingsPatch(Companion: candidate),
                    _lifetime.Token)
                .ConfigureAwait(true);
            if (result.Outcome is not (RuntimeSettingsWriteOutcome.NoChanges
                or RuntimeSettingsWriteOutcome.Applied
                or RuntimeSettingsWriteOutcome.PendingIdle))
            {
                throw new InvalidOperationException(result.Detail);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowFailure("Joydex dry run", exception);
        }
        finally
        {
            _modeItem.Enabled = !_demoMode && _connection is not null;
        }
    }

    private void OnTestControls(object? sender, EventArgs eventArgs)
    {
        var active = _snapshot?.Settings.Active.Companion;
        if (active is null)
        {
            return;
        }
        if (!active.Safety.DryRun)
        {
            MessageBox.Show(
                "Enable Dry run in Configure before testing controls.",
                "Test Joydex",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        if (_activityForm is { IsDisposed: false } existing)
        {
            ShowAndActivate(existing);
            return;
        }

        var form = new DryRunActivityForm(active, simulatedInput: _demoMode);
        form.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_activityForm, form))
            {
                _activityForm = null;
            }
        };
        foreach (var activity in _recentActivity)
        {
            form.Append(activity.Message);
        }
        form.SetConnectionStatus(_controllersItem.Text ?? "Controllers: Unavailable");
        _activityForm = form;
        form.Show();
    }

    private async void OnToggleTaskAlerts(object? sender, EventArgs eventArgs)
    {
        if (_demoMode || _connection is null)
        {
            return;
        }
        _taskAlertsItem.Enabled = false;
        try
        {
            await RuntimeTaskAlertsRecoveryPolicy.ReconcileBeforeNewActionAsync(_taskAlerts)
                .ConfigureAwait(true);
            var result = await _taskAlerts.SetEnabledAsync(
                    !_taskAlerts.Current.Snapshot.Enabled,
                    Guid.NewGuid(),
                    _lifetime.Token)
                .ConfigureAwait(true);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(result.Detail);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowFailure("Joydex task alerts", exception);
        }
        finally
        {
            _taskAlertsItem.Enabled = !_demoMode
                && _connection is not null
                && _snapshot?.Ui?.TaskAlerts is not null;
        }
    }

    private void OnTaskAlertsChanged(object? sender, RuntimeTaskAlertsPresentationState state)
    {
        _taskAlertsItem.Checked = state.Snapshot.Enabled;
        _taskAlertsItem.ToolTipText = state.LastAction?.Detail
            ?? state.Failure?.Message
            ?? state.Hooks.Detail
            ?? "Task alerts";
    }

    private void OnTaskAlertsStatus(object? sender, EventArgs eventArgs)
    {
        if (_demoMode || _exitStarted || _taskAlertsShowPending)
        {
            return;
        }
        // The nested menu still has dismissal/focus messages after its Click handler.
        _taskAlertsShowPending = true;
        Application.Idle += OnApplicationIdleShowTaskAlerts;
    }

    private void OnApplicationIdleShowTaskAlerts(object? sender, EventArgs eventArgs)
    {
        Application.Idle -= OnApplicationIdleShowTaskAlerts;
        _taskAlertsShowPending = false;
        if (_exitStarted)
        {
            return;
        }
        if (_taskAlertsForm is not { IsDisposed: false } form)
        {
            form = new TaskAlertsForm(
                _taskAlerts,
                Path.Combine(_dataRoot, "joydex-linktool.led.json"));
            var created = form;
            form.FormClosed += (_, _) =>
            {
                if (ReferenceEquals(_taskAlertsForm, created))
                {
                    _taskAlertsForm = null;
                }
            };
            _taskAlertsForm = form;
        }
        ShowAndActivate(form);
    }

    private void OnToggleVoice(object? sender, EventArgs eventArgs)
    {
        var form = EnsureVoiceForm();
        if (form.Visible)
        {
            form.HideWorkspace();
        }
        else
        {
            form.ShowWorkspace();
        }
    }

    private RoomVoiceForm EnsureVoiceForm()
    {
        if (_voiceForm is { IsDisposed: false } existing)
        {
            return existing;
        }
        _voiceForm = new RoomVoiceForm(
            _voiceAdapter.Conversation,
            Path.Combine(_dataRoot, "room-voice-window.json"),
            () => _voiceAdapter.RefreshConversationAsync(),
            () => _voiceAdapter.EndSessionAsync(),
            () => _voiceAdapter.RestartAsync(),
            () => OnConfigure(this, EventArgs.Empty),
            _ => { },
            _voiceAdapter.TaskMessaging,
            _voiceAdapter.ReadFullConversationAsync);
        return _voiceForm;
    }

    private async void OnReloadConfiguration(object? sender, EventArgs eventArgs)
    {
        try
        {
            var result = await ExecuteRecoverableCommandAsync(
                    RuntimeCommandKind.ReloadConfiguration,
                    arguments: null,
                    _lifetime.Token)
                .ConfigureAwait(true);
            RequireCompleted(result, "The configuration could not be reloaded.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowFailure("Joydex configuration", exception);
        }
    }

    private async void OnToggleStartAtLogin(object? sender, EventArgs eventArgs) =>
        await SetStartupRegistrationAsync(
                _startAtLoginItem,
                RuntimeCommandKind.SetJoydexStartAtLogin,
                "Joydex")
            .ConfigureAwait(true);

    private async void OnToggleStartLinkToolAtLogin(object? sender, EventArgs eventArgs) =>
        await SetStartupRegistrationAsync(
                _startLinkToolAtLoginItem,
                RuntimeCommandKind.SetLinkToolStartAtLogin,
                "VIRPIL LinkTool")
            .ConfigureAwait(true);

    private async Task SetStartupRegistrationAsync(
        ToolStripMenuItem item,
        RuntimeCommandKind kind,
        string displayName)
    {
        if (_demoMode || _connection is null)
        {
            return;
        }
        item.Enabled = false;
        try
        {
            var result = await ExecuteRecoverableCommandAsync(
                    kind,
                    new RuntimeCommandArguments(Enabled: !item.Checked),
                    _lifetime.Token)
                .ConfigureAwait(true);
            RequireCompleted(result, $"The {displayName} startup setting could not be changed.");
            var status = result.Payload?.StartupRegistration
                ?? throw new InvalidDataException("The runtime omitted the startup registration status.");
            item.Checked = status.Enabled;
            item.Enabled = status.Available;
            if (!status.Available && !string.IsNullOrWhiteSpace(status.Detail))
            {
                item.Text = $"Start {displayName} when I sign in (unavailable)";
                item.ToolTipText = status.Detail;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowFailure($"{displayName} login startup", exception);
        }
        finally
        {
            if (!_exitStarted
                && !(item.Text ?? string.Empty).EndsWith("(unavailable)", StringComparison.Ordinal))
            {
                item.Enabled = _connection is not null;
            }
        }
    }

    private void InitializeStartupRegistrationItems()
    {
        if (_demoMode)
        {
            _startAtLoginItem.Text += " (unavailable in demo)";
            _startLinkToolAtLoginItem.Text += " (unavailable in demo)";
            return;
        }
        InitializeStartupRegistrationItem(_startAtLoginItem, _joydexLoginStartup);
        if (_linkToolLoginStartup is null)
        {
            _startLinkToolAtLoginItem.Text += " (not installed)";
            _startLinkToolAtLoginItem.Enabled = false;
        }
        else
        {
            InitializeStartupRegistrationItem(_startLinkToolAtLoginItem, _linkToolLoginStartup);
        }
    }

    private static void InitializeStartupRegistrationItem(
        ToolStripMenuItem item,
        LoginStartupRegistration? registration)
    {
        if (registration is null)
        {
            item.Enabled = false;
            return;
        }
        try
        {
            item.Checked = registration.IsEnabled;
        }
        catch (Exception exception)
        {
            item.Enabled = false;
            item.ToolTipText = exception.Message;
        }
    }

    private bool CodexStillForeground()
    {
        var safety = _snapshot?.Settings.Active.Companion.Safety;
        return safety is not null
            && _foreground.Check(safety, actionMayBringCodexForward: false).Allowed;
    }

    private string GetButtonMapStatePath(string deviceId)
    {
        var statePath = Path.GetFullPath(Path.Combine(
            _dataRoot,
            $"button-map-{deviceId}-window.json"));
        var rootPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_dataRoot))
            + Path.DirectorySeparatorChar;
        if (!statePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Device ID '{deviceId}' produced an unsafe button-map state path.");
        }
        return statePath;
    }

    private static void ShowAndActivate(Form form)
    {
        if (form.WindowState == FormWindowState.Minimized)
        {
            form.WindowState = FormWindowState.Normal;
        }
        form.Show();
        form.BringToFront();
        form.Activate();
    }

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "Joydex could not open the requested path",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
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

    private static void RequireCompleted(RuntimeCommandResult result, string fallback)
    {
        if (result.Status != RuntimeCommandStatus.Completed)
        {
            throw new InvalidOperationException(result.Detail ?? fallback);
        }
    }

    private void ShowFailure(string title, Exception exception) =>
        _notifyIcon.ShowBalloonTip(5000, title, exception.Message, ToolTipIcon.Error);

    private void SetConnectionStatus(string text, string? detail)
    {
        _connectionItem.Text = text;
        _connectionItem.ToolTipText = detail ?? text;
    }

    private void BeginExit(bool shutdownRuntime = true)
    {
        if (_exitStarted)
        {
            return;
        }
        _exitStarted = true;
        Application.Idle -= OnApplicationIdleShowTaskAlerts;
        _taskAlertsShowPending = false;
        _notifyIcon.ContextMenuStrip!.Enabled = false;
        _ = ShutdownAndExitAsync(shutdownRuntime);
    }

    private async Task ShutdownAndExitAsync(bool shutdownRuntime)
    {
        var shutdownDelivered = false;
        try
        {
            if (shutdownRuntime && _connection is { } connection)
            {
                using var timeout = new CancellationTokenSource(ExitShutdownTimeout);
                shutdownDelivered = await TryRequestRuntimeShutdownAsync(
                        connection,
                        timeout.Token)
                    .ConfigureAwait(true);
            }
            await _lifetime.CancelAsync().ConfigureAwait(true);
            await _connectTask.ConfigureAwait(true);
            if (shutdownRuntime && !shutdownDelivered)
            {
                shutdownDelivered = await TryReconnectAndRequestRuntimeShutdownAsync()
                    .ConfigureAwait(true);
            }
            if (shutdownRuntime && _startedHost is { } startedHost)
            {
                await EnsureStartedRuntimeHostExitedAsync(startedHost, shutdownDelivered)
                    .ConfigureAwait(true);
            }
            if (_promptPickerEditor is { IsDisposed: false } promptEditor)
            {
                await promptEditor.QuiesceCaptureAsync().ConfigureAwait(true);
                promptEditor.Close();
                promptEditor.Dispose();
            }
            _taskAlertsForm?.Close();
            _taskAlertsForm?.Dispose();
            _activityForm?.Close();
            _activityForm?.Dispose();
            _voiceForm?.CloseWorkspace();
            _voiceForm?.Dispose();
            await _input.DisposeAsync().ConfigureAwait(true);
            if (_connection is { } current)
            {
                current.State.Changed -= OnRuntimeStateChanged;
                await current.DisposeAsync().ConfigureAwait(true);
                _connection = null;
            }
            _taskAlerts.Changed -= OnTaskAlertsChanged;
            _taskAlerts.Dispose();
            await _promptPicker.DisposeAsync().ConfigureAwait(true);
            _buttonMaps.Dispose();
            _voiceAdapter.Dispose();
        }
        finally
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _icon.Dispose();
            _startedHost?.Dispose();
            _lifetime.Dispose();
            ExitThread();
        }
    }

    private async Task<bool> TryReconnectAndRequestRuntimeShutdownAsync()
    {
        using var timeout = new CancellationTokenSource(ExitShutdownTimeout);
        RuntimeClientConnection? connection = null;
        try
        {
            var admission = await RequestAdmissionAsync(timeout.Token).ConfigureAwait(true);
            if (admission.Status != RuntimeBootstrapRendezvousStatus.Admitted
                || admission.LaunchTicket is not { } ticket)
            {
                return false;
            }
            connection = await RuntimeClientConnection.ConnectAsync(
                    _endpoint,
                    RuntimeClientKind.Tray,
                    ticket.Value,
                    _ui,
                    cancellationToken: timeout.Token)
                .ConfigureAwait(true);
            return await TryRequestRuntimeShutdownAsync(connection, timeout.Token)
                .ConfigureAwait(true);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (connection is not null)
            {
                try
                {
                    await connection.DisposeAsync().ConfigureAwait(true);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    private static Task<bool> TryRequestRuntimeShutdownAsync(
        RuntimeClientConnection connection,
        CancellationToken cancellationToken) => TryRequestRuntimeShutdownAsync(
            (request, token) => connection.Rpc.ExecuteCommandAsync(request, token),
            cancellationToken);

    internal static async Task<bool> TryRequestRuntimeShutdownAsync(
        Func<RuntimeCommandRequest, CancellationToken, Task<RuntimeCommandResult>> execute,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execute);
        try
        {
            var request = new RuntimeCommandRequest(
                Guid.NewGuid(),
                RuntimeCommandKind.ShutdownRuntime);
            var result = await execute(request, cancellationToken)
                .ConfigureAwait(true);
            return result.OperationId == request.OperationId
                && result.Kind == request.Kind
                && result.Status == RuntimeCommandStatus.Completed;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task EnsureStartedRuntimeHostExitedAsync(
        Process startedHost,
        bool shutdownDelivered)
    {
        try
        {
            if (startedHost.HasExited)
            {
                return;
            }
            if (shutdownDelivered)
            {
                try
                {
                    await startedHost.WaitForExitAsync()
                        .WaitAsync(ExitShutdownTimeout)
                        .ConfigureAwait(true);
                    return;
                }
                catch (TimeoutException)
                {
                }
            }

            // This handle identifies the exact RuntimeHost this tray launched, so the fallback
            // cannot terminate a different Joydex installation that happens to be running.
            startedHost.Kill(entireProcessTree: true);
            await startedHost.WaitForExitAsync()
                .WaitAsync(ExitShutdownTimeout)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or NotSupportedException
                                          or System.ComponentModel.Win32Exception
                                          or TimeoutException)
        {
        }
    }

    private static bool IsUsable(RuntimeClientState state)
    {
        var current = state.Current;
        return current.IsInitialized
            && !current.IsDisconnected
            && !current.ResynchronizationRequired
            && current.Snapshot is not null;
    }

    private static RuntimeSnapshot GetCheckedSnapshot(RuntimeClientState state) =>
        IsUsable(state)
            ? state.Current.Snapshot!
            : throw new InvalidOperationException(
                "The runtime connection is unavailable or needs to refresh.");

    private static int NextInputGeneration(int generation) => generation == int.MaxValue
        ? int.MinValue + 1
        : generation + 1;

    private sealed record PendingCommand(Guid OperationId, string Fingerprint, Guid EngineEpoch);

    private sealed record PendingSettingsWrite(Guid OperationId, string Fingerprint);

    private enum TraySettingsAction
    {
        PromptEditor,
        DryRun,
    }
}
