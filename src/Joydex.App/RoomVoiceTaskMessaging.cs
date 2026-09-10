using System.Runtime.InteropServices;
using Joydex.Core.Voice;

namespace Joydex.App;

internal sealed record RoomVoiceTaskMessagingSnapshot(
    bool Enabled,
    bool BridgeAvailable,
    string Status,
    IReadOnlyList<DesktopTaskSummary> Tasks,
    string SelectedTaskId,
    string SelectedHostId,
    string SelectedLabel,
    IReadOnlyList<VoiceTaskOutboxDraft> Drafts);

internal sealed record RoomVoiceTaskMessagingCallbacks(
    Func<CancellationToken, Task<RoomVoiceTaskMessagingSnapshot>> Refresh,
    Func<DesktopTaskSummary, CancellationToken, Task> SelectTarget,
    Func<VoiceTaskOutboxDraft, CancellationToken, Task<string>> Retry,
    Func<VoiceTaskOutboxDraft, DesktopTaskSummary, CancellationToken, Task> Retarget,
    Action<VoiceTaskOutboxDraft> Discard,
    Func<IReadOnlyList<VoiceTaskOutboxDraft>> LoadDrafts);

internal sealed class VoiceTaskOutboxForm : ThemedForm
{
    private readonly RoomVoiceTaskMessagingCallbacks _callbacks;
    private readonly ListBox _drafts = new() { Dock = DockStyle.Fill };
    private readonly TextBox _message = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
    };
    private readonly ComboBox _targets = new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList,
    };
    private readonly Label _status = new() { AutoSize = true, Tag = ThemeTone.Subtle };
    private readonly RoundedButton _retry = new() { Text = "Retry" };
    private readonly RoundedButton _retarget = new() { Text = "Retarget" };
    private readonly RoundedButton _copy = new() { Text = "Copy" };
    private readonly RoundedButton _discard = new() { Text = "Discard" };
    private IReadOnlyList<DesktopTaskSummary> _availableTargets = [];
    private bool _busy;

    public VoiceTaskOutboxForm(
        RoomVoiceTaskMessagingCallbacks callbacks,
        RoomVoiceTaskMessagingSnapshot snapshot)
    {
        _callbacks = callbacks;
        Text = "Pending Room Voice messages";
        ShowIcon = false;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(640, 420);
        Size = new Size(780, 560);

        _drafts.DisplayMember = nameof(DraftChoice.Display);
        _drafts.SelectedIndexChanged += (_, _) => RenderSelection();
        _targets.SelectedIndexChanged += (_, _) => UpdateButtons(SelectedDraft() is not null);
        _retry.Click += async (_, _) => await RetryAsync();
        _retarget.Click += async (_, _) => await RetargetAsync();
        _copy.Click += (_, _) => CopySelected();
        _discard.Click += (_, _) => DiscardSelected();

        var detail = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detail.Controls.Add(_status, 0, 0);
        detail.Controls.Add(_message, 0, 1);
        detail.Controls.Add(_targets, 0, 2);
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
        };
        buttons.Controls.Add(_discard);
        buttons.Controls.Add(_copy);
        buttons.Controls.Add(_retarget);
        buttons.Controls.Add(_retry);
        detail.Controls.Add(buttons, 0, 3);

        var root = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            SplitterDistance = 245,
            Padding = new Padding(12),
        };
        root.Panel1.Controls.Add(_drafts);
        root.Panel2.Controls.Add(detail);
        Controls.Add(root);
        ApplySnapshot(snapshot);
        ThemeService.Apply(this);
    }

    public event EventHandler? DraftsChanged;

    private void ApplySnapshot(RoomVoiceTaskMessagingSnapshot snapshot)
    {
        _availableTargets = snapshot.Tasks;
        _targets.BeginUpdate();
        _targets.Items.Clear();
        foreach (var task in _availableTargets)
        {
            _targets.Items.Add(new TargetChoice(task));
        }
        _targets.EndUpdate();
        RefreshDrafts(snapshot.Drafts);
    }

    private void RefreshDrafts(IReadOnlyList<VoiceTaskOutboxDraft>? drafts = null)
    {
        var selectedId = SelectedDraft()?.Id;
        var values = drafts ?? _callbacks.LoadDrafts();
        _drafts.BeginUpdate();
        _drafts.Items.Clear();
        foreach (var draft in values)
        {
            _drafts.Items.Add(new DraftChoice(draft));
        }
        _drafts.EndUpdate();
        _drafts.SelectedItem = _drafts.Items.Cast<DraftChoice>()
            .FirstOrDefault(choice => string.Equals(choice.Draft.Id, selectedId, StringComparison.Ordinal));
        if (_drafts.SelectedIndex < 0 && _drafts.Items.Count > 0)
        {
            _drafts.SelectedIndex = 0;
        }
        RenderSelection();
        DraftsChanged?.Invoke(this, EventArgs.Empty);
    }

    private VoiceTaskOutboxDraft? SelectedDraft() => (_drafts.SelectedItem as DraftChoice)?.Draft;

    private void RenderSelection()
    {
        var draft = SelectedDraft();
        _message.Text = draft?.Message ?? string.Empty;
        _status.Text = draft is null
            ? "No pending messages."
            : $"To {draft.TargetTitle} · {draft.Attempts} attempt(s) · {draft.LatestError}";
        _targets.SelectedItem = draft is null
            ? null
            : _targets.Items.Cast<TargetChoice>().FirstOrDefault(choice =>
                string.Equals(choice.Task.Id, draft.TargetTaskId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(choice.Task.HostId, draft.TargetHostId, StringComparison.OrdinalIgnoreCase));
        UpdateButtons(draft is not null);
    }

    private async Task RetryAsync()
    {
        var draft = SelectedDraft();
        if (draft is null || _busy)
        {
            return;
        }
        SetBusy(true);
        try
        {
            _status.Text = await _callbacks.Retry(draft, CancellationToken.None);
            RefreshDrafts();
        }
        catch (Exception exception)
        {
            _status.Text = "Retry failed: " + exception.Message;
            RefreshDrafts();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RetargetAsync()
    {
        var draft = SelectedDraft();
        var target = (_targets.SelectedItem as TargetChoice)?.Task;
        if (draft is null || target is null || _busy)
        {
            return;
        }
        SetBusy(true);
        try
        {
            await _callbacks.Retarget(draft, target, CancellationToken.None);
            RefreshDrafts();
        }
        catch (Exception exception)
        {
            _status.Text = "Retarget failed: " + exception.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void CopySelected()
    {
        if (SelectedDraft()?.Message is not { Length: > 0 } message)
        {
            return;
        }
        try
        {
            Clipboard.SetText(message);
        }
        catch (ExternalException exception)
        {
            MessageBox.Show(
                this,
                "Windows could not access the clipboard: " + exception.Message,
                "Copy pending message",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void DiscardSelected()
    {
        var draft = SelectedDraft();
        if (draft is null
            || MessageBox.Show(
                this,
                "Discard this pending message?",
                "Pending Room Voice message",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }
        _callbacks.Discard(draft);
        RefreshDrafts();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateButtons(SelectedDraft() is not null);
    }

    private void UpdateButtons(bool hasDraft)
    {
        _retry.Enabled = hasDraft && !_busy;
        _retarget.Enabled = hasDraft && !_busy && _targets.SelectedItem is not null;
        _copy.Enabled = hasDraft && !_busy;
        _discard.Enabled = hasDraft && !_busy;
    }

    private sealed record DraftChoice(VoiceTaskOutboxDraft Draft)
    {
        public string Display => $"{Draft.TargetTitle} · {Draft.CreatedAt:g}";
        public override string ToString() => Display;
    }

    private sealed record TargetChoice(DesktopTaskSummary Task)
    {
        public override string ToString() => Task.Title;
    }
}
