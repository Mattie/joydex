using Joydex.Contracts;

namespace Joydex.Ipc.Tests;

public sealed class RuntimeIpcTicketStoreTests
{
    private readonly RuntimeIpcPeer _peer = new(
        Environment.ProcessId,
        DateTimeOffset.UtcNow,
        Environment.CurrentManagedThreadId,
        "S-1-5-21-test",
        IsElevated: false);

    [Fact]
    public void TicketIsSingleUseAndBoundToClientKind()
    {
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var store = new RuntimeIpcTicketStore(clock, TimeSpan.FromSeconds(30));
        var ticket = store.Issue(RuntimeClientKind.HeadlessTest, null, expectedPeer: null);

        Assert.DoesNotContain(ticket.Value, ticket.ToString(), StringComparison.Ordinal);
        Assert.False(store.TryConsume(ticket.Value, RuntimeClientKind.Settings, _peer));
        Assert.True(store.TryConsume(ticket.Value, RuntimeClientKind.HeadlessTest, _peer));
        Assert.False(store.TryConsume(ticket.Value, RuntimeClientKind.HeadlessTest, _peer));
    }

    [Fact]
    public void ExpiredAndMalformedTicketsAreRejected()
    {
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var store = new RuntimeIpcTicketStore(clock, TimeSpan.FromSeconds(1));
        var ticket = store.Issue(RuntimeClientKind.HeadlessTest, null, expectedPeer: null);

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.False(store.TryConsume(ticket.Value, RuntimeClientKind.HeadlessTest, _peer));
        Assert.False(store.TryConsume("malformed", RuntimeClientKind.HeadlessTest, _peer));
    }

    [Fact]
    public void ProcessBindingChecksPidAndExactCreationTime()
    {
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var store = new RuntimeIpcTicketStore(clock, TimeSpan.FromSeconds(30));
        var expected = new ExpectedRuntimeIpcPeer(
            _peer.ProcessId,
            _peer.ProcessStartTimeUtc.UtcTicks);
        var ticket = store.Issue(RuntimeClientKind.HeadlessTest, null, expected);

        var wrongPeer = _peer with { ProcessId = _peer.ProcessId + 1 };

        Assert.False(store.TryConsume(ticket.Value, RuntimeClientKind.HeadlessTest, wrongPeer));
        Assert.True(store.TryConsume(ticket.Value, RuntimeClientKind.HeadlessTest, _peer));
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }
}
