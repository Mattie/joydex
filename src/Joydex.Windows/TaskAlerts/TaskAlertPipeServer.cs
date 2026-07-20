using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using Joydex.Core.TaskAlerts;

namespace Joydex.Windows.TaskAlerts;

public sealed class TaskAlertPipeServer(
    TaskAlertCoordinator coordinator,
    Action<string> log,
    string pipeName = TaskAlertPipeServer.DefaultPipeName) : IAsyncDisposable
{
    public const string DefaultPipeName = "Joydex.TaskAlerts.v1";
    private const int MaximumMessageBytes = 16 * 1024;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly HashSet<Task> _connections = [];
    private readonly object _connectionsLock = new();
    private Task? _acceptTask;

    public void Start()
    {
        _acceptTask ??= RunAcceptGuardedAsync(_cancellation.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        if (_acceptTask is not null)
        {
            try
            {
                await _acceptTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        Task[] connections;
        lock (_connectionsLock)
        {
            connections = [.. _connections];
        }

        await Task.WhenAll(connections).ConfigureAwait(false);
        _cancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.In,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                inBufferSize: 4096,
                outBufferSize: 0);
            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var connection = ReadOneAsync(server, cancellationToken);
                lock (_connectionsLock)
                {
                    _connections.Add(connection);
                }

                _ = connection.ContinueWith(
                    completed =>
                    {
                        lock (_connectionsLock)
                        {
                            _connections.Remove(completed);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                server = null!;
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    private async Task RunAcceptGuardedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await AcceptLoopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            log($"Task-alert pipe receiver stopped: {exception.Message}");
        }
    }

    private async Task ReadOneAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        await using (server.ConfigureAwait(false))
        {
            try
            {
                var buffer = new byte[MaximumMessageBytes + 1];
                var length = 0;
                int read;
                while ((read = await server
                           .ReadAsync(buffer.AsMemory(length), cancellationToken)
                           .ConfigureAwait(false)) > 0)
                {
                    length += read;
                    if (length > MaximumMessageBytes)
                    {
                        throw new InvalidDataException("Task-alert pipe message exceeded the size limit.");
                    }
                }

                var message = JsonSerializer.Deserialize<RelayMessage>(buffer.AsSpan(0, length));
                if (message is null
                    || string.IsNullOrWhiteSpace(message.SessionId)
                    || message.SessionId.Length > 512
                    || message.TurnId?.Length > 512
                    || message.AttentionKey?.Length > 128
                    || !Enum.TryParse<CodexLifecycleEvent>(message.Event, ignoreCase: false, out var lifecycleEvent))
                {
                    return;
                }

                if (lifecycleEvent is not (CodexLifecycleEvent.UserPromptSubmit
                    or CodexLifecycleEvent.PermissionRequest
                    or CodexLifecycleEvent.UserInputRequest
                    or CodexLifecycleEvent.ToolCompleted
                    or CodexLifecycleEvent.Stop))
                {
                    return;
                }

                var receivedAt = DateTimeOffset.FromUnixTimeMilliseconds(message.ReceivedAtUnixMs);
                if (Math.Abs((DateTimeOffset.UtcNow - receivedAt).TotalMinutes) > 5)
                {
                    return;
                }
                coordinator.TryPublish(new TaskAlertEvent(
                    lifecycleEvent,
                    message.SessionId,
                    message.TurnId,
                    receivedAt,
                    message.AttentionKey));
            }
            catch (Exception exception) when (exception is IOException
                or JsonException
                or InvalidDataException
                or ArgumentOutOfRangeException)
            {
                log($"Ignored invalid task-alert pipe message: {exception.Message}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private sealed record RelayMessage(
        [property: JsonPropertyName("event")] string Event,
        [property: JsonPropertyName("sessionId")] string SessionId,
        [property: JsonPropertyName("turnId")] string? TurnId,
        [property: JsonPropertyName("attentionKey")] string? AttentionKey,
        [property: JsonPropertyName("receivedAtUnixMs")] long ReceivedAtUnixMs);
}
