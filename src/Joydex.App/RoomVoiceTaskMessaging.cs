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
    IReadOnlyList<RoomVoiceOutboxDraftSummary> Drafts,
    bool DraftsTruncated = false)
{
    public RoomVoiceTaskMessagingSnapshot(
        bool Enabled,
        bool BridgeAvailable,
        string Status,
        IReadOnlyList<DesktopTaskSummary> Tasks,
        string SelectedTaskId,
        string SelectedHostId,
        string SelectedLabel,
        IReadOnlyList<VoiceTaskOutboxDraft> Drafts)
        : this(
            Enabled,
            BridgeAvailable,
            Status,
            Tasks,
            SelectedTaskId,
            SelectedHostId,
            SelectedLabel,
            Drafts.Select(RoomVoiceOutboxDraftSummary.FromFullDraft).ToArray())
    {
    }
}

/// <summary>
/// Bounded information that is safe to retain in a shared Room Voice snapshot. A truncated
/// MessagePreview must be expanded through the explicit full-delivery read callback.
/// </summary>
internal sealed record RoomVoiceOutboxDraftSummary(
    string Id,
    string TargetTaskId,
    string TargetHostId,
    string TargetTitle,
    string MessagePreview,
    bool MessageTruncated,
    DateTimeOffset CreatedAt,
    int Attempts,
    string LatestError)
{
    public static RoomVoiceOutboxDraftSummary FromFullDraft(VoiceTaskOutboxDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return new RoomVoiceOutboxDraftSummary(
            draft.Id,
            draft.TargetTaskId,
            draft.TargetHostId,
            draft.TargetTitle,
            draft.Message,
            MessageTruncated: false,
            draft.CreatedAt,
            draft.Attempts,
            draft.LatestError);
    }
}

internal sealed class RoomVoiceTaskMessagingCallbacks
{
    public RoomVoiceTaskMessagingCallbacks(
        Func<CancellationToken, Task<RoomVoiceTaskMessagingSnapshot>> refresh,
        Func<DesktopTaskSummary, CancellationToken, Task<RuntimeVoiceTargetSelectionResult>> selectTarget,
        Func<RoomVoiceOutboxDraftSummary, CancellationToken, Task<string>> retry,
        Func<RoomVoiceOutboxDraftSummary, DesktopTaskSummary, CancellationToken, Task> retarget,
        Func<IReadOnlyList<RoomVoiceOutboxDraftSummary>> loadDrafts,
        Func<string, CancellationToken, Task<string?>>? readFullOutboxDeliveryAsync = null,
        Func<string, CancellationToken, Task>? discardOutboxDeliveryAsync = null)
    {
        Refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        SelectTarget = selectTarget ?? throw new ArgumentNullException(nameof(selectTarget));
        Retry = retry ?? throw new ArgumentNullException(nameof(retry));
        Retarget = retarget ?? throw new ArgumentNullException(nameof(retarget));
        LoadDrafts = loadDrafts ?? throw new ArgumentNullException(nameof(loadDrafts));
        ReadFullOutboxDeliveryAsync = readFullOutboxDeliveryAsync;
        DiscardOutboxDeliveryAsync = discardOutboxDeliveryAsync;
    }

    public RoomVoiceTaskMessagingCallbacks(
        Func<CancellationToken, Task<RoomVoiceTaskMessagingSnapshot>> refresh,
        Func<DesktopTaskSummary, CancellationToken, Task> selectTarget,
        Func<VoiceTaskOutboxDraft, CancellationToken, Task<string>> retry,
        Func<VoiceTaskOutboxDraft, DesktopTaskSummary, CancellationToken, Task> retarget,
        Action<VoiceTaskOutboxDraft> discard,
        Func<IReadOnlyList<VoiceTaskOutboxDraft>> loadDrafts)
        : this(
            refresh,
            async (target, cancellationToken) =>
            {
                await selectTarget(target, cancellationToken).ConfigureAwait(false);
                return new RuntimeVoiceTargetSelectionResult(
                    RuntimeVoiceTargetSelectionStatus.Applied,
                    $"Voice commands will target {target.Title}.");
            },
            (draft, cancellationToken) => retry(
                FindFullDraft(loadDrafts, draft.Id),
                cancellationToken),
            (draft, target, cancellationToken) => retarget(
                FindFullDraft(loadDrafts, draft.Id),
                target,
                cancellationToken),
            () => loadDrafts().Select(RoomVoiceOutboxDraftSummary.FromFullDraft).ToArray(),
            (id, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var message = loadDrafts().FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, id, StringComparison.Ordinal))?.Message;
                return Task.FromResult(message);
            },
            (id, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var draft = loadDrafts().FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, id, StringComparison.Ordinal));
                if (draft is not null)
                {
                    discard(draft);
                }
                return Task.CompletedTask;
            })
    {
        ArgumentNullException.ThrowIfNull(retry);
        ArgumentNullException.ThrowIfNull(retarget);
        ArgumentNullException.ThrowIfNull(discard);
        ArgumentNullException.ThrowIfNull(loadDrafts);
    }

    public Func<CancellationToken, Task<RoomVoiceTaskMessagingSnapshot>> Refresh { get; }

    public Func<DesktopTaskSummary, CancellationToken, Task<RuntimeVoiceTargetSelectionResult>> SelectTarget { get; }

    public Func<RoomVoiceOutboxDraftSummary, CancellationToken, Task<string>> Retry { get; }

    public Func<RoomVoiceOutboxDraftSummary, DesktopTaskSummary, CancellationToken, Task> Retarget { get; }

    public Func<IReadOnlyList<RoomVoiceOutboxDraftSummary>> LoadDrafts { get; }

    public Func<string, CancellationToken, Task<string?>>? ReadFullOutboxDeliveryAsync { get; }

    public Func<string, CancellationToken, Task>? DiscardOutboxDeliveryAsync { get; }

    private static VoiceTaskOutboxDraft FindFullDraft(
        Func<IReadOnlyList<VoiceTaskOutboxDraft>> loadDrafts,
        string id) =>
        loadDrafts().FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.Ordinal))
        ?? throw new FileNotFoundException("The pending Room Voice message no longer exists.", id);
}

internal static class RoomVoiceOutboxInteraction
{
    private const string TruncatedMarker = "\r\n\r\n[Preview truncated. Copy to read the complete message.]";

    public static string Preview(RoomVoiceOutboxDraftSummary draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return draft.MessageTruncated ? draft.MessagePreview + TruncatedMarker : draft.MessagePreview;
    }

    public static string Status(RoomVoiceOutboxDraftSummary draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var status = $"To {draft.TargetTitle} · {draft.Attempts} attempt(s)";
        if (!string.IsNullOrWhiteSpace(draft.LatestError))
        {
            status += " · " + draft.LatestError;
        }
        return draft.MessageTruncated ? status + " · Preview truncated" : status;
    }

    public static async Task<string> ReadCopyTextAsync(
        RoomVoiceTaskMessagingCallbacks callbacks,
        RoomVoiceOutboxDraftSummary draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callbacks);
        ArgumentNullException.ThrowIfNull(draft);
        if (!draft.MessageTruncated)
        {
            return draft.MessagePreview;
        }
        if (callbacks.ReadFullOutboxDeliveryAsync is null)
        {
            throw new InvalidOperationException("The complete pending message is unavailable.");
        }

        return await callbacks.ReadFullOutboxDeliveryAsync(draft.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new FileNotFoundException("The pending Room Voice message no longer exists.", draft.Id);
    }
}

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
        _copy.Click += async (_, _) => await CopySelectedAsync();
        _discard.Click += async (_, _) => await DiscardSelectedAsync();

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
        Text = snapshot.DraftsTruncated
            ? $"Pending Room Voice messages — newest {snapshot.Drafts.Count}; older messages remain in the outbox"
            : "Pending Room Voice messages";
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

    private void RefreshDrafts(IReadOnlyList<RoomVoiceOutboxDraftSummary>? drafts = null)
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

    private RoomVoiceOutboxDraftSummary? SelectedDraft() => (_drafts.SelectedItem as DraftChoice)?.Draft;

    private void RenderSelection()
    {
        var draft = SelectedDraft();
        _message.Text = draft is null
            ? string.Empty
            : RoomVoiceOutboxInteraction.Preview(draft);
        _status.Text = draft is null
            ? "No pending messages."
            : RoomVoiceOutboxInteraction.Status(draft);
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

    private async Task CopySelectedAsync()
    {
        var draft = SelectedDraft();
        if (draft is null)
        {
            return;
        }
        string message;
        try
        {
            message = await RoomVoiceOutboxInteraction.ReadCopyTextAsync(
                _callbacks,
                draft,
                CancellationToken.None);
            if (message.Length == 0)
            {
                return;
            }
            Clipboard.SetText(message);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                "Windows could not access the clipboard: " + exception.Message,
                "Copy pending message",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private async Task DiscardSelectedAsync()
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
        SetBusy(true);
        try
        {
            if (_callbacks.DiscardOutboxDeliveryAsync is null)
            {
                throw new InvalidOperationException("Discarding pending messages is unavailable.");
            }
            await _callbacks.DiscardOutboxDeliveryAsync(draft.Id, CancellationToken.None);
            RefreshDrafts();
        }
        catch (Exception exception)
        {
            _status.Text = "Discard failed: " + exception.Message;
        }
        finally
        {
            SetBusy(false);
        }
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

    private sealed record DraftChoice(RoomVoiceOutboxDraftSummary Draft)
    {
        public string Display => $"{Draft.TargetTitle} · {Draft.CreatedAt:g}";
        public override string ToString() => Display;
    }

    private sealed record TargetChoice(DesktopTaskSummary Task)
    {
        public override string ToString() => Task.Title;
    }
}
