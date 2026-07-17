using VirpilCodexPad.Core.Config;
using VirpilCodexPad.Core.Input;
using VirpilCodexPad.Core.Mapping;
using VirpilCodexPad.Windows.Actions;
using VirpilCodexPad.Windows.Input;
using VirpilCodexPad.Windows.Runtime;

namespace VirpilCodexPad.Core.Tests;

public sealed class CompanionWorkerTests
{
    [Fact]
    public async Task StartClearsInjectedKeysAndContinuesWhenCleanupFails()
    {
        var logs = new List<string>();
        var callOrder = new List<string>();
        var source = new DisconnectedJoystickSource(callOrder);
        var lifecycle = new RecordingKeyStateLifecycle(callOrder)
        {
            ClearFailure = new InvalidOperationException("cleanup failed"),
        };
        var executor = new CodexActionExecutor(
            new SafetyOptions { DryRun = true },
            logs.Add,
            new UnusedResolver(),
            new OpenWorkingDirectoryOptions());
        await using var worker = new CompanionWorker(
            new CompanionConfig(),
            source,
            executor,
            logs.Add,
            lifecycle);

        worker.Start();
        await source.ConnectAttempted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, lifecycle.ClearCalls);
        Assert.Equal(1, source.ConnectAttempts);
        Assert.Equal(["clear", "connect"], callOrder);
        Assert.Contains(logs, message => message.Contains("cleanup failed", StringComparison.Ordinal));

        await worker.StopAsync();
        Assert.Equal(1, lifecycle.ReleaseCalls);
    }

    private sealed class RecordingKeyStateLifecycle(List<string> callOrder) : IInjectedKeyStateLifecycle
    {
        public Exception? ClearFailure { get; init; }

        public int ClearCalls { get; private set; }

        public int ReleaseCalls { get; private set; }

        public void ClearInjectedKeyState()
        {
            callOrder.Add("clear");
            ClearCalls++;
            if (ClearFailure is not null)
            {
                throw ClearFailure;
            }
        }

        public void ReleaseHeldKeys() => ReleaseCalls++;
    }

    private sealed class DisconnectedJoystickSource(List<string> callOrder) : IJoystickSource
    {
        public DirectInputDeviceInfo? ConnectedDevice => null;

        public IReadOnlyList<JoystickEvent> LatestBufferedButtonEvents => [];

        public int ConnectAttempts { get; private set; }

        public TaskCompletionSource ConnectAttempted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryConnect(DeviceSelector selector, out string message)
        {
            callOrder.Add("connect");
            ConnectAttempts++;
            ConnectAttempted.TrySetResult();
            message = "No device in lifecycle test.";
            return false;
        }

        public bool TryRead(out JoystickSnapshot? snapshot, out string? error)
        {
            snapshot = null;
            error = null;
            return false;
        }

        public void Disconnect()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class UnusedResolver : ICodexKeybindingResolver
    {
        public Task<CodexBindingResolution> ResolveAsync(
            CodexAction action,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The worker lifecycle test should not dispatch an action.");
    }
}
