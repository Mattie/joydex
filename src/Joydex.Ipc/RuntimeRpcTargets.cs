using Joydex.Contracts;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace Joydex.Ipc;

internal sealed class RuntimeRpcServerTarget : IAsyncDisposable
{
    private readonly RuntimeIpcConnectionContext _context;
    private readonly RuntimeRpcServerFactory _factory;
    private readonly RuntimeIpcTicketStore _tickets;
    private readonly RemoteRuntimeRpcClient _client;
    private readonly Action<Exception?> _abortConnection;
    private readonly int _maximumConcurrentRequests;
    private readonly CancellationTokenSource _connectionLifetime;
    private readonly TaskCompletionSource _attached = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private IRuntimeRpcServer? _implementation;
    private int _attachState;
    private int _transferAccessState;
    private int _activeRequests;
    private int _activeTransferRequests;
    private int _disposed;

    public RuntimeRpcServerTarget(
        RuntimeIpcConnectionContext context,
        RuntimeRpcServerFactory factory,
        RuntimeIpcTicketStore tickets,
        RemoteRuntimeRpcClient client,
        Action<Exception?> abortConnection,
        int maximumConcurrentRequests,
        CancellationToken serverLifetime,
        CancellationToken rpcDisconnected)
    {
        _context = context;
        _factory = factory;
        _tickets = tickets;
        _client = client;
        _abortConnection = abortConnection;
        _maximumConcurrentRequests = maximumConcurrentRequests;
        _connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            serverLifetime,
            rpcDisconnected);
    }

    public Task Attached => _attached.Task;

    [JsonRpcMethod(RuntimeRpcMethods.Attach)]
    public async Task<object> AttachAsync(
        RuntimeAttachRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Interlocked.CompareExchange(ref _attachState, 1, 0) != 0)
        {
            throw new InvalidOperationException("This physical runtime connection has already attempted attach.");
        }

        try
        {
            ValidateAttachRequest(request);
            if (!_tickets.TryConsume(request.LaunchTicket, request.ClientKind, _context.Peer))
            {
                throw new RuntimeIpcAuthenticationException(
                    "The runtime launch ticket is invalid, expired, already used, or bound to another process.");
            }

            var authenticatedContext = _context with
            {
                AuthenticatedLaunchTicketFingerprint =
                    RuntimeBootstrapTicketFingerprint.Compute(request.LaunchTicket),
            };

            var negotiatedMinor = Math.Min(request.ProtocolMinor, RuntimeProtocol.MinorVersion);
            _client.SetNegotiatedMinor(negotiatedMinor);
            Volatile.Write(ref _transferAccessState, negotiatedMinor >= 2 ? 1 : 0);
            var implementation = await _factory(
                    authenticatedContext,
                    request.ClientKind,
                    _client,
                    _abortConnection,
                    _connectionLifetime.Token)
                .ConfigureAwait(false);
            _implementation = implementation
                ?? throw new InvalidOperationException("The runtime RPC factory returned no connection implementation.");
            var negotiatedRequest = request with { ProtocolMinor = negotiatedMinor };
            var result = await implementation
                .AttachAsync(negotiatedRequest, cancellationToken)
                .ConfigureAwait(false);
            ValidateAttachResult(result);
            result = result with
            {
                ConnectionId = _context.ConnectionId,
                ProtocolMajor = RuntimeProtocol.MajorVersion,
                ProtocolMinor = negotiatedMinor,
                MaximumMessageBytes = RuntimeProtocol.MaximumMessageBytes,
            };
            var protectedResult = ProtectSettingsPayload(result, negotiatedMinor);
            Volatile.Write(ref _attachState, 2);
            _attached.TrySetResult();
            return protectedResult;
        }
        catch
        {
            Volatile.Write(ref _transferAccessState, 0);
            Volatile.Write(ref _attachState, 3);
            throw;
        }
    }

    [JsonRpcMethod(RuntimeRpcMethods.GetSnapshot)]
    public Task<object> GetSnapshotAsync(CancellationToken cancellationToken) =>
        InvokeSettingsResultAsync(
            implementation => implementation.GetSnapshotAsync(cancellationToken),
            cancellationToken);

    [JsonRpcMethod(RuntimeRpcMethods.PrepareSettings)]
    public Task<object> PrepareSettingsAsync(
        PrepareSettingsRequest request,
        CancellationToken cancellationToken) =>
        InvokeSettingsResultAsync(
            implementation => implementation.PrepareSettingsAsync(request, cancellationToken),
            cancellationToken);

    [JsonRpcMethod(SettingsTransferMethods.PrepareTransferred)]
    public Task<object> PrepareSettingsTransferredAsync(
        SettingsTransferReference reference,
        CancellationToken cancellationToken) =>
        InvokeSettingsResultAsync(
            async implementation =>
            {
                RequireSettingsTransfer();
                var request = await _client.ReceiveClientTransferAsync<PrepareSettingsRequest>(
                        reference,
                        cancellationToken)
                    .ConfigureAwait(false);
                return await implementation.PrepareSettingsAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);

    [JsonRpcMethod(RuntimeRpcMethods.ApplySettings)]
    public Task<object> ApplySettingsAsync(
        ApplySettingsRequest request,
        CancellationToken cancellationToken) =>
        InvokeSettingsResultAsync(
            implementation => implementation.ApplySettingsAsync(request, cancellationToken),
            cancellationToken);

    [JsonRpcMethod(RuntimeRpcMethods.GetSettingsOperation)]
    public Task<object> GetSettingsOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken) =>
        InvokeSettingsResultAsync(
            implementation => implementation.GetSettingsOperationAsync(operationId, cancellationToken),
            cancellationToken);

    [JsonRpcMethod(RuntimeRpcMethods.RefreshInputSources)]
    public Task<RuntimeInputSnapshot> RefreshInputSourcesAsync(CancellationToken cancellationToken) =>
        InvokeBoundedAsync(
            implementation => implementation.RefreshInputSourcesAsync(cancellationToken),
            cancellationToken);

    [JsonRpcMethod(RuntimeRpcMethods.BeginInputCapture)]
    public Task<RuntimeCaptureStartResult> BeginInputCaptureAsync(
        RuntimeCaptureRequest request,
        CancellationToken cancellationToken) =>
        InvokeBoundedAsync(
            implementation => implementation.BeginInputCaptureAsync(request, cancellationToken),
            cancellationToken);

    [JsonRpcMethod(RuntimeRpcMethods.RenewInputCapture)]
    public Task<RuntimeCaptureCommandResult> RenewInputCaptureAsync(
        Guid captureId,
        CancellationToken cancellationToken) =>
        InvokeBoundedAsync(
            implementation => implementation.RenewInputCaptureAsync(captureId, cancellationToken),
            cancellationToken);

    [JsonRpcMethod(RuntimeRpcMethods.CancelInputCapture)]
    public Task<RuntimeCaptureCommandResult> CancelInputCaptureAsync(
        Guid captureId,
        CancellationToken cancellationToken) =>
        InvokeBoundedAsync(
            implementation => implementation.CancelInputCaptureAsync(captureId, cancellationToken),
            cancellationToken);

    [JsonRpcMethod(RuntimeRpcMethods.GetInputCapture)]
    public Task<RuntimeCaptureLookupResult> GetInputCaptureAsync(
        Guid captureId,
        CancellationToken cancellationToken) =>
        InvokeBoundedAsync(
            implementation => implementation.GetInputCaptureAsync(captureId, cancellationToken),
            cancellationToken);

    [JsonRpcMethod(RuntimeRpcMethods.ExecuteCommand)]
    public Task<object> ExecuteCommandAsync(
        RuntimeCommandRequest request,
        CancellationToken cancellationToken) =>
        InvokeSettingsResultAsync(
            implementation => implementation.ExecuteCommandAsync(request, cancellationToken),
            cancellationToken);

    [JsonRpcMethod(RuntimeRpcMethods.GetCommandOperation)]
    public Task<object> GetCommandOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken) =>
        InvokeSettingsResultAsync(
            implementation => implementation.GetCommandOperationAsync(operationId, cancellationToken),
            cancellationToken);

    [JsonRpcMethod(SettingsTransferMethods.ReadServer)]
    public Task<SettingsTransferChunk> ReadServerTransferAsync(
        string transferId,
        long offset,
        CancellationToken cancellationToken) =>
        InvokeTransferBoundedAsync(
            () =>
            {
                RequireSettingsTransfer();
                return Task.FromResult(_client.OutboundTransfers.Read(transferId, offset));
            },
            cancellationToken);

    [JsonRpcMethod(SettingsTransferMethods.DiscardServer)]
    public async Task DiscardServerTransferAsync(
        string transferId,
        CancellationToken cancellationToken)
    {
        await InvokeTransferBoundedAsync(
            () =>
            {
                RequireSettingsTransfer();
                _client.OutboundTransfers.Discard(transferId);
                return Task.FromResult(true);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await _connectionLifetime.CancelAsync().ConfigureAwait(false);
        _client.DisposeTransfers();
        _connectionLifetime.Dispose();
        switch (_implementation)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    private void ValidateAttachRequest(RuntimeAttachRequest request)
    {
        if (request.ProtocolMajor != RuntimeProtocol.MajorVersion
            || request.ProtocolMinor < 0)
        {
            throw new InvalidOperationException(
                $"Runtime protocol {request.ProtocolMajor}.{request.ProtocolMinor} is not supported.");
        }
        if (!Enum.IsDefined(request.ClientKind))
        {
            throw new InvalidOperationException("The runtime client kind is invalid.");
        }
        if (request.ClientKind == RuntimeClientKind.HeadlessTest
            && _context.Endpoint.InstanceKind == RuntimeInstanceKind.Production)
        {
            throw new RuntimeIpcAuthenticationException(
                "Headless test clients cannot attach to the production runtime.");
        }
        if (request.InstanceKind != _context.Endpoint.InstanceKind)
        {
            throw new RuntimeIpcAuthenticationException(
                "The runtime client selected a different instance kind from the endpoint.");
        }
        if (!string.Equals(request.DataRootId, _context.Endpoint.DataRootId, StringComparison.Ordinal))
        {
            throw new RuntimeIpcAuthenticationException(
                "The runtime client selected a different data root from the endpoint.");
        }
        if ((request.PreviousEngineEpoch is null) != (request.AfterEventSequence is null)
            || request.PreviousEngineEpoch == Guid.Empty
            || request.AfterEventSequence < 0)
        {
            throw new InvalidOperationException("The runtime event recovery cursor is invalid.");
        }
    }

    private void ValidateAttachResult(RuntimeAttachResult result)
    {
        if (result is null)
        {
            throw new InvalidOperationException("The runtime attach implementation returned no result.");
        }
        if (result.Capabilities is null
            || result.Capabilities.Any(string.IsNullOrWhiteSpace)
            || result.Capabilities.Distinct(StringComparer.Ordinal).Count() != result.Capabilities.Length
            || result.Capabilities.Except(RuntimeProtocol.Capabilities, StringComparer.Ordinal).Any())
        {
            throw new InvalidOperationException("The runtime attach implementation returned invalid capabilities.");
        }
        if (result.Snapshot is null
            || result.Snapshot.Identity is null
            || result.Snapshot.Input is null
            || result.Snapshot.Settings is null)
        {
            throw new InvalidOperationException("The runtime attach implementation returned an incomplete snapshot.");
        }
        if (result.Snapshot.EngineEpoch == Guid.Empty
            || result.Snapshot.EventCursor < 0
            || result.Snapshot.InputEventCursor < 0)
        {
            throw new InvalidOperationException("The runtime attach implementation returned an invalid event cursor.");
        }
        if (result.Snapshot.Identity.InstanceKind != _context.Endpoint.InstanceKind
            || !string.Equals(
                result.Snapshot.Identity.DataRootId,
                _context.Endpoint.DataRootId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The runtime attach implementation returned identity for a different endpoint.");
        }
    }

    private IRuntimeRpcServer GetImplementation()
    {
        if (Volatile.Read(ref _attachState) != 2 || _implementation is null)
        {
            throw new InvalidOperationException(
                $"{RuntimeRpcMethods.Attach} must complete before this runtime method can be called.");
        }
        return _implementation;
    }

    private async Task<T> InvokeBoundedAsync<T>(
        Func<IRuntimeRpcServer, Task<T>> invoke,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var implementation = GetImplementation();
        if (Interlocked.Increment(ref _activeRequests) > _maximumConcurrentRequests)
        {
            Interlocked.Decrement(ref _activeRequests);
            throw new LocalRpcException(
                "The runtime IPC connection is at its concurrent request limit.")
            {
                ErrorCode = RuntimeIpcErrorCodes.RequestOverloaded,
            };
        }

        try
        {
            return await invoke(implementation).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeRequests);
        }
    }

    private async Task<T> InvokeTransferBoundedAsync<T>(
        Func<Task<T>> invoke,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _transferAccessState) != 1)
        {
            throw new InvalidOperationException(
                "Settings transfer access is unavailable before an authenticated minor-2 attach.");
        }
        var transferLimit = Math.Min(
            _maximumConcurrentRequests,
            SettingsTransferProtocol.MaximumConcurrentTransfers);
        if (Interlocked.Increment(ref _activeTransferRequests) > transferLimit)
        {
            Interlocked.Decrement(ref _activeTransferRequests);
            throw new LocalRpcException(
                "The runtime IPC connection is at its concurrent request limit.")
            {
                ErrorCode = RuntimeIpcErrorCodes.RequestOverloaded,
            };
        }

        try
        {
            return await invoke().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeTransferRequests);
        }
    }

    private async Task<object> InvokeSettingsResultAsync<T>(
        Func<IRuntimeRpcServer, Task<T>> invoke,
        CancellationToken cancellationToken)
        where T : notnull
    {
        var result = await InvokeBoundedAsync(invoke, cancellationToken).ConfigureAwait(false);
        return ProtectSettingsPayload(result, _client.NegotiatedMinor);
    }

    private object ProtectSettingsPayload(object result, int negotiatedMinor)
    {
        var payload = SettingsTransferSerializer.Serialize(result);
        if (SettingsTransferProtocol.FitsInline(payload.Length))
        {
            payload.Dispose();
            return result;
        }
        if (negotiatedMinor < 2)
        {
            payload.Dispose();
            throw new LocalRpcException(
                $"The runtime IPC response is too large for protocol minor {negotiatedMinor}; upgrade to minor 2 for settings transfer support.")
            {
                ErrorCode = RuntimeIpcErrorCodes.MessageTooLarge,
            };
        }
        return _client.OutboundTransfers.Add(payload);
    }

    private void RequireSettingsTransfer()
    {
        if (_client.NegotiatedMinor < 2 || Volatile.Read(ref _transferAccessState) != 1)
        {
            throw new InvalidOperationException(
                "The settings transfer methods require negotiated protocol minor 2.");
        }
    }
}

internal sealed class RemoteRuntimeRpcClient(JsonRpc rpc) : IRuntimeRpcClient
{
    private int _negotiatedMinor;
    private readonly SettingsTransferReceiver _inboundTransfers = new(
        rpc,
        SettingsTransferMethods.ReadClient,
        SettingsTransferMethods.DiscardClient);

    internal SettingsTransferStore OutboundTransfers { get; } = new();

    internal int NegotiatedMinor => Volatile.Read(ref _negotiatedMinor);

    internal void SetNegotiatedMinor(int minor) => Volatile.Write(ref _negotiatedMinor, minor);

    internal Task<T> ReceiveClientTransferAsync<T>(
        SettingsTransferReference reference,
        CancellationToken cancellationToken) =>
        _inboundTransfers.ReceiveAsync<T>(reference, cancellationToken);

    internal void DisposeTransfers()
    {
        OutboundTransfers.Dispose();
        _inboundTransfers.Dispose();
    }

    public Task RuntimeEventAsync(
        RuntimeEvent runtimeEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        return InvokeSettingsCallbackAsync(RuntimeRpcMethods.Event, runtimeEvent, cancellationToken);
    }

    public Task RuntimeInputEventAsync(
        RuntimeConnectionInputEvent inputEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputEvent);
        return rpc.InvokeWithCancellationAsync(
            RuntimeRpcMethods.InputEvent,
            [inputEvent],
            cancellationToken);
    }

    public Task RuntimeCommandCompletedAsync(
        RuntimeCommandResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        return InvokeSettingsCallbackAsync(RuntimeRpcMethods.CommandCompleted, result, cancellationToken);
    }

    private async Task InvokeSettingsCallbackAsync(
        string method,
        object value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = SettingsTransferSerializer.Serialize(value);
        if (SettingsTransferProtocol.FitsInline(payload.Length))
        {
            payload.Dispose();
            await rpc.InvokeWithCancellationAsync(method, [value], cancellationToken).ConfigureAwait(false);
            return;
        }
        if (NegotiatedMinor < 2)
        {
            payload.Dispose();
            throw new InvalidOperationException(
                $"The runtime IPC callback is too large for protocol minor {NegotiatedMinor}; upgrade to minor 2 for settings transfer support.");
        }
        var reference = OutboundTransfers.Add(payload);
        try
        {
            await rpc.InvokeWithCancellationAsync(method, [reference], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            OutboundTransfers.Discard(reference.TransferId);
        }
    }
}

internal sealed class LocalRuntimeRpcClient(
    IRuntimeRpcClient inner,
    SettingsTransferStore outboundTransfers,
    SettingsTransferReceiver inboundTransfers)
{
    [JsonRpcMethod(RuntimeRpcMethods.Event)]
    public async Task RuntimeEventAsync(JToken value, CancellationToken cancellationToken)
    {
        var runtimeEvent = await HydrateAsync<RuntimeEvent>(value, cancellationToken).ConfigureAwait(false);
        await inner.RuntimeEventAsync(runtimeEvent, cancellationToken).ConfigureAwait(false);
    }

    [JsonRpcMethod(RuntimeRpcMethods.InputEvent)]
    public Task RuntimeInputEventAsync(
        RuntimeConnectionInputEvent inputEvent,
        CancellationToken cancellationToken) =>
        inner.RuntimeInputEventAsync(inputEvent, cancellationToken);

    [JsonRpcMethod(RuntimeRpcMethods.CommandCompleted)]
    public async Task RuntimeCommandCompletedAsync(JToken value, CancellationToken cancellationToken)
    {
        var result = await HydrateAsync<RuntimeCommandResult>(value, cancellationToken).ConfigureAwait(false);
        await inner.RuntimeCommandCompletedAsync(result, cancellationToken).ConfigureAwait(false);
    }

    [JsonRpcMethod(SettingsTransferMethods.ReadClient)]
    public SettingsTransferChunk ReadClientTransfer(string transferId, long offset) =>
        outboundTransfers.Read(transferId, offset);

    [JsonRpcMethod(SettingsTransferMethods.DiscardClient)]
    public void DiscardClientTransfer(string transferId) => outboundTransfers.Discard(transferId);

    private Task<T> HydrateAsync<T>(JToken value, CancellationToken cancellationToken)
    {
        if (!SettingsTransferSerializer.TryGetReference(value, out var reference))
        {
            return Task.FromResult(SettingsTransferSerializer.DeserializeInline<T>(value));
        }
        return inboundTransfers.ReceiveAsync<T>(reference, cancellationToken);
    }
}
