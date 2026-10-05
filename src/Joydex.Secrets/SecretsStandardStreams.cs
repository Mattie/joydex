using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Joydex.Secrets;

/// <summary>Byte streams owned by the requesting CLI, kept outside metadata and audit output.</summary>
public sealed class SecretsStandardStreams : IDisposable
{
    public Stream Input { get; }
    public Stream Output { get; }
    public Stream Error { get; }

    public SecretsStandardStreams(Stream input, Stream output, Stream error)
        => (Input, Output, Error) = (input, output, error);

    public static async Task<SecretsStandardStreams> ConnectAsync(string id, uint requesterPid, CancellationToken token)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid standard stream identifier.");
        var pipes = new List<NamedPipeClientStream>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            foreach (var suffix in new[] { "in", "out", "err" })
            {
                var pipe = new NamedPipeClientStream(".", "Joydex.Stdio." + id + "." + suffix,
                    suffix == "in" ? PipeDirection.In : PipeDirection.Out,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                pipes.Add(pipe);
                await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var owner) || owner != requesterPid)
                    throw new UnauthorizedAccessException("Standard streams belong to another process.");
            }
            return new(pipes[0], pipes[1], pipes[2]);
        }
        catch { foreach (var pipe in pipes) pipe.Dispose(); throw; }
    }

    public void Dispose() { Input.Dispose(); Output.Dispose(); Error.Dispose(); }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
