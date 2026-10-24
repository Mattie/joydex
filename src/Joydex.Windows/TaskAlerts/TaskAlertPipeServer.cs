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
    private const int MaximumMessageBytes = 256 * 1024;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly HashSet<Task> _connections = [];
    private readonly object _connectionsLock = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _acceptTask;

    /// <summary>Completes on shutdown or faults if the receiver cannot continue.</summary>
    public Task Completion => _completion.Task;

    internal Func<NamedPipeServerStream, CancellationToken, Task> WaitForConnectionAsync { get; init; }
        = static (server, cancellationToken) => server.WaitForConnectionAsync(cancellationToken);

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

        try { await Task.WhenAll(connections).ConfigureAwait(false); }
        finally
        {
            _cancellation.Dispose();
            _completion.TrySetResult();
            GC.SuppressFinalize(this);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        var recovering = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    inBufferSize: 4096,
                    outBufferSize: 0);
                await WaitForConnectionAsync(server, cancellationToken).ConfigureAwait(false);
                if (recovering)
                {
                    WriteLog("Task-alert pipe receiver resumed accepting connections.");
                    recovering = false;
                }
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
            catch (IOException exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A disconnected client can interrupt an accept. Replace that pipe,
                // rather than permanently disabling all later lifecycle hooks.
                if (!recovering)
                {
                    WriteLog($"Task-alert pipe receiver interrupted; retrying: {exception.Message}");
                }
                recovering = true;
            }
            finally
            {
                server?.Dispose();
            }
            if (recovering)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
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
        catch (IOException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            WriteLog($"Task-alert pipe receiver stopped: {exception.Message}");
            _completion.TrySetException(exception);
            return;
        }
        _completion.TrySetResult();
    }

    private void WriteLog(string message)
    {
        try { log(message); }
        catch { /* A failed diagnostic sink must not stop receiver recovery or cleanup. */ }
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

                using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
                var root = document.RootElement;
                var messages = root.ValueKind == JsonValueKind.Array
                    ? root.EnumerateArray().Select(item => item.Deserialize<RelayMessage>()).ToArray()
                    : [root.Deserialize<RelayMessage>()];
                if (messages.Length is < 1 or > 33) return;
                var events = new List<TaskAlertEvent>(messages.Length);
                foreach (var message in messages)
                {
                    if (message is null
                        || string.IsNullOrWhiteSpace(message.SessionId)
                        || message.SessionId.Length > 512
                        || message.TurnId?.Length > 512
                        || message.AttentionKey?.Length > 128
                        || message.Workspace?.Length > TaskAlertSuppression.MaximumWorkspaceLength
                        || !Enum.TryParse<CodexLifecycleEvent>(message.Event, ignoreCase: false, out var lifecycleEvent))
                    {
                        return;
                    }

                    if (lifecycleEvent is not (CodexLifecycleEvent.UserPromptSubmit
                        or CodexLifecycleEvent.PermissionRequest
                        or CodexLifecycleEvent.UserInputRequest
                        or CodexLifecycleEvent.AsyncUserInputRequest
                        or CodexLifecycleEvent.AsyncUserInputAnswered
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
                    events.Add(new TaskAlertEvent(
                        lifecycleEvent,
                        message.SessionId,
                        message.TurnId,
                        receivedAt,
                        message.AttentionKey,
                        message.Workspace));
                }

                // Validate the whole packet before publishing it in its original order.
                foreach (var taskEvent in events) coordinator.TryPublish(taskEvent);
            }
            catch (Exception exception) when (exception is IOException
                or JsonException
                or InvalidDataException
                or ArgumentOutOfRangeException)
            {
                WriteLog($"Ignored invalid task-alert pipe message: {exception.Message}");
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
        [property: JsonPropertyName("workspace")] string? Workspace,
        [property: JsonPropertyName("receivedAtUnixMs")] long ReceivedAtUnixMs);
}
