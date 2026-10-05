using System.Collections.Concurrent;
using System.Threading.Channels;
using Joydex.App;
using Joydex.Core.Voice;
using Joydex.Ipc;
using Joydex.PebbleWorker;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace Joydex.RuntimeHost.Tests;

public sealed class PebbleWorkerServiceTests
{
    [Theory]
    [InlineData("capability")]
    [InlineData("version")]
    [InlineData("generation")]
    [InlineData("disabled")]
    public async Task RejectedStartNeverConstructsReceiver(string rejection)
    {
        await using var harness = new Harness(new FakeFactory());
        var request = harness.Request;
        request = rejection switch
        {
            "capability" => request with { Capability = "wrong" },
            "version" => request with { ProtocolMajor = request.ProtocolMajor + 1 },
            "generation" => request with { Generation = request.Generation + 1 },
            _ => request with { Preferences = request.Preferences with { Enabled = false } },
        };

        await Assert.ThrowsAsync<InvalidDataException>(
            () => harness.Service.StartAsync(request, CancellationToken.None));

        Assert.Equal(0, harness.Factory.StartCount);
        Assert.False(harness.Service.StopRequested);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task ReceiverMustRemainRunningBeforeStartCanSucceed(bool running, bool completed)
    {
        var factory = new FakeFactory { InitialStatus = new(running, "Synthetic status") };
        if (completed) factory.Runtime.Finish();
        await using var harness = new Harness(factory);

        var response = await harness.Service.StartAsync(harness.Request, CancellationToken.None);

        Assert.Null(response.Status);
        Assert.Equal(PebbleWorkerStartFailureKind.Transient, response.Failure?.Kind);
        Assert.Equal(1, factory.Runtime.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadyStatusPreservesInboxIdentityWithoutPrivateDeliveryContent(bool providedId)
    {
        var id = (providedId ? "provided-" : "") + new string('a', 64);
        var factory = new FakeFactory { InitialStatus = Status(id, int.MaxValue) };
        await using var harness = new Harness(factory);

        var response = await harness.Service.StartAsync(harness.Request, CancellationToken.None);

        Assert.Null(response.Failure);
        var status = Assert.IsType<PebbleWorkerStatus>(response.Status);
        Assert.True(status.Running);
        Assert.Equal(harness.Request.Generation, status.Generation);
        Assert.Equal(id, status.Latest?.HashedId);
        Assert.Equal(PebbleIndexDeliveryState.Received, status.Latest?.State);
        Assert.InRange(status.OutstandingCount, 0, 100_000);
        Assert.DoesNotContain("private-marker", status.Message);
        Assert.DoesNotContain("private-marker", status.Latest!.Detail);
        Assert.True(status.Message.Length < 256 && status.Latest.Detail.Length < 256);
        Assert.Equal(harness.Request.Paths.Secret, factory.Configuration?.SecretPath);
        Assert.Equal(harness.Request.Preferences.TargetTaskId, factory.Configuration?.Preferences.TargetTaskId);
        Assert.False(harness.Service.Completion.IsCompleted);

        await harness.Service.StopAsync(new(harness.Request.Generation), CancellationToken.None);
        await harness.Service.DisposeAsync();
        Assert.True(harness.Service.StopRequested);
        Assert.Equal(1, factory.Runtime.DisposeCount);
    }

    [Fact]
    public async Task MalformedStoredIdentityIsOmittedFromStatus()
    {
        var factory = new FakeFactory { InitialStatus = Status("private-marker/invalid", -1) };
        await using var harness = new Harness(factory);

        var response = await harness.Service.StartAsync(harness.Request, CancellationToken.None);

        Assert.Null(response.Failure);
        Assert.NotNull(response.Status);
        Assert.Null(response.Status.Latest);
        Assert.Equal(0, response.Status.OutstandingCount);
    }

    [Fact]
    public async Task ConcurrentStatusPublicationKeepsNewestStateAndDoesNotRegressSequence()
    {
        await using var harness = new Harness(new FakeFactory());
        var response = await harness.Service.StartAsync(harness.Request, CancellationToken.None);
        Assert.Null(response.Failure);
        await harness.Host.ReadStatusAsync(status => status.Sequence >= response.Status!.Sequence);

        await Task.WhenAll(Enumerable.Range(0, 128).Select(index => Task.Run(
            () => harness.Factory.Publish(new(true, "Synthetic update", OutstandingCount: index)))));
        // This marker is emitted after every concurrent producer has returned. Seeing it
        // proves that the publisher drained through the latest state, rather than merely
        // observing a convenient earlier update.
        harness.Factory.Publish(new(true, "Final synthetic update", OutstandingCount: 9876));
        var final = await harness.Host.ReadStatusAsync(status => status.OutstandingCount == 9876);

        Assert.True(final.Sequence > response.Status!.Sequence);
        var observed = harness.Host.ObservedStatuses.ToArray();
        Assert.All(observed, status => Assert.Equal(harness.Request.Generation, status.Generation));
        Assert.True(observed.Zip(observed.Skip(1), (left, right) => left.Sequence <= right.Sequence).All(value => value));
        Assert.False(harness.Service.Completion.IsCompleted);
    }

    [Fact]
    public async Task DiagnosticPayloadNeverLeavesWorkerVerbatim()
    {
        await using var harness = new Harness(new FakeFactory());
        var response = await harness.Service.StartAsync(harness.Request, CancellationToken.None);
        Assert.Null(response.Failure);
        harness.Factory.Log("private-marker transcript/token " + new string('x', 16_000));

        var message = await harness.Host.ReadLogAsync();

        Assert.Equal(harness.Request.Generation, message.Generation);
        Assert.DoesNotContain("private-marker", message.Message);
        Assert.True(message.Message.Length < 256);
    }

    private static PebbleIndexReceiverStatus Status(string id, int count) => new(
        true,
        "private-marker receiver error",
        new PebbleIndexDelivery(
            id, "private-marker transcript", "1", "client", "trigger", "task", "host",
            "private-marker label", PebbleIndexDeliveryState.Received, DateTimeOffset.UnixEpoch,
            Detail: "private-marker delivery detail"),
        count);

    private sealed class Harness : IAsyncDisposable
    {
        private readonly JsonRpc _workerRpc;
        private readonly JsonRpc _hostRpc;
        private readonly Stream _workerStream;
        private readonly Stream _hostStream;

        public Harness(FakeFactory factory)
        {
            Factory = factory;
            var ticket = new PebbleWorkerLaunchTicket(
                "unused", "synthetic-capability", 7,
                PebbleWorkerProtocol.MajorVersion, PebbleWorkerProtocol.MinorVersion, 1, 1, 0);
            Request = new(ticket.Capability, ticket.Generation, ticket.ProtocolMajor, ticket.ProtocolMinor,
                new(1, true, 5187, "01a080f7-1ef7-7e42-81bb-5fba0cf083ed", "local", "Synthetic task"),
                new("unused-secret-path", "unused-inbox-path", "unused-desktop-pipe"));
            var (worker, host) = FullDuplexStream.CreatePair();
            (_workerRpc, _workerStream) = RuntimeJsonRpc.Create(worker);
            (_hostRpc, _hostStream) = RuntimeJsonRpc.Create(host);
            Host = new HostCallbacks();
            _hostRpc.AddLocalRpcTarget(Host);
            _hostRpc.StartListening();
            _workerRpc.StartListening();
            Service = new(ticket, _workerRpc, factory);
        }

        public FakeFactory Factory { get; }
        public PebbleWorkerStartRequest Request { get; }
        public PebbleWorkerService Service { get; }
        public HostCallbacks Host { get; }

        public async ValueTask DisposeAsync()
        {
            try { await Service.DisposeAsync(); }
            finally
            {
                _workerRpc.Dispose();
                _hostRpc.Dispose();
                _workerStream.Dispose();
                _hostStream.Dispose();
            }
        }
    }

    private sealed class HostCallbacks
    {
        private readonly Channel<PebbleWorkerStatus> _statuses = Channel.CreateUnbounded<PebbleWorkerStatus>();
        private readonly Channel<PebbleWorkerLogMessage> _logs = Channel.CreateUnbounded<PebbleWorkerLogMessage>();
        public ConcurrentQueue<PebbleWorkerStatus> ObservedStatuses { get; } = new();

        [JsonRpcMethod(PebbleWorkerProtocol.PublishStatus)]
        public void PublishStatus(PebbleWorkerStatus status)
        {
            ObservedStatuses.Enqueue(status);
            _statuses.Writer.TryWrite(status);
        }

        [JsonRpcMethod(PebbleWorkerProtocol.Log)]
        public void Log(PebbleWorkerLogMessage message) => _logs.Writer.TryWrite(message);

        public async Task<PebbleWorkerStatus> ReadStatusAsync(Func<PebbleWorkerStatus, bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (var status in _statuses.Reader.ReadAllAsync(timeout.Token))
            {
                if (predicate(status)) return status;
            }
            throw new InvalidOperationException("No matching worker status arrived.");
        }

        public async Task<PebbleWorkerLogMessage> ReadLogAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await _logs.Reader.ReadAsync(timeout.Token);
        }
    }

    private sealed class FakeFactory : IPebbleWorkerRuntimeFactory
    {
        private Action<PebbleIndexReceiverStatus>? _status;
        private Action<string>? _log;
        public PebbleIndexReceiverStatus InitialStatus { get; init; } = new(true, "Synthetic ready");
        public FakeRuntime Runtime { get; } = new();
        public int StartCount { get; private set; }
        public PebbleWorkerRuntimeConfiguration? Configuration { get; private set; }

        public Task<IPebbleWorkerRuntime> StartAsync(
            PebbleWorkerRuntimeConfiguration configuration,
            Action<PebbleIndexReceiverStatus> status,
            Action<string> log,
            CancellationToken cancellationToken)
        {
            StartCount++;
            Configuration = configuration;
            _status = status;
            _log = log;
            status(InitialStatus);
            return Task.FromResult<IPebbleWorkerRuntime>(Runtime);
        }

        public void Publish(PebbleIndexReceiverStatus status) => _status!(status);
        public void Log(string message) => _log!(message);
    }

    private sealed class FakeRuntime : IPebbleWorkerRuntime
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completion => _completion.Task;
        public int DisposeCount { get; private set; }
        public void Finish() => _completion.TrySetResult();
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            Finish();
            return ValueTask.CompletedTask;
        }
    }
}
