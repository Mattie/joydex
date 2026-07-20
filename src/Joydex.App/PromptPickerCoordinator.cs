using Joydex.Core.Config;
using Joydex.Core.Runtime;
using Joydex.Windows.Actions;

namespace Joydex.App;

internal sealed record PromptPickerSnapshot(
    bool Visible,
    string PickerId,
    string PickerName,
    IReadOnlyList<string> Prompts,
    IReadOnlyList<bool> SubmitAfterInsert,
    int SelectedIndex);

/// <summary>Owns prompt selection state and the guarded insertion boundary.</summary>
internal sealed class PromptPickerCoordinator
{
    internal const string ExitOptionLabel = "[Exit / Nevermind]";

    private readonly object _gate = new();
    private readonly SafetyOptions _safety;
    private readonly IForegroundProcessGuard _foreground;
    private readonly IInputSender _input;
    private readonly Func<PromptPickerRequest, CancellationToken, Task>? _submit;
    private readonly Action<string> _log;
    private readonly SynchronizationContext _ui;
    private Dictionary<string, PromptPickerConfig> _pickers;
    private string? _activePickerId;
    private int _selectedIndex;
    private bool _visible;

    public PromptPickerCoordinator(
        CompanionConfig config,
        Action<string> log,
        SynchronizationContext ui,
        IForegroundProcessGuard? foreground = null,
        IInputSender? input = null,
        Func<PromptPickerRequest, CancellationToken, Task>? submit = null)
    {
        var normalized = CompanionConfigNormalizer.Normalize(config);
        _safety = normalized.Safety;
        _pickers = normalized.PromptPickers.ToDictionary(picker => picker.Id, StringComparer.OrdinalIgnoreCase);
        _log = log;
        _ui = ui;
        _foreground = foreground ?? new ForegroundProcessGuard();
        _input = input ?? new WindowsInputSender();
        _submit = submit;
    }

    public event EventHandler<PromptPickerSnapshot>? Changed;

    public void UpdateConfig(CompanionConfig config)
    {
        var normalized = CompanionConfigNormalizer.Normalize(config);
        lock (_gate)
        {
            _pickers = normalized.PromptPickers.ToDictionary(picker => picker.Id, StringComparer.OrdinalIgnoreCase);
            ResetLocked();
        }

        Publish(HiddenSnapshot());
    }

    public async Task HandleAsync(PromptPickerRequest request, CancellationToken cancellationToken)
    {
        if (request.Gesture == PromptPickerGesture.Dismiss)
        {
            Dismiss();
            return;
        }

        var foreground = _foreground.Check(_safety, actionMayBringCodexForward: false);
        if (!foreground.Allowed)
        {
            Dismiss();
            _log($"BLOCKED prompt-picker {request.Gesture.ToString().ToLowerInvariant()}; {foreground.Reason}");
            return;
        }

        PromptPickerSnapshot snapshot;
        string? textToInsert = null;
        int insertedIndex = -1;
        var submitAfterInsert = false;
        lock (_gate)
        {
            if (!_pickers.TryGetValue(request.PickerId, out var picker))
            {
                return;
            }

            var sameVisiblePicker = _visible
                && string.Equals(_activePickerId, picker.Id, StringComparison.OrdinalIgnoreCase);
            if (request.Gesture == PromptPickerGesture.Insert)
            {
                insertedIndex = sameVisiblePicker ? _selectedIndex : picker.DefaultPromptIndex;
                if (!picker.IncludeExitOption || insertedIndex < picker.Prompts.Count)
                {
                    textToInsert = picker.Prompts[insertedIndex];
                    submitAfterInsert = picker.SubmitAfterInsert[insertedIndex];
                }
                ResetLocked();
                snapshot = HiddenSnapshot();
            }
            else if (!sameVisiblePicker)
            {
                _activePickerId = picker.Id;
                _selectedIndex = picker.DefaultPromptIndex;
                _visible = true;
                snapshot = SnapshotLocked(picker);
            }
            else
            {
                var delta = request.Gesture == PromptPickerGesture.Up ? -1 : 1;
                var entryCount = picker.Prompts.Count + (picker.IncludeExitOption ? 1 : 0);
                _selectedIndex = (_selectedIndex + delta + entryCount) % entryCount;
                snapshot = SnapshotLocked(picker);
            }
        }

        Publish(snapshot);
        if (textToInsert is null)
        {
            return;
        }

        if (_safety.DryRun)
        {
            _log($"DRY RUN prompt-picker insert; picker={request.PickerId}; index={insertedIndex}; length={textToInsert.Length}; submit={submitAfterInsert}");
            return;
        }

        await _input.SendTextAsync(textToInsert, cancellationToken).ConfigureAwait(false);
        _log($"EXECUTED prompt-picker insert; picker={request.PickerId}; index={insertedIndex}; length={textToInsert.Length}; submit={submitAfterInsert}");
        if (submitAfterInsert)
        {
            var submit = _submit
                ?? throw new InvalidOperationException("Prompt submission is enabled, but no Codex Submit action is available.");
            await submit(request, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dismiss()
    {
        var changed = false;
        lock (_gate)
        {
            changed = _visible;
            ResetLocked();
        }

        if (changed)
        {
            Publish(HiddenSnapshot());
        }
    }

    public bool CodexStillForeground() =>
        _foreground.Check(_safety, actionMayBringCodexForward: false).Allowed;

    private PromptPickerSnapshot SnapshotLocked(PromptPickerConfig picker)
    {
        IReadOnlyList<string> entries = picker.IncludeExitOption
            ? [.. picker.Prompts, ExitOptionLabel]
            : picker.Prompts;
        IReadOnlyList<bool> submitAfterInsert = picker.IncludeExitOption
            ? [.. picker.SubmitAfterInsert, false]
            : picker.SubmitAfterInsert;
        return new PromptPickerSnapshot(true, picker.Id, picker.Name, entries, submitAfterInsert, _selectedIndex);
    }

    private static PromptPickerSnapshot HiddenSnapshot() => new(false, string.Empty, string.Empty, [], [], 0);

    private void ResetLocked()
    {
        _visible = false;
        _activePickerId = null;
        _selectedIndex = 0;
    }

    private void Publish(PromptPickerSnapshot snapshot) =>
        _ui.Post(_ => Changed?.Invoke(this, snapshot), null);
}
