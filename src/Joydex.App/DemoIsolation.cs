using Joydex.Core.Config;
using Joydex.Core.Mapping;
using Joydex.Core.TaskAlerts;
using Joydex.Windows.Actions;
using Joydex.Windows.TaskAlerts;

namespace Joydex.App;

/// <summary>Resolves no live Codex keybindings in demo mode.</summary>
internal sealed class DemoCodexKeybindingResolver : ICodexKeybindingResolver, IAsyncDisposable
{
    public Task<CodexBindingResolution> ResolveAsync(
        CodexAction action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CodexBindingResolution(
            action,
            action.ToString(),
            null,
            CodexBindingSource.None,
            CodexBindingSnapshotState.Unavailable,
            "Live keybinding resolution is unavailable in the demo inspector."));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Avoids inspecting the user's foreground process in demo mode.</summary>
internal sealed class DemoForegroundProcessGuard : IForegroundProcessGuard
{
    public ForegroundCheck Check(SafetyOptions safety, bool actionMayBringCodexForward) =>
        new(true, "Joydex.Demo", "Demo input is simulated.");
}

/// <summary>Accepts demo action requests without sending Windows input.</summary>
internal sealed class DemoInputSender : IInputSender
{
    public Task SendSequenceAsync(KeySequence sequence, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task SendTextAsync(string text, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public void HoldChord(KeyChord chord)
    {
    }

    public void ReleaseChord(KeyChord chord)
    {
    }

    public void SendMouseWheel(int delta)
    {
    }
}

/// <summary>Keeps task-alert rendering inert in demo mode.</summary>
internal sealed class DemoTaskAlertLedOutput : ITaskAlertLedOutput
{
    public event EventHandler<string>? StatusChanged
    {
        add { }
        remove { }
    }

    public event EventHandler<bool>? ProfileDirtyChanged
    {
        add { }
        remove { }
    }

    public bool RestorePending => false;

    public void Apply(TaskAlertSnapshot snapshot)
    {
    }

    public void RestoreAndReplay(bool replay)
    {
    }

    public void SetPaused(bool paused)
    {
    }

    public Task<bool> WaitForIdleAsync(TimeSpan timeout) => Task.FromResult(true);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
