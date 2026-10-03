using Joydex.Core.Config;
using Joydex.Core.Input;
using Joydex.Core.Mapping;
using Joydex.Core.Runtime;
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
        Assert.Equal(0, lifecycle.ReleaseCalls);
    }

    [Fact]
    public async Task FailedGenerationCleanupIsRetriedBeforeReconnect()
    {
        var logs = new List<string>();
        var source = new DisconnectingJoystickSource();
        var lifecycle = new RecordingKeyStateLifecycle([])
        {
            ReleaseFailuresRemaining = 1,
        };
        var config = new CompanionConfig
        {
            Polling = new PollingOptions
            {
                ConnectWarmupMs = 1,
                PollIntervalMs = 1,
                ReconnectIntervalMs = 1,
            },
        };
        var executor = new CodexActionExecutor(
            new SafetyOptions { DryRun = true },
            logs.Add,
            new UnusedResolver(),
            new OpenWorkingDirectoryOptions());
        await using var worker = new CompanionWorker(
            config,
            source,
            executor,
            logs.Add,
            lifecycle);

        worker.Start();
        await lifecycle.ReleaseRetried.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(lifecycle.ReleaseCalls >= 2);
        Assert.Equal(lifecycle.ReleasedSources[0], lifecycle.ReleasedSources[1]);
        Assert.Contains(logs, message => message.Contains("release failed", StringComparison.Ordinal));
        await worker.StopAsync();
    }

    [Fact]
    public async Task FailedCaptureCleanupIsRetriedBeforeInputDispatchResumes()
    {
        var logs = new List<string>();
        var source = new CaptureCleanupJoystickSource();
        var lifecycle = new RecordingKeyStateLifecycle([])
        {
            ReleaseFailuresRemaining = 1,
        };
        using var host = new RuntimeInputHost();
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.InputObserved += (_, _) => observed.TrySetResult();
        host.CaptureChanged += (_, update) =>
        {
            if (update.Lease.Status == InputCaptureStatus.Failed)
            {
                captureFailed.TrySetResult();
            }
        };
        var executor = new CodexActionExecutor(
            new SafetyOptions { DryRun = true },
            logs.Add,
            new UnusedResolver(),
            new OpenWorkingDirectoryOptions());
        await using var worker = new CompanionWorker(
            new CompanionConfig
            {
                Polling = new PollingOptions
                {
                    ConnectWarmupMs = 1,
                    PollIntervalMs = 1,
                    ReconnectIntervalMs = 1,
                },
            },
            source,
            executor,
            logs.Add,
            lifecycle,
            inputHost: host);

        worker.Start();
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var connected = Assert.Single(host.Sources, state => state.Connected);
        var capture = host.BeginCapture(new InputCaptureRequest(
            "ui-1",
            connected.Descriptor.SourceId,
            "binding",
            connected.Generation));
        source.EmitPress();

        await captureFailed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await lifecycle.ReleaseRetried.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(capture.Accepted);
        Assert.Equal(lifecycle.ReleasedSources[0], lifecycle.ReleasedSources[1]);
        Assert.DoesNotContain(logs, message => message.Contains("INPUT press", StringComparison.Ordinal));
        await worker.StopAsync();
    }

    [Fact]
    public async Task WorkerShutdownRetriesGenerationCleanupBeforeCompleting()
    {
        var logs = new List<string>();
        var lifecycle = new RecordingKeyStateLifecycle([])
        {
            ReleaseFailuresRemaining = 2,
        };
        using var host = new RuntimeInputHost();
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.InputObserved += (_, _) => observed.TrySetResult();
        var executor = new CodexActionExecutor(
            new SafetyOptions { DryRun = true },
            logs.Add,
            new UnusedResolver(),
            new OpenWorkingDirectoryOptions());
        await using var worker = new CompanionWorker(
            new CompanionConfig
            {
                Polling = new PollingOptions
                {
                    ConnectWarmupMs = 1,
                    PollIntervalMs = 1,
                    ReconnectIntervalMs = 1,
                },
            },
            new StableJoystickSource(),
            executor,
            logs.Add,
            lifecycle,
            inputHost: host);

        worker.Start();
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await worker.StopAsync();

        Assert.Equal(3, lifecycle.ReleaseCalls);
        Assert.All(lifecycle.ReleasedSources, source => Assert.Equal(lifecycle.ReleasedSources[0], source));
        Assert.Contains(logs, message => message.Contains("release failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CaptureCleanupHidesOnlyMapsHeldByItsSourceAndMapFailureDoesNotBlockActivation()
    {
        var logs = new List<string>();
        var config = new CompanionConfig
        {
            Devices =
            [
                new DeviceProfile
                {
                    Id = "stick-a",
                    DisplayName = "Stick A",
                    ButtonMapHoldControl = new DeviceControlReference
                    {
                        DeviceId = "stick-a",
                        Button = 1,
                    },
                },
                new DeviceProfile
                {
                    Id = "stick-b",
                    DisplayName = "Stick B",
                    ButtonMapHoldControl = new DeviceControlReference
                    {
                        DeviceId = "stick-b",
                        Button = 1,
                    },
                },
            ],
            Polling = new PollingOptions
            {
                ConnectWarmupMs = 1,
                PollIntervalMs = 1,
                ReconnectIntervalMs = 1,
            },
        };
        using var host = new RuntimeInputHost();
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.InputObserved += (_, _) => observed.TrySetResult();
        host.CaptureChanged += (_, update) =>
        {
            if (update.Lease.Status == InputCaptureStatus.Active)
            {
                active.TrySetResult();
            }
        };
        var hiddenMaps = new List<string>();
        var executor = new CodexActionExecutor(
            new SafetyOptions { DryRun = true },
            logs.Add,
            new UnusedResolver(),
            new OpenWorkingDirectoryOptions());
        await using var worker = new CompanionWorker(
            config,
            new StableJoystickSource(),
            executor,
            logs.Add,
            new RecordingKeyStateLifecycle([]),
            deviceId: "stick-a",
            buttonMapHandler: request =>
            {
                hiddenMaps.Add(request.DeviceId);
                throw new InvalidOperationException("closed map window");
            },
            inputHost: host);
        worker.Start();
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var source = Assert.Single(host.Sources, state => state.Descriptor.SourceId == "stick-a");

        var capture = host.BeginCapture(new InputCaptureRequest(
            "ui-1",
            "stick-a",
            "binding",
            source.Generation));
        await active.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(capture.Accepted);
        Assert.Equal(["stick-a"], hiddenMaps);
        Assert.Contains(logs, message => message.Contains("closed map window", StringComparison.Ordinal));
        await worker.StopAsync();
    }

    private sealed class RecordingKeyStateLifecycle(List<string> callOrder) : IInjectedKeyStateLifecycle
    {
        public Exception? ClearFailure { get; init; }

        public int ReleaseFailuresRemaining { get; init; }

        public int ClearCalls { get; private set; }

        public int ReleaseCalls { get; private set; }

        public List<InputSourceSession> ReleasedSources { get; } = [];

        public TaskCompletionSource ReleaseRetried { get; } = new(
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

        public void ReleaseHeldKeys(InputSourceSession source)
        {
            ReleasedSources.Add(source);
            ReleaseCalls++;
            if (ReleaseCalls <= ReleaseFailuresRemaining)
            {
                throw new InvalidOperationException("release failed");
            }
            if (ReleaseCalls > 1)
            {
                ReleaseRetried.TrySetResult();
            }
        }

        public void ReleaseAllHeldKeys() => ReleaseCalls++;
    }

    private sealed class DisconnectingJoystickSource : IJoystickSource
    {
        private int _readCount;

        public DirectInputDeviceInfo? ConnectedDevice { get; private set; }

        public IReadOnlyList<JoystickEvent> LatestBufferedButtonEvents => [];

        public bool TryConnect(DeviceSelector selector, out string message)
        {
            ConnectedDevice = new DirectInputDeviceInfo(
                "Synthetic Stick",
                "Synthetic Stick",
                Guid.NewGuid(),
                Guid.NewGuid());
            message = "Connected to synthetic stick.";
            return true;
        }

        public bool TryRead(out JoystickSnapshot? snapshot, out string? error)
        {
            _readCount++;
            if (_readCount == 2)
            {
                ConnectedDevice = null;
                snapshot = null;
                error = "synthetic disconnect";
                return false;
            }

            snapshot = new JoystickSnapshot(DateTimeOffset.UtcNow, new bool[8], [-1], [0]);
            error = null;
            return true;
        }

        public void Disconnect() => ConnectedDevice = null;

        public void Dispose()
        {
        }
    }

    private sealed class StableJoystickSource : IJoystickSource
    {
        public DirectInputDeviceInfo? ConnectedDevice { get; private set; }

        public IReadOnlyList<JoystickEvent> LatestBufferedButtonEvents => [];

        public bool TryConnect(DeviceSelector selector, out string message)
        {
            ConnectedDevice = new DirectInputDeviceInfo(
                "Synthetic Stick",
                "Synthetic Stick",
                Guid.NewGuid(),
                Guid.NewGuid());
            message = "Connected to synthetic stick.";
            return true;
        }

        public bool TryRead(out JoystickSnapshot? snapshot, out string? error)
        {
            snapshot = new JoystickSnapshot(DateTimeOffset.UtcNow, new bool[8], [-1], [0]);
            error = null;
            return true;
        }

        public void Disconnect() => ConnectedDevice = null;

        public void Dispose()
        {
        }
    }

    private sealed class CaptureCleanupJoystickSource : IJoystickSource
    {
        private readonly ManualResetEventSlim _emitPress = new(false);
        private int _readCount;
        private bool _pressed;
        private IReadOnlyList<JoystickEvent> _latestBufferedButtonEvents = [];

        public DirectInputDeviceInfo? ConnectedDevice { get; private set; }

        public IReadOnlyList<JoystickEvent> LatestBufferedButtonEvents => _latestBufferedButtonEvents;

        public bool TryConnect(DeviceSelector selector, out string message)
        {
            ConnectedDevice = new DirectInputDeviceInfo(
                "Synthetic Stick",
                "Synthetic Stick",
                Guid.NewGuid(),
                Guid.NewGuid());
            message = "Connected to synthetic stick.";
            return true;
        }

        public bool TryRead(out JoystickSnapshot? snapshot, out string? error)
        {
            _readCount++;
            if (_readCount == 2)
            {
                _emitPress.Wait();
                _pressed = true;
                _latestBufferedButtonEvents =
                [
                    new JoystickEvent(JoystickEventKind.ButtonPressed, 0, 1),
                ];
            }
            else
            {
                _latestBufferedButtonEvents = [];
            }

            snapshot = new JoystickSnapshot(
                DateTimeOffset.UtcNow,
                [_pressed, false, false, false, false, false, false, false],
                [-1],
                [0]);
            error = null;
            return true;
        }

        public void EmitPress() => _emitPress.Set();

        public void Disconnect()
        {
            _emitPress.Set();
            ConnectedDevice = null;
        }

        public void Dispose()
        {
            _emitPress.Set();
            _emitPress.Dispose();
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
