using System.Diagnostics;
using Joydex.Secrets;
using Microsoft.Win32;

namespace Joydex.SecretsHost;

/// <summary>Owns the requester pipe and presents one independent approval toast at a time.</summary>
internal sealed class SecretsBrokerApplicationContext : ApplicationContext
{
    private readonly SecretsBrokerRuntime _runtime;
    private readonly object _sessionStateGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly System.Windows.Forms.Timer _reviewTimer = new() { Interval = 150 };
    private readonly SynchronizationContext _ui;
    private readonly Process? _parent;
    private readonly Task _serverTask;
    private SecretsConsentToast? _toast;
    private bool _disposing;

    public SecretsBrokerApplicationContext(SecretsBrokerEndpoint endpoint, Process? parent)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _parent = parent;
        _runtime = new SecretsBrokerRuntime(endpoint.DataRoot);
        SystemEvents.SessionSwitch += OnSessionSwitch;
        lock (_sessionStateGate)
        {
            _runtime.SetSessionLocked(WindowsSessionState.IsLocked());
        }
        var server = new SecretsBrokerPipeServer(endpoint, _runtime);
        _serverTask = server.RunAsync(_lifetime.Token);
        if (_serverTask.IsCompleted) _serverTask.GetAwaiter().GetResult();
        _ = ObserveServerAsync();

        _reviewTimer.Tick += (_, _) => ShowNextRequest();
        _reviewTimer.Start();
        if (_parent is not null)
        {
            _parent.EnableRaisingEvents = true;
            _parent.Exited += OnParentExited;
            if (_parent.HasExited) ExitThread();
        }
    }

    protected override void ExitThreadCore()
    {
        if (_disposing) return;
        _disposing = true;
        _reviewTimer.Stop();
        _toast?.Close();
        _toast = null;
        _lifetime.Cancel();
        _ = FinishShutdownAsync();
    }

    private async Task FinishShutdownAsync()
    {
        try { await _serverTask.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch { SecretsExecLauncher.TerminateOwnedExecutions(); }
        finally { base.ExitThreadCore(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            if (_parent is not null) _parent.Exited -= OnParentExited;
            _reviewTimer.Dispose();
            _toast?.Dispose();
            _lifetime.Dispose();
        }
        base.Dispose(disposing);
    }

    private void ShowNextRequest()
    {
        if (_disposing || _runtime.SessionLocked) return;
        var pendingRequests = _runtime.PendingRequests();
        if (_toast is { IsDisposed: false } activeToast)
        {
            var current = pendingRequests.SingleOrDefault(request => request.AttemptId == activeToast.AttemptId);
            if (current is null)
            {
                activeToast.Close();
            }
            else
            {
                activeToast.UpdateRequest(current);
            }
            return;
        }
        var pending = pendingRequests.FirstOrDefault();
        if (pending is null) return;

        var toast = new SecretsConsentToast(pending);
        _toast = toast;
        toast.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_toast, toast)) _toast = null;
            if (toast.Decision is { } decision)
            {
                _runtime.Decide(
                    pending.AttemptId,
                    pending.DisplayChallenge,
                    decision.Choice,
                    requireOperation: !decision.ApplyToAllCommands);
            }
            else if (!_disposing
                     && !_runtime.SessionLocked
                     && !toast.TimedOut
                     && _runtime.PendingRequests().Any(request => request.AttemptId == pending.AttemptId))
            {
                _runtime.Decide(
                    pending.AttemptId,
                    pending.DisplayChallenge,
                    SecretsConsentChoice.No,
                    requireOperation: true);
            }
            toast.Dispose();
        };
        toast.Show();
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs eventArgs)
    {
        var locked = eventArgs.Reason is SessionSwitchReason.SessionLock
            or SessionSwitchReason.SessionLogoff
            or SessionSwitchReason.ConsoleDisconnect
            or SessionSwitchReason.RemoteDisconnect;
        var unlocked = eventArgs.Reason is SessionSwitchReason.SessionUnlock
            or SessionSwitchReason.SessionLogon
            or SessionSwitchReason.ConsoleConnect
            or SessionSwitchReason.RemoteConnect;
        if (!locked && !unlocked) return;
        lock (_sessionStateGate)
        {
            _runtime.SetSessionLocked(locked);
        }
        _ui.Post(_ =>
        {
            if (locked)
            {
                _toast?.Close();
                _toast = null;
            }
        }, null);
    }

    private void OnParentExited(object? sender, EventArgs eventArgs) =>
        _ui.Post(_ => ExitThread(), null);

    private async Task ObserveServerAsync()
    {
        try
        {
            await _serverTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch
        {
        }
        _ui.Post(_ => ExitThread(), null);
    }
}
