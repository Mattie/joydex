using System.Text.Json;
using Joydex.Contracts;

namespace Joydex.RuntimeHost;

internal interface IRuntimeCommandHandler
{
    Task<RuntimeCommandResult> ExecuteAsync(
        RuntimeCommandRequest request,
        CancellationToken runtimeCancellationToken);
}

internal interface IRuntimeCommandRouter
{
    Task<RuntimeCommandResult> ExecuteAsync(
        string connectionId,
        RuntimeCommandRequest request,
        CancellationToken runtimeCancellationToken);
}

internal sealed class UnsupportedRuntimeCommandHandler : IRuntimeCommandHandler
{
    public Task<RuntimeCommandResult> ExecuteAsync(
        RuntimeCommandRequest request,
        CancellationToken runtimeCancellationToken)
    {
        runtimeCancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new RuntimeCommandResult(
            request.OperationId,
            request.Kind,
            RuntimeCommandStatus.Rejected,
            $"The {request.Kind} command is unavailable in this runtime composition."));
    }
}

internal sealed class SyntheticRuntimeCommandHandler : IRuntimeCommandHandler
{
    public Task<RuntimeCommandResult> ExecuteAsync(
        RuntimeCommandRequest request,
        CancellationToken runtimeCancellationToken)
    {
        runtimeCancellationToken.ThrowIfCancellationRequested();
        var result = request.Kind switch
        {
            RuntimeCommandKind.InspectDesktopBridge => new RuntimeCommandResult(
                request.OperationId,
                request.Kind,
                RuntimeCommandStatus.Completed,
                Payload: new RuntimeCommandPayload(
                    DesktopBridge: new RuntimeDesktopBridgeStatus(
                        RuntimeDesktopBridgeState.NotInstalled,
                        "Synthetic runtime."))),
            RuntimeCommandKind.ListVoiceProjects => new RuntimeCommandResult(
                request.OperationId,
                request.Kind,
                RuntimeCommandStatus.Completed,
                Payload: new RuntimeCommandPayload(VoiceProjects: [])),
            RuntimeCommandKind.ListDesktopTasks => new RuntimeCommandResult(
                request.OperationId,
                request.Kind,
                RuntimeCommandStatus.Completed,
                Payload: new RuntimeCommandPayload(DesktopTasks: [])),
            _ => new RuntimeCommandResult(
                request.OperationId,
                request.Kind,
                RuntimeCommandStatus.Rejected,
                $"The {request.Kind} command has no synthetic side effect."),
        };
        return Task.FromResult(result);
    }
}

/// <summary>
/// Keeps accepted commands running when one caller stops waiting and binds operation IDs to exact
/// request payloads. Full completed results are bounded; evicted non-sensitive operations retain
/// only their ID and kind for the engine generation so recovery can terminate without replaying
/// an action. Secret-bearing or full-message reads stay live-call scoped.
/// </summary>
internal sealed class RuntimeCommandExecutor(
    IRuntimeCommandRouter router,
    CancellationToken runtimeCancellationToken)
{
    internal const int MaximumRunningOperations = 16;
    private const int MaximumCompletedOperations = 128;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly IRuntimeCommandRouter _router = router ?? throw new ArgumentNullException(nameof(router));
    private readonly CancellationToken _runtimeCancellationToken = runtimeCancellationToken;
    private readonly Dictionary<Guid, CommandOperation> _operations = [];
    private readonly Dictionary<Guid, RuntimeCommandKind> _evictedOperations = [];
    private readonly Queue<Guid> _completedOrder = new();
    private int _runningOperations;

    public Task<RuntimeCommandResult> ExecuteAsync(
        string connectionId,
        RuntimeCommandRequest request,
        CancellationToken callerCancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(request);
        if (request.OperationId == Guid.Empty)
        {
            return Task.FromResult(new RuntimeCommandResult(
                request.OperationId,
                request.Kind,
                RuntimeCommandStatus.Rejected,
                "A non-empty operation ID is required."));
        }

        if (!RuntimeCommandCanonicalizer.TryNormalize(request, out request, out var validationError))
        {
            return Task.FromResult(new RuntimeCommandResult(
                request.OperationId,
                request.Kind,
                RuntimeCommandStatus.Rejected,
                validationError));
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        Task<RuntimeCommandResult> operationTask;
        lock (_gate)
        {
            if (_evictedOperations.ContainsKey(request.OperationId))
            {
                return Task.FromResult(new RuntimeCommandResult(
                    request.OperationId,
                    request.Kind,
                    RuntimeCommandStatus.Rejected,
                    "This operation already finished, but its result expired. Check its effect before submitting a new action."));
            }
            if (_operations.TryGetValue(request.OperationId, out var existing))
            {
                if (!payload.AsSpan().SequenceEqual(existing.RequestPayload)
                    || (IsSensitive(request.Kind)
                        && !string.Equals(
                            existing.ConnectionId,
                            connectionId,
                            StringComparison.Ordinal)))
                {
                    return Task.FromResult(new RuntimeCommandResult(
                        request.OperationId,
                        request.Kind,
                        RuntimeCommandStatus.Rejected,
                        "The operation ID is already bound to another connection or command payload."));
                }
                operationTask = existing.Task;
            }
            else
            {
                if (_runningOperations >= MaximumRunningOperations)
                {
                    return Task.FromResult(new RuntimeCommandResult(
                        request.OperationId,
                        request.Kind,
                        RuntimeCommandStatus.Rejected,
                        "The runtime is already processing the maximum number of background commands."));
                }

                _runningOperations++;
                operationTask = ExecuteCoreAsync(connectionId, request);
                _operations.Add(
                    request.OperationId,
                    new CommandOperation(
                        connectionId,
                        payload,
                        operationTask,
                        request.Kind,
                        IsSensitive(request.Kind)));
            }
        }

        return operationTask.WaitAsync(callerCancellationToken);
    }

    public RuntimeCommandOperationResult GetOperation(string connectionId, Guid operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        lock (_gate)
        {
            if (_evictedOperations.TryGetValue(operationId, out var evictedKind))
            {
                return new RuntimeCommandOperationResult(
                    operationId,
                    RuntimeCommandOperationState.Completed,
                    new RuntimeCommandResult(operationId, evictedKind, RuntimeCommandStatus.Failed,
                        "This operation finished, but its result expired. Check its effect before choosing the action again."));
            }
            if (!_operations.TryGetValue(operationId, out var operation)
                || (operation.Sensitive
                    && !string.Equals(operation.ConnectionId, connectionId, StringComparison.Ordinal)))
            {
                return new RuntimeCommandOperationResult(
                    operationId,
                    RuntimeCommandOperationState.NotFound);
            }
            return operation.Task.IsCompletedSuccessfully
                ? new RuntimeCommandOperationResult(
                    operationId,
                    RuntimeCommandOperationState.Completed,
                    operation.Task.Result)
                : new RuntimeCommandOperationResult(
                    operationId,
                    RuntimeCommandOperationState.Running);
        }
    }

    public void Disconnect(string connectionId)
    {
        lock (_gate)
        {
            foreach (var operationId in _operations
                         .Where(item => string.Equals(
                             item.Value.ConnectionId,
                             connectionId,
                             StringComparison.Ordinal)
                                        && item.Value.Sensitive)
                         .Select(item => item.Key)
                         .ToArray())
            {
                _operations.Remove(operationId);
            }
        }
    }

    private async Task<RuntimeCommandResult> ExecuteCoreAsync(
        string connectionId,
        RuntimeCommandRequest request)
    {
        RuntimeCommandResult result;
        try
        {
            result = await _router
                .ExecuteAsync(connectionId, request, _runtimeCancellationToken)
                .ConfigureAwait(false);
            if (result.OperationId != request.OperationId || result.Kind != request.Kind)
            {
                result = new RuntimeCommandResult(
                    request.OperationId,
                    request.Kind,
                    RuntimeCommandStatus.Failed,
                    "The runtime command handler returned a mismatched operation identity.");
            }
        }
        catch (OperationCanceledException) when (_runtimeCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            result = new RuntimeCommandResult(
                request.OperationId,
                request.Kind,
                RuntimeCommandStatus.Failed,
                exception.Message);
        }
        finally
        {
            lock (_gate)
            {
                _runningOperations--;
            }
        }

        lock (_gate)
        {
            _completedOrder.Enqueue(request.OperationId);
            while (_completedOrder.Count > MaximumCompletedOperations)
            {
                var stale = _completedOrder.Dequeue();
                if (_operations.TryGetValue(stale, out var operation)
                    && operation.Task.IsCompleted)
                {
                    if (!operation.Sensitive)
                    {
                        _evictedOperations[stale] = operation.Kind;
                    }
                    _operations.Remove(stale);
                }
            }
        }
        return result;
    }

    private static bool IsSensitive(RuntimeCommandKind kind) =>
        kind is RuntimeCommandKind.ReadPebbleIndexAccess
            or RuntimeCommandKind.ReadVoiceConversationPage
            or RuntimeCommandKind.ReadVoiceOutboxDelivery;

    private sealed record CommandOperation(
        string ConnectionId,
        byte[] RequestPayload,
        Task<RuntimeCommandResult> Task,
        RuntimeCommandKind Kind,
        bool Sensitive);
}
