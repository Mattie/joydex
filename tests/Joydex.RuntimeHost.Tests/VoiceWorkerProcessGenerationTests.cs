using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Joydex.Contracts;
using Joydex.Core.Config;
using Joydex.Core.Mapping;
using Joydex.Core.Voice;
using Joydex.Ipc;
using Joydex.RuntimeHost.Plugins.Voice;
using Joydex.RuntimeHost.Production;
using Joydex.Windows.Actions;
using StreamJsonRpc;

namespace Joydex.RuntimeHost.Tests;

public sealed class VoiceWorkerProcessGenerationTests
{
    [Fact]
    public async Task SyntheticWorkerIsJobAssignedAndExactlyAuthenticatedBeforeStart()
    {
        var processFactory = new SyntheticProcessFactory();
        var job = new FakeJob();
        var callbacks = new RecordingCallbacks();
        var factory = new VoiceWorkerProcessGenerationFactory(
            Path.Combine(Path.GetTempPath(), "synthetic-voice-worker.exe"),
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
        Assert.Equal(2, generation.Snapshot.Sequence);
        Assert.Empty(callbacks.Snapshots);

        var initial = generation.ActivateCallbacks();

        Assert.Equal(2, initial.Sequence);
    }

    [Fact]
    public async Task InvalidWorkerProtocolIsRejectedAfterConfirmedCleanup()
    {
        var processFactory = new SyntheticProcessFactory(invalidProtocol: true);
        var job = new FakeJob();
        var factory = new VoiceWorkerProcessGenerationFactory(
            Path.Combine(Path.GetTempPath(), "synthetic-voice-worker.exe"),
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
        var factory = new VoiceWorkerProcessGenerationFactory(
            Path.Combine(Path.GetTempPath(), "synthetic-voice-worker.exe"),
            processFactory,
            () => job);

        await Assert.ThrowsAsync<RuntimeIpcAuthenticationException>(() => factory.StartAsync(
            Configuration(5),
            new RecordingCallbacks(),
            CancellationToken.None));

        Assert.True(processFactory.Process.Completion.IsCompleted);
        Assert.Equal(0, job.ActiveProcessCount);
    }

    [NonElevatedRuntimeIpcFact]
    public async Task NativeProbeJobKillsDescendantAndConfirmsEmptyBeforeDisposeReturns()
    {
        var executable = Path.Combine(
            AppContext.BaseDirectory,
            "ProcessProbe",
            "Joydex.ProcessProbe.exe");
        var factory = new VoiceWorkerProcessGenerationFactory(
            executable,
            WindowsRuntimeSettingsProcessFactory.Instance);
        var callbacks = new RecordingCallbacks();
        await using var generation = await factory.StartAsync(
            Configuration(11),
            callbacks,
            CancellationToken.None);

        var initial = generation.ActivateCallbacks();
        await generation.DisposeAsync();

        Assert.Equal(2, initial.Sequence);
    }

    private static VoiceWorkerGenerationConfiguration Configuration(long generation) => new(
        generation,
        VoicePePreferences.Default with
        {
            Enabled = true,
            DeviceEndpoint = "http://127.0.0.1/",
            PinnedTaskId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
        },
        CompanionConfig.CreateSafeDefault().Safety,
        Path.GetTempPath(),
        Path.Combine(Path.GetTempPath(), "active-voice.json"),
        Path.Combine(Path.GetTempPath(), "broker.exe"),
        "Joydex.Test.Broker");

    private sealed class SyntheticProcessFactory(
        bool invalidProtocol = false,
        bool reportWrongStartTime = false)
        : IRuntimeSettingsProcessFactory
    {
        public SyntheticProcess Process { get; } = new(invalidProtocol, reportWrongStartTime);

        public IRuntimeSettingsProcess Start(ProcessStartInfo startInfo)
        {
            Assert.Equal("--voice-worker", startInfo.Arguments);
            Assert.True(startInfo.CreateNoWindow);
            Assert.Equal(ProcessWindowStyle.Hidden, startInfo.WindowStyle);
            return Process;
        }
    }

    private sealed class SyntheticProcess(bool invalidProtocol, bool reportWrongStartTime) :
        IRuntimeSettingsProcess,
        IVoiceWorkerNativeProcess
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
            var ticket = JsonSerializer.Deserialize<VoiceWorkerLaunchTicket>(_input)
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

        private async Task RunClientAsync(VoiceWorkerLaunchTicket ticket)
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
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            finally { _completion.TrySetResult(); }
        }

        private sealed class SyntheticTarget(
            VoiceWorkerLaunchTicket ticket,
            JsonRpc rpc,
            SyntheticProcess owner,
            bool invalidProtocol)
        {
            [JsonRpcMethod(VoiceWorkerProtocol.Start)]
            public async Task<VoiceWorkerStartResponse> StartAsync(
                VoiceWorkerStartRequest request,
                CancellationToken cancellationToken)
            {
                owner.CapabilityMatched = request.Capability == ticket.Capability;
                var newer = Snapshot(request.Generation, 2);
                await rpc.InvokeWithCancellationAsync(
                    VoiceWorkerProtocol.PublishSnapshot,
                    [newer],
                    cancellationToken).ConfigureAwait(false);
                return new VoiceWorkerStartResponse(
                    invalidProtocol ? VoiceWorkerProtocol.MajorVersion + 1 : VoiceWorkerProtocol.MajorVersion,
                    VoiceWorkerProtocol.MinorVersion,
                    Snapshot(request.Generation, 1));
            }

            [JsonRpcMethod(VoiceWorkerProtocol.Stop)]
            public Task StopAsync(VoiceWorkerGenerationMessage request) => Task.CompletedTask;

            private static VoiceWorkerSnapshot Snapshot(long generation, long sequence) => new(
                generation,
                sequence,
                new RuntimeVoiceSnapshot(
                    RuntimeVoiceSessionState.Armed,
                    true,
                    false,
                    false,
                    false,
                    "Armed",
                    null,
                    sequence),
                []);
        }
    }

    private sealed class FakeJob : IVoiceWorkerJob
    {
        private SyntheticProcess? _process;
        public bool Assigned { get; private set; }
        public int ActiveProcessCount => _process?.Completion.IsCompleted == false ? 1 : 0;

        public void Assign(IVoiceWorkerNativeProcess process)
        {
            Assigned = true;
            _process = Assert.IsType<SyntheticProcess>(process);
            _process.Assign(this);
        }

        public void Terminate() => _process?.Kill();
        public void Dispose() { }
    }

    private sealed class RecordingCallbacks : IVoiceWorkerHostCallbacks
    {
        public List<VoiceWorkerSnapshot> Snapshots { get; } = [];
        public void PublishSnapshot(VoiceWorkerSnapshot snapshot) => Snapshots.Add(snapshot);
        public void VoiceBecameIdle(long generation, long sequence) { }
        public Task<bool> NavigateAsync(long generation, string taskId, CancellationToken token) => Task.FromResult(false);
        public Task<ActionExecutionResult> ExecuteActionAsync(long generation, ActionRequest request, CancellationToken token) =>
            Task.FromResult(ActionExecutionResult.Blocked("test"));
        public void WriteLog(long generation, string message) { }
    }
}
