using System.Security.Cryptography;
using System.Text;
using Joydex.Contracts;

namespace Joydex.Ipc;

internal sealed class RuntimeIpcTicketStore(TimeProvider timeProvider, TimeSpan defaultLifetime)
{
    private const int TicketBytes = 32;
    private readonly object _gate = new();
    private readonly List<TicketEntry> _tickets = [];

    public RuntimeIpcLaunchTicket Issue(
        RuntimeClientKind clientKind,
        TimeSpan? lifetime,
        ExpectedRuntimeIpcPeer? expectedPeer)
    {
        var effectiveLifetime = lifetime ?? defaultLifetime;
        if (effectiveLifetime <= TimeSpan.Zero || effectiveLifetime > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        var bytes = RandomNumberGenerator.GetBytes(TicketBytes);
        var expiresAt = timeProvider.GetUtcNow().Add(effectiveLifetime);
        lock (_gate)
        {
            RemoveExpiredLocked(timeProvider.GetUtcNow());
            _tickets.Add(new TicketEntry(bytes, clientKind, expiresAt, expectedPeer));
        }
        return new RuntimeIpcLaunchTicket(Convert.ToBase64String(bytes), expiresAt);
    }

    public bool TryConsume(
        string presentedValue,
        RuntimeClientKind clientKind,
        RuntimeIpcPeer peer)
    {
        var presented = DecodeTicket(presentedValue);
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            RemoveExpiredLocked(now);
            var matchedIndex = -1;
            for (var index = 0; index < _tickets.Count; index++)
            {
                var candidate = _tickets[index];
                var tokenMatches = CryptographicOperations.FixedTimeEquals(
                    candidate.Value,
                    presented);
                if (tokenMatches
                    && candidate.ClientKind == clientKind
                    && PeerMatches(candidate.ExpectedPeer, peer))
                {
                    matchedIndex = index;
                }
            }

            if (matchedIndex < 0)
            {
                return false;
            }
            _tickets.RemoveAt(matchedIndex);
            return true;
        }
    }

    private static byte[] DecodeTicket(string presentedValue)
    {
        if (string.IsNullOrWhiteSpace(presentedValue)
            || presentedValue.Length > 128)
        {
            return new byte[TicketBytes];
        }

        var decoded = new byte[TicketBytes];
        return Convert.TryFromBase64String(presentedValue, decoded, out var bytesWritten)
               && bytesWritten == TicketBytes
            ? decoded
            : new byte[TicketBytes];
    }

    private static bool PeerMatches(ExpectedRuntimeIpcPeer? expected, RuntimeIpcPeer actual) =>
        expected is null
        || (expected.ProcessId == actual.ProcessId
            && expected.ProcessStartTimeUtcTicks == actual.ProcessStartTimeUtc.UtcTicks);

    private void RemoveExpiredLocked(DateTimeOffset now) =>
        _tickets.RemoveAll(ticket => ticket.ExpiresAtUtc <= now);

    private sealed record TicketEntry(
        byte[] Value,
        RuntimeClientKind ClientKind,
        DateTimeOffset ExpiresAtUtc,
        ExpectedRuntimeIpcPeer? ExpectedPeer);
}
