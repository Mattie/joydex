using System.Diagnostics;
using System.Text.Json;
using Joydex.Core.TaskAlerts;
using Joydex.Virpil;

namespace Joydex.Windows.TaskAlerts;

public sealed class GuardianController : IDisposable
{
    private readonly string _guardianPath;
    private readonly Action<string> _log;
    private readonly string? _recoveryPath;
    private readonly string _sessionToken = Guid.NewGuid().ToString("N");
    private EventWaitHandle? _cleanEvent;
    private EventWaitHandle? _restoreEvent;
    private Process? _process;
    private bool _restoreRequired;

    public GuardianController(string guardianPath, Action<string> log, string? recoveryPath = null)
    {
        _guardianPath = guardianPath;
        _log = log;
        _recoveryPath = recoveryPath;
    }

    public bool Start()
    {
        if (_process is not null || !File.Exists(_guardianPath))
        {
            return false;
        }

        var eventName = $"Local\\Joydex.GuardianClean.{Environment.ProcessId}.{Guid.NewGuid():N}";
        var restoreEventName = $"Local\\Joydex.GuardianRestore.{Environment.ProcessId}.{Guid.NewGuid():N}";
        _cleanEvent = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        _restoreEvent = new EventWaitHandle(_restoreRequired, EventResetMode.ManualReset, restoreEventName);
        var arguments = $"--parent {Environment.ProcessId} --clean-event \"{eventName}\" " +
            $"--restore-event \"{restoreEventName}\" --port 4123";
        if (_recoveryPath is not null)
        {
            arguments += $" --recovery \"{_recoveryPath}\" --token \"{_sessionToken}\"";
        }
        try
        {
            _process = Process.Start(new ProcessStartInfo(_guardianPath, arguments)
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
            _log($"Could not start LED guardian: {exception.Message}");
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

    public void UpdateRecovery(TaskAlertSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_recoveryPath is null)
        {
            return;
        }

        var options = snapshot.EffectiveLedOutput;
        JoydexLedRecoveryDocument document;
        if (options.Mode == TaskAlertLedOutputMode.DirectHid)
        {
            var frames = VirpilLedFrameComposer.Compose(snapshot, options, suppressAlerts: true);
            document = new JoydexLedRecoveryDocument(
                JoydexLedRecoveryDocument.CurrentVersion,
                _sessionToken,
                JoydexLedRecoveryDocument.DirectHidBackend,
                frames.Throttle,
                frames.Alpha,
                ResetAlpha: frames.Alpha[0] == 0);
        }
        else
        {
            document = new JoydexLedRecoveryDocument(
                JoydexLedRecoveryDocument.CurrentVersion,
                _sessionToken,
                JoydexLedRecoveryDocument.LinkToolBackend);
        }

        var fullPath = Path.GetFullPath(_recoveryPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The guardian recovery path has no parent directory."));
        var temporaryPath = fullPath + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, document);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void SignalCleanExit()
    {
        _cleanEvent?.Set();
        if (_process is { HasExited: false })
        {
            _process.WaitForExit(2000);
        }

        TryDeleteRecovery();
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

    private void TryDeleteRecovery()
    {
        try
        {
            if (_recoveryPath is not null)
            {
                File.Delete(_recoveryPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log($"Could not remove LED guardian recovery state: {exception.Message}");
        }
    }
}
