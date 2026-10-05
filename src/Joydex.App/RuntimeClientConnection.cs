using Joydex.Contracts;
using Joydex.Ipc;

namespace Joydex.App;

internal sealed record RuntimeClientResumeCursor(Guid EngineEpoch, long EventSequence);

/// <summary>
/// Owns one attached IPC connection and its checked callback state. The state is initialized only
/// after the attach reply, so callbacks racing that reply are reconciled against its cursor.
/// </summary>
internal sealed class RuntimeClientConnection : IAsyncDisposable
{
    private readonly RuntimeIpcClientConnection _connection;
    private readonly Task _completion;
    private int _disposed;

    private RuntimeClientConnection(
        RuntimeIpcClientConnection connection,
        RuntimeClientState state)
    {
        _connection = connection;
        State = state;
        _completion = ObserveCompletionAsync();
    }

    public RuntimeClientState State { get; }

    public IRuntimeRpcServer Rpc => _connection;

    public Task Completion => _completion;

    public bool SupportsPluginManagement => _connection.AttachResult.Capabilities.Contains(
        RuntimeProtocol.PluginManagementCapability,
        StringComparer.Ordinal);

    public static async Task<RuntimeClientConnection> ConnectAsync(
        RuntimeIpcEndpoint endpoint,
        RuntimeClientKind clientKind,
        string launchTicket,
        SynchronizationContext notificationContext,
        RuntimeClientResumeCursor? resumeCursor = null,
        RuntimeIpcClientOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(launchTicket);
        ArgumentNullException.ThrowIfNull(notificationContext);
        if (clientKind == RuntimeClientKind.HeadlessTest)
        {
            throw new ArgumentOutOfRangeException(
                nameof(clientKind),
                "Bundled UI connections must use the Tray or Settings role.");
        }
        if (resumeCursor is { EngineEpoch: var epoch, EventSequence: var sequence }
            && (epoch == Guid.Empty || sequence < 0))
        {
            throw new ArgumentException("The runtime resume cursor is invalid.", nameof(resumeCursor));
        }

        var state = new RuntimeClientState(notificationContext);
        RuntimeIpcClientConnection? connection = null;
        try
        {
            connection = await RuntimeIpcClient.ConnectAsync(
                    endpoint,
                    new RuntimeAttachRequest(
                        RuntimeProtocol.MajorVersion,
                        RuntimeProtocol.MinorVersion,
                        clientKind,
                        endpoint.InstanceKind,
                        endpoint.DataRootId,
                        launchTicket.Trim(),
                        resumeCursor?.EngineEpoch,
                        resumeCursor?.EventSequence),
                    state,
                    options,
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureReliableStateProtocol(connection.AttachResult);
            state.Initialize(connection.AttachResult);
            return new RuntimeClientConnection(connection, state);
        }
        catch (Exception exception)
        {
            state.MarkDisconnected(exception);
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    internal static void EnsureReliableStateProtocol(RuntimeAttachResult attachResult)
    {
        ArgumentNullException.ThrowIfNull(attachResult);
        if (attachResult.ProtocolMajor != RuntimeProtocol.MajorVersion
            || attachResult.ProtocolMinor < 2
            || attachResult.Capabilities is null
            || !attachResult.Capabilities.Contains(
                RuntimeProtocol.ReliableCursorsCapability,
                StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The runtime does not support the reliable cursors required by this UI client.");
        }
        if (attachResult.Snapshot is null || attachResult.Snapshot.InputEventCursor < 0)
        {
            throw new InvalidDataException("The runtime returned an invalid reliable cursor snapshot.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await _connection.DisposeAsync().ConfigureAwait(false);
        await _completion.ConfigureAwait(false);
    }

    private async Task ObserveCompletionAsync()
    {
        Exception? failure = null;
        try
        {
            await _connection.Completion.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        State.MarkDisconnected(failure);
    }
}
