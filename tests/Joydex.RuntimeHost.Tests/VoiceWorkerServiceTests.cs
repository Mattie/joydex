using System.Diagnostics;
using System.Text.Json;
using Joydex.App;
using Joydex.Contracts;
using Joydex.Ipc;
using Joydex.VoiceWorker;
using Joydex.Windows.Voice;

namespace Joydex.RuntimeHost.Tests;

public sealed class VoiceWorkerServiceTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task StartRejectsWrongCapabilityOrProtocolBeforeVoiceConstruction(
        bool validCapability,
        int protocolMajor)
    {
        using var process = Process.GetCurrentProcess();
        var ticket = new VoiceWorkerLaunchTicket(
            "unused",
            "capability",
            4,
            VoiceWorkerProtocol.MajorVersion,
            VoiceWorkerProtocol.MinorVersion,
            process.Id,
            process.StartTime.ToUniversalTime().Ticks,
            process.SessionId);
        var (rpc, bounded) = RuntimeJsonRpc.Create(new MemoryStream());
        await using var service = new VoiceWorkerService(ticket, rpc);
        using (rpc)
        using (bounded)
        {
            var request = new VoiceWorkerStartRequest(
                validCapability ? ticket.Capability : "wrong",
                ticket.Generation,
                protocolMajor,
                VoiceWorkerProtocol.MinorVersion,
                null!,
                null!,
                null!);

            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.StartAsync(request, CancellationToken.None));
        }

        Assert.False(service.StopRequested);
    }

    [Fact]
    public void ConversationPagesPreserveCompleteEntriesWithinTheRpcFrameBound()
    {
        var boundaryText = new string('\u0001',
            VoiceWorkerProtocol.MaximumConversationEntryTextCharacters);
        var secondText = new string('z', 40 * 1024);
        var snapshot = new RoomVoiceConversationSnapshot(
            [
                new RoomVoiceConversationEntry(
                    "first", DateTimeOffset.UnixEpoch, CodexVoiceConversationKind.User,
                    boundaryText, RawText: boundaryText),
                new RoomVoiceConversationEntry(
                    "second", DateTimeOffset.UnixEpoch, CodexVoiceConversationKind.Assistant,
                    secondText),
            ],
            VoicePeSessionState.Armed,
            OwnerReady: true,
            SessionActive: false,
            HistoryAvailable: true,
            Stale: false,
            "Ready",
            Error: null,
            ConversationVersion: 9);

        var firstPage = VoiceWorkerService.CreateConversationPage(snapshot, offset: 0);

        var first = Assert.Single(firstPage.Entries);
        Assert.Equal(boundaryText, first.Text);
        Assert.Equal(boundaryText, first.RawText);
        Assert.NotNull(firstPage.NextContinuationToken);
        Assert.True(
            JsonSerializer.SerializeToUtf8Bytes(firstPage).Length
                < RuntimeProtocol.MaximumMessageBytes,
            "The worst-case escaped page must fit the bounded JSON-RPC frame.");

        var secondPage = VoiceWorkerService.CreateConversationPage(snapshot, offset: 1);
        Assert.Equal(secondText, Assert.Single(secondPage.Entries).Text);
        Assert.Null(secondPage.NextContinuationToken);

        var oversized = snapshot with
        {
            Entries =
            [
                snapshot.Entries[0] with
                {
                    Text = new string('x',
                        VoiceWorkerProtocol.MaximumConversationEntryTextCharacters + 1),
                },
            ],
        };
        Assert.Throws<InvalidDataException>(
            () => VoiceWorkerService.CreateConversationPage(oversized, offset: 0));
    }
}
