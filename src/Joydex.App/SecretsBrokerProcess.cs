using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Joydex.Secrets;

namespace Joydex.App;

/// <summary>Keeps the Secrets broker available for the current Joydex tray session.</summary>
internal sealed class SecretsBrokerProcess : IDisposable
{
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(6);
    private readonly object _gate = new();
    private readonly string _dataRoot;
    private readonly string _hostPath;
    private readonly SecretsBrokerEndpoint _endpoint;
    private readonly System.Threading.Timer _monitor;
    private Process? _process;
    private DateTimeOffset _processStartedAt;
    private DateTimeOffset _nextStartAt;
    private int _consecutiveFailures;
    private bool _disposed;

    private SecretsBrokerProcess(string dataRoot, string hostPath)
    {
        _dataRoot = Path.GetFullPath(dataRoot);
        _hostPath = hostPath;
        _endpoint = SecretsBrokerEndpoint.Create(_dataRoot);
        _monitor = new System.Threading.Timer(
            _ => EnsureStarted(),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        EnsureStarted();
        _monitor.Change(MonitorInterval, MonitorInterval);
    }

    public static SecretsBrokerProcess? TryStart(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var hostPath = Path.Combine(AppContext.BaseDirectory, "Joydex.SecretsHost.exe");
        return File.Exists(hostPath)
            ? new SecretsBrokerProcess(dataRoot, hostPath)
            : null;
    }

    public void Dispose()
    {
        Process? process;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _monitor.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            process = _process;
            _process = null;
        }
        _monitor.Dispose();
        Stop(process);
    }

    private void EnsureStarted()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_process is not null)
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        if (BrokerIsReady())
                        {
                            ResetRetryLocked();
                            return;
                        }
                        if (DateTimeOffset.UtcNow - _processStartedAt < StartupTimeout) return;
                        Stop(_process);
                        _process = null;
                        ScheduleRetryLocked();
                    }
                    else
                    {
                        ScheduleRetryLocked();
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
                {
                    ScheduleRetryLocked();
                }
                if (_process is not null)
                {
                    _process.Dispose();
                    _process = null;
                }
            }
            if (BrokerIsReady())
            {
                ResetRetryLocked();
                return;
            }
            if (BrokerOwnershipExists() || DateTimeOffset.UtcNow < _nextStartAt) return;

            try
            {
                using var current = Process.GetCurrentProcess();
                var start = new ProcessStartInfo
                {
                    CreateNoWindow = true,
                    FileName = _hostPath,
                    UseShellExecute = false,
                    WorkingDirectory = AppContext.BaseDirectory,
                };
                start.ArgumentList.Add("--broker");
                start.ArgumentList.Add(_dataRoot);
                start.ArgumentList.Add("--parent-pid");
                start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                start.ArgumentList.Add("--parent-start-ticks");
                start.ArgumentList.Add(current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
                _process = Process.Start(start);
                _processStartedAt = DateTimeOffset.UtcNow;
                if (_process is null) ScheduleRetryLocked();
            }
            catch (Exception exception) when (exception is Win32Exception
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
            {
                _process?.Dispose();
                _process = null;
                ScheduleRetryLocked();
            }
        }
    }

    private void ScheduleRetryLocked()
    {
        _consecutiveFailures = Math.Min(_consecutiveFailures + 1, 5);
        var delaySeconds = Math.Min(120, 10 * (1 << (_consecutiveFailures - 1)));
        _nextStartAt = DateTimeOffset.UtcNow.AddSeconds(delaySeconds);
    }

    private void ResetRetryLocked()
    {
        _consecutiveFailures = 0;
        _nextStartAt = DateTimeOffset.MinValue;
    }

    private bool BrokerOwnershipExists()
    {
        try
        {
            if (!Mutex.TryOpenExisting(_endpoint.MutexName, out var ownership)) return false;
            ownership.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private bool BrokerIsReady()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(_endpoint.ReadyEventName, out var ready)) return false;
            using (ready) return ready.WaitOne(0);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void Stop(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
        }
        finally
        {
            process.Dispose();
        }
    }
}
