using System.Diagnostics;
using Joydex.Contracts;

namespace Joydex.Ipc;

/// <summary>Host-side limits for one runtime IPC endpoint.</summary>
public sealed class RuntimeIpcServerOptions
{
    public int MaximumConnections { get; init; } = 8;

    public int MaximumConcurrentRequestsPerConnection { get; init; } = 8;

    public TimeSpan AttachTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan DefaultTicketLifetime { get; init; } = TimeSpan.FromSeconds(30);

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    internal void Validate()
    {
        if (MaximumConnections is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumConnections),
                "The maximum connection count must be between 1 and 64.");
        }
        if (MaximumConcurrentRequestsPerConnection is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumConcurrentRequestsPerConnection),
                "The per-connection concurrent request limit must be between 1 and 64.");
        }
        if (AttachTimeout <= TimeSpan.Zero || AttachTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(AttachTimeout));
        }
        if (DefaultTicketLifetime <= TimeSpan.Zero || DefaultTicketLifetime > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(DefaultTicketLifetime));
        }
        ArgumentNullException.ThrowIfNull(TimeProvider);
    }
}

/// <summary>Client-side connection limits for a runtime IPC endpoint.</summary>
public sealed class RuntimeIpcClientOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    internal void Validate()
    {
        if (ConnectTimeout <= TimeSpan.Zero || ConnectTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(ConnectTimeout));
        }
    }
}

/// <summary>The verified identity of a process at the other end of a pipe.</summary>
public sealed record RuntimeIpcPeer(
    int ProcessId,
    DateTimeOffset ProcessStartTimeUtc,
    int SessionId,
    string UserSid,
    bool IsElevated);

/// <summary>Context the transport binds to one accepted physical pipe.</summary>
public sealed record RuntimeIpcConnectionContext(
    string ConnectionId,
    RuntimeIpcEndpoint Endpoint,
    RuntimeIpcPeer Peer)
{
    /// <summary>
    /// Correlates a factory call with the exact launch ticket that the transport consumed.
    /// The transport sets this only after authentication and never exposes the ticket itself.
    /// </summary>
    internal string? AuthenticatedLaunchTicketFingerprint { get; init; }
}

/// <summary>Creates the runtime implementation for an authenticated physical connection.</summary>
public delegate ValueTask<IRuntimeRpcServer> RuntimeRpcServerFactory(
    RuntimeIpcConnectionContext context,
    RuntimeClientKind authorizedClientKind,
    IRuntimeRpcClient client,
    Action<Exception?> abortConnection,
    CancellationToken connectionCancellationToken);

/// <summary>An opaque, expiring capability that permits one runtime attach.</summary>
public sealed record RuntimeIpcLaunchTicket(string Value, DateTimeOffset ExpiresAtUtc)
{
    public override string ToString() => "[runtime IPC launch ticket]";
}

internal sealed record ExpectedRuntimeIpcPeer(
    int ProcessId,
    long ProcessStartTimeUtcTicks)
{
    public static ExpectedRuntimeIpcPeer FromProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return new ExpectedRuntimeIpcPeer(
            process.Id,
            process.StartTime.ToUniversalTime().Ticks);
    }
}
