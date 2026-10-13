using System.Diagnostics;
using System.Text.Json;
using Joydex.App;
using Joydex.Contracts;
using Joydex.Core.Voice;
using Joydex.Ipc;
using Joydex.VoiceWorker;
using Joydex.Windows.Voice;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace Joydex.RuntimeHost.Tests;

public sealed class VoiceWorkerServiceTests
{
    [Fact]
    public async Task DotOnlyDryRunKeepsTheWorkerAliveWithoutAVoiceOwner()
    {
        using var process = Process.GetCurrentProcess();
        var ticket = new VoiceWorkerLaunchTicket("unused", "capability", 4,
            VoiceWorkerProtocol.MajorVersion, VoiceWorkerProtocol.MinorVersion,
            process.Id, process.StartTime.ToUniversalTime().Ticks, process.SessionId);
        var (workerStream, hostStream) = FullDuplexStream.CreatePair();
        var (rpc, bounded) = RuntimeJsonRpc.Create(workerStream);
        var (hostRpc, hostBounded) = RuntimeJsonRpc.Create(hostStream);
        using (rpc)
        using (bounded)
        using (hostRpc)
        using (hostBounded)
        {
            var host = new DryRunHost();
            hostRpc.AddLocalRpcTarget(host);
            hostRpc.StartListening();
            rpc.StartListening();
            await using var service = new VoiceWorkerService(ticket, rpc);
            var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var request = new VoiceWorkerStartRequest(ticket.Capability, ticket.Generation,
                VoiceWorkerProtocol.MajorVersion, VoiceWorkerProtocol.MinorVersion,
                new VoiceWorkerPreferences(7, true, "http://127.0.0.1:1/", "", "",
                    (int)VoicePeSessionMode.NativeDot, "", "", "", "", "", "", "", 1,
                    false, false, "", "", "",
                    new DotVoiceTarget("account-123", "user-123", "account-123~dot-123",
                        "01000000-0000-0000-0000-000000000000", "Example dot")),
                new VoiceWorkerSafety(true, true, [], []),
                new VoiceWorkerPaths(Path.Combine(directory, "webview"),
                    Path.Combine(directory, "voice.json"), Path.Combine(directory, "bridge.exe"), "unused"));

            var response = await service.StartAsync(request, CancellationToken.None);
            await host.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Null(response.Failure);
            Assert.NotNull(response.Snapshot);
            Assert.Equal(RuntimeVoiceSessionState.Armed, response.Snapshot.Voice.SessionState);
            Assert.False(response.Snapshot.Voice.OwnerReady);
            Assert.False(response.Snapshot.Voice.SessionActive);
            Assert.False(service.Completion.IsCompleted, service.Completion.Exception?.ToString());
            Assert.False(Directory.Exists(directory));
        }
    }

    private sealed class DryRunHost
    {
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        [JsonRpcMethod(VoiceWorkerProtocol.Log)]
        public void Log(VoiceWorkerLogMessage message)
        {
            if (message.Message.StartsWith("Voice PE dry-run simulation started for dot", StringComparison.Ordinal))
                Ready.TrySetResult();
        }

        [JsonRpcMethod(VoiceWorkerProtocol.PublishSnapshot)]
        public void PublishSnapshot(VoiceWorkerSnapshot snapshot) { }
    }

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
