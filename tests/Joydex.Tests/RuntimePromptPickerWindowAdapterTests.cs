using System.Collections.Concurrent;
using Joydex.App;
using Joydex.Contracts;
using Joydex.Core.Config;

namespace Joydex.Tests;

public sealed class RuntimePromptPickerWindowAdapterTests
{
    [Fact]
    public void ProjectsTheCompleteActivePickerIncludingLongPromptsFlagsAndExitIndex()
    {
        var ui = new QueuedSynchronizationContext();
        var view = new RecordingView(ui);
        var prompts = Enumerable.Range(0, 96)
            .Select(index => index == 73 ? new string('λ', 8_192) : $"prompt-{index}")
            .ToList();
        var flags = prompts.Select((_, index) => index % 3 == 0).ToList();
        var picker = Picker("picker-large", "Complete library", prompts, flags, includeExit: true);
        var adapter = CreateAdapter(ui, view);

        adapter.Apply(
            new RuntimePromptPickerSnapshot(true, picker.Id, prompts.Count),
            Config(picker));

        Assert.Empty(view.Applied);
        ui.Drain();
        var projected = Assert.Single(view.Applied);
        Assert.Equal(picker.Id, projected.PickerId);
        Assert.Equal(picker.Name, projected.PickerName);
        Assert.Equal(prompts.Count, projected.SelectedIndex);
        Assert.Equal(prompts, projected.Prompts.Take(prompts.Count));
        Assert.Equal(PromptPickerCoordinator.ExitOptionLabel, projected.Prompts[^1]);
        Assert.Equal(flags, projected.SubmitAfterInsert.Take(flags.Count));
        Assert.False(projected.SubmitAfterInsert[^1]);
        Assert.Equal(8_192, projected.Prompts[73].Length);
        ui.DrainDispose(adapter);
    }

    [Fact]
    public void RepeatedAndReconnectedStateDoesNotRecreateOrReapplyTheView()
    {
        var ui = new QueuedSynchronizationContext();
        var view = new RecordingView(ui);
        var factoryCalls = 0;
        var adapter = CreateAdapter(ui, view, () => factoryCalls++);
        var firstConfig = Config(Picker("picker", "Quick", ["one", "two"], [false, true]));
        var reconnectConfig = Config(Picker("picker", "Quick", ["one", "two"], [false, true]));
        var state = new RuntimePromptPickerSnapshot(true, "picker", 1);

        adapter.Apply(state, firstConfig);
        adapter.Apply(state, reconnectConfig);
        adapter.Apply(state, firstConfig);
        ui.Drain();

        Assert.Equal(1, factoryCalls);
        Assert.Single(view.Applied);
        adapter.Apply(state with { SelectedIndex = 0 }, reconnectConfig);
        ui.Drain();
        Assert.Equal(2, view.Applied.Count);
        ui.DrainDispose(adapter);
    }

    [Fact]
    public void RemovedPickerAndInvalidAbsoluteIndexHideWithoutSelectingAnotherPicker()
    {
        var ui = new QueuedSynchronizationContext();
        var view = new RecordingView(ui);
        var adapter = CreateAdapter(ui, view);
        var selected = Picker("selected", "Selected", ["first", "second"], [false, false]);
        var other = Picker("other", "Other", ["wrong"], [true]);
        var visible = new RuntimePromptPickerSnapshot(true, selected.Id, 1);

        adapter.Apply(visible, Config(selected, other));
        ui.Drain();
        adapter.Apply(visible, Config(other));
        ui.Drain();
        Assert.Equal(1, view.HideCount);
        Assert.Single(view.Applied);

        adapter.Apply(visible, Config(selected, other));
        ui.Drain();
        adapter.Apply(visible with { SelectedIndex = 2 }, Config(selected, other));
        ui.Drain();
        Assert.Equal(2, view.HideCount);
        Assert.Equal(2, view.Applied.Count);
        Assert.DoesNotContain(view.Applied, snapshot => snapshot.PickerId == other.Id);
        ui.DrainDispose(adapter);
    }

    [Fact]
    public async Task DisposalSkipsQueuedUpdatesAndClosesTheExistingViewOnTheUiContext()
    {
        var ui = new QueuedSynchronizationContext();
        var view = new RecordingView(ui);
        var adapter = CreateAdapter(ui, view);
        var picker = Picker("picker", "Quick", ["one", "two"], [false, false]);

        adapter.Apply(new RuntimePromptPickerSnapshot(true, picker.Id, 0), Config(picker));
        ui.Drain();
        adapter.Apply(new RuntimePromptPickerSnapshot(true, picker.Id, 1), Config(picker));
        var disposal = adapter.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);

        ui.Drain();
        await disposal;
        Assert.Single(view.Applied);
        Assert.Equal(1, view.HideCount);
        Assert.Equal(1, view.CloseCount);
        Assert.Equal(1, view.DisposeCount);
        Assert.True(view.AllCallsWereOnUiContext);

        adapter.Apply(new RuntimePromptPickerSnapshot(true, picker.Id, 0), Config(picker));
        Assert.Equal(0, ui.PendingCount);
    }

    [Fact]
    public void DuplicateDismissSignalsSendOneCommandWithOneOperationIdPerVisibleLifecycle()
    {
        var ui = new QueuedSynchronizationContext();
        var view = new RecordingView(ui);
        var commands = new List<RuntimeCommandRequest>();
        var foreground = true;
        Func<bool>? viewForegroundProbe = null;
        var adapter = new RuntimePromptPickerWindowAdapter(
            ui,
            () => foreground,
            (request, _) =>
            {
                commands.Add(request);
                return Task.FromResult(new RuntimeCommandResult(
                    request.OperationId,
                    request.Kind,
                    RuntimeCommandStatus.Completed));
            },
            probe =>
            {
                viewForegroundProbe = probe;
                return view;
            });
        var picker = Picker("picker", "Quick", ["one"], [false]);
        var visible = new RuntimePromptPickerSnapshot(true, picker.Id, 0);

        adapter.Apply(visible, Config(picker));
        ui.Drain();
        var foregroundProbe = Assert.IsType<Func<bool>>(viewForegroundProbe);
        Assert.True(foregroundProbe());
        foreground = false;
        Assert.False(foregroundProbe());
        view.RequestDismiss();
        view.RequestDismiss();

        var first = Assert.Single(commands);
        Assert.NotEqual(Guid.Empty, first.OperationId);
        Assert.Equal(RuntimeCommandKind.DismissPromptPicker, first.Kind);
        Assert.Null(first.Arguments);
        Assert.Equal(1, view.HideCount);
        Assert.True(view.AllCallsWereOnUiContext);

        adapter.Apply(new RuntimePromptPickerSnapshot(false, string.Empty, 0), Config(picker));
        adapter.Apply(visible, Config(picker));
        ui.Drain();
        view.RequestDismiss();
        Assert.Equal(2, commands.Count);
        Assert.NotEqual(first.OperationId, commands[1].OperationId);
        ui.DrainDispose(adapter);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnsuccessfulDismissAllowsIdenticalAuthoritativeStateAndFreshExplicitDismissal(bool uncertain)
    {
        var ui = new QueuedSynchronizationContext();
        var view = new RecordingView(ui);
        var commands = new List<RuntimeCommandRequest>();
        var attempt = 0;
        var adapter = new RuntimePromptPickerWindowAdapter(
            ui,
            () => true,
            (request, _) =>
            {
                commands.Add(request);
                attempt++;
                if (attempt == 1)
                {
                    return uncertain
                        ? Task.FromException<RuntimeCommandResult>(new IOException("connection lost"))
                        : Task.FromResult(new RuntimeCommandResult(
                            request.OperationId,
                            request.Kind,
                            RuntimeCommandStatus.Rejected));
                }
                return Task.FromResult(new RuntimeCommandResult(
                        request.OperationId,
                        request.Kind,
                        RuntimeCommandStatus.Completed));
            },
            _ => view);
        var picker = Picker("picker", "Quick", ["one"], [false]);
        var visible = new RuntimePromptPickerSnapshot(true, picker.Id, 0);

        adapter.Apply(visible, Config(picker));
        ui.Drain();
        view.RequestDismiss();
        adapter.Apply(visible, Config(picker));
        ui.Drain();

        Assert.Equal(2, view.Applied.Count);
        view.RequestDismiss();
        Assert.Equal(2, commands.Count);
        if (uncertain)
        {
            Assert.Equal(commands[0].OperationId, commands[1].OperationId);
        }
        else
        {
            Assert.NotEqual(commands[0].OperationId, commands[1].OperationId);
        }
        ui.DrainDispose(adapter);
    }

    [Fact]
    public async Task ChangedAuthoritativeSelectionRendersWhileOneDismissCommandRemainsInFlight()
    {
        var ui = new QueuedSynchronizationContext();
        var view = new RecordingView(ui);
        var commands = new List<RuntimeCommandRequest>();
        var firstCompletion = new TaskCompletionSource<RuntimeCommandResult>();
        var adapter = new RuntimePromptPickerWindowAdapter(
            ui,
            () => true,
            (request, _) =>
            {
                commands.Add(request);
                return commands.Count == 1
                    ? firstCompletion.Task
                    : Task.FromResult(new RuntimeCommandResult(
                        request.OperationId,
                        request.Kind,
                        RuntimeCommandStatus.Completed));
            },
            _ => view);
        var picker = Picker("picker", "Quick", ["one", "two"], [false, false]);

        adapter.Apply(new RuntimePromptPickerSnapshot(true, picker.Id, 0), Config(picker));
        ui.Drain();
        view.RequestDismiss();
        adapter.Apply(new RuntimePromptPickerSnapshot(true, picker.Id, 1), Config(picker));
        ui.Drain();
        view.RequestDismiss();
        Assert.Single(commands);
        Assert.Equal(1, view.Applied[^1].SelectedIndex);

        firstCompletion.SetResult(new RuntimeCommandResult(
            commands[0].OperationId,
            RuntimeCommandKind.DismissPromptPicker,
            RuntimeCommandStatus.Completed));
        await ui.WaitForPostAsync();
        ui.Drain();
        view.RequestDismiss();
        Assert.Equal(2, commands.Count);
        Assert.NotEqual(commands[0].OperationId, commands[1].OperationId);
        ui.DrainDispose(adapter);
    }

    [Fact]
    public async Task DisposalAttemptsCloseAndDisposeWhenEarlierViewCleanupFails()
    {
        var ui = new QueuedSynchronizationContext();
        var view = new RecordingView(ui)
        {
            ThrowWhenHiding = true,
            ThrowWhenClosing = true,
        };
        var adapter = CreateAdapter(ui, view);
        var picker = Picker("picker", "Quick", ["one"], [false]);
        adapter.Apply(new RuntimePromptPickerSnapshot(true, picker.Id, 0), Config(picker));
        ui.Drain();

        var disposal = adapter.DisposeAsync().AsTask();
        ui.Drain();
        var failure = await Assert.ThrowsAsync<AggregateException>(() => disposal);

        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.Equal(1, view.HideCount);
        Assert.Equal(1, view.CloseCount);
        Assert.Equal(1, view.DisposeCount);
        Assert.True(view.AllCallsWereOnUiContext);
    }

    private static RuntimePromptPickerWindowAdapter CreateAdapter(
        QueuedSynchronizationContext ui,
        RecordingView view,
        Action? onFactory = null) => new(
            ui,
            () => true,
            (request, _) => Task.FromResult(new RuntimeCommandResult(
                request.OperationId,
                request.Kind,
                RuntimeCommandStatus.Completed)),
            _ =>
            {
                onFactory?.Invoke();
                return view;
            });

    private static CompanionConfig Config(params PromptPickerConfig[] pickers) => new()
    {
        PromptPickers = [.. pickers],
    };

    private static PromptPickerConfig Picker(
        string id,
        string name,
        IEnumerable<string> prompts,
        IEnumerable<bool> submit,
        bool includeExit = false) => new()
        {
            Id = id,
            Name = name,
            Prompts = [.. prompts],
            SubmitAfterInsert = [.. submit],
            IncludeExitOption = includeExit,
            Controls = new PromptPickerControls(),
        };

    private sealed class RecordingView(QueuedSynchronizationContext ui) : IRuntimePromptPickerView
    {
        public event EventHandler? DismissRequested;
        public List<PromptPickerSnapshot> Applied { get; } = [];
        public int HideCount { get; private set; }
        public int CloseCount { get; private set; }
        public int DisposeCount { get; private set; }
        public bool AllCallsWereOnUiContext { get; private set; } = true;
        public bool ThrowWhenHiding { get; init; }
        public bool ThrowWhenClosing { get; init; }

        public void Apply(PromptPickerSnapshot snapshot)
        {
            RecordContext();
            Applied.Add(snapshot);
        }

        public void HidePicker()
        {
            RecordContext();
            HideCount++;
            if (ThrowWhenHiding)
            {
                throw new InvalidOperationException("hide failed");
            }
        }

        public void Close()
        {
            RecordContext();
            CloseCount++;
            if (ThrowWhenClosing)
            {
                throw new InvalidOperationException("close failed");
            }
        }

        public void Dispose()
        {
            RecordContext();
            DisposeCount++;
        }

        public void RequestDismiss()
        {
            ui.Post(_ => DismissRequested?.Invoke(this, EventArgs.Empty), null);
            ui.Drain();
        }

        private void RecordContext() => AllCallsWereOnUiContext &= ui.IsExecuting;
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _pending = new();
        private readonly SemaphoreSlim _posted = new(0);
        public bool IsExecuting { get; private set; }
        public int PendingCount => _pending.Count;

        public override void Post(SendOrPostCallback callback, object? state)
        {
            _pending.Enqueue((callback, state));
            _posted.Release();
        }

        public async Task WaitForPostAsync()
        {
            if (PendingCount == 0)
            {
                await _posted.WaitAsync(TimeSpan.FromSeconds(2));
            }
            Assert.True(PendingCount > 0, "The expected UI callback was not posted.");
        }

        public void Drain()
        {
            while (_pending.TryDequeue(out var work))
            {
                _posted.Wait(0);
                IsExecuting = true;
                try
                {
                    work.Callback(work.State);
                }
                finally
                {
                    IsExecuting = false;
                }
            }
        }

        public void DrainDispose(RuntimePromptPickerWindowAdapter adapter)
        {
            var disposal = adapter.DisposeAsync().AsTask();
            Drain();
            disposal.GetAwaiter().GetResult();
        }
    }
}
