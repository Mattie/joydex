using Joydex.Core.Voice;
using Joydex.Windows.Voice;

namespace Joydex.App;

internal sealed class RoomVoiceSettingsControl : UserControl
{
    private readonly CheckBox _enabled = new()
    {
        AutoSize = true,
        Text = "Enable Room Voice",
    };
    private readonly ComboBox _sessionMode = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _endpoint = new() { Dock = DockStyle.Fill };
    private readonly TextBox _taskReference = new() { Dock = DockStyle.Fill };
    private readonly TextBox _taskLabel = new() { Dock = DockStyle.Fill };
    private readonly TextBox _dedicatedTaskReference = new() { Dock = DockStyle.Fill };
    private readonly TextBox _dedicatedTaskLabel = new() { Dock = DockStyle.Fill };
    private readonly TextBox _codexAppServerPath = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _agentProject = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _agentWorkspacePath = new() { Dock = DockStyle.Fill };
    private readonly Label _workspaceStatus = new()
    {
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        MaximumSize = new Size(680, 0),
        Padding = new Padding(8, 0, 8, 6),
    };
    private readonly ComboBox _realtimeVoice = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _conversationSpeakerGain = CreateNumber(
        VoicePePreferences.MinimumConversationSpeakerGain,
        VoicePePreferences.MaximumConversationSpeakerGain,
        VoicePePreferences.DefaultConversationSpeakerGain);
    private readonly NumericUpDown _microphoneGain = CreateNumber(
        VoicePeWakeTuning.MinimumMicrophoneGain,
        VoicePeWakeTuning.MaximumMicrophoneGain,
        VoicePeWakeTuning.DefaultMicrophoneGain);
    private readonly NumericUpDown _wakeProbability = CreateNumber(
        VoicePeWakeTuning.MinimumWakeProbabilityCutoff,
        VoicePeWakeTuning.MaximumWakeProbabilityCutoff,
        VoicePeWakeTuning.DefaultWakeProbabilityCutoff,
        decimalPlaces: 2,
        increment: 0.01);
    private readonly NumericUpDown _slidingWindow = CreateNumber(
        VoicePeWakeTuning.MinimumSlidingWindow,
        VoicePeWakeTuning.MaximumSlidingWindow,
        VoicePeWakeTuning.DefaultSlidingWindow);
    private readonly NumericUpDown _vadProbability = CreateNumber(
        VoicePeWakeTuning.MinimumVadProbabilityCutoff,
        VoicePeWakeTuning.MaximumVadProbabilityCutoff,
        VoicePeWakeTuning.DefaultVadProbabilityCutoff,
        decimalPlaces: 2,
        increment: 0.01);
    private readonly Func<VoicePePreferences, Task<bool>> _testTarget;
    private readonly Func<Uri, CancellationToken, Task<VoicePeWakeTuning>> _readWakeTuning;
    private readonly Func<Uri, VoicePeWakeTuning, CancellationToken, Task<VoicePeWakeTuning>> _writeWakeTuning;
    private readonly Func<string, CancellationToken, Task<CodexProjectCatalog>>? _listProjects;
    private readonly Func<string, CodexVoiceWorkspaceProvisioningRequest, CancellationToken, Task<CodexVoiceWorkspaceProvisioningResult>>? _provisionWorkspace;
    private readonly CancellationTokenSource _tuningCancellation = new();
    private readonly CancellationToken _tuningCancellationToken;
    private readonly Button _testButton;
    private readonly Button _loadTuningButton;
    private readonly Button _applyTuningButton;
    private readonly Button _provisionWorkspaceButton;
    private readonly Label _tuningStatus = new()
    {
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        MaximumSize = new Size(680, 0),
        Padding = new Padding(8, 0, 8, 6),
        Text = "Load the current values before applying changes.",
    };
    private readonly CheckBox _preserveAssistantAudioDiagnostics = new()
    {
        AutoSize = true,
        Text = "Preserve assistant audio as WAV files",
    };
    private readonly CheckBox _desktopTaskMessaging = new()
    {
        AutoSize = true,
        Text = "Enable experimental Desktop task messaging",
    };
    private readonly Label _desktopBridgeStatus = new()
    {
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        MaximumSize = new Size(680, 0),
    };
    private readonly DesktopBridgeConfigurationManager _desktopBridgeConfiguration;
    private readonly string _desktopBridgeHostPath;
    private readonly string _voiceTargetTaskId;
    private readonly string _voiceTargetHostId;
    private readonly string _voiceTargetTaskLabel;
    private string? _loadedTuningEndpoint;
    private string _provisionedWorkspacePath;
    private string _provisionedProjectId;
    private bool _updatingWorkspaceControls;
    private bool _tuningCancellationDisposed;

    public RoomVoiceSettingsControl(
        VoicePePreferences initial,
        Func<VoicePePreferences, Task<bool>> testTarget,
        Func<Uri, CancellationToken, Task<VoicePeWakeTuning>> readWakeTuning,
        Func<Uri, VoicePeWakeTuning, CancellationToken, Task<VoicePeWakeTuning>> writeWakeTuning,
        Func<string, CancellationToken, Task<CodexProjectCatalog>>? listProjects = null,
        Func<string, CodexVoiceWorkspaceProvisioningRequest, CancellationToken, Task<CodexVoiceWorkspaceProvisioningResult>>? provisionWorkspace = null)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _testTarget = testTarget ?? throw new ArgumentNullException(nameof(testTarget));
        _readWakeTuning = readWakeTuning ?? throw new ArgumentNullException(nameof(readWakeTuning));
        _writeWakeTuning = writeWakeTuning ?? throw new ArgumentNullException(nameof(writeWakeTuning));
        _listProjects = listProjects;
        _provisionWorkspace = provisionWorkspace;
        _tuningCancellationToken = _tuningCancellation.Token;
        _desktopBridgeHostPath = Path.Combine(AppContext.BaseDirectory, "Joydex.DesktopBridgeHost.exe");
        _desktopBridgeConfiguration = new DesktopBridgeConfigurationManager(
            DesktopBridgeConfigurationManager.DefaultConfigPath());
        _voiceTargetTaskId = initial.VoiceTargetTaskId;
        _voiceTargetHostId = initial.VoiceTargetHostId;
        _voiceTargetTaskLabel = initial.VoiceTargetTaskLabel;

        AutoScroll = true;
        Dock = DockStyle.Fill;

        _enabled.Checked = initial.Enabled;
        _sessionMode.Items.AddRange([
            new SessionModeChoice(VoicePeSessionMode.JoydexOwner, "Joydex owns a dedicated task"),
            new SessionModeChoice(VoicePeSessionMode.LastVoiceFallback, "Native LASTVOICE fallback"),
        ]);
        _sessionMode.SelectedItem = _sessionMode.Items
            .Cast<SessionModeChoice>()
            .First(choice => choice.Mode == initial.SessionMode);
        _endpoint.Text = initial.DeviceEndpoint;
        _taskReference.Text = string.IsNullOrWhiteSpace(initial.PinnedTaskId)
            ? string.Empty
            : CodexTaskReference.BuildDeepLink(initial.PinnedTaskId);
        _taskLabel.Text = initial.PinnedTaskLabel;
        _dedicatedTaskReference.Text = string.IsNullOrWhiteSpace(initial.DedicatedTaskId)
            ? string.Empty
            : CodexTaskReference.BuildDeepLink(initial.DedicatedTaskId);
        _dedicatedTaskReference.ReadOnly = true;
        _dedicatedTaskLabel.Text = initial.DedicatedTaskLabel;
        _codexAppServerPath.Text = initial.CodexAppServerPath;
        _agentWorkspacePath.Text = string.IsNullOrWhiteSpace(initial.AgentWorkspacePath)
            ? SuggestDefaultWorkspacePath()
            : initial.AgentWorkspacePath;
        _provisionedWorkspacePath = initial.AgentWorkspacePath;
        _provisionedProjectId = initial.AgentProjectId;
        InitializeProjectChoices(initial);
        _realtimeVoice.Items.Add(new RealtimeVoiceChoice(string.Empty, "Codex default"));
        foreach (var voice in VoicePePreferences.SupportedRealtimeVoices)
        {
            _realtimeVoice.Items.Add(new RealtimeVoiceChoice(voice, voice));
        }
        _realtimeVoice.SelectedItem = _realtimeVoice.Items
            .Cast<RealtimeVoiceChoice>()
            .First(choice => string.Equals(choice.Value, initial.RealtimeVoice, StringComparison.Ordinal));
        _conversationSpeakerGain.Value = initial.ConversationSpeakerGain;
        _preserveAssistantAudioDiagnostics.Checked = initial.PreserveAssistantAudioDiagnostics;
        _desktopTaskMessaging.Checked = initial.DesktopTaskMessagingEnabled;

        var workspaceFields = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Dock = DockStyle.Top,
            Padding = new Padding(8),
        };
        workspaceFields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        workspaceFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddField(workspaceFields, 0, "Codex project", _agentProject);
        AddField(workspaceFields, 1, "Working folder", _agentWorkspacePath);

        var workspaceCommands = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(8, 0, 8, 4),
            WrapContents = true,
        };
        var browseWorkspace = new RoundedButton { AutoSize = true, Text = "Browse folder…" };
        browseWorkspace.Click += OnBrowseWorkspace;
        _provisionWorkspaceButton = new RoundedButton
        {
            AutoSize = true,
            Text = "Create fresh owned task",
            Variant = ButtonVariant.Primary,
            Enabled = _provisionWorkspace is not null,
        };
        _provisionWorkspaceButton.Click += OnProvisionWorkspace;
        var openWorkspace = new RoundedButton { AutoSize = true, Text = "Open workspace" };
        openWorkspace.Click += (_, _) => OpenSelectedDirectory(sessionRecords: false);
        var openSessionRecords = new RoundedButton { AutoSize = true, Text = "Open session records" };
        openSessionRecords.Click += (_, _) => OpenSelectedDirectory(sessionRecords: true);
        workspaceCommands.Controls.Add(browseWorkspace);
        workspaceCommands.Controls.Add(_provisionWorkspaceButton);
        workspaceCommands.Controls.Add(openWorkspace);
        workspaceCommands.Controls.Add(openSessionRecords);

        var workspaceLayout = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 1,
            Dock = DockStyle.Top,
        };
        workspaceLayout.Controls.Add(workspaceFields, 0, 0);
        workspaceLayout.Controls.Add(workspaceCommands, 0, 1);
        workspaceLayout.Controls.Add(_workspaceStatus, 0, 2);
        workspaceLayout.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(680, 0),
            Padding = new Padding(8, 0, 8, 8),
            Text = "This folder is the agent's intended working location and session archive. It is not a filesystem security boundary.",
        }, 0, 3);
        var workspaceGroup = CreateGroup("Agent workspace", workspaceLayout);
        UpdateWorkspaceStatus(initial);

        var conversationFields = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Dock = DockStyle.Top,
            Padding = new Padding(8),
        };
        conversationFields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        conversationFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddField(conversationFields, 0, "Dedicated task label", _dedicatedTaskLabel);
        AddField(conversationFields, 1, "Realtime voice", _realtimeVoice);
        AddField(conversationFields, 2, "Speaker gain", _conversationSpeakerGain);
        var conversationGroup = CreateGroup("Conversation", conversationFields);

        var desktopBridgeCommands = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            WrapContents = true,
        };
        var installDesktopBridge = new RoundedButton { AutoSize = true, Text = "Install/Repair Desktop Bridge" };
        installDesktopBridge.Click += (_, _) => InstallDesktopBridge();
        var removeDesktopBridge = new RoundedButton { AutoSize = true, Text = "Remove Desktop Bridge" };
        removeDesktopBridge.Click += (_, _) => RemoveDesktopBridge();
        desktopBridgeCommands.Controls.Add(installDesktopBridge);
        desktopBridgeCommands.Controls.Add(removeDesktopBridge);
        var desktopBridgeLayout = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 1,
            Dock = DockStyle.Top,
            Padding = new Padding(8),
        };
        desktopBridgeLayout.Controls.Add(_desktopTaskMessaging, 0, 0);
        desktopBridgeLayout.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(680, 0),
            Text = "Lets Room Voice send clean follow-up prompts to a selected local Codex Desktop task. Desktop must be running. Failed messages wait for manual review.",
        }, 0, 1);
        desktopBridgeLayout.Controls.Add(desktopBridgeCommands, 0, 2);
        desktopBridgeLayout.Controls.Add(_desktopBridgeStatus, 0, 3);
        var desktopBridgeGroup = CreateGroup("Desktop task messaging (experimental)", desktopBridgeLayout);
        RefreshDesktopBridgeStatus();

        _testButton = new RoundedButton
        {
            AutoSize = true,
            Text = "Open LASTVOICE fallback task",
        };
        _testButton.Click += OnTestTarget;

        var deviceFields = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Dock = DockStyle.Top,
            Padding = new Padding(8),
        };
        deviceFields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        deviceFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddField(deviceFields, 0, "Device endpoint", _endpoint);

        _loadTuningButton = new RoundedButton
        {
            AutoSize = true,
            Text = "Load from device",
        };
        _loadTuningButton.Click += OnLoadWakeTuning;
        _applyTuningButton = new RoundedButton
        {
            AutoSize = true,
            Enabled = false,
            Text = "Apply to device",
            Variant = ButtonVariant.Primary,
        };
        _applyTuningButton.Click += OnApplyWakeTuning;

        var tuningFields = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Dock = DockStyle.Top,
            Padding = new Padding(8, 8, 8, 0),
        };
        tuningFields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tuningFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddField(tuningFields, 0, "Microphone gain", _microphoneGain);
        AddField(tuningFields, 1, "Wake probability cutoff", _wakeProbability);
        AddField(tuningFields, 2, "Sliding window", _slidingWindow);
        AddField(tuningFields, 3, "VAD probability cutoff", _vadProbability);

        var tuningCommands = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(8, 4, 8, 4),
            WrapContents = false,
        };
        tuningCommands.Controls.Add(_loadTuningButton);
        tuningCommands.Controls.Add(_applyTuningButton);

        var tuningLayout = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 1,
            Dock = DockStyle.Top,
        };
        tuningLayout.Controls.Add(tuningFields, 0, 0);
        tuningLayout.Controls.Add(tuningCommands, 0, 1);
        tuningLayout.Controls.Add(_tuningStatus, 0, 2);
        tuningLayout.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(680, 0),
            Text = "Full duplex requires Joydex Audio Barge In. Joydex verifies that switch when Room Voice connects.",
        }, 0, 3);
        var tuningGroup = new GroupBox
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(8),
            Text = "Device and wake",
        };
        var deviceAndWake = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 1,
            Dock = DockStyle.Top,
        };
        deviceAndWake.Controls.Add(deviceFields, 0, 0);
        deviceAndWake.Controls.Add(tuningLayout, 0, 1);
        tuningGroup.Controls.Add(deviceAndWake);

        var diagnosticsCommands = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            WrapContents = true,
        };
        var openCaptures = new RoundedButton { Text = "Open legacy captures" };
        openCaptures.Click += (_, _) => OpenDiagnosticsFolder();
        var deleteCaptures = new RoundedButton { Text = "Delete legacy captures" };
        deleteCaptures.Click += (_, _) => DeleteDiagnosticsCaptures();
        diagnosticsCommands.Controls.Add(openCaptures);
        diagnosticsCommands.Controls.Add(deleteCaptures);
        var diagnosticsLayout = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 1,
            Dock = DockStyle.Top,
            Padding = new Padding(8),
        };
        diagnosticsLayout.Controls.Add(_preserveAssistantAudioDiagnostics, 0, 0);
        diagnosticsLayout.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(680, 0),
            Text = "Numeric audio health remains in the Joydex log when WAV preservation is off.",
        }, 0, 1);
        diagnosticsLayout.Controls.Add(diagnosticsCommands, 0, 2);
        var diagnosticsGroup = CreateGroup("Diagnostics", diagnosticsLayout);

        var advancedFields = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Dock = DockStyle.Top,
            Padding = new Padding(8),
        };
        advancedFields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        advancedFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddField(advancedFields, 0, "Session route", _sessionMode);
        AddField(advancedFields, 1, "Dedicated task deep link", _dedicatedTaskReference);
        AddField(advancedFields, 2, "Pinned App Server executable", _codexAppServerPath);
        AddField(advancedFields, 3, "LASTVOICE fallback task", _taskReference);
        AddField(advancedFields, 4, "Fallback task label", _taskLabel);
        var advancedCommands = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(8, 0, 8, 8),
        };
        advancedCommands.Controls.Add(_testButton);
        var advancedLayout = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 1,
            Dock = DockStyle.Top,
        };
        advancedLayout.Controls.Add(advancedFields, 0, 0);
        advancedLayout.Controls.Add(advancedCommands, 0, 1);
        advancedLayout.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(680, 0),
            Padding = new Padding(8, 0, 8, 8),
            Text = "Joydex keeps the dedicated task's writer lock. Codex Desktop cannot open that task while Room Voice is running.",
        }, 0, 2);
        var advancedGroup = CreateGroup("Advanced", advancedLayout);

        var root = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            RowCount = 9,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(680, 0),
            Text = "Configure the dedicated room device, conversation, and recovery diagnostics.",
        }, 0, 0);
        root.Controls.Add(_enabled, 0, 1);
        root.Controls.Add(workspaceGroup, 0, 2);
        root.Controls.Add(conversationGroup, 0, 3);
        root.Controls.Add(desktopBridgeGroup, 0, 4);
        root.Controls.Add(tuningGroup, 0, 5);
        root.Controls.Add(diagnosticsGroup, 0, 6);
        root.Controls.Add(advancedGroup, 0, 7);
        Controls.Add(root);

        _endpoint.TextChanged += OnEndpointChanged;
        _agentWorkspacePath.TextChanged += OnWorkspaceSelectionChanged;
        _agentProject.SelectedIndexChanged += OnProjectSelected;
        _sessionMode.SelectedIndexChanged += (_, _) => UpdateRouteFields();
        Load += OnLoaded;
        Disposed += OnDisposed;
        UpdateRouteFields();
        ThemeService.Apply(this);
    }

    internal VoicePePreferences ReadPreferences() => new VoicePePreferences(
        Enabled: _enabled.Checked,
        DeviceEndpoint: _endpoint.Text,
        PinnedTaskId: _taskReference.Text,
        PinnedTaskLabel: _taskLabel.Text,
        SessionMode: (_sessionMode.SelectedItem as SessionModeChoice)?.Mode
            ?? VoicePeSessionMode.LastVoiceFallback,
        DedicatedTaskId: WorkspaceMatchesProvisioned() ? _dedicatedTaskReference.Text : string.Empty,
        DedicatedTaskLabel: _dedicatedTaskLabel.Text,
        CodexAppServerPath: _codexAppServerPath.Text,
        AgentWorkspacePath: _agentWorkspacePath.Text,
        AgentProjectId: SelectedProjectId(),
        AgentProjectLabel: SelectedProjectLabel(),
        RealtimeVoice: (_realtimeVoice.SelectedItem as RealtimeVoiceChoice)?.Value ?? string.Empty,
        ConversationSpeakerGain: decimal.ToInt32(_conversationSpeakerGain.Value),
        PreserveAssistantAudioDiagnostics: _preserveAssistantAudioDiagnostics.Checked,
        DesktopTaskMessagingEnabled: _desktopTaskMessaging.Checked,
        VoiceTargetTaskId: _voiceTargetTaskId,
        VoiceTargetHostId: _voiceTargetHostId,
        VoiceTargetTaskLabel: _voiceTargetTaskLabel).Normalize();

    private void InstallDesktopBridge()
    {
        try
        {
            if (!File.Exists(_desktopBridgeHostPath))
            {
                throw new FileNotFoundException(
                    "This Joydex package does not contain Joydex.DesktopBridgeHost.exe.",
                    _desktopBridgeHostPath);
            }
            _desktopBridgeConfiguration.InstallOrRepair(_desktopBridgeHostPath);
            _desktopTaskMessaging.Checked = true;
            RefreshDesktopBridgeStatus();
            MessageBox.Show(
                this,
                "The Desktop Task Bridge is configured. Joydex will start it with Room Voice; no Desktop restart is required.",
                "Desktop Task Bridge",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException)
        {
            MessageBox.Show(this, exception.Message, "Desktop Task Bridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RefreshDesktopBridgeStatus();
        }
    }

    private void RemoveDesktopBridge()
    {
        try
        {
            _desktopBridgeConfiguration.Remove();
            _desktopTaskMessaging.Checked = false;
            RefreshDesktopBridgeStatus();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException)
        {
            MessageBox.Show(this, exception.Message, "Desktop Task Bridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RefreshDesktopBridgeStatus()
    {
        try
        {
            var status = _desktopBridgeConfiguration.Inspect(_desktopBridgeHostPath);
            _desktopBridgeStatus.Text = status.Message;
            _desktopBridgeStatus.ForeColor = status.State == DesktopBridgeConfigurationState.Installed
                ? JoydexTheme.Success
                : status.State == DesktopBridgeConfigurationState.Conflict
                    ? Color.IndianRed
                    : SystemColors.GrayText;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException)
        {
            _desktopBridgeStatus.Text = "Desktop Task Bridge status is unavailable: " + exception.Message;
            _desktopBridgeStatus.ForeColor = Color.IndianRed;
        }
    }

    private void InitializeProjectChoices(VoicePePreferences initial)
    {
        _updatingWorkspaceControls = true;
        try
        {
            _agentProject.Items.Clear();
            var dedicated = WorkspaceProjectChoice.NewDedicated();
            _agentProject.Items.Add(dedicated);
            WorkspaceProjectChoice? initialProject = null;
            if (!string.IsNullOrWhiteSpace(initial.AgentProjectId)
                && !string.IsNullOrWhiteSpace(initial.AgentWorkspacePath))
            {
                initialProject = WorkspaceProjectChoice.Existing(
                    initial.AgentProjectId,
                    string.IsNullOrWhiteSpace(initial.AgentProjectLabel)
                        ? "Local Codex project"
                        : initial.AgentProjectLabel,
                    initial.AgentWorkspacePath);
                _agentProject.Items.Add(initialProject);
            }

            var custom = WorkspaceProjectChoice.Custom();
            _agentProject.Items.Add(custom);
            _agentProject.SelectedItem = initialProject
                ?? (string.IsNullOrWhiteSpace(initial.AgentWorkspacePath) ? dedicated : custom);
        }
        finally
        {
            _updatingWorkspaceControls = false;
        }
    }

    private async Task LoadProjectChoicesAsync()
    {
        if (_listProjects is null || string.IsNullOrWhiteSpace(_codexAppServerPath.Text))
        {
            return;
        }

        var currentPath = _agentWorkspacePath.Text;
        var currentProjectId = SelectedProjectId();
        try
        {
            _workspaceStatus.Text = "Loading local Codex projects…";
            var catalog = await _listProjects(_codexAppServerPath.Text, _tuningCancellationToken);
            _tuningCancellationToken.ThrowIfCancellationRequested();
            _updatingWorkspaceControls = true;
            try
            {
                _agentProject.Items.Clear();
                var dedicated = WorkspaceProjectChoice.NewDedicated();
                _agentProject.Items.Add(dedicated);
                foreach (var root in catalog.Roots)
                {
                    _agentProject.Items.Add(WorkspaceProjectChoice.Existing(
                        root.ProjectId,
                        root.ProjectName,
                        root.RootPath));
                }
                var custom = WorkspaceProjectChoice.Custom();
                _agentProject.Items.Add(custom);

                var selected = _agentProject.Items
                    .Cast<WorkspaceProjectChoice>()
                    .FirstOrDefault(choice =>
                        choice.Kind == WorkspaceProjectKind.Existing
                        && string.Equals(choice.ProjectId, currentProjectId, StringComparison.Ordinal)
                        && PathsEqualWhenValid(choice.RootPath, currentPath))
                    ?? _agentProject.Items
                        .Cast<WorkspaceProjectChoice>()
                        .FirstOrDefault(choice =>
                            choice.Kind == WorkspaceProjectKind.Existing
                            && PathsEqualWhenValid(choice.RootPath, currentPath))
                    ?? (string.IsNullOrWhiteSpace(_provisionedWorkspacePath) ? dedicated : custom);
                _agentProject.SelectedItem = selected;
                _agentWorkspacePath.Text = currentPath;
            }
            finally
            {
                _updatingWorkspaceControls = false;
            }

            _workspaceStatus.Text = catalog.Warning
                ?? (WorkspaceMatchesProvisioned()
                    ? "The owned task and working folder match the saved configuration."
                    : "Create a fresh owned task to apply this workspace selection.");
        }
        catch (OperationCanceledException) when (_tuningCancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _workspaceStatus.Text = $"Local projects could not be loaded. A custom folder still works: {exception.Message}";
        }
    }

    private void OnProjectSelected(object? sender, EventArgs eventArgs)
    {
        if (_updatingWorkspaceControls || _agentProject.SelectedItem is not WorkspaceProjectChoice choice)
        {
            return;
        }

        _updatingWorkspaceControls = true;
        try
        {
            if (choice.Kind == WorkspaceProjectKind.NewDedicated)
            {
                _agentWorkspacePath.Text = SuggestDefaultWorkspacePath();
            }
            else if (choice.Kind == WorkspaceProjectKind.Existing)
            {
                _agentWorkspacePath.Text = choice.RootPath;
            }
        }
        finally
        {
            _updatingWorkspaceControls = false;
        }

        MarkWorkspaceSelectionChanged();
    }

    private void OnWorkspaceSelectionChanged(object? sender, EventArgs eventArgs)
    {
        if (!_updatingWorkspaceControls)
        {
            MarkWorkspaceSelectionChanged();
        }
    }

    private void MarkWorkspaceSelectionChanged()
    {
        _workspaceStatus.Text = WorkspaceMatchesProvisioned()
            ? "The owned task and working folder match the saved configuration."
            : "Create a fresh owned task to apply this workspace selection.";
    }

    private void OnBrowseWorkspace(object? sender, EventArgs eventArgs)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the working folder for the Joydex-owned Codex task.",
            ShowNewFolderButton = true,
            UseDescriptionForTitle = true,
        };
        if (Directory.Exists(_agentWorkspacePath.Text))
        {
            dialog.InitialDirectory = _agentWorkspacePath.Text;
        }

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _updatingWorkspaceControls = true;
        try
        {
            _agentProject.SelectedItem = _agentProject.Items
                .Cast<WorkspaceProjectChoice>()
                .First(choice => choice.Kind == WorkspaceProjectKind.Custom);
            _agentWorkspacePath.Text = dialog.SelectedPath;
        }
        finally
        {
            _updatingWorkspaceControls = false;
        }
        MarkWorkspaceSelectionChanged();
    }

    private async void OnProvisionWorkspace(object? sender, EventArgs eventArgs)
    {
        if (_provisionWorkspace is null)
        {
            return;
        }

        var path = _agentWorkspacePath.Text.Trim();
        if (!Path.IsPathFullyQualified(path))
        {
            MessageBox.Show(
                this,
                "Choose a fully qualified working folder first.",
                "Agent workspace required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var choice = _agentProject.SelectedItem as WorkspaceProjectChoice ?? WorkspaceProjectChoice.Custom();
        _provisionWorkspaceButton.Enabled = false;
        _workspaceStatus.Text = "Creating the workspace and fresh owned task…";
        try
        {
            var request = new CodexVoiceWorkspaceProvisioningRequest(
                WorkspacePath: path,
                ProjectId: SelectedProjectId(),
                ProjectLabel: choice.Kind == WorkspaceProjectKind.NewDedicated
                    ? "Joydex Voice"
                    : SelectedProjectLabel(),
                RegisterProject: choice.Kind == WorkspaceProjectKind.NewDedicated,
                TaskName: "Joydex Voice Chat — Owned (joydex_voice)");
            var result = await _provisionWorkspace(
                _codexAppServerPath.Text,
                request,
                _tuningCancellationToken);
            _tuningCancellationToken.ThrowIfCancellationRequested();

            _provisionedWorkspacePath = result.WorkspacePath;
            _provisionedProjectId = result.ProjectId;
            _updatingWorkspaceControls = true;
            try
            {
                _agentWorkspacePath.Text = result.WorkspacePath;
                var provisionedChoice = WorkspaceProjectChoice.Existing(
                    result.ProjectId,
                    string.IsNullOrWhiteSpace(result.ProjectLabel) ? "Folder-only workspace" : result.ProjectLabel,
                    result.WorkspacePath);
                _agentProject.Items.Insert(Math.Max(1, _agentProject.Items.Count - 1), provisionedChoice);
                _agentProject.SelectedItem = provisionedChoice;
                _dedicatedTaskReference.Text = CodexTaskReference.BuildDeepLink(result.TaskId);
                _dedicatedTaskLabel.Text = "Joydex Voice Chat — Owned (joydex_voice)";
            }
            finally
            {
                _updatingWorkspaceControls = false;
            }

            _workspaceStatus.Text = result.Warning
                ?? "Fresh owned task created and verified in this working folder. Save Configuration to activate it.";
        }
        catch (OperationCanceledException) when (_tuningCancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _workspaceStatus.Text = "Provisioning failed. The saved Room Voice configuration is unchanged.";
            MessageBox.Show(
                this,
                exception.Message,
                "Could not create Agent Workspace",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            if (!IsDisposed && !Disposing)
            {
                _provisionWorkspaceButton.Enabled = true;
            }
        }
    }

    private void OpenSelectedDirectory(bool sessionRecords)
    {
        try
        {
            var workspace = _agentWorkspacePath.Text.Trim();
            if (!Path.IsPathFullyQualified(workspace))
            {
                throw new InvalidDataException("Choose a fully qualified Agent Workspace first.");
            }

            var directory = sessionRecords
                ? VoiceSessionArchive.GetSessionsRoot(workspace)
                : Path.GetFullPath(workspace);
            Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is
            IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException)
        {
            MessageBox.Show(this, exception.Message, "Agent workspace", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private string SelectedProjectId()
    {
        if (_agentProject.SelectedItem is not WorkspaceProjectChoice
            {
                Kind: WorkspaceProjectKind.Existing,
            } choice
            || !PathsEqualWhenValid(choice.RootPath, _agentWorkspacePath.Text))
        {
            return string.Empty;
        }

        return choice.ProjectId;
    }

    private string SelectedProjectLabel() =>
        _agentProject.SelectedItem is WorkspaceProjectChoice
        {
            Kind: WorkspaceProjectKind.Existing,
        } choice
        && PathsEqualWhenValid(choice.RootPath, _agentWorkspacePath.Text)
            ? choice.Label
            : string.Empty;

    private bool WorkspaceMatchesProvisioned() =>
        PathsEqualWhenValid(_agentWorkspacePath.Text, _provisionedWorkspacePath)
        && string.Equals(SelectedProjectId(), _provisionedProjectId, StringComparison.Ordinal);

    private static bool PathsEqualWhenValid(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right);
        }

        try
        {
            return CodexVoiceWorkspaceService.PathsEqual(left, right);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static string SuggestDefaultWorkspacePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return Path.Combine(directory.Parent?.FullName ?? directory.FullName, "joydex_voice");
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "joydex_voice");
    }

    private void UpdateWorkspaceStatus(VoicePePreferences initial)
    {
        _workspaceStatus.Text = string.IsNullOrWhiteSpace(initial.AgentWorkspacePath)
            ? "Choose a location and create a fresh owned task."
            : "The owned task and working folder match the saved configuration.";
    }

    private async void OnTestTarget(object? sender, EventArgs eventArgs)
    {
        var preferences = ReadPreferences();
        if (!CodexTaskReference.TryParse(preferences.PinnedTaskId, out _))
        {
            MessageBox.Show(
                this,
                "Paste a valid codex://threads/<task-id> deep link first.",
                "Launch task required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        _testButton.Enabled = false;
        try
        {
            if (!await _testTarget(preferences))
            {
                MessageBox.Show(
                    this,
                    "Joydex did not open the launch task. Dry-run or foreground safety may have blocked the action; see the Joydex log for the exact reason.",
                    "Launch task was not opened",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Launch task test failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _testButton.Enabled = true;
        }
    }

    private async void OnLoaded(object? sender, EventArgs eventArgs)
    {
        await LoadProjectChoicesAsync();
        if (VoicePeEndpoint.TryParse(_endpoint.Text, out _))
        {
            await LoadWakeTuningAsync(showErrors: false);
        }
    }

    private async void OnLoadWakeTuning(object? sender, EventArgs eventArgs) =>
        await LoadWakeTuningAsync(showErrors: true);

    internal async Task LoadWakeTuningAsync(bool showErrors)
    {
        if (!VoicePeEndpoint.TryParse(_endpoint.Text, out var endpoint))
        {
            _tuningStatus.Text = "Enter a valid Voice PE endpoint first.";
            if (showErrors)
            {
                MessageBox.Show(
                    this,
                    _tuningStatus.Text,
                    "Voice PE endpoint required",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            return;
        }

        SetTuningBusy(true, "Loading wake tuning…");
        try
        {
            var tuning = await _readWakeTuning(endpoint, _tuningCancellationToken);
            _tuningCancellationToken.ThrowIfCancellationRequested();
            SetTuningValues(tuning);
            _loadedTuningEndpoint = endpoint.AbsoluteUri;
            _tuningStatus.Text = "Loaded from the device.";
        }
        catch (OperationCanceledException) when (_tuningCancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _loadedTuningEndpoint = null;
            _tuningStatus.Text = DescribeTuningFailure(exception);
            if (showErrors)
            {
                MessageBox.Show(
                    this,
                    _tuningStatus.Text,
                    "Could not load wake tuning",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        finally
        {
            if (!IsDisposed && !Disposing)
            {
                SetTuningBusy(false, _tuningStatus.Text);
            }
        }
    }

    private async void OnApplyWakeTuning(object? sender, EventArgs eventArgs)
    {
        if (!VoicePeEndpoint.TryParse(_endpoint.Text, out var endpoint)
            || !string.Equals(endpoint.AbsoluteUri, _loadedTuningEndpoint, StringComparison.OrdinalIgnoreCase))
        {
            _tuningStatus.Text = "Reload values after changing the device endpoint.";
            _applyTuningButton.Enabled = false;
            return;
        }

        var tuning = new VoicePeWakeTuning(
            MicrophoneGain: decimal.ToInt32(_microphoneGain.Value),
            WakeProbabilityCutoff: decimal.ToDouble(_wakeProbability.Value),
            SlidingWindow: decimal.ToInt32(_slidingWindow.Value),
            VadProbabilityCutoff: decimal.ToDouble(_vadProbability.Value));
        SetTuningBusy(true, "Applying and verifying wake tuning…");
        try
        {
            var confirmed = await _writeWakeTuning(endpoint, tuning, _tuningCancellationToken);
            _tuningCancellationToken.ThrowIfCancellationRequested();
            SetTuningValues(confirmed);
            _tuningStatus.Text = "Applied, persisted, and read back from the device.";
        }
        catch (OperationCanceledException) when (_tuningCancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            var failure = DescribeTuningFailure(exception);
            try
            {
                var actual = await _readWakeTuning(endpoint, _tuningCancellationToken);
                _tuningCancellationToken.ThrowIfCancellationRequested();
                SetTuningValues(actual);
                _tuningStatus.Text = $"{failure} Current device values were reloaded.";
            }
            catch (OperationCanceledException) when (_tuningCancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                _loadedTuningEndpoint = null;
                _tuningStatus.Text = $"{failure} Reload the device values before retrying.";
            }

            MessageBox.Show(
                this,
                _tuningStatus.Text,
                "Could not apply wake tuning",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            if (!IsDisposed && !Disposing)
            {
                SetTuningBusy(false, _tuningStatus.Text);
            }
        }
    }

    private void OnEndpointChanged(object? sender, EventArgs eventArgs)
    {
        if (!VoicePeEndpoint.TryParse(_endpoint.Text, out var endpoint)
            || !string.Equals(endpoint.AbsoluteUri, _loadedTuningEndpoint, StringComparison.OrdinalIgnoreCase))
        {
            _applyTuningButton.Enabled = false;
            _tuningStatus.Text = "Load the current values before applying changes.";
        }
    }

    private void UpdateRouteFields()
    {
        var ownerMode = (_sessionMode.SelectedItem as SessionModeChoice)?.Mode
            == VoicePeSessionMode.JoydexOwner;
        _dedicatedTaskReference.Enabled = ownerMode;
        _dedicatedTaskLabel.Enabled = ownerMode;
        _codexAppServerPath.Enabled = ownerMode;
        _agentProject.Enabled = ownerMode;
        _agentWorkspacePath.Enabled = ownerMode;
        _provisionWorkspaceButton.Enabled = ownerMode && _provisionWorkspace is not null;
        _realtimeVoice.Enabled = ownerMode;
        _conversationSpeakerGain.Enabled = ownerMode;
        _desktopTaskMessaging.Enabled = ownerMode;
    }

    private void OnDisposed(object? sender, EventArgs eventArgs)
    {
        if (_tuningCancellationDisposed)
        {
            return;
        }

        _tuningCancellationDisposed = true;
        _tuningCancellation.Cancel();
        _tuningCancellation.Dispose();
    }

    private void SetTuningValues(VoicePeWakeTuning tuning)
    {
        _microphoneGain.Value = tuning.MicrophoneGain;
        _wakeProbability.Value = Convert.ToDecimal(tuning.WakeProbabilityCutoff);
        _slidingWindow.Value = tuning.SlidingWindow;
        _vadProbability.Value = Convert.ToDecimal(tuning.VadProbabilityCutoff);
    }

    private void SetTuningBusy(bool busy, string status)
    {
        _loadTuningButton.Enabled = !busy;
        _applyTuningButton.Enabled = !busy
            && VoicePeEndpoint.TryParse(_endpoint.Text, out var endpoint)
            && string.Equals(endpoint.AbsoluteUri, _loadedTuningEndpoint, StringComparison.OrdinalIgnoreCase);
        _microphoneGain.Enabled = !busy;
        _wakeProbability.Enabled = !busy;
        _slidingWindow.Enabled = !busy;
        _vadProbability.Enabled = !busy;
        _tuningStatus.Text = status;
    }

    private static string DescribeTuningFailure(Exception exception) =>
        exception is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound }
            ? "This device does not expose Joydex wake tuning; firmware 0.1.8 or later is required."
            : exception.Message;

    private void OpenDiagnosticsFolder()
    {
        try
        {
            var directory = DiagnosticsDirectory();
            Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "Room Voice diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void DeleteDiagnosticsCaptures()
    {
        var directory = DiagnosticsDirectory();
        if (!Directory.Exists(directory))
        {
            MessageBox.Show(this, "There are no saved Room Voice captures.", "Room Voice diagnostics");
            return;
        }

        var captures = Directory.GetFiles(directory, "*.wav", SearchOption.TopDirectoryOnly);
        if (captures.Length == 0)
        {
            MessageBox.Show(this, "There are no saved Room Voice captures.", "Room Voice diagnostics");
            return;
        }

        if (MessageBox.Show(
                this,
                $"Delete {captures.Length} saved Room Voice WAV capture(s)?",
                "Delete Room Voice captures",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        try
        {
            foreach (var capture in captures)
            {
                File.Delete(capture);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "Room Voice diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string DiagnosticsDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Joydex",
        "voice-diagnostics");

    private static GroupBox CreateGroup(string title, Control content)
    {
        var group = new GroupBox
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(8),
            Text = title,
        };
        group.Controls.Add(content);
        return group;
    }

    private static void AddField(TableLayoutPanel fields, int row, string label, Control control)
    {
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.Controls.Add(new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 9, 14, 9),
            Text = label,
        }, 0, row);
        control.Margin = new Padding(0, 6, 0, 6);
        fields.Controls.Add(control, 1, row);
    }

    private static NumericUpDown CreateNumber(
        double minimum,
        double maximum,
        double initial,
        int decimalPlaces = 0,
        double increment = 1) => new()
        {
            DecimalPlaces = decimalPlaces,
            Increment = Convert.ToDecimal(increment),
            Maximum = Convert.ToDecimal(maximum),
            Minimum = Convert.ToDecimal(minimum),
            Value = Convert.ToDecimal(initial),
            Width = 110,
        };

    private sealed record SessionModeChoice(VoicePeSessionMode Mode, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record RealtimeVoiceChoice(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    private enum WorkspaceProjectKind
    {
        NewDedicated,
        Existing,
        Custom,
    }

    private sealed record WorkspaceProjectChoice(
        WorkspaceProjectKind Kind,
        string ProjectId,
        string Label,
        string RootPath)
    {
        public static WorkspaceProjectChoice NewDedicated() => new(
            WorkspaceProjectKind.NewDedicated,
            string.Empty,
            "New dedicated workspace…",
            string.Empty);

        public static WorkspaceProjectChoice Existing(string projectId, string label, string rootPath) => new(
            WorkspaceProjectKind.Existing,
            projectId,
            label,
            rootPath);

        public static WorkspaceProjectChoice Custom() => new(
            WorkspaceProjectKind.Custom,
            string.Empty,
            "Custom folder…",
            string.Empty);

        public override string ToString() => Kind == WorkspaceProjectKind.Existing
            ? $"{Label} — {RootPath}"
            : Label;
    }
}
