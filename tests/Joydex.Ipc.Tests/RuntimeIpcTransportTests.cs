using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using Joydex.Contracts;
using Joydex.Core.Input;
using StreamJsonRpc;

namespace Joydex.Ipc.Tests;

public sealed class RuntimeIpcTransportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "joydex-ipc-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LocalPipePeerVerificationSucceeds()
    {
        var pipeName = $"joydex-ipc-test-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var accept = server.WaitForConnectionAsync();
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync();
        await accept;

        var peer = WindowsPipePeerVerifier.VerifyClient(
            server,
            Process.GetCurrentProcess().SessionId);

        Assert.Equal(Environment.ProcessId, peer.ProcessId);
    }

    [Fact]
    public async Task DuplexConnectionAuthenticatesDispatchesAndPreservesCallbacks()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        RuntimeIpcConnectionContext? acceptedContext = null;
        RuntimeClientKind? acceptedClientKind = null;
        FakeRuntimeServer? implementation = null;
        await using var server = RuntimeIpcServer.Start(
            endpoint,
            (context, authorizedClientKind, client, _, _) =>
            {
                acceptedContext = context;
                acceptedClientKind = authorizedClientKind;
                implementation = new FakeRuntimeServer(endpoint, client);
                return ValueTask.FromResult<IRuntimeRpcServer>(implementation);
            });
        using var process = Process.GetCurrentProcess();
        var ticket = server.IssueLaunchTicket(
            RuntimeClientKind.HeadlessTest,
            process,
            TimeSpan.FromSeconds(30));
        var callbacks = new RecordingRuntimeClient();

        await using var connection = await RuntimeIpcClient.ConnectAsync(
            endpoint,
            RuntimeIpcTestData.AttachRequest(endpoint, ticket),
            callbacks);

        Assert.NotNull(acceptedContext);
        Assert.Equal(RuntimeClientKind.HeadlessTest, acceptedClientKind);
        Assert.Equal(Environment.ProcessId, acceptedContext.Peer.ProcessId);
        Assert.Equal(endpoint.DataRootId, acceptedContext.Endpoint.DataRootId);
        Assert.False(acceptedContext.Peer.IsElevated);
        Assert.Equal(acceptedContext.ConnectionId, connection.AttachResult.ConnectionId);
        Assert.NotEqual("implementation-selected", connection.AttachResult.ConnectionId);
        Assert.Equal(RuntimeProtocol.MaximumMessageBytes, connection.AttachResult.MaximumMessageBytes);

        var snapshot = await connection.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal(implementation!.Snapshot.EngineEpoch, snapshot.EngineEpoch);
        Assert.Equal(implementation.Snapshot.EventCursor, snapshot.EventCursor);
        Assert.Equal(endpoint.DataRootId, snapshot.Identity.DataRootId);
        Assert.Equal(
            SettingsPrepareStatus.NoChanges,
            (await connection.PrepareSettingsAsync(
                new PrepareSettingsRequest(1, new SettingsPatch()),
                CancellationToken.None)).Status);
        Assert.Equal(
            SettingsApplyStatus.Applied,
            (await connection.ApplySettingsAsync(
                new ApplySettingsRequest(Guid.NewGuid(), "prepared"),
                CancellationToken.None)).Status);
        Assert.Equal(
            SettingsOperationState.NotFound,
            (await connection.GetSettingsOperationAsync(
                Guid.NewGuid(),
                CancellationToken.None)).State);
        Assert.Single((await connection.RefreshInputSourcesAsync(CancellationToken.None)).Sources);

        var capture = await connection.BeginInputCaptureAsync(
            new RuntimeCaptureRequest("source-1", "configure"),
            CancellationToken.None);
        Assert.True(capture.Accepted);
        Assert.True((await connection.RenewInputCaptureAsync(
            capture.Lease!.CaptureId,
            CancellationToken.None)).Succeeded);
        Assert.True((await connection.CancelInputCaptureAsync(
            capture.Lease.CaptureId,
            CancellationToken.None)).Succeeded);
        Assert.Equal(
            RuntimeCaptureLookupStatus.Completed,
            (await connection.GetInputCaptureAsync(
                capture.Lease.CaptureId,
                CancellationToken.None)).Status);

        var operationId = Guid.NewGuid();
        Assert.Equal(
            RuntimeCommandStatus.Completed,
            (await connection.ExecuteCommandAsync(
                new RuntimeCommandRequest(operationId, RuntimeCommandKind.ReloadConfiguration),
                CancellationToken.None)).Status);
        Assert.Equal(
            RuntimeCommandOperationState.Completed,
            (await connection.GetCommandOperationAsync(
                operationId,
                CancellationToken.None)).State);

        var globalEvent = new RuntimeEvent(
            implementation.Snapshot.EngineEpoch,
            Sequence: 41,
            RuntimeEventKind.InputSourcesChanged,
            InputSources: new RuntimeInputSourceSnapshot([]));
        var nextGlobalEvent = new RuntimeEvent(
            implementation.Snapshot.EngineEpoch,
            Sequence: 42,
            RuntimeEventKind.RuntimeIdentityChanged,
            Identity: implementation.Snapshot.Identity);
        var inputEvent = new RuntimeConnectionInputEvent(
            implementation.Snapshot.EngineEpoch,
            Sequence: 7,
            RuntimeConnectionInputEventKind.InputObserved,
            Observation: new RuntimeInputObservation(
                5,
                "source-1",
                2,
                new JoystickSnapshot(DateTimeOffset.UtcNow, [], [], []),
                []));
        var commandResult = new RuntimeCommandResult(
            operationId,
            RuntimeCommandKind.ReloadConfiguration,
            RuntimeCommandStatus.Completed);
        await implementation.Client.RuntimeEventAsync(globalEvent, CancellationToken.None);
        await implementation.Client.RuntimeEventAsync(nextGlobalEvent, CancellationToken.None);
        await implementation.Client.RuntimeInputEventAsync(inputEvent, CancellationToken.None);
        await implementation.Client.RuntimeCommandCompletedAsync(commandResult, CancellationToken.None);

        Assert.Collection(
            callbacks.RuntimeEvents,
            observedGlobal =>
            {
                Assert.Equal(globalEvent.EngineEpoch, observedGlobal.EngineEpoch);
                Assert.Equal(globalEvent.Sequence, observedGlobal.Sequence);
                Assert.Equal(globalEvent.Kind, observedGlobal.Kind);
            },
            observedGlobal =>
            {
                Assert.Equal(nextGlobalEvent.EngineEpoch, observedGlobal.EngineEpoch);
                Assert.Equal(nextGlobalEvent.Sequence, observedGlobal.Sequence);
                Assert.Equal(nextGlobalEvent.Kind, observedGlobal.Kind);
            });
        var observedInput = Assert.Single(callbacks.InputEvents);
        Assert.Equal(inputEvent.EngineEpoch, observedInput.EngineEpoch);
        Assert.Equal(inputEvent.Sequence, observedInput.Sequence);
        Assert.Equal(inputEvent.Kind, observedInput.Kind);
        Assert.Equal(commandResult, Assert.Single(callbacks.CommandResults));
    }

    [Fact]
    public async Task LargeSettingsHydrateAcrossAttachResultsEventsAndCommandPaths()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        FakeRuntimeServer? implementation = null;
        await using var server = RuntimeIpcServer.Start(
            endpoint,
            (context, _, client, _, _) =>
            {
                implementation = new FakeRuntimeServer(context.Endpoint, client)
                {
                    Snapshot = RuntimeIpcTestData.LargeSnapshot(context.Endpoint),
                    ReturnSettingsInAllResults = true,
                };
                return ValueTask.FromResult<IRuntimeRpcServer>(implementation);
            });
        var callbacks = new RecordingRuntimeClient();
        var ticket = server.IssueLaunchTicket(RuntimeClientKind.HeadlessTest);

        await using var connection = await RuntimeIpcClient.ConnectAsync(
            endpoint,
            RuntimeIpcTestData.AttachRequest(endpoint, ticket),
            callbacks);

        Assert.Equal(600_000, connection.AttachResult.Snapshot.Settings.Desired.Voice.PinnedTaskLabel.Length);
        Assert.Equal(600_000, (await connection.GetSnapshotAsync(CancellationToken.None))
            .Settings.Desired.Voice.PinnedTaskLabel.Length);
        Assert.Equal(600_000, (await connection.PrepareSettingsAsync(
            new PrepareSettingsRequest(1, new SettingsPatch()),
            CancellationToken.None)).Snapshot.Desired.Voice.PinnedTaskLabel.Length);
        Assert.Equal(600_000, (await connection.ApplySettingsAsync(
            new ApplySettingsRequest(Guid.NewGuid(), "prepared"),
            CancellationToken.None)).Snapshot.Desired.Voice.PinnedTaskLabel.Length);
        Assert.Equal(600_000, (await connection.GetSettingsOperationAsync(
            Guid.NewGuid(),
            CancellationToken.None)).Result!.Snapshot.Desired.Voice.PinnedTaskLabel.Length);
        Assert.Equal(600_000, (await connection.ExecuteCommandAsync(
            new RuntimeCommandRequest(Guid.NewGuid(), RuntimeCommandKind.ReloadConfiguration),
            CancellationToken.None)).Payload!.Settings!.Desired.Voice.PinnedTaskLabel.Length);
        Assert.Equal(600_000, (await connection.GetCommandOperationAsync(
            Guid.NewGuid(),
            CancellationToken.None)).Result!.Payload!.Settings!.Desired.Voice.PinnedTaskLabel.Length);

        var firstEvent = new RuntimeEvent(
            implementation!.Snapshot.EngineEpoch,
            41,
            RuntimeEventKind.SettingsChanged,
            Settings: implementation.Snapshot.Settings);
        var secondEvent = new RuntimeEvent(
            implementation.Snapshot.EngineEpoch,
            42,
            RuntimeEventKind.OperationCompleted,
            Operation: new ApplySettingsResult(
                Guid.NewGuid(),
                SettingsApplyStatus.Applied,
                true,
                true,
                [],
                [],
                implementation.Snapshot.Settings));
        var thirdEvent = new RuntimeEvent(
            implementation.Snapshot.EngineEpoch,
            43,
            RuntimeEventKind.RuntimeIdentityChanged,
            Identity: implementation.Snapshot.Identity);
        var commandResult = new RuntimeCommandResult(
            Guid.NewGuid(),
            RuntimeCommandKind.ReloadConfiguration,
            RuntimeCommandStatus.Completed,
            Payload: new RuntimeCommandPayload(Settings: implementation.Snapshot.Settings));

        await implementation.Client.RuntimeEventAsync(firstEvent, CancellationToken.None);
        await implementation.Client.RuntimeEventAsync(secondEvent, CancellationToken.None);
        await implementation.Client.RuntimeEventAsync(thirdEvent, CancellationToken.None);
        await implementation.Client.RuntimeCommandCompletedAsync(commandResult, CancellationToken.None);

        Assert.Collection(
            callbacks.RuntimeEvents,
            observed => Assert.Equal(600_000, observed.Settings!.Desired.Voice.PinnedTaskLabel.Length),
            observed => Assert.Equal(
                600_000,
                observed.Operation!.Snapshot.Desired.Voice.PinnedTaskLabel.Length),
            observed => Assert.Equal(43, observed.Sequence));
        Assert.Equal(
            600_000,
            Assert.Single(callbacks.CommandResults).Payload!.Settings!.Desired.Voice.PinnedTaskLabel.Length);
    }

    [Fact]
    public async Task LargeReplayCallbacksHydrateInOrderWhileAttachIsStillRunning()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        await using var server = RuntimeIpcServer.Start(
            endpoint,
            (context, _, client, _, _) => ValueTask.FromResult<IRuntimeRpcServer>(
                new FakeRuntimeServer(context.Endpoint, client)
                {
                    Snapshot = RuntimeIpcTestData.LargeSnapshot(context.Endpoint),
                    ReplayEventsDuringAttach = true,
                }));
        var callbacks = new RecordingRuntimeClient();
        var ticket = server.IssueLaunchTicket(RuntimeClientKind.HeadlessTest);

        await using var connection = await RuntimeIpcClient.ConnectAsync(
            endpoint,
            RuntimeIpcTestData.AttachRequest(endpoint, ticket),
            callbacks);

        Assert.Equal(600_000, connection.AttachResult.Snapshot.Settings.Desired.Voice.PinnedTaskLabel.Length);
        Assert.Collection(
            callbacks.RuntimeEvents,
            first =>
            {
                Assert.Equal(1, first.Sequence);
                Assert.Equal(600_000, first.Settings!.Desired.Voice.PinnedTaskLabel.Length);
            },
            second => Assert.Equal(2, second.Sequence));
    }

    [Fact]
    public async Task LargeCallbackCanPullChunksReentrantlyDuringAdmittedRequest()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        await using var server = RuntimeIpcServer.Start(
            endpoint,
            (context, _, client, _, _) => ValueTask.FromResult<IRuntimeRpcServer>(
                new FakeRuntimeServer(context.Endpoint, client)
                {
                    Snapshot = RuntimeIpcTestData.LargeSnapshot(context.Endpoint),
                    EmitLargeEventDuringPrepare = true,
                }),
            new RuntimeIpcServerOptions { MaximumConcurrentRequestsPerConnection = 1 });
        var callbacks = new RecordingRuntimeClient();
        var ticket = server.IssueLaunchTicket(RuntimeClientKind.HeadlessTest);
        await using var connection = await RuntimeIpcClient.ConnectAsync(
            endpoint,
            RuntimeIpcTestData.AttachRequest(endpoint, ticket),
            callbacks);

        var result = await connection.PrepareSettingsAsync(
            new PrepareSettingsRequest(1, new SettingsPatch()),
            CancellationToken.None);

        Assert.Equal(SettingsPrepareStatus.NoChanges, result.Status);
        Assert.Equal(
            600_000,
            Assert.Single(callbacks.RuntimeEvents).Settings!.Desired.Voice.PinnedTaskLabel.Length);
    }

    [Fact]
    public async Task TransferSupportsSettingsPayloadLargerThanSixteenMiB()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        await using var server = RuntimeIpcServer.Start(
            endpoint,
            (context, _, client, _, _) => ValueTask.FromResult<IRuntimeRpcServer>(
                new FakeRuntimeServer(context.Endpoint, client)
                {
                    Snapshot = RuntimeIpcTestData.LargeSnapshot(context.Endpoint, 8_500_000),
                }));
        var ticket = server.IssueLaunchTicket(RuntimeClientKind.HeadlessTest);

        await using var connection = await RuntimeIpcClient.ConnectAsync(
            endpoint,
            RuntimeIpcTestData.AttachRequest(endpoint, ticket),
            new RecordingRuntimeClient());

        Assert.Equal(8_500_000, connection.AttachResult.Snapshot.Settings.Desired.Voice.PinnedTaskLabel.Length);
    }

    [Fact]
    public async Task LargePrepareUploadsBeforeCallingSettingsAuthority()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        FakeRuntimeServer? implementation = null;
        await using var server = RuntimeIpcServer.Start(
            endpoint,
            (context, _, client, _, _) =>
            {
                implementation = new FakeRuntimeServer(context.Endpoint, client);
                return ValueTask.FromResult<IRuntimeRpcServer>(implementation);
            });
        var ticket = server.IssueLaunchTicket(RuntimeClientKind.HeadlessTest);
        await using var connection = await RuntimeIpcClient.ConnectAsync(
            endpoint,
            RuntimeIpcTestData.AttachRequest(endpoint, ticket),
            new RecordingRuntimeClient());
        var label = new string('p', 1_100_000);
        var request = new PrepareSettingsRequest(
            connection.AttachResult.Snapshot.Settings.Revision,
            new SettingsPatch(Voice: connection.AttachResult.Snapshot.Settings.Desired.Voice with
            {
                PinnedTaskLabel = label,
            }));

        var result = await connection.PrepareSettingsAsync(request, CancellationToken.None);

        Assert.Equal(SettingsPrepareStatus.NoChanges, result.Status);
        Assert.NotNull(implementation!.LastPrepareRequest);
        Assert.Equal(label, implementation.LastPrepareRequest.Patch.Voice!.PinnedTaskLabel);
    }

    [Fact]
    public async Task LegacyMinorGetsExplicitOversizeErrorsAndConnectionRemainsUsable()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        FakeRuntimeServer? implementation = null;
        await using var server = RuntimeIpcServer.Start(
            endpoint,
            (context, _, client, _, _) =>
            {
                implementation = new FakeRuntimeServer(context.Endpoint, client)
                {
                    Snapshot = RuntimeIpcTestData.LargeSnapshot(context.Endpoint),
                    AttachSnapshot = RuntimeIpcTestData.Snapshot(context.Endpoint),
                };
                return ValueTask.FromResult<IRuntimeRpcServer>(implementation);
            });
        var ticket = server.IssueLaunchTicket(RuntimeClientKind.HeadlessTest);
        var request = RuntimeIpcTestData.AttachRequest(endpoint, ticket) with { ProtocolMinor = 1 };
        await using var connection = await RuntimeIpcClient.ConnectAsync(
            endpoint,
            request,
            new RecordingRuntimeClient());

        var responseError = await Assert.ThrowsAsync<RemoteInvocationException>(() =>
            connection.GetSnapshotAsync(CancellationToken.None));
        Assert.Equal(RuntimeIpcErrorCodes.MessageTooLarge, responseError.ErrorCode);
        Assert.Contains("upgrade to minor 2", responseError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single((await connection.RefreshInputSourcesAsync(CancellationToken.None)).Sources);

        var prepareError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connection.PrepareSettingsAsync(
                new PrepareSettingsRequest(
                    1,
                    new SettingsPatch(Voice: connection.AttachResult.Snapshot.Settings.Desired.Voice with
                    {
                        PinnedTaskLabel = new string('p', 1_100_000),
                    })),
                CancellationToken.None));
        Assert.Contains("upgrade to minor 2", prepareError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(implementation!.LastPrepareRequest);
        Assert.Single((await connection.RefreshInputSourcesAsync(CancellationToken.None)).Sources);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task LegacyMinorGetsExplicitOversizeAttachError(int protocolMinor)
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        await using var server = RuntimeIpcServer.Start(
            endpoint,
            (context, _, client, _, _) => ValueTask.FromResult<IRuntimeRpcServer>(
                new FakeRuntimeServer(context.Endpoint, client)
                {
                    Snapshot = RuntimeIpcTestData.LargeSnapshot(context.Endpoint),
                }));
        var ticket = server.IssueLaunchTicket(RuntimeClientKind.HeadlessTest);
        var request = RuntimeIpcTestData.AttachRequest(endpoint, ticket) with
        {
            ProtocolMinor = protocolMinor,
        };

        var error = await Assert.ThrowsAsync<RemoteInvocationException>(() =>
            RuntimeIpcClient.ConnectAsync(endpoint, request, new RecordingRuntimeClient()));

        Assert.Equal(RuntimeIpcErrorCodes.MessageTooLarge, error.ErrorCode);
        Assert.Contains("upgrade to minor 2", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LegacyOversizeCallbackFailsExplicitlyWithoutDroppingConnection()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        FakeRuntimeServer? implementation = null;
        await using var server = RuntimeIpcServer.Start(
            endpoint,
            (context, _, client, _, _) =>
            {
                implementation = new FakeRuntimeServer(context.Endpoint, client)
                {
                    Snapshot = RuntimeIpcTestData.LargeSnapshot(context.Endpoint),
                    AttachSnapshot = RuntimeIpcTestData.Snapshot(context.Endpoint),
                };
                return ValueTask.FromResult<IRuntimeRpcServer>(implementation);
            });
        var ticket = server.IssueLaunchTicket(RuntimeClientKind.HeadlessTest);
        await using var connection = await RuntimeIpcClient.ConnectAsync(
            endpoint,
            RuntimeIpcTestData.AttachRequest(endpoint, ticket) with { ProtocolMinor = 1 },
            new RecordingRuntimeClient());
        var largeEvent = new RuntimeEvent(
            implementation!.Snapshot.EngineEpoch,
            1,
            RuntimeEventKind.SettingsChanged,
            Settings: implementation.Snapshot.Settings);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            implementation.Client.RuntimeEventAsync(largeEvent, CancellationToken.None));

        Assert.Contains("upgrade to minor 2", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single((await connection.RefreshInputSourcesAsync(CancellationToken.None)).Sources);
    }

    [Fact]
    public async Task TransferMethodsRejectPreAttachAndLegacyCallersWithoutConsumingAuthorityOrTicket()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        FakeRuntimeServer? implementation = null;
        await using var server = RuntimeIpcServer.Start(
            endpoint,
            (context, _, client, _, _) =>
            {
                implementation = new FakeRuntimeServer(context.Endpoint, client);
                return ValueTask.FromResult<IRuntimeRpcServer>(implementation);
            });
        var ticket = server.IssueLaunchTicket(RuntimeClientKind.HeadlessTest);

        await using (var pipe = await ConnectRawAsync(endpoint))
        {
            var (rpc, bounded) = RuntimeJsonRpc.Create(pipe);
            await using (bounded.ConfigureAwait(false))
            using (rpc)
            {
                rpc.StartListening();
                await Assert.ThrowsAsync<RemoteInvocationException>(() =>
                    rpc.InvokeWithCancellationAsync<SettingsTransferChunk>(
                        SettingsTransferMethods.ReadServer,
                        [Guid.NewGuid().ToString("N"), 0L],
                        CancellationToken.None));
            }
        }

        await using (var pipe = await ConnectRawAsync(endpoint))
        {
            var (rpc, bounded) = RuntimeJsonRpc.Create(pipe);
            await using (bounded.ConfigureAwait(false))
            using (rpc)
            {
                rpc.StartListening();
                var legacyRequest = RuntimeIpcTestData.AttachRequest(endpoint, ticket) with { ProtocolMinor = 1 };
                _ = await rpc.InvokeWithCancellationAsync<RuntimeAttachResult>(
                    RuntimeRpcMethods.Attach,
                    [legacyRequest],
                    CancellationToken.None);
                var reference = new SettingsTransferReference(
                    SettingsTransferProtocol.Discriminator,
                    Guid.NewGuid().ToString("N"),
                    10,
                    new string('0', 64),
                    (DateTimeOffset.UtcNow + SettingsTransferProtocol.TransferLifetime)
                    .ToUnixTimeMilliseconds());
                await Assert.ThrowsAsync<RemoteInvocationException>(() =>
                    rpc.InvokeWithCancellationAsync<object>(
                        SettingsTransferMethods.PrepareTransferred,
                        [reference],
                        CancellationToken.None));
                await Assert.ThrowsAsync<RemoteInvocationException>(() =>
                    rpc.InvokeWithCancellationAsync<SettingsTransferChunk>(
                        SettingsTransferMethods.ReadServer,
                        [reference.TransferId, 0L],
                        CancellationToken.None));
                Assert.Null(implementation!.LastPrepareRequest);
                Assert.Single((await rpc.InvokeWithCancellationAsync<RuntimeInputSnapshot>(
                    RuntimeRpcMethods.RefreshInputSources,
                    [],
                    CancellationToken.None)).Sources);
            }
        }
    }

    [Fact]
    public async Task TransferredPrepareRejectsHashLengthOrderAndReplayBeforeAuthorityCall()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        FakeRuntimeServer? implementation = null;
        await using var server = RuntimeIpcServer.Start(
            endpoint,
            (context, _, client, _, _) =>
            {
                implementation = new FakeRuntimeServer(context.Endpoint, client);
                return ValueTask.FromResult<IRuntimeRpcServer>(implementation);
            });
        var ticket = server.IssueLaunchTicket(RuntimeClientKind.HeadlessTest);
        var requestBytes = SerializeBytes(new PrepareSettingsRequest(1, new SettingsPatch()));
        var transferSource = new TransferSourceTarget();

        await using var pipe = await ConnectRawAsync(endpoint);
        var (rpc, bounded) = RuntimeJsonRpc.Create(pipe);
        await using (bounded.ConfigureAwait(false))
        using (rpc)
        {
            rpc.AddLocalRpcTarget(transferSource);
            rpc.StartListening();
            _ = await rpc.InvokeWithCancellationAsync<RuntimeAttachResult>(
                RuntimeRpcMethods.Attach,
                [RuntimeIpcTestData.AttachRequest(endpoint, ticket)],
                CancellationToken.None);

            var valid = transferSource.Add(requestBytes);
            _ = await rpc.InvokeWithCancellationAsync<object>(
                SettingsTransferMethods.PrepareTransferred,
                [valid],
                CancellationToken.None);
            Assert.Equal(1, implementation!.PrepareCallCount);

            await Assert.ThrowsAsync<RemoteInvocationException>(() =>
                rpc.InvokeWithCancellationAsync<object>(
                    SettingsTransferMethods.PrepareTransferred,
                    [valid],
                    CancellationToken.None));
            Assert.Equal(1, implementation.PrepareCallCount);

            var badHash = transferSource.Add(requestBytes) with { Sha256 = new string('0', 64) };
            await Assert.ThrowsAsync<RemoteInvocationException>(() =>
                rpc.InvokeWithCancellationAsync<object>(
                    SettingsTransferMethods.PrepareTransferred,
                    [badHash],
                    CancellationToken.None));

            var badLength = transferSource.Add(requestBytes) with { Length = requestBytes.Length + 1 };
            await Assert.ThrowsAsync<RemoteInvocationException>(() =>
                rpc.InvokeWithCancellationAsync<object>(
                    SettingsTransferMethods.PrepareTransferred,
                    [badLength],
                    CancellationToken.None));

            var badOrder = transferSource.Add(requestBytes);
            transferSource.WrongOffsetId = badOrder.TransferId;
            await Assert.ThrowsAsync<RemoteInvocationException>(() =>
                rpc.InvokeWithCancellationAsync<object>(
                    SettingsTransferMethods.PrepareTransferred,
                    [badOrder],
                    CancellationToken.None));
            Assert.Equal(1, implementation.PrepareCallCount);
        }
    }

    [Fact]
    public async Task ConcurrentClientsUseIndependentBoundedPipeInstances()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        var connectionIds = new ConcurrentQueue<string>();
        await using var server = RuntimeIpcServer.Start(
            endpoint,
            (context, _, client, _, _) =>
            {
                connectionIds.Enqueue(context.ConnectionId);
                return ValueTask.FromResult<IRuntimeRpcServer>(
                    new FakeRuntimeServer(context.Endpoint, client));
            });
        var firstTicket = server.IssueLaunchTicket(RuntimeClientKind.HeadlessTest);
        var secondTicket = server.IssueLaunchTicket(RuntimeClientKind.HeadlessTest);

        await using var first = await RuntimeIpcClient.ConnectAsync(
            endpoint,
            RuntimeIpcTestData.AttachRequest(endpoint, firstTicket),
            new RecordingRuntimeClient());
        await using var second = await RuntimeIpcClient.ConnectAsync(
            endpoint,
            RuntimeIpcTestData.AttachRequest(endpoint, secondTicket),
            new RecordingRuntimeClient());

        Assert.Equal(2, connectionIds.Count);
        Assert.NotEqual(first.AttachResult.ConnectionId, second.AttachResult.ConnectionId);
        Assert.Equal(
            first.AttachResult.Snapshot.EngineEpoch,
            (await first.GetSnapshotAsync(CancellationToken.None)).EngineEpoch);
        Assert.Equal(
            second.AttachResult.Snapshot.EngineEpoch,
            (await second.GetSnapshotAsync(CancellationToken.None)).EngineEpoch);
    }

    [Fact]
    public async Task NewerSameMajorClientNegotiatesToServerMinorVersion()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        FakeRuntimeServer? implementation = null;
        await using var server = RuntimeIpcServer.Start(
            endpoint,
            (context, _, client, _, _) => ValueTask.FromResult<IRuntimeRpcServer>(
                implementation = new FakeRuntimeServer(context.Endpoint, client)));
        var ticket = server.IssueLaunchTicket(RuntimeClientKind.HeadlessTest);
        var request = RuntimeIpcTestData.AttachRequest(endpoint, ticket) with
        {
            ProtocolMinor = RuntimeProtocol.MinorVersion + 1,
        };

        await using var connection = await RuntimeIpcClient.ConnectAsync(
            endpoint,
            request,
            new RecordingRuntimeClient());

        Assert.Equal(RuntimeProtocol.MajorVersion, connection.AttachResult.ProtocolMajor);
        Assert.Equal(RuntimeProtocol.MinorVersion, connection.AttachResult.ProtocolMinor);
        Assert.Equal(RuntimeProtocol.MinorVersion, implementation!.AttachedRequest!.ProtocolMinor);
    }

    [Fact]
    public async Task FactoryAbortClosesOnlyItsPhysicalConnection()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        var aborts = new ConcurrentDictionary<string, Action<Exception?>>();
        var implementations = new ConcurrentDictionary<string, FakeRuntimeServer>();
        await using var server = RuntimeIpcServer.Start(
            endpoint,
            (context, _, client, abortConnection, lifetime) =>
            {
                var implementation = new FakeRuntimeServer(context.Endpoint, client);
                lifetime.Register(() => implementation.ConnectionCancelled.TrySetResult());
                aborts[context.ConnectionId] = abortConnection;
                implementations[context.ConnectionId] = implementation;
                return ValueTask.FromResult<IRuntimeRpcServer>(implementation);
            });
        var firstTicket = server.IssueLaunchTicket(RuntimeClientKind.HeadlessTest);
        var secondTicket = server.IssueLaunchTicket(RuntimeClientKind.HeadlessTest);

        await using var first = await RuntimeIpcClient.ConnectAsync(
            endpoint,
            RuntimeIpcTestData.AttachRequest(endpoint, firstTicket),
            new RecordingRuntimeClient());
        await using var second = await RuntimeIpcClient.ConnectAsync(
            endpoint,
            RuntimeIpcTestData.AttachRequest(endpoint, secondTicket),
            new RecordingRuntimeClient());

        var firstId = first.AttachResult.ConnectionId;
        var secondId = second.AttachResult.ConnectionId;
        aborts[firstId](new InvalidOperationException("Synthetic callback dispatcher failure."));
        aborts[firstId](new InvalidOperationException("Repeated abort must be harmless."));

        await implementations[firstId].ConnectionCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await implementations[firstId].Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single((await second.RefreshInputSourcesAsync(CancellationToken.None)).Sources);
        Assert.False(implementations[secondId].ConnectionCancelled.Task.IsCompleted);
    }

    [Fact]
    public async Task NonAttachMethodIsRejectedBeforeFactoryCreation()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        var factoryCalls = 0;
        await using var server = RuntimeIpcServer.Start(
            endpoint,
            (_, _, _, _, _) =>
            {
                Interlocked.Increment(ref factoryCalls);
                throw new InvalidOperationException("The factory must stay gated.");
            });

        await using var pipe = await ConnectRawAsync(endpoint);
        var (rpc, bounded) = RuntimeJsonRpc.Create(pipe);
        await using (bounded.ConfigureAwait(false))
        using (rpc)
        {
            rpc.StartListening();

            var error = await Assert.ThrowsAsync<RemoteInvocationException>(() =>
                rpc.InvokeWithCancellationAsync<RuntimeSnapshot>(
                    RuntimeRpcMethods.GetSnapshot,
                    [],
                    CancellationToken.None));

            Assert.Contains(RuntimeRpcMethods.Attach, error.Message, StringComparison.Ordinal);
            Assert.Equal(0, Volatile.Read(ref factoryCalls));
        }
    }

    [Fact]
    public async Task TicketCannotBeReplayedAfterDisconnect()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        var factoryCalls = 0;
        await using var server = RuntimeIpcServer.Start(
            endpoint,
            (context, _, client, _, _) =>
            {
                Interlocked.Increment(ref factoryCalls);
                return ValueTask.FromResult<IRuntimeRpcServer>(
                    new FakeRuntimeServer(context.Endpoint, client));
            });
        var ticket = server.IssueLaunchTicket(RuntimeClientKind.HeadlessTest);
        var request = RuntimeIpcTestData.AttachRequest(endpoint, ticket);

        await using (var first = await RuntimeIpcClient.ConnectAsync(
                         endpoint,
                         request,
                         new RecordingRuntimeClient()))
        {
            Assert.Equal(1, Volatile.Read(ref factoryCalls));
        }

        await Assert.ThrowsAsync<RemoteInvocationException>(() =>
            RuntimeIpcClient.ConnectAsync(
                endpoint,
                request,
                new RecordingRuntimeClient()));
        Assert.Equal(1, Volatile.Read(ref factoryCalls));
    }

    [Fact]
    public async Task MismatchedRootDoesNotConsumeTicketAndValidAttachStillSucceeds()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        await using var server = RuntimeIpcServer.Start(
            endpoint,
            (context, _, client, _, _) => ValueTask.FromResult<IRuntimeRpcServer>(
                new FakeRuntimeServer(context.Endpoint, client)));
        var ticket = server.IssueLaunchTicket(RuntimeClientKind.HeadlessTest);

        await using (var pipe = await ConnectRawAsync(endpoint))
        {
            var (rpc, bounded) = RuntimeJsonRpc.Create(pipe);
            await using (bounded.ConfigureAwait(false))
            using (rpc)
            {
                rpc.StartListening();
                var request = RuntimeIpcTestData.AttachRequest(endpoint, ticket) with
                {
                    DataRootId = "root-v1-wrong",
                };
                await Assert.ThrowsAsync<RemoteInvocationException>(() =>
                    rpc.InvokeWithCancellationAsync<RuntimeAttachResult>(
                        RuntimeRpcMethods.Attach,
                        [request],
                        CancellationToken.None));
            }
        }

        await using var connection = await RuntimeIpcClient.ConnectAsync(
            endpoint,
            RuntimeIpcTestData.AttachRequest(endpoint, ticket),
            new RecordingRuntimeClient());
        Assert.Equal(endpoint.DataRootId, connection.AttachResult.Snapshot.Identity.DataRootId);
    }

    [Fact]
    public async Task RequestSaturationReturnsExplicitErrorAndCancellationReleasesAdmission()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        FakeRuntimeServer? implementation = null;
        await using var server = RuntimeIpcServer.Start(
            endpoint,
            (context, _, client, _, _) =>
            {
                implementation = new FakeRuntimeServer(context.Endpoint, client)
                {
                    BlockSnapshots = true,
                };
                return ValueTask.FromResult<IRuntimeRpcServer>(implementation);
            },
            new RuntimeIpcServerOptions
            {
                MaximumConcurrentRequestsPerConnection = 1,
            });
        var ticket = server.IssueLaunchTicket(RuntimeClientKind.HeadlessTest);
        await using var connection = await RuntimeIpcClient.ConnectAsync(
            endpoint,
            RuntimeIpcTestData.AttachRequest(endpoint, ticket),
            new RecordingRuntimeClient());
        using var cancellation = new CancellationTokenSource();

        var request = connection.GetSnapshotAsync(cancellation.Token);
        await implementation!.SnapshotStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var overload = await Assert.ThrowsAsync<RemoteInvocationException>(() =>
            connection.RefreshInputSourcesAsync(CancellationToken.None));
        Assert.Equal(RuntimeIpcErrorCodes.RequestOverloaded, overload.ErrorCode);
        Assert.False(request.IsCompleted);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await implementation.SnapshotCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single((await connection.RefreshInputSourcesAsync(CancellationToken.None)).Sources);
    }

    [Fact]
    public async Task DisconnectCancelsLifetimeAndDisposesConnectionImplementation()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        FakeRuntimeServer? implementation = null;
        await using var server = RuntimeIpcServer.Start(
            endpoint,
            (context, _, client, _, lifetime) =>
            {
                implementation = new FakeRuntimeServer(context.Endpoint, client);
                lifetime.Register(() => implementation.ConnectionCancelled.TrySetResult());
                return ValueTask.FromResult<IRuntimeRpcServer>(implementation);
            });
        var ticket = server.IssueLaunchTicket(RuntimeClientKind.HeadlessTest);
        var connection = await RuntimeIpcClient.ConnectAsync(
            endpoint,
            RuntimeIpcTestData.AttachRequest(endpoint, ticket),
            new RecordingRuntimeClient());

        await connection.DisposeAsync();

        await implementation!.ConnectionCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await implementation.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DisconnectDisposesOutstandingConnectionBoundTransferState()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        FakeRuntimeServer? implementation = null;
        await using var server = RuntimeIpcServer.Start(
            endpoint,
            (context, _, client, _, _) =>
            {
                implementation = new FakeRuntimeServer(context.Endpoint, client);
                return ValueTask.FromResult<IRuntimeRpcServer>(implementation);
            });
        var ticket = server.IssueLaunchTicket(RuntimeClientKind.HeadlessTest);
        var connection = await RuntimeIpcClient.ConnectAsync(
            endpoint,
            RuntimeIpcTestData.AttachRequest(endpoint, ticket),
            new RecordingRuntimeClient());
        var remoteClient = Assert.IsType<RemoteRuntimeRpcClient>(implementation!.Client);
        var reference = remoteClient.OutboundTransfers.Add(
            SettingsTransferSerializer.FromBytes(RandomNumberGenerator.GetBytes(1024)));

        await connection.DisposeAsync();
        await implementation.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Throws<ObjectDisposedException>(() =>
            remoteClient.OutboundTransfers.Read(reference.TransferId, 0));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private RuntimeIpcEndpoint Endpoint() => RuntimeIpcEndpoint.CreateSynthetic(
        _root,
        $"joydex-ipc-test-{Guid.NewGuid():N}");

    private static async Task<NamedPipeClientStream> ConnectRawAsync(RuntimeIpcEndpoint endpoint)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            endpoint.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await pipe.ConnectAsync(timeout.Token);
        _ = WindowsPipePeerVerifier.VerifyServer(pipe, endpoint.SessionId);
        return pipe;
    }

    private sealed class RecordingRuntimeClient : IRuntimeRpcClient
    {
        public ConcurrentQueue<RuntimeEvent> RuntimeEvents { get; } = new();

        public ConcurrentQueue<RuntimeConnectionInputEvent> InputEvents { get; } = new();

        public ConcurrentQueue<RuntimeCommandResult> CommandResults { get; } = new();

        public Task RuntimeEventAsync(RuntimeEvent runtimeEvent, CancellationToken cancellationToken)
        {
            RuntimeEvents.Enqueue(runtimeEvent);
            return Task.CompletedTask;
        }

        public Task RuntimeInputEventAsync(
            RuntimeConnectionInputEvent inputEvent,
            CancellationToken cancellationToken)
        {
            InputEvents.Enqueue(inputEvent);
            return Task.CompletedTask;
        }

        public Task RuntimeCommandCompletedAsync(
            RuntimeCommandResult result,
            CancellationToken cancellationToken)
        {
            CommandResults.Enqueue(result);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRuntimeServer(
        RuntimeIpcEndpoint endpoint,
        IRuntimeRpcClient client) : IRuntimeRpcServer, IAsyncDisposable
    {
        private readonly RuntimeCaptureLease _capture = new(
            Guid.NewGuid(),
            "source-1",
            "configure",
            SourceGeneration: 2,
            DateTimeOffset.UtcNow.AddSeconds(30),
            InputCaptureStatus.Active,
            Revision: 1);

        public IRuntimeRpcClient Client { get; } = client;

        public RuntimeSnapshot Snapshot { get; init; } = RuntimeIpcTestData.Snapshot(endpoint, cursor: 40);

        public RuntimeSnapshot? AttachSnapshot { get; init; }

        public bool ReturnSettingsInAllResults { get; init; }

        public bool ReplayEventsDuringAttach { get; init; }

        public bool EmitLargeEventDuringPrepare { get; init; }

        public PrepareSettingsRequest? LastPrepareRequest { get; private set; }

        public int PrepareCallCount { get; private set; }

        public bool BlockSnapshots { get; init; }

        public RuntimeAttachRequest? AttachedRequest { get; private set; }

        public TaskCompletionSource SnapshotStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SnapshotCancelled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ConnectionCancelled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Disposed { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<RuntimeAttachResult> AttachAsync(
            RuntimeAttachRequest request,
            CancellationToken cancellationToken)
        {
            AttachedRequest = request;
            if (ReplayEventsDuringAttach)
            {
                await Client.RuntimeEventAsync(
                    new RuntimeEvent(
                        Snapshot.EngineEpoch,
                        1,
                        RuntimeEventKind.SettingsChanged,
                        Settings: Snapshot.Settings),
                    cancellationToken);
                await Client.RuntimeEventAsync(
                    new RuntimeEvent(
                        Snapshot.EngineEpoch,
                        2,
                        RuntimeEventKind.RuntimeIdentityChanged,
                        Identity: Snapshot.Identity),
                    cancellationToken);
            }
            return new RuntimeAttachResult(
                "implementation-selected",
                request.ProtocolMajor,
                request.ProtocolMinor,
                RuntimeProtocol.Capabilities,
                MaximumMessageBytes: 12,
                ResynchronizationRequired: false,
                AttachSnapshot ?? Snapshot);
        }

        public async Task<RuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            if (!BlockSnapshots)
            {
                return Snapshot;
            }
            SnapshotStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The snapshot wait unexpectedly completed.");
            }
            catch (OperationCanceledException)
            {
                SnapshotCancelled.TrySetResult();
                throw;
            }
        }

        public async Task<PrepareSettingsResult> PrepareSettingsAsync(
            PrepareSettingsRequest request,
            CancellationToken cancellationToken)
        {
            LastPrepareRequest = request;
            PrepareCallCount++;
            if (EmitLargeEventDuringPrepare)
            {
                await Client.RuntimeEventAsync(
                    new RuntimeEvent(
                        Snapshot.EngineEpoch,
                        10,
                        RuntimeEventKind.SettingsChanged,
                        Settings: Snapshot.Settings),
                    cancellationToken);
            }
            return new PrepareSettingsResult(
                SettingsPrepareStatus.NoChanges,
                null,
                null,
                null,
                [],
                [],
                Snapshot.Settings);
        }

        public Task<ApplySettingsResult> ApplySettingsAsync(
            ApplySettingsRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ApplySettingsResult(
                request.OperationId,
                SettingsApplyStatus.Applied,
                DesiredStateCommitted: true,
                CanCloseSettings: true,
                [],
                [],
                Snapshot.Settings));

        public Task<SettingsOperationResult> GetSettingsOperationAsync(
            Guid operationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(ReturnSettingsInAllResults
                ? new SettingsOperationResult(
                    operationId,
                    SettingsOperationState.Completed,
                    new ApplySettingsResult(
                        operationId,
                        SettingsApplyStatus.Applied,
                        true,
                        true,
                        [],
                        [],
                        Snapshot.Settings))
                : new SettingsOperationResult(operationId, SettingsOperationState.NotFound));

        public Task<RuntimeInputSnapshot> RefreshInputSourcesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new RuntimeInputSnapshot(
                [new RuntimeInputSource("source-1", "Test source", null, null, null, 2, true)],
                [_capture]));

        public Task<RuntimeCaptureStartResult> BeginInputCaptureAsync(
            RuntimeCaptureRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RuntimeCaptureStartResult(true, _capture, null));

        public Task<RuntimeCaptureCommandResult> RenewInputCaptureAsync(
            Guid captureId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RuntimeCaptureCommandResult(true, _capture, null));

        public Task<RuntimeCaptureCommandResult> CancelInputCaptureAsync(
            Guid captureId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RuntimeCaptureCommandResult(true, _capture, null));

        public Task<RuntimeCaptureLookupResult> GetInputCaptureAsync(
            Guid captureId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RuntimeCaptureLookupResult(
                RuntimeCaptureLookupStatus.Completed,
                new RuntimeCaptureUpdate(_capture)));

        public Task<RuntimeCommandResult> ExecuteCommandAsync(
            RuntimeCommandRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RuntimeCommandResult(
                request.OperationId,
                request.Kind,
                RuntimeCommandStatus.Completed,
                Payload: ReturnSettingsInAllResults
                    ? new RuntimeCommandPayload(Settings: Snapshot.Settings)
                    : null));

        public Task<RuntimeCommandOperationResult> GetCommandOperationAsync(
            Guid operationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RuntimeCommandOperationResult(
                operationId,
                RuntimeCommandOperationState.Completed,
                new RuntimeCommandResult(
                    operationId,
                    RuntimeCommandKind.ReloadConfiguration,
                    RuntimeCommandStatus.Completed,
                    Payload: ReturnSettingsInAllResults
                        ? new RuntimeCommandPayload(Settings: Snapshot.Settings)
                        : null)));

        public ValueTask DisposeAsync()
        {
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TransferSourceTarget
    {
        private readonly ConcurrentDictionary<string, byte[]> _payloads = new(StringComparer.Ordinal);

        public string? WrongOffsetId { get; set; }

        public SettingsTransferReference Add(byte[] payload)
        {
            var id = Guid.NewGuid().ToString("N");
            _payloads[id] = payload;
            return new SettingsTransferReference(
                SettingsTransferProtocol.Discriminator,
                id,
                payload.Length,
                Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
                (DateTimeOffset.UtcNow + SettingsTransferProtocol.TransferLifetime)
                .ToUnixTimeMilliseconds());
        }

        [JsonRpcMethod(SettingsTransferMethods.ReadClient)]
        public SettingsTransferChunk Read(string transferId, long offset)
        {
            var payload = _payloads[transferId];
            var count = (int)Math.Min(SettingsTransferProtocol.ChunkBytes, payload.Length - offset);
            return new SettingsTransferChunk(
                transferId,
                string.Equals(transferId, WrongOffsetId, StringComparison.Ordinal) ? offset + 1 : offset,
                payload.AsSpan(checked((int)offset), count).ToArray(),
                offset + count == payload.Length);
        }

        [JsonRpcMethod(SettingsTransferMethods.DiscardClient)]
        public void Discard(string transferId) => _payloads.TryRemove(transferId, out _);
    }

    private static byte[] SerializeBytes(object value)
    {
        using var payload = SettingsTransferSerializer.Serialize(value);
        var bytes = new byte[checked((int)payload.Length)];
        payload.Stream.ReadExactly(bytes);
        return bytes;
    }
}
