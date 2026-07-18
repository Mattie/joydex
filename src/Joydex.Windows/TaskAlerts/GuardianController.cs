using System.Diagnostics;

namespace Joydex.Windows.TaskAlerts;

public sealed class GuardianController(
    string guardianPath,
    Action<string> log) : IDisposable
{
    private EventWaitHandle? _cleanEvent;
    private EventWaitHandle? _restoreEvent;
    private Process? _process;
    private bool _restoreRequired;

    public bool Start()
    {
        if (_process is not null || !File.Exists(guardianPath))
        {
            return false;
        }

        var eventName = $"Local\\Joydex.GuardianClean.{Environment.ProcessId}.{Guid.NewGuid():N}";
        var restoreEventName = $"Local\\Joydex.GuardianRestore.{Environment.ProcessId}.{Guid.NewGuid():N}";
        _cleanEvent = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        _restoreEvent = new EventWaitHandle(_restoreRequired, EventResetMode.ManualReset, restoreEventName);
        var arguments = $"--parent {Environment.ProcessId} --clean-event \"{eventName}\" --restore-event \"{restoreEventName}\" --port 4123";
        try
        {
            _process = Process.Start(new ProcessStartInfo(guardianPath, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (_process is not null)
            {
                return true;
            }

            _cleanEvent?.Dispose();
            _cleanEvent = null;
            _restoreEvent?.Dispose();
            _restoreEvent = null;
            return false;
        }
        catch (Exception exception)
        {
            log($"Could not start LED guardian: {exception.Message}");
            _cleanEvent?.Dispose();
            _cleanEvent = null;
            _restoreEvent?.Dispose();
            _restoreEvent = null;
            return false;
        }
    }

    public void SetRestoreRequired(bool required)
    {
        _restoreRequired = required;
        if (required)
        {
            _restoreEvent?.Set();
        }
        else
        {
            _restoreEvent?.Reset();
        }
    }

    public void SignalCleanExit()
    {
        _cleanEvent?.Set();
        if (_process is { HasExited: false })
        {
            _process.WaitForExit(2000);
        }
    }

    public void Dispose()
    {
        _process?.Dispose();
        _cleanEvent?.Dispose();
        _restoreEvent?.Dispose();
        _process = null;
        _cleanEvent = null;
        _restoreEvent = null;
        GC.SuppressFinalize(this);
    }
}
