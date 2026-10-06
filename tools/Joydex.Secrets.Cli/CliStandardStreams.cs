using System.IO.Pipes;

namespace Joydex.Secrets.Cli;

/// <summary>Connects the broker's child streams to this CLI's standard streams without text conversion.</summary>
internal sealed class CliStandardStreams : IDisposable
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    private readonly NamedPipeServerStream _input;
    private readonly NamedPipeServerStream _output;
    private readonly NamedPipeServerStream _error;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _outputTask;
    private readonly Task _errorTask;

    public CliStandardStreams()
    {
        NamedPipeServerStream Create(string suffix, PipeDirection direction) => new(
            "Joydex.Stdio." + Id + "." + suffix, direction, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _input = Create("in", PipeDirection.Out);
        _output = Create("out", PipeDirection.In);
        _error = Create("err", PipeDirection.In);
        _ = FeedInputAsync();
        _outputTask = DrainAsync(_output, Console.OpenStandardOutput());
        _errorTask = DrainAsync(_error, Console.OpenStandardError());
    }

    public Task DrainAsync() => Task.WhenAll(_outputTask, _errorTask);

    private async Task DrainAsync(NamedPipeServerStream pipe, Stream destination)
    {
        try
        {
            await pipe.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
            await pipe.CopyToAsync(destination, _lifetime.Token).ConfigureAwait(false);
            await destination.FlushAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch
        {
            // A closed downstream consumer must wake the broker rather than block its child.
            pipe.Dispose();
            throw;
        }
    }

    private async Task FeedInputAsync()
    {
        try
        {
            await _input.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
            await Console.OpenStandardInput().CopyToAsync(_input, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { _input.Dispose(); }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _input.Dispose(); _output.Dispose(); _error.Dispose();
        // Observe failures when execution was rejected before connecting its streams.
        _ = _outputTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        _ = _errorTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        _lifetime.Dispose();
    }
}
