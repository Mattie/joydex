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
    Func<ProcessStartInfo, Process?>? startProcess = null,
    Func<string?>? resolveT3Executable = null) : ITaskAlertNavigator
{
    private readonly IForegroundProcessGuard _foregroundGuard = foregroundGuard ?? new ForegroundProcessGuard();
    private readonly Func<ProcessStartInfo, Process?> _startProcess = startProcess ?? Process.Start;
    private readonly Func<string?> _resolveT3Executable = resolveT3Executable ?? InstalledT3ForegroundResolver.FindInstalledExecutable;

    public Task<bool> NavigateAsync(TaskAlertNavigationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            request.TaskIdentity.Validate();
            if (request.Identity is not null && request.Identity.PoolKey != request.SessionId)
            {
                log("BLOCKED task alert navigation; the target identity does not match its assignment.");
                return Task.FromResult(false);
            }
        }
        catch (InvalidDataException)
        {
            log("BLOCKED task alert navigation; the target identity is invalid.");
            return Task.FromResult(false);
        }

        var foreground = _foregroundGuard.Check(safety, actionMayBringCodexForward: true);
        if (!foreground.Allowed)
        {
            log($"BLOCKED task alert M{request.Bank} B{request.Button} slot={request.Slot}; session={request.SessionId}; error={foreground.Reason}");
            return Task.FromResult(false);
        }

        if (safety.DryRun)
        {
            log($"DRY RUN task alert M{request.Bank} B{request.Button} slot={request.Slot}; target={BuildUri(request.TaskIdentity)}");
            return Task.FromResult(false);
        }

        try
        {
            var target = BuildUri(request.TaskIdentity);
            var startInfo = new ProcessStartInfo(target) { UseShellExecute = true };
            if (request.TaskIdentity.Backend == TaskAlertBackend.RemoteT3)
            {
                var executable = _resolveT3Executable();
                if (string.IsNullOrWhiteSpace(executable))
                {
                    log("BLOCKED task alert navigation; the installed T3 Desktop executable could not be verified.");
                    return Task.FromResult(false);
                }
                startInfo.FileName = executable;
                startInfo.ArgumentList.Add(target);
            }
            _startProcess(startInfo);
            log($"EXECUTED task alert M{request.Bank} B{request.Button} slot={request.Slot}; session={request.SessionId}; target={target}");
            return Task.FromResult(true);
        }
        catch (Exception exception)
        {
            log($"FAILED task alert M{request.Bank} B{request.Button} slot={request.Slot}; session={request.SessionId}; error={exception.Message}");
            return Task.FromResult(false);
        }
    }

    public static string BuildUri(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return $"codex://threads/{Uri.EscapeDataString(sessionId)}";
    }

    public static string BuildUri(TaskAlertIdentity identity)
    {
        identity.Validate();
        return identity.Backend == TaskAlertBackend.RemoteT3
            ? $"t3code://threads/{Uri.EscapeDataString(identity.EnvironmentId)}/{Uri.EscapeDataString(identity.NativeThreadId)}"
            : BuildUri(identity.NativeThreadId);
    }
}
