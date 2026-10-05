using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Joydex.Core.Voice;
using Joydex.Ipc;
using Joydex.RuntimeHost.Plugins;
using Joydex.RuntimeHost.Plugins.Pebble;
using Joydex.RuntimeHost.Production;
using StreamJsonRpc;

namespace Joydex.RuntimeHost.Tests;

public sealed class PebbleWorkerProcessGenerationTests
{
    [Fact]
    public async Task SyntheticWorkerIsJobAssignedAndExactlyAuthenticatedBeforeStart()
    {
        var processFactory = new SyntheticProcessFactory();
        var job = new FakeJob();
        var callbacks = new RecordingCallbacks();
        var factory = new PebbleWorkerProcessGenerationFactory(
            Path.Combine(Path.GetTempPath(), "synthetic-pebble-worker.exe"),
            processFactory,
            () => job);

        await using var generation = await factory.StartAsync(
            Configuration(7),
            callbacks,
            CancellationToken.None);

        Assert.True(job.Assigned);
        Assert.True(processFactory.Process.PeerVerified);
        Assert.True(processFactory.Process.CapabilityMatched);
        Assert.True(processFactory.Process.AssignedBeforeTicketRead);
        Assert.Equal(2, generation.Status.Sequence);
        Assert.Empty(callbacks.Statuses);

        var initial = generation.ActivateCallbacks();

        Assert.Equal(2, initial.Sequence);
    }

    [Fact]
    public async Task InvalidWorkerProtocolIsRejectedAfterConfirmedCleanup()
    {
        var processFactory = new SyntheticProcessFactory(invalidProtocol: true);
        var job = new FakeJob();
        var factory = new PebbleWorkerProcessGenerationFactory(
            Path.Combine(Path.GetTempPath(), "synthetic-pebble-worker.exe"),
            processFactory,
            () => job);

        await Assert.ThrowsAsync<InvalidDataException>(() => factory.StartAsync(
            Configuration(3),
            new RecordingCallbacks(),
            CancellationToken.None));

        Assert.True(job.Assigned);
        Assert.True(processFactory.Process.Completion.IsCompleted);
        Assert.Equal(0, job.ActiveProcessCount);
    }

    [Fact]
    public async Task ExactPeerMismatchIsRejectedAndCleanupIsConfirmed()
    {
        var processFactory = new SyntheticProcessFactory(reportWrongStartTime: true);
        var job = new FakeJob();
        var factory = new PebbleWorkerProcessGenerationFactory(
            Path.Combine(Path.GetTempPath(), "synthetic-pebble-worker.exe"),
            processFactory,
            () => job);

        await Assert.ThrowsAsync<RuntimeIpcAuthenticationException>(() => factory.StartAsync(
            Configuration(5),
            new RecordingCallbacks(),
            CancellationToken.None));

        Assert.True(processFactory.Process.Completion.IsCompleted);
        Assert.Equal(0, job.ActiveProcessCount);
    }

    private static PebbleWorkerGenerationConfiguration Configuration(long generation) => new(
        generation,
        PebbleIndexPreferences.Default with
        {
            Enabled = true,
            TargetTaskId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            TargetHostId = "local",
            TargetTaskLabel = "Pebble",
        },
        Path.Combine(Path.GetTempPath(), "pebble.secret"),
        Path.Combine(Path.GetTempPath(), "pebble-inbox"),
        "Joydex.Test.Broker");

    private sealed class SyntheticProcessFactory(
        bool invalidProtocol = false,
        bool reportWrongStartTime = false) : IRuntimeSettingsProcessFactory
    {
        public SyntheticProcess Process { get; } = new(invalidProtocol, reportWrongStartTime);

        public IRuntimeSettingsProcess Start(ProcessStartInfo startInfo)
        {
            Assert.Equal("--pebble-worker", startInfo.Arguments);
            Assert.True(startInfo.CreateNoWindow);
            Assert.Equal(ProcessWindowStyle.Hidden, startInfo.WindowStyle);
            return Process;
        }
    }

    private sealed class SyntheticProcess(bool invalidProtocol, bool reportWrongStartTime) :
        IRuntimeSettingsProcess,
        IRuntimeWorkerProcess
    {
        private readonly MemoryStream _input = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Process _current = Process.GetCurrentProcess();
        private Task? _client;
        private FakeJob? _job;

        public int Id => _current.Id;
        public bool HasExited => _completion.Task.IsCompleted;
        public Stream StandardInput => _input;
        public Task Completion => _completion.Task;
        public Microsoft.Win32.SafeHandles.SafeProcessHandle ProcessHandle => _current.SafeHandle;
        public long ProcessStartTimeUtcTicks =>
            _current.StartTime.ToUniversalTime().Ticks + (reportWrongStartTime ? 1 : 0);
        public int SessionId => _current.SessionId;
        public bool PeerVerified { get; private set; }
        public bool CapabilityMatched { get; private set; }
        public bool AssignedBeforeTicketRead { get; private set; }

        public void Assign(FakeJob job) => _job = job;

        public void CloseInput()
        {
            AssignedBeforeTicketRead = _job?.Assigned == true;
            _input.Position = 0;
            var ticket = JsonSerializer.Deserialize<PebbleWorkerLaunchTicket>(_input)
                ?? throw new InvalidDataException();
            _client = RunClientAsync(ticket);
        }

        public void Kill() => _lifetime.Cancel();

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            if (_client is not null)
            {
                try { await _client.ConfigureAwait(false); } catch { }
            }
            _input.Dispose();
            _lifetime.Dispose();
            _current.Dispose();
        }

        private async Task RunClientAsync(PebbleWorkerLaunchTicket ticket)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(
                    ".",
                    ticket.PipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(_lifetime.Token).ConfigureAwait(false);
                _ = WindowsPipePeerVerifier.VerifyServer(
                    pipe,
                    ticket.ExpectedSessionId,
                    ticket.ExpectedHostProcessId,
                    ticket.ExpectedHostStartTimeUtcTicks);
                PeerVerified = true;
                var (rpc, bounded) = RuntimeJsonRpc.Create(pipe);
                using (rpc)
                using (bounded)
                {
                    rpc.AddLocalRpcTarget(new SyntheticTarget(ticket, rpc, this, invalidProtocol));
                    rpc.StartListening();
                    await rpc.Completion.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            finally
            {
                _completion.TrySetResult();
            }
        }

        private sealed class SyntheticTarget(
            PebbleWorkerLaunchTicket ticket,
            JsonRpc rpc,
            SyntheticProcess owner,
            bool invalidProtocol)
        {
            [JsonRpcMethod(PebbleWorkerProtocol.Start)]
            public async Task<PebbleWorkerStartResponse> StartAsync(
                PebbleWorkerStartRequest request,
                CancellationToken cancellationToken)
            {
                owner.CapabilityMatched = request.Capability == ticket.Capability;
                var newer = Status(request.Generation, 2);
                await rpc.InvokeWithCancellationAsync(
                    PebbleWorkerProtocol.PublishStatus,
                    [newer],
                    cancellationToken).ConfigureAwait(false);
                return new PebbleWorkerStartResponse(
                    invalidProtocol
                        ? PebbleWorkerProtocol.MajorVersion + 1
                        : PebbleWorkerProtocol.MajorVersion,
                    PebbleWorkerProtocol.MinorVersion,
                    Status(request.Generation, 1));
            }

            [JsonRpcMethod(PebbleWorkerProtocol.Stop)]
            public Task StopAsync(PebbleWorkerGenerationMessage request) => Task.CompletedTask;

            private static PebbleWorkerStatus Status(long generation, long sequence) => new(
                generation,
                sequence,
                Running: true,
                "Pebble Index receiver is listening on loopback.",
                0);
        }
    }

    private sealed class FakeJob : IWorkerProcessJob
    {
        private SyntheticProcess? _process;
        public bool Assigned { get; private set; }
        public int ActiveProcessCount => _process?.Completion.IsCompleted == false ? 1 : 0;

        public void Assign(IRuntimeWorkerProcess process)
        {
            Assigned = true;
            _process = Assert.IsType<SyntheticProcess>(process);
            _process.Assign(this);
        }

        public void Terminate() => _process?.Kill();
        public void Dispose() { }
    }

    private sealed class RecordingCallbacks : IPebbleWorkerHostCallbacks
    {
        public List<PebbleWorkerStatus> Statuses { get; } = [];
        public void PublishStatus(PebbleWorkerStatus status) => Statuses.Add(status);
        public void WriteLog(long generation, string message) { }
    }
}
