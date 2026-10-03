using System.Security.Cryptography;

namespace Joydex.Ipc;

/// <summary>Outcome of asking the runtime to admit this process as its tray client.</summary>
public enum RuntimeBootstrapRendezvousStatus
{
    Admitted,
    TrayAlreadyReserved,
}

/// <summary>A validated tray admission result from the production RuntimeHost.</summary>
public sealed record RuntimeBootstrapRendezvousResult(
    RuntimeBootstrapRendezvousStatus Status,
    RuntimeIpcLaunchTicket? LaunchTicket,
    string? Detail = null);

/// <summary>Host-side bounds for the small production bootstrap rendezvous.</summary>
public sealed class RuntimeBootstrapRendezvousOptions
{
    public int MaximumConcurrentRequests { get; init; } = 4;

    public int MaximumRequestBytes { get; init; } = 4096;

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan TicketLifetime { get; init; } = TimeSpan.FromSeconds(15);

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    internal TimeSpan ClaimRaceGrace { get; init; } = TimeSpan.FromSeconds(2);

    internal void Validate()
    {
        if (MaximumConcurrentRequests is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentRequests));
        }
        if (MaximumRequestBytes is < 256 or > 64 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumRequestBytes));
        }
        if (RequestTimeout < TimeSpan.FromMilliseconds(100)
            || RequestTimeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        }
        if (TicketLifetime < TimeSpan.FromMilliseconds(100)
            || TicketLifetime > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(TicketLifetime));
        }
        if (ClaimRaceGrace < TimeSpan.Zero || ClaimRaceGrace > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(nameof(ClaimRaceGrace));
        }
        ArgumentNullException.ThrowIfNull(TimeProvider);
    }
}

/// <summary>Client-side bounds for one bootstrap rendezvous request.</summary>
public sealed class RuntimeBootstrapRendezvousClientOptions
{
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);

    internal int MaximumResponseBytes { get; init; } = 4096;

    internal void Validate()
    {
        if (RequestTimeout < TimeSpan.FromMilliseconds(100)
            || RequestTimeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        }
        if (MaximumResponseBytes is < 256 or > 64 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumResponseBytes));
        }
    }
}

internal static class RuntimeBootstrapTicketFingerprint
{
    public static string Compute(string launchTicket)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launchTicket);
        byte[] ticketBytes;
        try
        {
            ticketBytes = Convert.FromBase64String(launchTicket);
        }
        catch (FormatException)
        {
            throw new RuntimeIpcAuthenticationException(
                "The consumed runtime launch ticket had an invalid encoding.");
        }

        return Convert.ToHexString(SHA256.HashData(ticketBytes)).ToLowerInvariant();
    }
}

internal static class RuntimeBootstrapRendezvousProtocol
{
    public const int Version = 1;
    public const string PipeSuffix = "-bootstrap-v1";

    public static string GetPipeName(RuntimeIpcEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var pipeName = endpoint.PipeName + PipeSuffix;
        if (pipeName.Length > 240)
        {
            throw new ArgumentException("The runtime endpoint name is too long for bootstrap rendezvous.", nameof(endpoint));
        }
        return pipeName;
    }
}

internal enum RuntimeBootstrapWireStatus
{
    Admitted,
    TrayAlreadyReserved,
    Rejected,
}

internal sealed record RuntimeBootstrapWireRequest(
    int ProtocolVersion,
    string DataRootId,
    string ClientKind);

internal sealed record RuntimeBootstrapWireResponse(
    RuntimeBootstrapWireStatus Status,
    string? LaunchTicket,
    long? TicketExpiresAtUnixMilliseconds,
    string? AcknowledgementNonce,
    string? Detail);

internal sealed record RuntimeBootstrapWireAcknowledgement(string Nonce);

internal sealed record RuntimeBootstrapWireCommit(bool Accepted);
