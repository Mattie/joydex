using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Joydex.Secrets;

public enum SecretsBrokerCommandKind
{
    Catalog,
    Request,
    Poll,
    Cancel,
    Detach,
    Run,
}

/// <summary>One bounded broker command. Run carries the caller environment; the credential travels outside this JSON.</summary>
public sealed record SecretsBrokerCommand(
    SecretsBrokerCommandKind Kind,
    string ClientId,
    string ProjectReference,
    string? RequestId = null,
    string? Reason = null,
    string? RecipeId = null,
    IReadOnlyDictionary<string, string>? Parameters = null,
    ExecOperationProposal? Operation = null,
    string? Reservation = null,
    SecretsDetachReason? DetachReason = null,
    int? ExecutionTimeoutSeconds = null,
    string? StandardStreamsId = null,
    IReadOnlyDictionary<string, string>? CallerEnvironment = null);

/// <summary>One bounded broker response containing status, safe metadata, or sanitized process output.</summary>
public sealed record SecretsBrokerReply(
    SecretsRequestStatus Status,
    string? RequestId = null,
    Guid? AttemptId = null,
    string? Reservation = null,
    DateTimeOffset? ExpiresAt = null,
    IReadOnlyList<SecretAliasMetadata>? Aliases = null,
    IReadOnlyList<SecretsRecipeMetadata>? Recipes = null,
    SecretsExecResult? Execution = null,
    string? Error = null,
    bool? SecretsInjected = null);

/// <summary>Serves authenticated Secrets commands over a current-user, local named pipe.</summary>
public sealed class SecretsBrokerPipeServer
{
    private const int MaximumPayloadBytes = 512 * 1024;
    private const int MaximumConcurrentClients = 32;
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumRunDuration = TimeSpan.FromHours(1);
    private static readonly byte[] Magic = "JDS1"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly SecretsBrokerEndpoint _endpoint;
    private readonly SecretsBrokerRuntime _runtime;
    private readonly int _sessionId;
    private readonly TimeSpan _runDuration;

    public SecretsBrokerPipeServer(
        SecretsBrokerEndpoint endpoint,
        SecretsBrokerRuntime runtime,
        TimeSpan? maximumRunDuration = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _runDuration = maximumRunDuration ?? MaximumRunDuration;
        if (_runDuration <= TimeSpan.Zero || _runDuration > MaximumRunDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRunDuration));
        }
        _sessionId = Process.GetCurrentProcess().SessionId;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var active = new HashSet<Task>();
        using var capacity = new SemaphoreSlim(MaximumConcurrentClients, MaximumConcurrentClients);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await capacity.WaitAsync(cancellationToken).ConfigureAwait(false);
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = CreatePipe();
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    if (pipe is not null) await pipe.DisposeAsync().ConfigureAwait(false);
                    capacity.Release();
                    throw;
                }

                var work = HandleDisposeAndReleaseAsync(pipe, capacity, cancellationToken);
                lock (active)
                {
                    active.RemoveWhere(task => task.IsCompleted);
                    active.Add(work);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            Task[] pending;
            lock (active) pending = active.ToArray();
            try { await Task.WhenAll(pending).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
    }

    private NamedPipeServerStream CreatePipe() => new(
        _endpoint.PipeName,
        PipeDirection.InOut,
        MaximumConcurrentClients,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
        inBufferSize: MaximumPayloadBytes,
        outBufferSize: MaximumPayloadBytes);

    private async Task HandleDisposeAndReleaseAsync(
        NamedPipeServerStream pipe,
        SemaphoreSlim capacity,
        CancellationToken cancellationToken)
    {
        try
        {
            await HandleAndDisposeAsync(pipe, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            capacity.Release();
        }
    }

    private async Task HandleAndDisposeAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            byte[]? credential = null;
            try
            {
                VerifyClientSession(pipe, _sessionId);
                using var frameTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                frameTimeout.CancelAfter(FrameTimeout);
                var frameToken = frameTimeout.Token;
                var header = new byte[12];
                await pipe.ReadExactlyAsync(header, frameToken).ConfigureAwait(false);
                if (!header.AsSpan(0, 4).SequenceEqual(Magic))
                {
                    throw new InvalidDataException("The Secrets protocol header is invalid.");
                }
                var credentialLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
                var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));
                if (credentialLength != 32 || payloadLength is < 1 or > MaximumPayloadBytes)
                {
                    throw new InvalidDataException("The Secrets protocol frame has invalid bounds.");
                }

                credential = new byte[credentialLength];
                await pipe.ReadExactlyAsync(credential, frameToken).ConfigureAwait(false);
                var payload = new byte[payloadLength];
                await pipe.ReadExactlyAsync(payload, frameToken).ConfigureAwait(false);
                SecretsBrokerCommand command;
                try
                {
                    command = JsonSerializer.Deserialize<SecretsBrokerCommand>(payload, JsonOptions)
                        ?? throw new InvalidDataException("The Secrets command is empty.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(payload);
                }

                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var requesterPid))
                    throw new UnauthorizedAccessException("Cannot identify requester.");
                using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var disconnect = command.Kind == SecretsBrokerCommandKind.Run
                    ? ObserveDisconnectAsync(pipe, connectionLifetime)
                    : Task.CompletedTask;
                SecretsBrokerReply reply;
                try
                {
                    reply = await DispatchAsync(command, credential, connectionLifetime.Token, requesterPid).ConfigureAwait(false);
                }
                finally
                {
                    connectionLifetime.Cancel();
                    await disconnect.ConfigureAwait(false);
                }
                using var replyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                replyTimeout.CancelAfter(FrameTimeout);
                await WriteReplyAsync(pipe, reply, replyTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var reply = new SecretsBrokerReply(
                    exception is UnauthorizedAccessException
                        ? SecretsRequestStatus.IdentityUnverified
                        : exception is SecretsProviderUnavailableException
                            ? SecretsRequestStatus.ProviderUnavailable
                            : exception is InvalidDataException or JsonException or ArgumentException or IOException
                                ? SecretsRequestStatus.InvalidRequest
                                : SecretsRequestStatus.Revoked,
                    Error: SafeError(exception));
                try
                {
                    using var replyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    replyTimeout.CancelAfter(FrameTimeout);
                    await WriteReplyAsync(pipe, reply, replyTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception writeException) when (writeException is IOException
                    or InvalidOperationException
                    or OperationCanceledException) { }
            }
            finally
            {
                if (credential is not null) CryptographicOperations.ZeroMemory(credential);
            }
        }
    }

    private static async Task ObserveDisconnectAsync(Stream pipe, CancellationTokenSource lifetime)
    {
        try
        {
            // A Run transaction has no further inbound frames. EOF means the wrapper exited.
            await pipe.ReadAsync(new byte[1], lifetime.Token).ConfigureAwait(false);
            lifetime.Cancel();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (IOException) { lifetime.Cancel(); }
    }

    private async Task<SecretsBrokerReply> DispatchAsync(
        SecretsBrokerCommand command,
        byte[] credential,
        CancellationToken cancellationToken,
        uint requesterPid)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ClientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProjectReference);
        if (command.Kind != SecretsBrokerCommandKind.Run && command.CallerEnvironment is not null)
            throw new InvalidDataException("The caller environment is only accepted for execution.");
        switch (command.Kind)
        {
            case SecretsBrokerCommandKind.Catalog:
            {
                var catalog = _runtime.ListCatalog(
                    command.ClientId,
                    credential,
                    command.ProjectReference);
                return new(catalog.Status, Aliases: catalog.Aliases, Recipes: catalog.Recipes);
            }
            case SecretsBrokerCommandKind.Request:
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(command.RequestId);
                var result = _runtime.Submit(new SecretsBrokerSubmission(
                    command.RequestId,
                    command.ClientId,
                    command.ProjectReference,
                    command.Reason ?? string.Empty,
                    command.RecipeId,
                    command.Parameters,
                    command.Operation), credential);
                return Reply(result);
            }
            case SecretsBrokerCommandKind.Poll:
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(command.RequestId);
                return Reply(_runtime.GetStatus(
                    command.ClientId,
                    credential,
                    command.ProjectReference,
                    command.RequestId));
            }
            case SecretsBrokerCommandKind.Cancel:
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(command.RequestId);
                return Reply(_runtime.Cancel(
                    command.ClientId,
                    credential,
                    command.ProjectReference,
                    command.RequestId));
            }
            case SecretsBrokerCommandKind.Detach:
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(command.RequestId);
                if (command.DetachReason is null)
                {
                    throw new InvalidDataException("A detach reason is required.");
                }
                return Reply(_runtime.Detach(
                    command.ClientId,
                    credential,
                    command.ProjectReference,
                    command.RequestId,
                    command.DetachReason.Value));
            }
            case SecretsBrokerCommandKind.Run:
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(command.RequestId);
                ArgumentException.ThrowIfNullOrWhiteSpace(command.Reservation);
                if (command.CallerEnvironment is null
                    || command.CallerEnvironment.Keys.Any(name => string.IsNullOrEmpty(name)
                        || name.Contains('=') || name.Contains('\0'))
                    || command.CallerEnvironment.Values.Any(value => value is null || value.Contains('\0'))
                    || command.CallerEnvironment.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                        != command.CallerEnvironment.Count)
                {
                    throw new InvalidDataException("The caller environment is invalid.");
                }
                if (command.ExecutionTimeoutSeconds is < 1 or > 3600)
                {
                    throw new InvalidDataException("The execution timeout must be between 1 and 3600 seconds.");
                }
                using var runTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var requestedDuration = command.ExecutionTimeoutSeconds is { } seconds
                    ? TimeSpan.FromSeconds(seconds)
                    : _runDuration;
                runTimeout.CancelAfter(requestedDuration < _runDuration ? requestedDuration : _runDuration);
                try
                {
                    using var streams = command.StandardStreamsId is { } streamId
                        ? await SecretsStandardStreams.ConnectAsync(streamId, requesterPid, runTimeout.Token).ConfigureAwait(false)
                        : null;
                    var execution = await _runtime.RunAsync(
                        command.ClientId,
                        credential,
                        command.ProjectReference,
                        command.RequestId,
                        command.Reservation,
                        command.CallerEnvironment, runTimeout.Token, streams).ConfigureAwait(false);
                    return new(
                        execution.Task?.State == "unknown" ? SecretsRequestStatus.LaunchUnconfirmed : SecretsRequestStatus.Completed,
                        command.RequestId,
                        Execution: execution,
                        SecretsInjected: true);
                }
                catch (SecretsExecutionCanceledException exception)
                    when (!cancellationToken.IsCancellationRequested && runTimeout.IsCancellationRequested)
                {
                    return TimeoutReply(
                        exception.LaunchUnconfirmed
                            ? SecretsRequestStatus.LaunchUnconfirmed
                            : SecretsRequestStatus.Revoked,
                        command.RequestId);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                    && runTimeout.IsCancellationRequested)
                {
                    var status = _runtime.GetStatus(
                        command.ClientId,
                        credential,
                        command.ProjectReference,
                        command.RequestId).Status;
                    return TimeoutReply(status, command.RequestId);
                }
            }
            default:
                throw new InvalidDataException("The Secrets command kind is unsupported.");
        }
    }

    private static SecretsBrokerReply Reply(SecretsRequestResponse response) => new(
        response.Status,
        response.RequestId,
        response.AttemptId,
        response.Reservation,
        response.ExpiresAt);

    internal static SecretsBrokerReply TimeoutReply(
        SecretsRequestStatus status,
        string requestId) => status == SecretsRequestStatus.Revoked
            ? new(
                SecretsRequestStatus.Revoked,
                requestId,
                Error: "The approved command exceeded its execution time limit and was stopped.")
            : new(
                SecretsRequestStatus.LaunchUnconfirmed,
                requestId,
                Error: "The command exceeded its execution time limit, but Joydex could not confirm that it stopped. Do not retry it yet.");

    private static async Task WriteReplyAsync(
        Stream stream,
        SecretsBrokerReply reply,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(reply, JsonOptions);
        if (payload.Length > MaximumPayloadBytes)
        {
            throw new InvalidDataException("The Secrets response exceeds its protocol limit.");
        }
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(payload);
    }

    private static string SafeError(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "The local requester could not be authenticated.",
        SecretsProviderUnavailableException => "The configured secret provider is unavailable.",
        InvalidDataException or JsonException or ArgumentException or IOException => "The Secrets request is invalid.",
        _ => "The Secrets request is no longer authorized.",
    };

    private static void VerifyClientSession(NamedPipeServerStream pipe, int expectedSessionId)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId))
        {
            throw new UnauthorizedAccessException("The Secrets client process could not be identified.");
        }
        using var process = Process.GetProcessById(checked((int)processId));
        if (process.SessionId != expectedSessionId)
        {
            throw new UnauthorizedAccessException("The Secrets client belongs to another Windows session.");
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe,
        out uint clientProcessId);
}

/// <summary>Authenticated client used by the local helper without exposing credential bytes to arguments.</summary>
public sealed class SecretsBrokerPipeClient
{
    private const int MaximumPayloadBytes = 512 * 1024;
    private static readonly byte[] Magic = "JDS1"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly SecretsBrokerEndpoint _endpoint;
    private readonly NamedClientCredentialStore _credentials;

    public SecretsBrokerPipeClient(string dataRoot)
    {
        _endpoint = SecretsBrokerEndpoint.Create(dataRoot);
        _credentials = new NamedClientCredentialStore(SecretsPaths.GetCredentialDirectory(dataRoot));
    }

    public async Task<SecretsBrokerReply> SendAsync(
        SecretsBrokerCommand command,
        TimeSpan transactionTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var credential = _credentials.Load(command.ClientId);
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                _endpoint.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(transactionTimeout);
            var transactionToken = timeout.Token;
            await pipe.ConnectAsync(transactionToken).ConfigureAwait(false);
            VerifyServerSession(pipe, Process.GetCurrentProcess().SessionId);

            var payload = JsonSerializer.SerializeToUtf8Bytes(command, JsonOptions);
            if (payload.Length > MaximumPayloadBytes)
            {
                throw new InvalidDataException("The Secrets command exceeds its protocol limit.");
            }
            try
            {
                var header = new byte[12];
                Magic.CopyTo(header, 0);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), credential.Length);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), payload.Length);
                await pipe.WriteAsync(header, transactionToken).ConfigureAwait(false);
                await pipe.WriteAsync(credential, transactionToken).ConfigureAwait(false);
                await pipe.WriteAsync(payload, transactionToken).ConfigureAwait(false);
                await pipe.FlushAsync(transactionToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }

            var replyHeader = new byte[4];
            await pipe.ReadExactlyAsync(replyHeader, transactionToken).ConfigureAwait(false);
            var replyLength = BinaryPrimitives.ReadInt32LittleEndian(replyHeader);
            if (replyLength is < 1 or > MaximumPayloadBytes)
            {
                throw new InvalidDataException("The Secrets response has invalid bounds.");
            }
            var replyPayload = new byte[replyLength];
            await pipe.ReadExactlyAsync(replyPayload, transactionToken).ConfigureAwait(false);
            try
            {
                return JsonSerializer.Deserialize<SecretsBrokerReply>(replyPayload, JsonOptions)
                    ?? throw new InvalidDataException("The Secrets response is empty.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(replyPayload);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credential);
        }
    }

    private static void VerifyServerSession(NamedPipeClientStream pipe, int expectedSessionId)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId))
        {
            throw new UnauthorizedAccessException("The Secrets broker process could not be identified.");
        }
        using var process = Process.GetProcessById(checked((int)processId));
        if (process.SessionId != expectedSessionId)
        {
            throw new UnauthorizedAccessException("The Secrets broker belongs to another Windows session.");
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe,
        out uint serverProcessId);
}
