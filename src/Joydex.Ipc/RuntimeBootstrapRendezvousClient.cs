using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace Joydex.Ipc;

/// <summary>Obtains a process-bound tray launch ticket from an existing RuntimeHost.</summary>
public static class RuntimeBootstrapRendezvousClient
{
    public static Task<RuntimeBootstrapRendezvousResult> RequestTrayAsync(
        RuntimeIpcEndpoint endpoint,
        string frozenRuntimeHostPath,
        RuntimeBootstrapRendezvousClientOptions? options = null,
        CancellationToken cancellationToken = default) =>
        RequestTrayCoreAsync(
            endpoint,
            frozenRuntimeHostPath,
            options,
            WindowsRuntimeBootstrapImageVerifier.Instance,
            requiredInstanceKind: Joydex.Contracts.RuntimeInstanceKind.Production,
            validateExecutableName: true,
            request: null,
            cancellationToken);

    /// <summary>
    /// Obtains a process-bound tray ticket from an explicitly synthetic demo RuntimeHost.
    /// </summary>
    public static Task<RuntimeBootstrapRendezvousResult> RequestDemoTrayAsync(
        RuntimeIpcEndpoint endpoint,
        string frozenRuntimeHostPath,
        RuntimeBootstrapRendezvousClientOptions? options = null,
        CancellationToken cancellationToken = default) =>
        RequestTrayCoreAsync(
            endpoint,
            frozenRuntimeHostPath,
            options,
            WindowsRuntimeBootstrapImageVerifier.Instance,
            requiredInstanceKind: Joydex.Contracts.RuntimeInstanceKind.Synthetic,
            validateExecutableName: true,
            request: null,
            cancellationToken);

    internal static Task<RuntimeBootstrapRendezvousResult> RequestTrayForTestAsync(
        RuntimeIpcEndpoint endpoint,
        string frozenRuntimeHostPath,
        RuntimeBootstrapRendezvousClientOptions? options,
        IRuntimeBootstrapImageVerifier imageVerifier,
        RuntimeBootstrapWireRequest? request = null,
        CancellationToken cancellationToken = default) =>
        RequestTrayCoreAsync(
            endpoint,
            frozenRuntimeHostPath,
            options,
            imageVerifier,
            requiredInstanceKind: Joydex.Contracts.RuntimeInstanceKind.Synthetic,
            validateExecutableName: false,
            request,
            cancellationToken);

    private static async Task<RuntimeBootstrapRendezvousResult> RequestTrayCoreAsync(
        RuntimeIpcEndpoint endpoint,
        string frozenRuntimeHostPath,
        RuntimeBootstrapRendezvousClientOptions? options,
        IRuntimeBootstrapImageVerifier imageVerifier,
        Joydex.Contracts.RuntimeInstanceKind requiredInstanceKind,
        bool validateExecutableName,
        RuntimeBootstrapWireRequest? request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(frozenRuntimeHostPath);
        ArgumentNullException.ThrowIfNull(imageVerifier);
        options ??= new RuntimeBootstrapRendezvousClientOptions();
        options.Validate();

        if (endpoint.InstanceKind != requiredInstanceKind)
        {
            throw new InvalidOperationException(
                $"{requiredInstanceKind} bootstrap rendezvous requires a {requiredInstanceKind} endpoint.");
        }

        var expectedHostImage = imageVerifier.CaptureFrozenImage(frozenRuntimeHostPath);
        if (validateExecutableName
            && !string.Equals(
                Path.GetFileName(expectedHostImage.CanonicalPath),
                "Joydex.RuntimeHost.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The frozen runtime image must be Joydex.RuntimeHost.exe.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.RequestTimeout);
        var pipe = new NamedPipeClientStream(
            ".",
            RuntimeBootstrapRendezvousProtocol.GetPipeName(endpoint),
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
                var peer = WindowsPipePeerVerifier.VerifyServer(pipe, endpoint.SessionId);
                using (var hostProcess = Process.GetProcessById(peer.ProcessId))
                {
                    imageVerifier.VerifyPeerProcess(
                        hostProcess,
                        peer,
                        expectedHostImage,
                        "RuntimeHost");
                }

                request ??= new RuntimeBootstrapWireRequest(
                    RuntimeBootstrapRendezvousProtocol.Version,
                    endpoint.DataRootId,
                    Joydex.Contracts.RuntimeClientKind.Tray.ToString());
                await RuntimeBootstrapWire.WriteAsync(
                        pipe,
                        request,
                        options.MaximumResponseBytes,
                        deadline.Token)
                    .ConfigureAwait(false);
                var response = await RuntimeBootstrapWire.ReadAsync<RuntimeBootstrapWireResponse>(
                        pipe,
                        options.MaximumResponseBytes,
                        deadline.Token)
                    .ConfigureAwait(false);

                if (response.Status == RuntimeBootstrapWireStatus.Rejected)
                {
                    throw new RuntimeIpcAuthenticationException(
                        response.Detail ?? "The RuntimeHost rejected the bootstrap request.");
                }
                if (response.Status == RuntimeBootstrapWireStatus.TrayAlreadyReserved)
                {
                    return new RuntimeBootstrapRendezvousResult(
                        RuntimeBootstrapRendezvousStatus.TrayAlreadyReserved,
                        LaunchTicket: null,
                        response.Detail);
                }
                if (response.Status != RuntimeBootstrapWireStatus.Admitted
                    || string.IsNullOrWhiteSpace(response.LaunchTicket)
                    || response.TicketExpiresAtUnixMilliseconds is null
                    || string.IsNullOrWhiteSpace(response.AcknowledgementNonce))
                {
                    throw new RuntimeIpcAuthenticationException(
                        "The RuntimeHost returned an invalid bootstrap admission.");
                }

                await RuntimeBootstrapWire.WriteAsync(
                        pipe,
                        new RuntimeBootstrapWireAcknowledgement(response.AcknowledgementNonce),
                        options.MaximumResponseBytes,
                        deadline.Token)
                    .ConfigureAwait(false);
                var commit = await RuntimeBootstrapWire.ReadAsync<RuntimeBootstrapWireCommit>(
                        pipe,
                        options.MaximumResponseBytes,
                        deadline.Token)
                    .ConfigureAwait(false);
                if (!commit.Accepted)
                {
                    throw new RuntimeIpcAuthenticationException(
                        "The RuntimeHost did not commit the bootstrap admission.");
                }

                return new RuntimeBootstrapRendezvousResult(
                    RuntimeBootstrapRendezvousStatus.Admitted,
                    new RuntimeIpcLaunchTicket(
                        response.LaunchTicket,
                        DateTimeOffset.FromUnixTimeMilliseconds(
                            response.TicketExpiresAtUnixMilliseconds.Value)),
                    response.Detail);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "The RuntimeHost bootstrap rendezvous did not complete before its deadline.");
            }
        }
    }
}

internal static class RuntimeBootstrapWire
{
    private const int LengthPrefixBytes = sizeof(int);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync<T>(
        Stream stream,
        T value,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (payload.Length > maximumBytes)
        {
            throw new RuntimeIpcMessageTooLargeException(payload.Length, maximumBytes);
        }

        var prefix = new byte[LengthPrefixBytes];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[LengthPrefixBytes];
        await ReadExactlyAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > maximumBytes)
        {
            throw new RuntimeIpcMessageTooLargeException(length, maximumBytes);
        }

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, JsonOptions)
            ?? throw new InvalidDataException("The bootstrap rendezvous message was empty.");
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException("The bootstrap rendezvous pipe closed mid-message.");
            }
            read += count;
        }
    }
}
