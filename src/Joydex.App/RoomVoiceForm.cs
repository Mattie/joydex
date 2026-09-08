using System.Runtime.InteropServices;
using Joydex.Core.Voice;
using Joydex.Windows.Voice;

namespace Joydex.App;

/// <summary>
/// Presents the Joydex-owned conversation and recovery controls without taking task ownership
/// away from the voice runtime.
/// </summary>
internal sealed class RoomVoiceForm : ThemedForm
{
    private static readonly Size PreferredMinimumSize = new(720, 520);
    private readonly RoomVoiceConversationModel _model;
    private readonly string _windowStatePath;
    private readonly Func<Task> _refresh;
    private readonly Func<Task> _endSession;
    private readonly Func<Task> _restart;
    private readonly Action _openSettings;
    private readonly Action<bool> _visibilityChanged;
    private readonly RoomVoiceTaskMessagingCallbacks _taskMessaging;
    private readonly Label _state = new() { AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, Tag = ThemeTone.Subtle };
    private readonly FlowLayoutPanel _indicators = new() { AutoSize = true, WrapContents = true };
    private readonly Label _ownerIndicator = Indicator("Owner", ready: false);
    private readonly Label _microphoneIndicator = Indicator("Microphone", ready: false);
    private readonly Label _transcriptIndicator = Indicator("Transcript", ready: false);
    private readonly Label _speakerIndicator = Indicator("Speaker", ready: false);
    private readonly RoomVoiceTranscriptView _transcript = new() { Dock = DockStyle.Fill };
    private readonly RoundedButton _refreshButton = new() { Text = "Refresh" };
    private readonly RoundedButton _endButton = new() { Text = "End session" };
    private readonly RoundedButton _restartButton = new() { Text = "Restart" };
    private readonly RoundedButton _clearButton = new() { Text = "Clear view" };
    private readonly ComboBox _voiceTarget = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 420,
    };
    private readonly RoundedButton _refreshTargetsButton = new() { Text = "Refresh targets" };
    private readonly RoundedButton _pendingMessagesButton = new() { Visible = false };
    private readonly Label _taskMessagingStatus = new() { AutoSize = true, Tag = ThemeTone.Subtle };
    private readonly CheckBox _showRaw = new()
    {
        AutoSize = true,
        Text = "Show raw metadata",
    };
    private readonly System.Windows.Forms.Timer _renderTimer = new() { Interval = 100 };
    private readonly System.Windows.Forms.Timer _taskMessagingTimer = new() { Interval = 2000 };
    private bool _allowClose;
    private bool _busy;
    private bool _renderDirty = true;
    private bool _windowStateRestored;
    private int _renderPostPending;
    private long _renderedConversationVersion = -1;
    private bool _renderedRaw;
    private bool _updatingVoiceTarget;
    private bool _refreshingTaskMessaging;
    private RoomVoiceTaskMessagingSnapshot? _taskMessagingSnapshot;

    public RoomVoiceForm(
        RoomVoiceConversationModel model,
        string windowStatePath,
        Func<Task> refresh,
        Func<Task> endSession,
        Func<Task> restart,
        Action openSettings,
        Action<bool> visibilityChanged,
        RoomVoiceTaskMessagingCallbacks taskMessaging)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _windowStatePath = windowStatePath ?? throw new ArgumentNullException(nameof(windowStatePath));
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        _endSession = endSession ?? throw new ArgumentNullException(nameof(endSession));
        _restart = restart ?? throw new ArgumentNullException(nameof(restart));
        _openSettings = openSettings ?? throw new ArgumentNullException(nameof(openSettings));
        _visibilityChanged = visibilityChanged ?? throw new ArgumentNullException(nameof(visibilityChanged));
        _taskMessaging = taskMessaging ?? throw new ArgumentNullException(nameof(taskMessaging));

        Text = "Room Voice";
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        SetLogicalMinimumSize(PreferredMinimumSize);
        Size = new Size(920, 720);
        BuildLayout();

        _renderTimer.Tick += (_, _) =>
        {
            _renderTimer.Stop();
            Interlocked.Exchange(ref _renderPostPending, 0);
            if (!Visible)
            {
                _renderDirty = true;
                return;
            }
            _renderDirty = false;
            RenderSnapshot(_model.GetSnapshot());
        };
        _model.Changed += OnModelChanged;
        _taskMessagingTimer.Tick += (_, _) => RefreshPendingMessages();
        VisibleChanged += (_, _) => OnWorkspaceVisibilityChanged();
        ThemeService.Apply(this);
    }

    public void ShowWorkspace()
    {
        if (!Visible)
        {
            Show();
        }
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }
        BringToFront();
        Activate();
    }

    public void HideWorkspace()
    {
        SaveWindowState();
        Hide();
    }

    public void CloseWorkspace()
    {
        _allowClose = true;
        Close();
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            HideWorkspace();
            return true;
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    protected override void OnLoad(EventArgs eventArgs)
    {
        base.OnLoad(eventArgs);
        if (!_windowStateRestored)
        {
            _windowStateRestored = true;
            RestoreWindowState();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
        SaveWindowState();
        if (!_allowClose && eventArgs.CloseReason == CloseReason.UserClosing)
        {
            eventArgs.Cancel = true;
            Hide();
            return;
        }

        _model.Changed -= OnModelChanged;
        Interlocked.Exchange(ref _renderPostPending, 0);
        _renderTimer.Stop();
        _renderTimer.Dispose();
        _taskMessagingTimer.Stop();
        _taskMessagingTimer.Dispose();
        base.OnFormClosing(eventArgs);
    }

    protected override void OnThemeApplied()
    {
        base.OnThemeApplied();
        _transcript.ApplyTheme();
        ScheduleRender();
    }

    private void BuildLayout()
    {
        var title = new Label
        {
            AutoSize = true,
            Font = JoydexTheme.FontFor(this, JoydexTheme.UiSemiboldFont, 7F),
            Text = "Room Voice",
        };
        _state.Font = JoydexTheme.FontFor(this, JoydexTheme.UiSemiboldFont);

        var heading = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
        };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.Controls.Add(title, 0, 0);
        heading.Controls.Add(_state, 1, 0);
        heading.Controls.Add(_status, 0, 1);
        heading.SetColumnSpan(_status, 2);
        heading.Controls.Add(_indicators, 0, 2);
        heading.SetColumnSpan(_indicators, 2);
        _indicators.Controls.Add(_ownerIndicator);
        _indicators.Controls.Add(_microphoneIndicator);
        _indicators.Controls.Add(_transcriptIndicator);
        _indicators.Controls.Add(_speakerIndicator);

        var targetBar = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 8),
        };
        targetBar.Controls.Add(new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 8, 8, 0),
            Text = "Voice Target",
        });
        targetBar.Controls.Add(_voiceTarget);
        targetBar.Controls.Add(_refreshTargetsButton);
        targetBar.Controls.Add(_pendingMessagesButton);
        targetBar.Controls.Add(_taskMessagingStatus);
        heading.Controls.Add(targetBar, 0, 3);
        heading.SetColumnSpan(targetBar, 2);

        _voiceTarget.SelectedIndexChanged += async (_, _) => await OnVoiceTargetSelectedAsync();
        _refreshTargetsButton.Click += async (_, _) => await RefreshTaskMessagingAsync(showErrors: true);
        _pendingMessagesButton.Click += (_, _) => OpenPendingMessages();

        var transcriptCard = new Panel
        {
            BackColor = JoydexTheme.Surface,
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
        };
        transcriptCard.Controls.Add(_transcript);

        _refreshButton.Click += async (_, _) => await RunCommandAsync(_refresh);
        _endButton.Click += async (_, _) => await RunCommandAsync(_endSession);
        _restartButton.Click += async (_, _) =>
        {
            var snapshot = _model.GetSnapshot();
            if (snapshot.SessionActive
                && MessageBox.Show(
                    this,
                    "End the active voice session and restart Room Voice?",
                    "Restart Room Voice",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return;
            }
            await RunCommandAsync(_restart);
        };
        var settings = new RoundedButton { Text = "Settings" };
        settings.Click += (_, _) => _openSettings();
        var copy = new RoundedButton { Text = "Copy conversation" };
        copy.Click += (_, _) => CopyConversation();
        _clearButton.Click += (_, _) =>
        {
            _model.ClearVisibleConversation();
        };
        _clearButton.AccessibleDescription =
            "Hide the currently visible conversation for this Joydex run without deleting the Codex task.";
        _showRaw.CheckedChanged += (_, _) => ScheduleRender();

        var commands = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Padding(0, 12, 0, 0),
            WrapContents = true,
        };
        commands.Controls.Add(settings);
        commands.Controls.Add(copy);
        commands.Controls.Add(_clearButton);
        commands.Controls.Add(_restartButton);
        commands.Controls.Add(_endButton);
        commands.Controls.Add(_refreshButton);
        commands.Controls.Add(_showRaw);

        var root = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            RowCount = 3,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(heading, 0, 0);
        root.Controls.Add(transcriptCard, 0, 1);
        root.Controls.Add(commands, 0, 2);
        Controls.Add(root);
    }

    private async Task RunCommandAsync(Func<Task> command)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        UpdateCommandState(_model.GetSnapshot());
        try
        {
            await command();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Room Voice", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _busy = false;
            UpdateCommandState(_model.GetSnapshot());
        }
    }

    private void OnModelChanged(object? sender, EventArgs eventArgs)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            if (Interlocked.Exchange(ref _renderPostPending, 1) != 0)
            {
                return;
            }

            try
            {
                BeginInvoke(() =>
                {
                    if (!IsDisposed && !Disposing)
                    {
                        ScheduleRender();
                    }
                });
            }
            catch (InvalidOperationException)
            {
                Interlocked.Exchange(ref _renderPostPending, 0);
            }
            return;
        }

        ScheduleRender();
    }

    private void ScheduleRender()
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        _renderDirty = true;
        if (!Visible)
        {
            _renderTimer.Stop();
            _taskMessagingTimer.Stop();
            return;
        }

        _taskMessagingTimer.Start();
        _ = RefreshTaskMessagingAsync(showErrors: false);

        if (!_renderTimer.Enabled)
        {
            _renderTimer.Start();
        }
    }

    private async Task RefreshTaskMessagingAsync(bool showErrors)
    {
        if (_refreshingTaskMessaging || IsDisposed || Disposing)
        {
            return;
        }
        _refreshingTaskMessaging = true;
        _refreshTargetsButton.Enabled = false;
        try
        {
            var snapshot = await _taskMessaging.Refresh(CancellationToken.None);
            if (IsDisposed || Disposing)
            {
                return;
            }
            _taskMessagingSnapshot = snapshot;
            PopulateVoiceTargets(snapshot);
            UpdatePendingMessages(snapshot.Drafts);
        }
        catch (Exception exception)
        {
            _taskMessagingStatus.Text = "Desktop task list unavailable";
            _voiceTarget.Enabled = false;
            if (showErrors)
            {
                MessageBox.Show(this, exception.Message, "Desktop Task Bridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            _refreshingTaskMessaging = false;
            _refreshTargetsButton.Enabled = true;
        }
    }

    private void PopulateVoiceTargets(RoomVoiceTaskMessagingSnapshot snapshot)
    {
        _updatingVoiceTarget = true;
        try
        {
            _voiceTarget.BeginUpdate();
            _voiceTarget.Items.Clear();
            VoiceTargetChoice? selected = null;
            foreach (var task in snapshot.Tasks)
            {
                var choice = new VoiceTargetChoice(task, FormatTarget(task));
                _voiceTarget.Items.Add(choice);
                if (string.Equals(task.Id, snapshot.SelectedTaskId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(task.HostId, snapshot.SelectedHostId, StringComparison.OrdinalIgnoreCase))
                {
                    selected = choice;
                }
            }
            if (selected is null && snapshot.SelectedTaskId.Length > 0)
            {
                selected = new VoiceTargetChoice(
                    null,
                    (snapshot.SelectedLabel.Length > 0 ? snapshot.SelectedLabel : snapshot.SelectedTaskId)
                    + " (unavailable)");
                _voiceTarget.Items.Insert(0, selected);
            }
            _voiceTarget.SelectedItem = selected;
            _voiceTarget.EndUpdate();
            _voiceTarget.Enabled = snapshot.Enabled && snapshot.BridgeAvailable && snapshot.Tasks.Count > 0;
            _taskMessagingStatus.Text = snapshot.Status;
            _taskMessagingStatus.ForeColor = snapshot.BridgeAvailable
                ? JoydexTheme.Success
                : snapshot.Enabled ? Color.IndianRed : JoydexTheme.TextFaint;
        }
        finally
        {
            _updatingVoiceTarget = false;
        }
    }

    private async Task OnVoiceTargetSelectedAsync()
    {
        if (_updatingVoiceTarget || (_voiceTarget.SelectedItem as VoiceTargetChoice)?.Task is not { } target)
        {
            return;
        }
        try
        {
            await _taskMessaging.SelectTarget(target, CancellationToken.None);
            _taskMessagingStatus.Text = $"Voice commands will target {target.Title}.";
            if (_taskMessagingSnapshot is { } snapshot)
            {
                _taskMessagingSnapshot = snapshot with
                {
                    SelectedTaskId = target.Id,
                    SelectedHostId = target.HostId,
                    SelectedLabel = target.Title,
                };
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Voice Target", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            await RefreshTaskMessagingAsync(showErrors: false);
        }
    }

    private void RefreshPendingMessages()
    {
        try
        {
            UpdatePendingMessages(_taskMessaging.LoadDrafts());
        }
        catch (Exception)
        {
            // A transient archive read failure must not interrupt the conversation display.
        }
    }

    private void UpdatePendingMessages(IReadOnlyList<VoiceTaskOutboxDraft> drafts)
    {
        _pendingMessagesButton.Visible = drafts.Count > 0;
        _pendingMessagesButton.Text = $"Pending messages ({drafts.Count})";
        if (_taskMessagingSnapshot is { } snapshot)
        {
            _taskMessagingSnapshot = snapshot with { Drafts = drafts };
        }
    }

    private void OpenPendingMessages()
    {
        if (_taskMessagingSnapshot is not { } snapshot)
        {
            return;
        }
        using var form = new VoiceTaskOutboxForm(_taskMessaging, snapshot with
        {
            Drafts = _taskMessaging.LoadDrafts(),
        });
        form.DraftsChanged += (_, _) => RefreshPendingMessages();
        form.ShowDialog(this);
        RefreshPendingMessages();
    }

    private static string FormatTarget(DesktopTaskSummary task) => IsRunning(task.Status)
        ? $"{task.Title}  · running"
        : task.Pinned ? $"{task.Title}  · pinned" : task.Title;

    private static bool IsRunning(string status) =>
        status.Equals("active", StringComparison.OrdinalIgnoreCase)
        || status.Equals("running", StringComparison.OrdinalIgnoreCase)
        || status.Equals("inProgress", StringComparison.OrdinalIgnoreCase);

    private sealed record VoiceTargetChoice(DesktopTaskSummary? Task, string Display)
    {
        public override string ToString() => Display;
    }

    private void OnWorkspaceVisibilityChanged()
    {
        _visibilityChanged(Visible);
        if (!Visible)
        {
            _renderTimer.Stop();
            return;
        }

        if (_renderDirty)
        {
            _renderTimer.Stop();
            Interlocked.Exchange(ref _renderPostPending, 0);
            _renderDirty = false;
            RenderSnapshot(_model.GetSnapshot());
        }
    }

    private void RenderSnapshot(RoomVoiceConversationSnapshot snapshot)
    {
        var stateText = snapshot.SessionState.ToString();
        if (!string.Equals(_state.Text, stateText, StringComparison.Ordinal))
        {
            _state.Text = stateText;
        }
        var stateColor = snapshot.SessionState switch
        {
            VoicePeSessionState.Listening => JoydexTheme.Success,
            VoicePeSessionState.Muted => Color.Goldenrod,
            VoicePeSessionState.Error => Color.IndianRed,
            _ => JoydexTheme.Accent,
        };
        if (_state.ForeColor != stateColor)
        {
            _state.ForeColor = stateColor;
        }
        var statusText = snapshot.Error is { Length: > 0 }
            ? $"{snapshot.Status}  {snapshot.Error}"
            : snapshot.Stale
                ? snapshot.Status + " Refreshing conversation…"
                : snapshot.Status;
        if (!string.Equals(_status.Text, statusText, StringComparison.Ordinal))
        {
            _status.Text = statusText;
        }

        UpdateIndicator(_ownerIndicator, "Owner", snapshot.OwnerReady);
        UpdateIndicator(
            _microphoneIndicator,
            "Microphone",
            snapshot.SessionActive && snapshot.SessionState != VoicePeSessionState.Muted);
        UpdateIndicator(_transcriptIndicator, "Transcript", snapshot.HistoryAvailable);
        UpdateIndicator(
            _speakerIndicator,
            "Speaker",
            snapshot.SessionState is VoicePeSessionState.Listening or VoicePeSessionState.Muted);

        if (_renderedConversationVersion != snapshot.ConversationVersion
            || _renderedRaw != _showRaw.Checked)
        {
            _transcript.Render(snapshot.Entries, _showRaw.Checked);
            _renderedConversationVersion = snapshot.ConversationVersion;
            _renderedRaw = _showRaw.Checked;
        }
        UpdateCommandState(snapshot);
    }

    private void CopyConversation()
    {
        var text = _transcript.BuildPlainText();
        if (text.Length == 0)
        {
            return;
        }
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Could not copy Room Voice conversation",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private static Label Indicator(string name, bool ready) => new()
    {
        AutoSize = true,
        ForeColor = ready ? JoydexTheme.Success : JoydexTheme.TextFaint,
        Margin = new Padding(0, 8, 18, 8),
        Text = $"{(ready ? "●" : "○")} {name}",
    };

    private static void UpdateIndicator(Label indicator, string name, bool ready)
    {
        var text = $"{(ready ? "●" : "○")} {name}";
        var color = ready ? JoydexTheme.Success : JoydexTheme.TextFaint;
        if (!string.Equals(indicator.Text, text, StringComparison.Ordinal))
        {
            indicator.Text = text;
        }
        if (indicator.ForeColor != color)
        {
            indicator.ForeColor = color;
        }
    }

    private void UpdateCommandState(RoomVoiceConversationSnapshot snapshot)
    {
        _refreshButton.Enabled = !_busy && snapshot.OwnerReady && !snapshot.SessionActive;
        _endButton.Enabled = !_busy && snapshot.OwnerReady && snapshot.SessionActive;
        _restartButton.Enabled = !_busy
            && (snapshot.OwnerReady || snapshot.SessionState == VoicePeSessionState.Error);
        _clearButton.Enabled = !_busy && snapshot.Entries.Count > 0;
    }

    private void RestoreWindowState()
    {
        var state = ButtonMapWindowStateStore.Load(_windowStatePath);
        if (state is null)
        {
            return;
        }

        var sourceDpi = state.Dpi > 0 ? state.Dpi : DpiUtilities.SystemDpi;
        var size = DpiUtilities.ScaleBetween(
            new Size(state.Width, state.Height),
            sourceDpi,
            DeviceDpi);
        var requested = new Rectangle(state.Left, state.Top, size.Width, size.Height);
        var screen = Screen.AllScreens.FirstOrDefault(candidate =>
            candidate.WorkingArea.IntersectsWith(requested));
        if (screen is null)
        {
            return;
        }

        var working = screen.WorkingArea;
        Bounds = new Rectangle(
            Math.Clamp(requested.Left, working.Left, Math.Max(working.Left, working.Right - requested.Width)),
            Math.Clamp(requested.Top, working.Top, Math.Max(working.Top, working.Bottom - requested.Height)),
            Math.Min(requested.Width, working.Width),
            Math.Min(requested.Height, working.Height));
        if (state.Maximized)
        {
            WindowState = FormWindowState.Maximized;
        }
    }

    private void SaveWindowState()
    {
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        ButtonMapWindowStateStore.Save(
            _windowStatePath,
            new ButtonMapWindowState(
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height,
                WindowState == FormWindowState.Maximized,
                DeviceDpi));
    }
}
