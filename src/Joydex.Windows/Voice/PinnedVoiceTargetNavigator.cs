using System.Diagnostics;
using Joydex.Core.Config;
using Joydex.Core.Voice;
using Joydex.Windows.Actions;

namespace Joydex.Windows.Voice;

public interface IPinnedVoiceTargetNavigator
{
    Task<bool> NavigateAsync(string taskId, CancellationToken cancellationToken);
}

public sealed class PinnedVoiceTargetNavigator(
    SafetyOptions safety,
    Action<string> log,
    IForegroundProcessGuard? foregroundGuard = null,
    Func<ProcessStartInfo, Process?>? startProcess = null) : IPinnedVoiceTargetNavigator
{
    private readonly IForegroundProcessGuard _foregroundGuard = foregroundGuard ?? new ForegroundProcessGuard();
    private readonly Func<ProcessStartInfo, Process?> _startProcess = startProcess ?? Process.Start;

    public Task<bool> NavigateAsync(string taskId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = CodexTaskReference.BuildDeepLink(taskId);
        var foreground = _foregroundGuard.Check(safety, actionMayBringCodexForward: true);
        if (!foreground.Allowed)
        {
            log($"BLOCKED Voice PE pinned target; target={target}; error={foreground.Reason}");
            return Task.FromResult(false);
        }

        if (safety.DryRun)
        {
            log($"DRY RUN Voice PE pinned target; target={target}");
            return Task.FromResult(false);
        }

        try
        {
            _startProcess(new ProcessStartInfo(target) { UseShellExecute = true });
            log($"EXECUTED Voice PE pinned target; target={target}");
            return Task.FromResult(true);
        }
        catch (Exception exception)
        {
            log($"FAILED Voice PE pinned target; target={target}; error={exception.Message}");
            return Task.FromResult(false);
        }
    }
}
