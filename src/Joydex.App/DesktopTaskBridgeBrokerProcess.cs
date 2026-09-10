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

    private DesktopTaskBridgeBrokerProcess(Process process, Task stderr)
    {
        _process = process;
        _stderr = stderr;
    }

    public static async Task<DesktopTaskBridgeBrokerProcess> StartAsync(
        string executablePath,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(log);
        var fullPath = Path.GetFullPath(executablePath.Trim());
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The Desktop Task Bridge host is missing.", fullPath);
        }

        var startInfo = CreateStartInfo(fullPath);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Desktop Task Bridge broker worker did not start.");
        var stderr = DrainStandardErrorAsync(process, log);
        var broker = new DesktopTaskBridgeBrokerProcess(process, stderr);
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
        catch
        {
            await broker.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
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
