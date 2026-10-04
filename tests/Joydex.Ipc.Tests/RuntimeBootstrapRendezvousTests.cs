using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using Joydex.Contracts;
using StreamJsonRpc;

namespace Joydex.Ipc.Tests;

public sealed class RuntimeBootstrapRendezvousTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"joydex-bootstrap-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task AdmittedTicketClaimsExactPeerAndDisconnectAllowsReplacement()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        var verifier = new FakeImageVerifier();
        RuntimeBootstrapRendezvousServer? rendezvous = null;
        RuntimeIpcConnectionContext? observedContext = null;
        await using var runtime = RuntimeIpcServer.Start(
            endpoint,
            (context, kind, _, _, lifetime) =>
            {
                observedContext = context;
                var lease = rendezvous!.ClaimTrayConnection(context, kind, lifetime);
                return ValueTask.FromResult<IRuntimeRpcServer>(new AttachedServer(endpoint, lease));
            });
        await using (rendezvous = StartRendezvous(runtime, endpoint, verifier))
        {
            var admission = await RequestAsync(endpoint, verifier);
            Assert.Equal(RuntimeBootstrapRendezvousStatus.Admitted, admission.Status);
            Assert.NotNull(admission.LaunchTicket);

            var competing = await RequestAsync(endpoint, verifier);
            Assert.Equal(RuntimeBootstrapRendezvousStatus.TrayAlreadyReserved, competing.Status);
            Assert.Null(competing.LaunchTicket);

            var request = TrayAttachRequest(endpoint, admission.LaunchTicket!);
            var connection = await RuntimeIpcClient.ConnectAsync(
                endpoint,
                request,
                new NoOpRuntimeClient());

            Assert.Equal(Environment.ProcessId, observedContext!.Peer.ProcessId);
            Assert.False(string.IsNullOrWhiteSpace(
                observedContext.AuthenticatedLaunchTicketFingerprint));
            Assert.True(rendezvous.HasTrayReservation);

            await connection.DisposeAsync();
            await WaitUntilAsync(() => !rendezvous.HasTrayReservation);

            var replacement = await RequestAsync(endpoint, verifier);
            Assert.Equal(RuntimeBootstrapRendezvousStatus.Admitted, replacement.Status);
        }
    }

    [Fact]
    public async Task WrongRootRoleAndProtocolAreRejectedWithoutReservation()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        var verifier = new FakeImageVerifier();
        await using var runtime = RuntimeIpcServer.Start(
            endpoint,
            RejectUnexpectedRuntimeConnection);
        await using var rendezvous = StartRendezvous(runtime, endpoint, verifier);

        var requests = new[]
        {
            new RuntimeBootstrapWireRequest(1, "wrong-root", RuntimeClientKind.Tray.ToString()),
            new RuntimeBootstrapWireRequest(1, endpoint.DataRootId, RuntimeClientKind.Settings.ToString()),
            new RuntimeBootstrapWireRequest(1, endpoint.DataRootId, RuntimeClientKind.HeadlessTest.ToString()),
            new RuntimeBootstrapWireRequest(2, endpoint.DataRootId, RuntimeClientKind.Tray.ToString()),
        };

        foreach (var request in requests)
        {
            await Assert.ThrowsAsync<RuntimeIpcAuthenticationException>(() =>
                RequestAsync(endpoint, verifier, request));
            Assert.False(rendezvous.HasTrayReservation);
        }
    }

    [Fact]
    public async Task ImageChecksAndFrozenPublishRootAreEnforcedBeforeAdmission()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        await using var runtime = RuntimeIpcServer.Start(
            endpoint,
            RejectUnexpectedRuntimeConnection);

        var wrongRootVerifier = new FakeImageVerifier();
        Assert.Throws<InvalidOperationException>(() =>
            RuntimeBootstrapRendezvousServer.StartForTest(
                runtime,
                endpoint,
                Path.Combine(_root, "app", "Joydex.App.exe"),
                Path.Combine(_root, "host", "Joydex.RuntimeHost.exe"),
                options: null,
                wrongRootVerifier));

        var elevatedHostVerifier = new FakeImageVerifier { RejectCurrentHost = true };
        Assert.Throws<RuntimeIpcAuthenticationException>(() =>
            StartRendezvous(runtime, endpoint, elevatedHostVerifier));

        var serverVerifier = new FakeImageVerifier { RejectAppPeer = true };
        await using (var rendezvous = StartRendezvous(runtime, endpoint, serverVerifier))
        {
            await Assert.ThrowsAsync<RuntimeIpcAuthenticationException>(() =>
                RequestAsync(endpoint, new FakeImageVerifier()));
            Assert.False(rendezvous.HasTrayReservation);
        }

        var acceptingVerifier = new FakeImageVerifier();
        await using (var rendezvous = StartRendezvous(runtime, endpoint, acceptingVerifier))
        {
            var rejectingClientVerifier = new FakeImageVerifier { RejectHostPeer = true };
            await Assert.ThrowsAsync<RuntimeIpcAuthenticationException>(() =>
                RequestAsync(endpoint, rejectingClientVerifier));
            Assert.False(rendezvous.HasTrayReservation);
        }
    }

    [Fact]
    public async Task CompetingAdmissionIsReleasedAfterBoundedExpiry()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        var verifier = new FakeImageVerifier();
        var options = new RuntimeBootstrapRendezvousOptions
        {
            TicketLifetime = TimeSpan.FromSeconds(1),
            ClaimRaceGrace = TimeSpan.FromSeconds(1),
        };
        await using var runtime = RuntimeIpcServer.Start(
            endpoint,
            RejectUnexpectedRuntimeConnection,
            new RuntimeIpcServerOptions
            {
                DefaultTicketLifetime = TimeSpan.FromSeconds(1),
            });
        await using var rendezvous = StartRendezvous(runtime, endpoint, verifier, options);

        Assert.Equal(
            RuntimeBootstrapRendezvousStatus.Admitted,
            (await RequestAsync(endpoint, verifier)).Status);
        Assert.Equal(
            RuntimeBootstrapRendezvousStatus.TrayAlreadyReserved,
            (await RequestAsync(endpoint, verifier)).Status);

        await WaitUntilAsync(() => !rendezvous.HasTrayReservation);

        Assert.Equal(
            RuntimeBootstrapRendezvousStatus.Admitted,
            (await RequestAsync(endpoint, verifier)).Status);
    }

    [Fact]
    public async Task AcknowledgementAfterTicketExpiryIsRejectedAndReleasesReservation()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        var verifier = new FakeImageVerifier();
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var options = new RuntimeBootstrapRendezvousOptions
        {
            TimeProvider = clock,
            TicketLifetime = TimeSpan.FromMilliseconds(100),
            ClaimRaceGrace = TimeSpan.FromSeconds(1),
        };
        await using var runtime = RuntimeIpcServer.Start(
            endpoint,
            RejectUnexpectedRuntimeConnection,
            new RuntimeIpcServerOptions
            {
                TimeProvider = clock,
                DefaultTicketLifetime = TimeSpan.FromMilliseconds(100),
            });
        await using var rendezvous = StartRendezvous(runtime, endpoint, verifier, options);

        var pipe = await ConnectRawAsync(endpoint);
        await using (pipe.ConfigureAwait(false))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await RuntimeBootstrapWire.WriteAsync(
                pipe,
                new RuntimeBootstrapWireRequest(
                    RuntimeBootstrapRendezvousProtocol.Version,
                    endpoint.DataRootId,
                    RuntimeClientKind.Tray.ToString()),
                4096,
                timeout.Token);
            var response = await RuntimeBootstrapWire.ReadAsync<RuntimeBootstrapWireResponse>(
                pipe,
                4096,
                timeout.Token);
            Assert.Equal(RuntimeBootstrapWireStatus.Admitted, response.Status);

            clock.Advance(TimeSpan.FromMilliseconds(200));
            await RuntimeBootstrapWire.WriteAsync(
                pipe,
                new RuntimeBootstrapWireAcknowledgement(response.AcknowledgementNonce!),
                4096,
                timeout.Token);
            var commit = await RuntimeBootstrapWire.ReadAsync<RuntimeBootstrapWireCommit>(
                pipe,
                4096,
                timeout.Token);

            Assert.False(commit.Accepted);
            Assert.False(rendezvous.HasTrayReservation);
        }

        Assert.Equal(
            RuntimeBootstrapRendezvousStatus.Admitted,
            (await RequestAsync(endpoint, verifier)).Status);
    }

    [Fact]
    public async Task IncompleteAcknowledgementRollsBackAndLeakedTicketCannotClaimReplacement()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        var verifier = new FakeImageVerifier();
        RuntimeBootstrapRendezvousServer? rendezvous = null;
        Exception? rejectedClaim = null;
        await using var runtime = RuntimeIpcServer.Start(
            endpoint,
            (context, kind, _, _, lifetime) =>
            {
                try
                {
                    _ = rendezvous!.ClaimTrayConnection(context, kind, lifetime);
                }
                catch (Exception exception)
                {
                    rejectedClaim = exception;
                    throw;
                }
                return ValueTask.FromResult<IRuntimeRpcServer>(new AttachedServer(endpoint));
            });
        await using (rendezvous = StartRendezvous(runtime, endpoint, verifier))
        {
            var leakedTicket = await ReadAdmissionAndDisconnectDuringAcknowledgementAsync(endpoint);
            await WaitUntilAsync(() => !rendezvous.HasTrayReservation);

            var replacement = await RequestAsync(endpoint, verifier);
            Assert.Equal(RuntimeBootstrapRendezvousStatus.Admitted, replacement.Status);

            await Assert.ThrowsAnyAsync<Exception>(() => RuntimeIpcClient.ConnectAsync(
                endpoint,
                TrayAttachRequest(endpoint, leakedTicket),
                new NoOpRuntimeClient()));
            Assert.IsType<RuntimeIpcAuthenticationException>(rejectedClaim);
            Assert.True(rendezvous.HasTrayReservation);
        }
    }

    [Fact]
    public async Task DelayedExpiredClaimCannotTakeLaterSameProcessAdmission()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        var verifier = new FakeImageVerifier();
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var options = new RuntimeBootstrapRendezvousOptions
        {
            TimeProvider = clock,
            TicketLifetime = TimeSpan.FromSeconds(1),
            ClaimRaceGrace = TimeSpan.FromSeconds(1),
        };
        var firstFactoryEntered = new TaskCompletionSource<RuntimeIpcConnectionContext>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstFactory = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? staleClaimFailure = null;
        RuntimeBootstrapTrayLease? secondLease = null;
        var factoryCall = 0;
        RuntimeBootstrapRendezvousServer? rendezvous = null;

        await using var runtime = RuntimeIpcServer.Start(
            endpoint,
            async (context, kind, _, _, lifetime) =>
            {
                if (Interlocked.Increment(ref factoryCall) == 1)
                {
                    firstFactoryEntered.TrySetResult(context);
                    await releaseFirstFactory.Task.ConfigureAwait(false);
                    try
                    {
                        _ = rendezvous!.ClaimTrayConnection(context, kind, lifetime);
                    }
                    catch (Exception exception)
                    {
                        staleClaimFailure = exception;
                        throw;
                    }
                }

                secondLease = rendezvous!.ClaimTrayConnection(context, kind, lifetime);
                return new AttachedServer(endpoint, secondLease);
            },
            new RuntimeIpcServerOptions
            {
                TimeProvider = clock,
                DefaultTicketLifetime = TimeSpan.FromSeconds(1),
            });
        await using (rendezvous = StartRendezvous(runtime, endpoint, verifier, options))
        {
            var firstAdmission = await RequestAsync(endpoint, verifier);
            var firstConnection = RuntimeIpcClient.ConnectAsync(
                endpoint,
                TrayAttachRequest(endpoint, firstAdmission.LaunchTicket!),
                new NoOpRuntimeClient());
            var oldContext = await firstFactoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var oldFingerprint = oldContext.AuthenticatedLaunchTicketFingerprint;

            clock.Advance(TimeSpan.FromSeconds(3));
            var secondAdmission = await RequestAsync(endpoint, verifier);
            Assert.Equal(RuntimeBootstrapRendezvousStatus.Admitted, secondAdmission.Status);
            Assert.NotEqual(
                oldFingerprint,
                RuntimeBootstrapTicketFingerprint.Compute(secondAdmission.LaunchTicket!.Value));

            releaseFirstFactory.TrySetResult();
            await Assert.ThrowsAnyAsync<Exception>(() => firstConnection);
            Assert.IsType<RuntimeIpcAuthenticationException>(staleClaimFailure);
            Assert.True(rendezvous.HasTrayReservation);

            var secondConnection = await RuntimeIpcClient.ConnectAsync(
                endpoint,
                TrayAttachRequest(endpoint, secondAdmission.LaunchTicket),
                new NoOpRuntimeClient());
            Assert.NotNull(secondLease);
            Assert.True(rendezvous.HasTrayReservation);

            await secondConnection.DisposeAsync();
            await WaitUntilAsync(() => !rendezvous.HasTrayReservation);
        }
    }

    [Fact]
    public async Task WrongPeerContextAndTicketReplayCannotDisplaceAttachedReservation()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        var verifier = new FakeImageVerifier();
        RuntimeBootstrapRendezvousServer? rendezvous = null;
        RuntimeIpcConnectionContext? contextSeen = null;
        await using var runtime = RuntimeIpcServer.Start(
            endpoint,
            (context, kind, _, _, lifetime) =>
            {
                contextSeen = context;
                var wrongContexts = new[]
                {
                    context with
                    {
                        Peer = context.Peer with { ProcessId = context.Peer.ProcessId + 1 },
                    },
                    context with
                    {
                        Peer = context.Peer with { SessionId = context.Peer.SessionId + 1 },
                    },
                    context with
                    {
                        Peer = context.Peer with { UserSid = context.Peer.UserSid + "-other" },
                    },
                    context with
                    {
                        Peer = context.Peer with { IsElevated = true },
                    },
                    context with
                    {
                        Endpoint = RuntimeIpcEndpoint.CreateSynthetic(
                            Path.Combine(_root, "other"),
                            $"wrong-endpoint-{Guid.NewGuid():N}"),
                    },
                };

                foreach (var wrongContext in wrongContexts)
                {
                    Assert.Throws<RuntimeIpcAuthenticationException>(() =>
                        rendezvous!.ClaimTrayConnection(wrongContext, kind, lifetime));
                    Assert.True(rendezvous!.HasTrayReservation);
                }

                var lease = rendezvous!.ClaimTrayConnection(context, kind, lifetime);
                return ValueTask.FromResult<IRuntimeRpcServer>(new AttachedServer(endpoint, lease));
            });
        await using (rendezvous = StartRendezvous(runtime, endpoint, verifier))
        {
            var admission = await RequestAsync(endpoint, verifier);
            var attachRequest = TrayAttachRequest(endpoint, admission.LaunchTicket!);
            var connection = await RuntimeIpcClient.ConnectAsync(
                endpoint,
                attachRequest,
                new NoOpRuntimeClient());

            Assert.NotNull(contextSeen);
            await Assert.ThrowsAsync<RemoteInvocationException>(() => RuntimeIpcClient.ConnectAsync(
                endpoint,
                attachRequest,
                new NoOpRuntimeClient()));
            Assert.True(rendezvous.HasTrayReservation);

            await connection.DisposeAsync();
            await WaitUntilAsync(() => !rendezvous.HasTrayReservation);
        }
    }

    [Fact]
    public async Task ExplicitFactoryAndAttachFailureCleanupAllowReplacement()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        var verifier = new FakeImageVerifier();
        var factoryCall = 0;
        RuntimeBootstrapRendezvousServer? rendezvous = null;
        await using var runtime = RuntimeIpcServer.Start(
            endpoint,
            (context, kind, _, _, lifetime) =>
            {
                var lease = rendezvous!.ClaimTrayConnection(context, kind, lifetime);
                if (Interlocked.Increment(ref factoryCall) == 1)
                {
                    lease.Dispose();
                    return ValueTask.FromException<IRuntimeRpcServer>(
                        new InvalidOperationException("Synthetic factory failure."));
                }

                return ValueTask.FromResult<IRuntimeRpcServer>(
                    new AttachedServer(endpoint, lease, failAttach: true));
            });
        await using (rendezvous = StartRendezvous(runtime, endpoint, verifier))
        {
            var factoryFailureAdmission = await RequestAsync(endpoint, verifier);
            await Assert.ThrowsAnyAsync<Exception>(() => RuntimeIpcClient.ConnectAsync(
                endpoint,
                TrayAttachRequest(endpoint, factoryFailureAdmission.LaunchTicket!),
                new NoOpRuntimeClient()));
            await WaitUntilAsync(() => !rendezvous.HasTrayReservation);

            var attachFailureAdmission = await RequestAsync(endpoint, verifier);
            await Assert.ThrowsAnyAsync<Exception>(() => RuntimeIpcClient.ConnectAsync(
                endpoint,
                TrayAttachRequest(endpoint, attachFailureAdmission.LaunchTicket!),
                new NoOpRuntimeClient()));
            await WaitUntilAsync(() => !rendezvous.HasTrayReservation);

            Assert.Equal(
                RuntimeBootstrapRendezvousStatus.Admitted,
                (await RequestAsync(endpoint, verifier)).Status);
        }
    }

    [Fact]
    public async Task DisposeClearsPendingAdmissionAndStopsRendezvous()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        var verifier = new FakeImageVerifier();
        await using var runtime = RuntimeIpcServer.Start(
            endpoint,
            RejectUnexpectedRuntimeConnection);
        var rendezvous = StartRendezvous(runtime, endpoint, verifier);

        Assert.Equal(
            RuntimeBootstrapRendezvousStatus.Admitted,
            (await RequestAsync(endpoint, verifier)).Status);
        Assert.True(rendezvous.HasTrayReservation);

        await rendezvous.DisposeAsync();

        Assert.False(rendezvous.HasTrayReservation);
        await Assert.ThrowsAnyAsync<Exception>(() => RuntimeBootstrapRendezvousClient.RequestTrayForTestAsync(
            endpoint,
            Path.Combine(_root, "publish", "Joydex.RuntimeHost.exe"),
            new RuntimeBootstrapRendezvousClientOptions
            {
                RequestTimeout = TimeSpan.FromMilliseconds(200),
            },
            verifier));
    }

    [Fact]
    public async Task ProductionAndDemoEntryPointsRejectTheOppositeInstanceKind()
    {
        Directory.CreateDirectory(_root);
        var syntheticEndpoint = Endpoint();
        await using var syntheticRuntime = RuntimeIpcServer.Start(
            syntheticEndpoint,
            RejectUnexpectedRuntimeConnection);
        var publish = Path.Combine(_root, "publish");
        var appPath = Path.Combine(publish, "Joydex.App.exe");
        var hostPath = Path.Combine(publish, "Joydex.RuntimeHost.exe");

        Assert.Throws<InvalidOperationException>(() =>
            RuntimeBootstrapRendezvousServer.Start(
                syntheticRuntime,
                syntheticEndpoint,
                appPath,
                hostPath));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RuntimeBootstrapRendezvousClient.RequestTrayAsync(syntheticEndpoint, hostPath));

        var productionEndpoint = RuntimeIpcEndpoint.CreateProduction(
            Path.Combine(_root, "production"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RuntimeBootstrapRendezvousClient.RequestDemoTrayAsync(productionEndpoint, hostPath));
    }

    [Fact]
    public void WindowsVerifierUsesOpenedProcessAndCurrentDeploymentFileIdentity()
    {
        var path = Environment.ProcessPath
            ?? throw new InvalidOperationException("The test process has no executable path.");
        var verifier = WindowsRuntimeBootstrapImageVerifier.Instance;
        var image = verifier.CaptureFrozenImage(path);
        using var process = Process.GetCurrentProcess();
        var peer = new RuntimeIpcPeer(
            process.Id,
            process.StartTime.ToUniversalTime(),
            process.SessionId,
            CurrentSid(),
            IsElevated: false);

        verifier.VerifyPeerProcess(process, peer, image, "test process");

        Assert.Throws<RuntimeIpcAuthenticationException>(() =>
            verifier.VerifyPeerProcess(
                process,
                peer with { ProcessStartTimeUtc = peer.ProcessStartTimeUtc.AddTicks(1) },
                image,
                "test process"));

        Directory.CreateDirectory(_root);
        var copyPath = Path.Combine(_root, "different-file.exe");
        File.Copy(path, copyPath);
        var differentImage = verifier.CaptureFrozenImage(copyPath);
        Assert.Throws<RuntimeIpcAuthenticationException>(() =>
            verifier.VerifyPeerProcess(process, peer, differentImage, "test process"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private RuntimeBootstrapRendezvousServer StartRendezvous(
        RuntimeIpcServer runtime,
        RuntimeIpcEndpoint endpoint,
        IRuntimeBootstrapImageVerifier verifier,
        RuntimeBootstrapRendezvousOptions? options = null)
    {
        var publish = Path.Combine(_root, "publish");
        return RuntimeBootstrapRendezvousServer.StartForTest(
            runtime,
            endpoint,
            Path.Combine(publish, "Joydex.App.exe"),
            Path.Combine(publish, "Joydex.RuntimeHost.exe"),
            options,
            verifier);
    }

    private Task<RuntimeBootstrapRendezvousResult> RequestAsync(
        RuntimeIpcEndpoint endpoint,
        IRuntimeBootstrapImageVerifier verifier,
        RuntimeBootstrapWireRequest? request = null) =>
        RuntimeBootstrapRendezvousClient.RequestTrayForTestAsync(
            endpoint,
            Path.Combine(_root, "publish", "Joydex.RuntimeHost.exe"),
            options: null,
            verifier,
            request);

    private async Task<RuntimeIpcLaunchTicket> ReadAdmissionAndDisconnectDuringAcknowledgementAsync(
        RuntimeIpcEndpoint endpoint)
    {
        var pipe = await ConnectRawAsync(endpoint);
        await using (pipe.ConfigureAwait(false))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await RuntimeBootstrapWire.WriteAsync(
                pipe,
                new RuntimeBootstrapWireRequest(
                    RuntimeBootstrapRendezvousProtocol.Version,
                    endpoint.DataRootId,
                    RuntimeClientKind.Tray.ToString()),
                4096,
                timeout.Token);
            var response = await RuntimeBootstrapWire.ReadAsync<RuntimeBootstrapWireResponse>(
                pipe,
                4096,
                timeout.Token);
            Assert.Equal(RuntimeBootstrapWireStatus.Admitted, response.Status);

            var partialPrefix = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(partialPrefix, 100);
            await pipe.WriteAsync(partialPrefix, timeout.Token);
            await pipe.WriteAsync(new byte[] { (byte)'{' }, timeout.Token);
            await pipe.FlushAsync(timeout.Token);

            return new RuntimeIpcLaunchTicket(
                response.LaunchTicket!,
                DateTimeOffset.FromUnixTimeMilliseconds(
                    response.TicketExpiresAtUnixMilliseconds!.Value));
        }
    }

    private static async Task<NamedPipeClientStream> ConnectRawAsync(RuntimeIpcEndpoint endpoint)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            RuntimeBootstrapRendezvousProtocol.GetPipeName(endpoint),
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await pipe.ConnectAsync(timeout.Token);
        _ = WindowsPipePeerVerifier.VerifyServer(pipe, endpoint.SessionId);
        return pipe;
    }

    private RuntimeIpcEndpoint Endpoint() => RuntimeIpcEndpoint.CreateSynthetic(
        _root,
        $"joydex-bootstrap-test-{Guid.NewGuid():N}");

    private static RuntimeAttachRequest TrayAttachRequest(
        RuntimeIpcEndpoint endpoint,
        RuntimeIpcLaunchTicket ticket) =>
        RuntimeIpcTestData.AttachRequest(endpoint, ticket) with
        {
            ClientKind = RuntimeClientKind.Tray,
        };

    private static ValueTask<IRuntimeRpcServer> RejectUnexpectedRuntimeConnection(
        RuntimeIpcConnectionContext context,
        RuntimeClientKind authorizedKind,
        IRuntimeRpcClient client,
        Action<Exception?> abortConnection,
        CancellationToken connectionCancellationToken) =>
        throw new InvalidOperationException("This test does not expect a runtime IPC connection.");

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static string CurrentSid()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return identity.User!.Value;
    }

    private sealed class FakeImageVerifier : IRuntimeBootstrapImageVerifier
    {
        public bool RejectCurrentHost { get; init; }

        public bool RejectAppPeer { get; init; }

        public bool RejectHostPeer { get; init; }

        public RuntimeBootstrapImageIdentity CaptureFrozenImage(string path)
        {
            var canonicalPath = Path.GetFullPath(path);
            var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(canonicalPath);
            return new RuntimeBootstrapImageIdentity(canonicalPath, 1, unchecked((uint)hash));
        }

        public void VerifyCurrentProcess(RuntimeBootstrapImageIdentity expectedImage)
        {
            if (RejectCurrentHost)
            {
                throw new RuntimeIpcAuthenticationException("Synthetic elevated host rejection.");
            }
        }

        public void VerifyPeerProcess(
            Process process,
            RuntimeIpcPeer peer,
            RuntimeBootstrapImageIdentity expectedImage,
            string peerName)
        {
            Assert.Equal(peer.ProcessId, process.Id);
            Assert.Equal(peer.ProcessStartTimeUtc.UtcTicks, process.StartTime.ToUniversalTime().Ticks);
            if ((RejectAppPeer && peerName == "Joydex.App")
                || (RejectHostPeer && peerName == "RuntimeHost"))
            {
                throw new RuntimeIpcAuthenticationException("Synthetic image mismatch.");
            }
        }
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly object _gate = new();

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return now;
            }
        }

        public void Advance(TimeSpan duration)
        {
            lock (_gate)
            {
                now = now.Add(duration);
            }
        }
    }

    private sealed class NoOpRuntimeClient : IRuntimeRpcClient
    {
        public Task RuntimeEventAsync(RuntimeEvent runtimeEvent, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RuntimeInputEventAsync(
            RuntimeConnectionInputEvent inputEvent,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RuntimeCommandCompletedAsync(
            RuntimeCommandResult result,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class AttachedServer(
        RuntimeIpcEndpoint endpoint,
        RuntimeBootstrapTrayLease? lease = null,
        bool failAttach = false) : IRuntimeRpcServer, IAsyncDisposable
    {
        public Task<RuntimeAttachResult> AttachAsync(
            RuntimeAttachRequest request,
            CancellationToken cancellationToken)
        {
            if (failAttach)
            {
                lease?.Dispose();
                throw new InvalidOperationException("Synthetic attach failure.");
            }

            return Task.FromResult(new RuntimeAttachResult(
                "test",
                RuntimeProtocol.MajorVersion,
                RuntimeProtocol.MinorVersion,
                RuntimeProtocol.Capabilities,
                RuntimeProtocol.MaximumMessageBytes,
                ResynchronizationRequired: false,
                RuntimeIpcTestData.Snapshot(endpoint)));
        }

        public Task<RuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PrepareSettingsResult> PrepareSettingsAsync(
            PrepareSettingsRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ApplySettingsResult> ApplySettingsAsync(
            ApplySettingsRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SettingsOperationResult> GetSettingsOperationAsync(
            Guid operationId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RuntimeInputSnapshot> RefreshInputSourcesAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RuntimeCaptureStartResult> BeginInputCaptureAsync(
            RuntimeCaptureRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RuntimeCaptureCommandResult> RenewInputCaptureAsync(
            Guid captureId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RuntimeCaptureCommandResult> CancelInputCaptureAsync(
            Guid captureId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RuntimeCaptureLookupResult> GetInputCaptureAsync(
            Guid captureId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RuntimeCommandResult> ExecuteCommandAsync(
            RuntimeCommandRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RuntimeCommandOperationResult> GetCommandOperationAsync(
            Guid operationId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            lease?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
