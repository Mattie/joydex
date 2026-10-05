using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Joydex.Ipc;

namespace Joydex.VoiceWorker;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!args.Contains("--voice-worker", StringComparer.Ordinal))
        {
            return 2;
        }

        VoiceWorkerLaunchTicket ticket;
        try
        {
            var line = await Console.In.ReadLineAsync().ConfigureAwait(false);
            ticket = JsonSerializer.Deserialize<VoiceWorkerLaunchTicket>(line ?? string.Empty)
                ?? throw new InvalidDataException("The Voice worker launch ticket is missing.");
            ValidateTicket(ticket);
        }
        catch
        {
            return 3;
        }

        using var pipe = new NamedPipeClientStream(
            ".",
            ticket.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
            _ = WindowsPipePeerVerifier.VerifyServer(
                pipe,
                ticket.ExpectedSessionId,
                ticket.ExpectedHostProcessId,
                ticket.ExpectedHostStartTimeUtcTicks);
        }
        catch
        {
            return 4;
        }

        var (rpc, boundedStream) = RuntimeJsonRpc.Create(pipe);
        await using var service = new VoiceWorkerService(ticket, rpc);
        Task winner;
        using (rpc)
        using (boundedStream)
        {
            rpc.AddLocalRpcTarget(service);
            rpc.StartListening();
            winner = await Task.WhenAny(rpc.Completion, service.Completion).ConfigureAwait(false);
        }

        Exception? failure = null;
        try
        {
            await winner.ConfigureAwait(false);
            if (ReferenceEquals(winner, rpc.Completion)
                && !service.Completion.IsCompleted
                && !service.StopRequested)
            {
                failure = new IOException("The Voice worker lost its host connection.");
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        try
        {
            await service.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            return 5;
        }
        return failure is null ? 0 : 6;
    }

    private static void ValidateTicket(VoiceWorkerLaunchTicket ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket.PipeName)
            || string.IsNullOrWhiteSpace(ticket.Capability)
            || ticket.Generation <= 0
            || ticket.ProtocolMajor != VoiceWorkerProtocol.MajorVersion
            || ticket.ProtocolMinor < 0
            || ticket.ExpectedHostProcessId <= 0
            || ticket.ExpectedHostStartTimeUtcTicks <= 0
            || ticket.ExpectedSessionId < 0)
        {
            throw new InvalidDataException("The Voice worker launch ticket is invalid.");
        }
    }
}
