using System.Diagnostics;
using Joydex.Core.Config;
using Joydex.Core.TaskAlerts;
using Joydex.Windows.Actions;

namespace Joydex.Windows.TaskAlerts;

public interface ITaskAlertNavigator
{
    Task<bool> NavigateAsync(TaskAlertNavigationRequest request, CancellationToken cancellationToken);
}

public sealed class TaskDeepLinkNavigator(
    SafetyOptions safety,
    Action<string> log,
    IForegroundProcessGuard? foregroundGuard = null,
    Func<ProcessStartInfo, Process?>? startProcess = null) : ITaskAlertNavigator
{
    private readonly IForegroundProcessGuard _foregroundGuard = foregroundGuard ?? new ForegroundProcessGuard();
    private readonly Func<ProcessStartInfo, Process?> _startProcess = startProcess ?? Process.Start;

    public Task<bool> NavigateAsync(TaskAlertNavigationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var foreground = _foregroundGuard.Check(safety, actionMayBringCodexForward: true);
        if (!foreground.Allowed)
        {
            log($"BLOCKED task alert B{request.Channel}; session={request.SessionId}; error={foreground.Reason}");
            return Task.FromResult(false);
        }

        if (safety.DryRun)
        {
            log($"DRY RUN task alert B{request.Channel}; session={request.SessionId}; target={BuildUri(request.SessionId)}");
            return Task.FromResult(false);
        }

        try
        {
            var target = BuildUri(request.SessionId);
            _startProcess(new ProcessStartInfo(target) { UseShellExecute = true });
            log($"EXECUTED task alert B{request.Channel}; session={request.SessionId}; target={target}");
            return Task.FromResult(true);
        }
        catch (Exception exception)
        {
            log($"FAILED task alert B{request.Channel}; session={request.SessionId}; error={exception.Message}");
            return Task.FromResult(false);
        }
    }

    public static string BuildUri(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return $"codex://threads/{Uri.EscapeDataString(sessionId)}";
    }
}
