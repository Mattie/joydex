using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Joydex.Windows.T3;

public sealed class T3TransportException(string errorClass, string reason) : Exception(reason)
{
    public string ErrorClass { get; } = errorClass;
}

public interface IT3SshTransport
{
    Task<string> ExecuteAsync(string host, string script, CancellationToken cancellationToken);
}

/// <summary>Owns one finite ssh child. The remote command is constant; configuration travels on stdin.</summary>
public sealed partial class T3SshTransport : IT3SshTransport
{
    public const int MaximumOutputBytes = 8 * 1024 * 1024;
    private readonly Func<string, ProcessStartInfo> _createStartInfo;
    private readonly TimeSpan _deadline;

    public T3SshTransport() : this(CreateStartInfo, TimeSpan.FromSeconds(35)) { }

    internal T3SshTransport(Func<string, ProcessStartInfo> createStartInfo, TimeSpan deadline)
    {
        _createStartInfo = createStartInfo;
        _deadline = deadline;
    }
    public static bool IsValidHost(string? host) => host is { Length: > 0 and <= 128 } && HostPattern().IsMatch(host);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();

    public static ProcessStartInfo CreateStartInfo(string host)
    {
        if (!IsValidHost(host))
            throw new T3TransportException("InvalidHost", "Choose a current-user SSH configuration alias using letters, numbers, dots, dashes or underscores.");
        var start = new ProcessStartInfo("ssh.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in new[] { "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=5", "-o", "ConnectionAttempts=1", "--", host, "python3", "-" })
            start.ArgumentList.Add(argument);
        return start;
    }

    public async Task<string> ExecuteAsync(string host, string script, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_deadline);
        if (!IsValidHost(host)) throw new T3TransportException("InvalidHost", "Choose a valid current-user SSH configuration alias.");
        using var process = new Process { StartInfo = _createStartInfo(host) };
        try
        {
            if (!process.Start())
                throw new T3TransportException("SshUnavailable", "The SSH reader could not start.");
            using var registration = deadline.Token.Register(() => KillOwned(process));
            var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, MaximumOutputBytes, deadline.Token, () => KillOwned(process));
            var stderr = ReadBoundedAsync(process.StandardError.BaseStream, 64 * 1024, deadline.Token, () => KillOwned(process), retain: false);
            await process.StandardInput.WriteAsync(script.AsMemory(), deadline.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new T3TransportException("SshFailed", "The SSH reader failed. Check the saved alias and noninteractive current-user authentication.");
            return await stdout.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new T3TransportException("Timeout", "The SSH metadata read timed out.");
        }
        catch (OperationCanceledException) { throw; }
        catch (T3TransportException) { throw; }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception) when (deadline.IsCancellationRequested)
        {
            throw new T3TransportException("Timeout", "The SSH metadata read timed out.");
        }
        catch
        {
            throw new T3TransportException("SshUnavailable", "The SSH reader could not run; private process output was withheld.");
        }
        finally
        {
            KillOwned(process);
            // The registration has already killed a hung child. Bound reaping as well.
            try { if (process.Id > 0) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception) { }
        }
    }

    private static void KillOwned(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception) { }
    }

    private static async Task<string> ReadBoundedAsync(Stream stream, int limit, CancellationToken cancellationToken, Action limitExceeded, bool retain = true)
    {
        using var result = new MemoryStream();
        var buffer = new byte[16 * 1024];
        var count = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
            if (count > limit)
            {
                limitExceeded();
                throw new T3TransportException("PayloadLimit", "The SSH reader exceeded its output limit.");
            }
            if (retain) result.Write(buffer, 0, read);
        }
        return retain ? Encoding.UTF8.GetString(result.ToArray()) : "";
    }
}
