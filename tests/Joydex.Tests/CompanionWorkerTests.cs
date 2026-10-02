using Joydex.Core.Config;
using Joydex.Core.Input;
using Joydex.Core.Mapping;
using Joydex.Windows.Actions;
using Joydex.Windows.Input;
using Joydex.Windows.Runtime;

namespace Joydex.Tests;

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

    [Fact]
    public async Task ControllerDisconnectReleasesHeldKeysBeforeReconnect()
    {
        var logs = new List<string>();
        var callOrder = new List<string>();
        var source = new DisconnectingJoystickSource(callOrder);
        var lifecycle = new RecordingKeyStateLifecycle(callOrder);
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
        await lifecycle.ReleaseAttempted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, lifecycle.ReleaseCalls);
        Assert.Equal(["clear", "read", "release"], callOrder);
        Assert.Contains(logs, message => message.Contains("DirectInput disconnected: unplugged", StringComparison.Ordinal));

        await worker.StopAsync();
        Assert.Equal(2, lifecycle.ReleaseCalls);
    }

    [Fact]
    public async Task ControllerDisconnectRetriesIncompleteCleanupBeforeReconnect()
    {
        var logs = new List<string>();
        var callOrder = new List<string>();
        var source = new DisconnectingJoystickSource(callOrder);
        var lifecycle = new RecordingKeyStateLifecycle(callOrder);
        lifecycle.ReleaseResults.Enqueue(false);
        var executor = new CodexActionExecutor(
            new SafetyOptions { DryRun = true },
            logs.Add,
            new UnusedResolver(),
            new OpenWorkingDirectoryOptions());
        await using var worker = new CompanionWorker(
            new CompanionConfig
            {
                Polling = new PollingOptions { ReconnectIntervalMs = 250 },
            },
            source,
            executor,
            logs.Add,
            lifecycle);

        worker.Start();
        await source.ConnectAttempted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(2, lifecycle.ReleaseCalls);
        Assert.Equal(["clear", "read", "release", "release", "connect"], callOrder);

        await worker.StopAsync();
        Assert.Equal(3, lifecycle.ReleaseCalls);
    }

    [Fact]
    public async Task WorkerShutdownRetriesIncompleteCleanupBeforeCompleting()
    {
        var logs = new List<string>();
        var callOrder = new List<string>();
        var source = new DisconnectedJoystickSource(callOrder);
        var lifecycle = new RecordingKeyStateLifecycle(callOrder);
        lifecycle.ReleaseResults.Enqueue(false);
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
        await worker.StopAsync();

        Assert.Equal(2, lifecycle.ReleaseCalls);
        Assert.Equal(["clear", "connect", "release", "release"], callOrder);
    }

    private sealed class RecordingKeyStateLifecycle(List<string> callOrder) : IInjectedKeyStateLifecycle
    {
        public Exception? ClearFailure { get; init; }

        public int ClearCalls { get; private set; }

        public int ReleaseCalls { get; private set; }

        public Queue<bool> ReleaseResults { get; } = new();

        public TaskCompletionSource ReleaseAttempted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void ClearInjectedKeyState()
        {
            callOrder.Add("clear");
            ClearCalls++;
            if (ClearFailure is not null)
            {
                throw ClearFailure;
            }
        }

        public bool ReleaseHeldKeys()
        {
            callOrder.Add("release");
            ReleaseCalls++;
            ReleaseAttempted.TrySetResult();
            return ReleaseResults.TryDequeue(out var result) ? result : true;
        }
    }

    private sealed class DisconnectingJoystickSource(List<string> callOrder) : IJoystickSource
    {
        public DirectInputDeviceInfo? ConnectedDevice { get; private set; } = new(
            "instance",
            "controller",
            Guid.NewGuid(),
            Guid.NewGuid());

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
            callOrder.Add("read");
            ConnectedDevice = null;
            snapshot = null;
            error = "unplugged";
            return false;
        }

        public void Disconnect() => ConnectedDevice = null;

        public void Dispose()
        {
        }
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
