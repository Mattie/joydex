using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using Joydex.Contracts;

namespace Joydex.Ipc.Tests;

public sealed class RuntimeListenerLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"joydex-listener-lifecycle-{Guid.NewGuid():N}");

    [Fact]
    public async Task RuntimeCompletionReportsAcceptFaultAndDisposeStillReleasesEndpoint()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        var acceptFailure = new InvalidOperationException("Synthetic runtime accept failure.");
        var server = RuntimeIpcServer.StartForTest(
            endpoint,
            RejectUnexpectedRuntimeConnection,
            options: null,
            new RuntimeListenerLifecycleTestHooks
            {
                BeforeAcceptAsync = () => ValueTask.FromException(acceptFailure),
            });

        var completionFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => server.Completion);
        Assert.Same(acceptFailure, completionFailure);

        var disposeFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.DisposeAsync().AsTask());
        Assert.Same(acceptFailure, disposeFailure);

        await using var replacement = RuntimeIpcServer.Start(
            endpoint,
            RejectUnexpectedRuntimeConnection);
        Assert.False(replacement.Completion.IsCompleted);
        await replacement.DisposeAsync();
        Assert.True(replacement.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RendezvousCompletionReportsAcceptFaultAndDisposeStillReleasesEndpoint()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        var verifier = new FakeImageVerifier();
        await using var runtime = RuntimeIpcServer.Start(
            endpoint,
            RejectUnexpectedRuntimeConnection);
        var acceptFailure = new InvalidOperationException("Synthetic rendezvous accept failure.");
        var acceptCall = 0;
        var rendezvous = StartRendezvous(
            runtime,
            endpoint,
            verifier,
            new RuntimeListenerLifecycleTestHooks
            {
                BeforeAcceptAsync = () => Interlocked.Increment(ref acceptCall) == 1
                    ? ValueTask.CompletedTask
                    : ValueTask.FromException(acceptFailure),
            });

        var publish = Path.Combine(_root, "publish");
        var admission = await RuntimeBootstrapRendezvousClient.RequestTrayForTestAsync(
            endpoint,
            Path.Combine(publish, "Joydex.RuntimeHost.exe"),
            options: null,
            verifier);
        Assert.Equal(RuntimeBootstrapRendezvousStatus.Admitted, admission.Status);
        Assert.True(rendezvous.HasTrayReservation);

        var completionFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rendezvous.Completion);
        Assert.Same(acceptFailure, completionFailure);

        var disposeFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rendezvous.DisposeAsync().AsTask());
        Assert.Same(acceptFailure, disposeFailure);
        Assert.False(rendezvous.HasTrayReservation);

        await using var replacement = StartRendezvous(runtime, endpoint, verifier);
        Assert.False(replacement.Completion.IsCompleted);
        await replacement.DisposeAsync();
        Assert.True(replacement.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RuntimeDisposeWaitsForTrackedConnectionCleanupBeforeDisposingSlot()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        var cleanupEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var server = RuntimeIpcServer.StartForTest(
            endpoint,
            RejectUnexpectedRuntimeConnection,
            options: null,
            new RuntimeListenerLifecycleTestHooks
            {
                BeforeConnectionCleanupAsync = async () =>
                {
                    cleanupEntered.TrySetResult();
                    await releaseCleanup.Task.ConfigureAwait(false);
                },
            });
        await using var client = await ConnectRawAsync(endpoint.PipeName, endpoint.SessionId);

        var disposeTask = server.DisposeAsync().AsTask();
        try
        {
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(server.Completion.IsCompletedSuccessfully);
            Assert.False(disposeTask.IsCompleted);
        }
        finally
        {
            releaseCleanup.TrySetResult();
        }

        await disposeTask;
    }

    [Fact]
    public async Task RendezvousDisposeWaitsForTrackedRequestCleanupBeforeDisposingSlot()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        var verifier = new FakeImageVerifier();
        await using var runtime = RuntimeIpcServer.Start(
            endpoint,
            RejectUnexpectedRuntimeConnection);
        var cleanupEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var rendezvous = StartRendezvous(
            runtime,
            endpoint,
            verifier,
            new RuntimeListenerLifecycleTestHooks
            {
                BeforeConnectionCleanupAsync = async () =>
                {
                    cleanupEntered.TrySetResult();
                    await releaseCleanup.Task.ConfigureAwait(false);
                },
            });
        await using var client = await ConnectRawAsync(rendezvous.PipeName, endpoint.SessionId);

        var disposeTask = rendezvous.DisposeAsync().AsTask();
        try
        {
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await rendezvous.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(rendezvous.Completion.IsCompletedSuccessfully);
            Assert.False(disposeTask.IsCompleted);
        }
        finally
        {
            releaseCleanup.TrySetResult();
        }

        await disposeTask;
    }

    [Fact]
    public async Task MalformedRuntimeClientsRemainIsolatedFromListenerAndShutdown()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        using var cleanupObserved = new SemaphoreSlim(0);
        var server = RuntimeIpcServer.StartForTest(
            endpoint,
            RejectUnexpectedRuntimeConnection,
            new RuntimeIpcServerOptions { MaximumConnections = 1 },
            new RuntimeListenerLifecycleTestHooks
            {
                BeforeConnectionCleanupAsync = () =>
                {
                    cleanupObserved.Release();
                    return ValueTask.CompletedTask;
                },
            });
        var invalidHeader = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(
            invalidHeader,
            RuntimeProtocol.MaximumMessageBytes + 1);

        for (var index = 0; index < 16; index++)
        {
            await using var client = await ConnectRawAsync(endpoint.PipeName, endpoint.SessionId);
            await client.WriteAsync(invalidHeader);
            await client.FlushAsync();
            Assert.True(await cleanupObserved.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        Assert.False(server.Completion.IsCompleted);
        await server.DisposeAsync();
        Assert.True(server.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DisposeReportsAcceptAndTrackedCleanupFailuresAfterCleanupCompletes()
    {
        Directory.CreateDirectory(_root);
        var endpoint = Endpoint();
        var acceptFailure = new InvalidOperationException("Synthetic later accept failure.");
        var cleanupFailure = new InvalidOperationException("Synthetic connection cleanup failure.");
        var cleanupObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptCall = 0;
        var server = RuntimeIpcServer.StartForTest(
            endpoint,
            RejectUnexpectedRuntimeConnection,
            options: null,
            new RuntimeListenerLifecycleTestHooks
            {
                BeforeAcceptAsync = () => Interlocked.Increment(ref acceptCall) == 1
                    ? ValueTask.CompletedTask
                    : ValueTask.FromException(acceptFailure),
                BeforeConnectionCleanupAsync = () =>
                {
                    cleanupObserved.TrySetResult();
                    return ValueTask.FromException(cleanupFailure);
                },
            });
        var client = await ConnectRawAsync(endpoint.PipeName, endpoint.SessionId);

        var completionFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => server.Completion);
        Assert.Same(acceptFailure, completionFailure);
        await client.DisposeAsync();
        await cleanupObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disposeFailure = await Assert.ThrowsAsync<AggregateException>(
            () => server.DisposeAsync().AsTask());
        var failures = disposeFailure.Flatten().InnerExceptions;
        Assert.Contains(failures, exception => ReferenceEquals(exception, acceptFailure));
        Assert.Contains(failures, exception => ReferenceEquals(exception, cleanupFailure));

        await using var replacement = RuntimeIpcServer.Start(
            endpoint,
            RejectUnexpectedRuntimeConnection);
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
        $"joydex-listener-test-{Guid.NewGuid():N}");

    private RuntimeBootstrapRendezvousServer StartRendezvous(
        RuntimeIpcServer runtime,
        RuntimeIpcEndpoint endpoint,
        IRuntimeBootstrapImageVerifier verifier,
        RuntimeListenerLifecycleTestHooks? hooks = null)
    {
        var publish = Path.Combine(_root, "publish");
        return RuntimeBootstrapRendezvousServer.StartForTest(
            runtime,
            endpoint,
            Path.Combine(publish, "Joydex.App.exe"),
            Path.Combine(publish, "Joydex.RuntimeHost.exe"),
            options: null,
            verifier,
            hooks);
    }

    private static async Task<NamedPipeClientStream> ConnectRawAsync(
        string pipeName,
        int sessionId)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await pipe.ConnectAsync(timeout.Token);
        _ = WindowsPipePeerVerifier.VerifyServer(pipe, sessionId);
        return pipe;
    }

    private static ValueTask<IRuntimeRpcServer> RejectUnexpectedRuntimeConnection(
        RuntimeIpcConnectionContext context,
        RuntimeClientKind authorizedKind,
        IRuntimeRpcClient client,
        Action<Exception?> abortConnection,
        CancellationToken connectionCancellationToken) =>
        throw new InvalidOperationException("This lifecycle test does not attach a runtime client.");

    private sealed class FakeImageVerifier : IRuntimeBootstrapImageVerifier
    {
        public RuntimeBootstrapImageIdentity CaptureFrozenImage(string path) =>
            new(Path.GetFullPath(path), 1, 1);

        public void VerifyCurrentProcess(RuntimeBootstrapImageIdentity expectedImage)
        {
        }

        public void VerifyPeerProcess(
            Process process,
            RuntimeIpcPeer peer,
            RuntimeBootstrapImageIdentity expectedImage,
            string peerName)
        {
            Assert.Equal(peer.ProcessId, process.Id);
            Assert.Equal(peer.ProcessStartTimeUtc.UtcTicks, process.StartTime.ToUniversalTime().Ticks);
        }
    }
}
