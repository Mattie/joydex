using System.IO.Pipes;
using Joydex.Contracts;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace Joydex.Ipc;

/// <summary>Connects one local client to an existing Joydex runtime endpoint.</summary>
public static class RuntimeIpcClient
{
    public static async Task<RuntimeIpcClientConnection> ConnectAsync(
        RuntimeIpcEndpoint endpoint,
        RuntimeAttachRequest request,
        IRuntimeRpcClient callbacks,
        RuntimeIpcClientOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(callbacks);
        options ??= new RuntimeIpcClientOptions();
        options.Validate();
        ValidateRequest(endpoint, request);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.ConnectTimeout);
        var pipe = new NamedPipeClientStream(
            ".",
            endpoint.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        RuntimeIpcClientConnection? connection = null;
        try
        {
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            _ = WindowsPipePeerVerifier.VerifyServer(pipe, endpoint.SessionId);
            var (rpc, boundedStream) = RuntimeJsonRpc.Create(pipe);
            connection = new RuntimeIpcClientConnection(rpc, boundedStream, callbacks);
            await connection.AttachCoreAsync(request, deadline.Token).ConfigureAwait(false);
            return connection;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
            throw new TimeoutException(
                $"The runtime IPC connection did not attach within {options.ConnectTimeout.TotalSeconds:0.###} seconds.");
        }
        catch
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    private static void ValidateRequest(
        RuntimeIpcEndpoint endpoint,
        RuntimeAttachRequest request)
    {
        if (request.InstanceKind != endpoint.InstanceKind
            || !string.Equals(request.DataRootId, endpoint.DataRootId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The runtime attach request does not match the endpoint.", nameof(request));
        }
        if (string.IsNullOrWhiteSpace(request.LaunchTicket))
        {
            throw new ArgumentException("A runtime launch ticket is required.", nameof(request));
        }
    }
}

/// <summary>An attached duplex runtime connection.</summary>
public sealed class RuntimeIpcClientConnection : IRuntimeRpcServer, IAsyncDisposable
{
    private readonly JsonRpc _rpc;
    private readonly BoundedMessageStream _stream;
    private readonly LocalRuntimeRpcClient _callbacks;
    private readonly SettingsTransferStore _outboundTransfers = new();
    private readonly SettingsTransferReceiver _inboundTransfers;
    private int _disposed;

    internal RuntimeIpcClientConnection(
        JsonRpc rpc,
        BoundedMessageStream stream,
        IRuntimeRpcClient callbacks)
    {
        _rpc = rpc;
        _stream = stream;
        _inboundTransfers = new SettingsTransferReceiver(
            _rpc,
            SettingsTransferMethods.ReadServer,
            SettingsTransferMethods.DiscardServer);
        _callbacks = new LocalRuntimeRpcClient(callbacks, _outboundTransfers, _inboundTransfers);
        _rpc.AddLocalRpcTarget(_callbacks);
        _rpc.StartListening();
    }

    public RuntimeAttachResult AttachResult { get; private set; } = null!;

    public Task Completion => _rpc.Completion;

    Task<RuntimeAttachResult> IRuntimeRpcServer.AttachAsync(
        RuntimeAttachRequest request,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The runtime IPC client connection is already attached.");

    public Task<RuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        InvokeSettingsResultAsync<RuntimeSnapshot>(RuntimeRpcMethods.GetSnapshot, [], cancellationToken);

    public async Task<PrepareSettingsResult> PrepareSettingsAsync(
        PrepareSettingsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var payload = SettingsTransferSerializer.Serialize(request);
        if (SettingsTransferProtocol.FitsInline(payload.Length))
        {
            payload.Dispose();
            return await InvokeSettingsResultAsync<PrepareSettingsResult>(
                    RuntimeRpcMethods.PrepareSettings,
                    [request],
                    cancellationToken)
                .ConfigureAwait(false);
        }
        if (AttachResult.ProtocolMinor < 2)
        {
            payload.Dispose();
            throw new InvalidOperationException(
                $"The settings prepare request is too large for protocol minor {AttachResult.ProtocolMinor}; upgrade to minor 2 for settings transfer support.");
        }

        var reference = _outboundTransfers.Add(payload);
        try
        {
            return await InvokeSettingsResultAsync<PrepareSettingsResult>(
                    SettingsTransferMethods.PrepareTransferred,
                    [reference],
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            _outboundTransfers.Discard(reference.TransferId);
            throw;
        }
    }

    public Task<ApplySettingsResult> ApplySettingsAsync(
        ApplySettingsRequest request,
        CancellationToken cancellationToken) =>
        InvokeSettingsResultAsync<ApplySettingsResult>(
            RuntimeRpcMethods.ApplySettings,
            [request],
            cancellationToken);

    public Task<SettingsOperationResult> GetSettingsOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken) =>
        InvokeSettingsResultAsync<SettingsOperationResult>(
            RuntimeRpcMethods.GetSettingsOperation,
            [operationId],
            cancellationToken);

    public Task<RuntimeInputSnapshot> RefreshInputSourcesAsync(CancellationToken cancellationToken) =>
        InvokeAsync<RuntimeInputSnapshot>(RuntimeRpcMethods.RefreshInputSources, [], cancellationToken);

    public Task<RuntimeCaptureStartResult> BeginInputCaptureAsync(
        RuntimeCaptureRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync<RuntimeCaptureStartResult>(
            RuntimeRpcMethods.BeginInputCapture,
            [request],
            cancellationToken);

    public Task<RuntimeCaptureCommandResult> RenewInputCaptureAsync(
        Guid captureId,
        CancellationToken cancellationToken) =>
        InvokeAsync<RuntimeCaptureCommandResult>(
            RuntimeRpcMethods.RenewInputCapture,
            [captureId],
            cancellationToken);

    public Task<RuntimeCaptureCommandResult> CancelInputCaptureAsync(
        Guid captureId,
        CancellationToken cancellationToken) =>
        InvokeAsync<RuntimeCaptureCommandResult>(
            RuntimeRpcMethods.CancelInputCapture,
            [captureId],
            cancellationToken);

    public Task<RuntimeCaptureLookupResult> GetInputCaptureAsync(
        Guid captureId,
        CancellationToken cancellationToken) =>
        InvokeAsync<RuntimeCaptureLookupResult>(
            RuntimeRpcMethods.GetInputCapture,
            [captureId],
            cancellationToken);

    public Task<RuntimeCommandResult> ExecuteCommandAsync(
        RuntimeCommandRequest request,
        CancellationToken cancellationToken) =>
        InvokeSettingsResultAsync<RuntimeCommandResult>(
            RuntimeRpcMethods.ExecuteCommand,
            [request],
            cancellationToken);

    public Task<RuntimeCommandOperationResult> GetCommandOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken) =>
        InvokeSettingsResultAsync<RuntimeCommandOperationResult>(
            RuntimeRpcMethods.GetCommandOperation,
            [operationId],
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _rpc.Dispose();
        _outboundTransfers.Dispose();
        _inboundTransfers.Dispose();
        try
        {
            await _rpc.Completion.ConfigureAwait(false);
        }
        catch (Exception) when (_rpc.IsDisposed)
        {
        }
        await _rpc.DispatchCompletion.ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    internal async Task AttachCoreAsync(
        RuntimeAttachRequest request,
        CancellationToken cancellationToken)
    {
        AttachResult = await InvokeSettingsResultAsync<RuntimeAttachResult>(
                RuntimeRpcMethods.Attach,
                [request],
                cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<T> InvokeAsync<T>(
        string method,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return _rpc.InvokeWithCancellationAsync<T>(method, arguments, cancellationToken);
    }

    private async Task<T> InvokeSettingsResultAsync<T>(
        string method,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        var value = await InvokeAsync<JToken>(method, arguments, cancellationToken).ConfigureAwait(false);
        if (!SettingsTransferSerializer.TryGetReference(value, out var reference))
        {
            return SettingsTransferSerializer.DeserializeInline<T>(value);
        }
        return await _inboundTransfers.ReceiveAsync<T>(reference, cancellationToken)
            .ConfigureAwait(false);
    }
}
