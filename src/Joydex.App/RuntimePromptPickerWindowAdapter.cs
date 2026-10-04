using Joydex.Contracts;
using Joydex.Core.Config;

namespace Joydex.App;

internal interface IRuntimePromptPickerView : IDisposable
{
    event EventHandler? DismissRequested;

    void Apply(PromptPickerSnapshot snapshot);

    void HidePicker();

    void Close();
}

/// <summary>Projects host-owned prompt-picker state onto the local overlay window.</summary>
internal sealed class RuntimePromptPickerWindowAdapter : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SynchronizationContext _ui;
    private readonly Func<bool> _codexStillForeground;
    private readonly Func<RuntimeCommandRequest, CancellationToken, Task<RuntimeCommandResult>> _sendCommand;
    private readonly Func<Func<bool>, IRuntimePromptPickerView> _viewFactory;
    private readonly CancellationTokenSource _lifetime = new();
    private IRuntimePromptPickerView? _view;
    private PromptPickerSnapshot? _lastApplied;
    private bool _hasApplied;
    private Guid _dismissalOperationId;
    private Guid? _dismissInFlightOperationId;
    private bool _dismissSuppressed;
    private bool _restoreIdenticalProjection;
    private long _presentationVersion;
    private bool _disposeRequested;
    private Task? _disposeTask;

    public RuntimePromptPickerWindowAdapter(
        SynchronizationContext ui,
        Func<bool> codexStillForeground,
        Func<RuntimeCommandRequest, CancellationToken, Task<RuntimeCommandResult>> sendCommand,
        Func<Func<bool>, IRuntimePromptPickerView>? viewFactory = null)
    {
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _codexStillForeground = codexStillForeground
            ?? throw new ArgumentNullException(nameof(codexStillForeground));
        _sendCommand = sendCommand ?? throw new ArgumentNullException(nameof(sendCommand));
        _viewFactory = viewFactory ?? (foreground => new PromptPickerOverlayView(foreground));
    }

    public void Apply(RuntimePromptPickerSnapshot? state, CompanionConfig activeConfig)
    {
        ArgumentNullException.ThrowIfNull(activeConfig);
        var projection = Project(state, activeConfig);
        lock (_gate)
        {
            if (_disposeRequested)
            {
                return;
            }
        }

        _ui.Post(_ => ApplyOnUi(projection), null);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposeRequested = true;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
            try
            {
                _ui.Post(_ => DisposeOnUi(completion), null);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            return new ValueTask(_disposeTask);
        }
    }

    private static PromptPickerSnapshot? Project(
        RuntimePromptPickerSnapshot? state,
        CompanionConfig activeConfig)
    {
        if (state is not { Visible: true }
            || string.IsNullOrWhiteSpace(state.PickerId))
        {
            return null;
        }

        var matches = activeConfig.PromptPickers
            .Where(candidate => string.Equals(candidate.Id, state.PickerId, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (matches.Length != 1)
        {
            return null;
        }

        var picker = matches[0];
        if (string.IsNullOrWhiteSpace(picker.Id)
            || string.IsNullOrWhiteSpace(picker.Name)
            || picker.Prompts is null
            || picker.SubmitAfterInsert is null
            || picker.Prompts.Count == 0
            || picker.Prompts.Any(string.IsNullOrWhiteSpace))
        {
            return null;
        }

        var entryCount = picker.Prompts.Count + (picker.IncludeExitOption ? 1 : 0);
        if (state.SelectedIndex < 0 || state.SelectedIndex >= entryCount)
        {
            return null;
        }

        var prompts = picker.IncludeExitOption
            ? picker.Prompts.Concat([PromptPickerCoordinator.ExitOptionLabel]).ToArray()
            : picker.Prompts.ToArray();
        var submitAfterInsert = Enumerable.Range(0, picker.Prompts.Count)
            .Select(index => index < picker.SubmitAfterInsert.Count && picker.SubmitAfterInsert[index])
            .Concat(picker.IncludeExitOption ? [false] : [])
            .ToArray();
        return new PromptPickerSnapshot(
            true,
            picker.Id,
            picker.Name,
            prompts,
            submitAfterInsert,
            state.SelectedIndex);
    }

    private void ApplyOnUi(PromptPickerSnapshot? projection)
    {
        lock (_gate)
        {
            var equivalent = Equivalent(_lastApplied, projection);
            if (_disposeRequested
                || (_hasApplied && equivalent && !_restoreIdenticalProjection))
            {
                return;
            }
            _hasApplied = true;
            _restoreIdenticalProjection = false;

            if (projection is null)
            {
                _lastApplied = null;
                _dismissalOperationId = Guid.Empty;
                _dismissInFlightOperationId = null;
                _dismissSuppressed = false;
                _view?.HidePicker();
                return;
            }

            if (_lastApplied is null
                || !string.Equals(_lastApplied.PickerId, projection.PickerId, StringComparison.OrdinalIgnoreCase)
                || (!equivalent && (_dismissSuppressed || _dismissInFlightOperationId is not null))
                || _dismissalOperationId == Guid.Empty)
            {
                _dismissalOperationId = Guid.NewGuid();
            }
            _lastApplied = projection;
            _dismissSuppressed = false;
            _presentationVersion++;
            _view ??= CreateView();
            _view.Apply(projection);
        }
    }

    private IRuntimePromptPickerView CreateView()
    {
        var view = _viewFactory(_codexStillForeground)
            ?? throw new InvalidOperationException("The prompt-picker view factory returned no view.");
        view.DismissRequested += OnDismissRequested;
        return view;
    }

    private void OnDismissRequested(object? sender, EventArgs eventArgs)
    {
        Guid operationId;
        long presentationVersion;
        lock (_gate)
        {
            if (_disposeRequested
                || _lastApplied is null
                || _dismissSuppressed
                || _dismissInFlightOperationId is not null)
            {
                return;
            }
            operationId = _dismissalOperationId;
            presentationVersion = _presentationVersion;
            _dismissInFlightOperationId = operationId;
            _dismissSuppressed = true;
            _view?.HidePicker();
        }

        _ = SendDismissAsync(operationId, presentationVersion);
    }

    private async Task SendDismissAsync(Guid operationId, long presentationVersion)
    {
        RuntimeCommandResult? result = null;
        var uncertain = false;
        try
        {
            result = await _sendCommand(
                    new RuntimeCommandRequest(operationId, RuntimeCommandKind.DismissPromptPicker),
                    _lifetime.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            uncertain = true;
        }

        try
        {
            _ui.Post(
                _ => CompleteDismissOnUi(operationId, presentationVersion, result, uncertain),
                null);
        }
        catch (Exception)
        {
            // A failed UI scheduler cannot safely update or recreate the overlay.
        }
    }

    private void CompleteDismissOnUi(
        Guid operationId,
        long presentationVersion,
        RuntimeCommandResult? result,
        bool uncertain)
    {
        lock (_gate)
        {
            if (_disposeRequested || _dismissInFlightOperationId != operationId)
            {
                return;
            }

            _dismissInFlightOperationId = null;
            if (_presentationVersion != presentationVersion)
            {
                _dismissSuppressed = false;
                return;
            }
            if (!uncertain
                && result is
                {
                    OperationId: var completedOperationId,
                    Kind: RuntimeCommandKind.DismissPromptPicker,
                    Status: RuntimeCommandStatus.Completed,
                }
                && completedOperationId == operationId)
            {
                return;
            }

            _dismissSuppressed = false;
            _restoreIdenticalProjection = true;
            _hasApplied = false;
            if (!uncertain)
            {
                _dismissalOperationId = Guid.NewGuid();
            }
        }
    }

    private void DisposeOnUi(TaskCompletionSource completion)
    {
        IRuntimePromptPickerView? view;
        lock (_gate)
        {
            view = _view;
            _view = null;
            _lastApplied = null;
        }

        var failures = new List<Exception>();
        TryCleanup(_lifetime.Cancel, failures);
        if (view is not null)
        {
            TryCleanup(() => view.DismissRequested -= OnDismissRequested, failures);
            TryCleanup(view.HidePicker, failures);
            TryCleanup(view.Close, failures);
            TryCleanup(view.Dispose, failures);
        }
        TryCleanup(_lifetime.Dispose, failures);

        if (failures.Count == 0)
        {
            completion.TrySetResult();
        }
        else if (failures.Count == 1)
        {
            completion.TrySetException(failures[0]);
        }
        else
        {
            completion.TrySetException(new AggregateException(failures));
        }
    }

    private static void TryCleanup(Action cleanup, List<Exception> failures)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static bool Equivalent(PromptPickerSnapshot? left, PromptPickerSnapshot? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }
        return left.Visible == right.Visible
            && string.Equals(left.PickerId, right.PickerId, StringComparison.Ordinal)
            && string.Equals(left.PickerName, right.PickerName, StringComparison.Ordinal)
            && left.SelectedIndex == right.SelectedIndex
            && left.Prompts.SequenceEqual(right.Prompts, StringComparer.Ordinal)
            && left.SubmitAfterInsert.SequenceEqual(right.SubmitAfterInsert);
    }

    private sealed class PromptPickerOverlayView(Func<bool> codexStillForeground) : IRuntimePromptPickerView
    {
        private readonly PromptPickerOverlayForm _form = new(codexStillForeground);

        public event EventHandler? DismissRequested
        {
            add => _form.DismissRequested += value;
            remove => _form.DismissRequested -= value;
        }

        public void Apply(PromptPickerSnapshot snapshot) => _form.Apply(snapshot);

        public void HidePicker() => _form.HidePicker();

        public void Close() => _form.Close();

        public void Dispose() => _form.Dispose();
    }
}
