using System.Diagnostics;
using System.Text;

namespace Joydex.App;

internal sealed class DesktopTaskBridgeBrokerProcess : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly Process _process;
    private readonly Task _stderr;
    private int _disposed;

    private DesktopTaskBridgeBrokerProcess(Process process, Task stderr, string pipeName)
    {
        _process = process;
        _stderr = stderr;
        PipeName = pipeName;
    }

    public string PipeName { get; }

    public Task Completion => _process.WaitForExitAsync();

    public static async Task<DesktopTaskBridgeBrokerProcess> StartAsync(
        string executablePath,
        string pipeName,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(log);
        var fullPath = Path.GetFullPath(executablePath.Trim());
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The Desktop Task Bridge host is missing.", fullPath);
        }

        var startInfo = CreateStartInfo(fullPath, pipeName);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Desktop Task Bridge broker worker did not start.");
        var stderr = DrainStandardErrorAsync(process, log);
        var broker = new DesktopTaskBridgeBrokerProcess(process, stderr, pipeName.Trim());
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(TimeSpan.FromSeconds(5));
            var ready = await process.StandardOutput.ReadLineAsync(startup.Token).ConfigureAwait(false);
            if (!string.Equals(
                    ready,
                    "Joydex Desktop Task Bridge broker worker started.",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    ready is null
                        ? "The Desktop Task Bridge broker worker exited during startup."
                        : $"Unexpected Desktop Task Bridge startup response: {ready}");
            }
            log("Joydex Desktop Task Bridge broker worker started.");
            return broker;
        }
        catch (Exception startupException)
        {
            try
            {
                await broker.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                throw DesktopTaskBridgeOwnershipCleanupException.ForStartupFailure(
                    startupException,
                    cleanupException);
            }
            throw;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string executablePath, string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        var fullPath = Path.GetFullPath(executablePath.Trim());
        var startInfo = new ProcessStartInfo
        {
            FileName = fullPath,
            WorkingDirectory = Path.GetDirectoryName(fullPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--serve-desktop");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(pipeName.Trim());
        return startInfo;
    }

    private static async Task DrainStandardErrorAsync(Process process, Action<string> log)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                log(line);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
        }
        _process.Dispose();
        try
        {
            await _stderr.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>
/// Reports that the Desktop Task Bridge broker may still own its child process after cleanup
/// failed. A caller must treat this runtime generation as terminal because replacement is unsafe.
/// </summary>
public sealed class DesktopTaskBridgeOwnershipCleanupException(
    string message,
    IEnumerable<Exception> failures) : AggregateException(message, failures)
{
    internal static DesktopTaskBridgeOwnershipCleanupException ForStartupFailure(
        Exception startupFailure,
        Exception cleanupFailure)
    {
        ArgumentNullException.ThrowIfNull(startupFailure);
        ArgumentNullException.ThrowIfNull(cleanupFailure);
        return new DesktopTaskBridgeOwnershipCleanupException(
            "The Desktop Task Bridge broker failed to start and its child process could not be "
            + "released. Its owner generation must not be replaced.",
            [startupFailure, cleanupFailure]);
    }
}

internal static class DesktopTaskBridgeBrokerFailurePolicy
{
    internal static void HandleTerminalStartupFailure(
        DesktopTaskBridgeOwnershipCleanupException exception,
        DesktopTaskBridgeBrokerAdmission admission,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(log);
        admission.StopPermanently(exception);
        log(
            $"Desktop Task Bridge broker worker is unavailable: {exception.Message} "
            + "Automatic replacement is disabled until Joydex restarts.");
    }

    internal static void HandleRetryableStartupFailure(
        Exception exception,
        Action<string> log,
        Action scheduleRestart)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(scheduleRestart);
        log($"Desktop Task Bridge broker worker is unavailable: {exception.Message}");
        scheduleRestart();
    }
}

internal sealed class DesktopTaskBridgeBrokerAdmission
{
    private DesktopTaskBridgeOwnershipCleanupException? _terminalFailure;

    internal bool CanStart => Volatile.Read(ref _terminalFailure) is null;

    internal DesktopTaskBridgeOwnershipCleanupException? TerminalFailure =>
        Volatile.Read(ref _terminalFailure);

    internal void StopPermanently(DesktopTaskBridgeOwnershipCleanupException terminalFailure)
    {
        ArgumentNullException.ThrowIfNull(terminalFailure);
        _ = Interlocked.CompareExchange(ref _terminalFailure, terminalFailure, null);
    }
}
