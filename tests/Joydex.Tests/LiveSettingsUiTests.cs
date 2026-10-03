using System.Runtime.ExceptionServices;
using Joydex.App;
using Joydex.Core.Config;
using Joydex.Core.Input;
using Joydex.Core.Mapping;
using Joydex.Core.Runtime;
using Joydex.Core.Voice;
using Joydex.Windows.Actions;
using Joydex.Windows.Input;
using Joydex.Windows.Runtime;

namespace Joydex.Tests;

public sealed class LiveSettingsUiTests
{
    [Fact]
    public void DemoTrayOpeningAndCancellingSettingsKeepsConfiguredWorkersAndSourceGenerations()
    {
        var directory = CreateDirectory();
        try
        {
            var configPath = Path.Combine(directory, "config.json");
            var factory = new DemoJoystickSourceFactory();
            ConfigStore.Save(configPath, DemoConfig(factory));

            RunSta(() =>
            {
                TrayApplicationContext? tray = null;
                try
                {
                    tray = new TrayApplicationContext(
                        configPath,
                        demoMode: true,
                        demoInputSourceFactory: factory);
                    WaitWithoutPumping(() => ConnectedSources(tray).Count == 2);
                    var workersBeforeOpen = tray.WorkersForTesting.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value,
                        StringComparer.OrdinalIgnoreCase);
                    var generationsBeforeOpen = ConnectedSources(tray);

                    PumpUntil(() => tray.ConfigurationFormForTesting is { Visible: true });

                    AssertSameWorkers(tray, workersBeforeOpen);
                    AssertSameSourceGenerations(tray, generationsBeforeOpen);

                    var form = Assert.IsType<ConfigurationForm>(tray.ConfigurationFormForTesting);
                    var cancel = Assert.IsAssignableFrom<Button>(Assert.Single(
                        form.Controls.Find("ConfigurationCancel", searchAllChildren: true)));
                    cancel.PerformClick();
                    PumpUntil(() => tray.ConfigurationFormForTesting is null && form.IsDisposed);

                    AssertSameWorkers(tray, workersBeforeOpen);
                    AssertSameSourceGenerations(tray, generationsBeforeOpen);
                }
                finally
                {
                    if (tray is not null)
                    {
                        tray.BeginExitForTesting();
                        PumpUntil(() => tray.ExitCompletedForTesting);
                        tray.Dispose();
                    }
                }
            }, TimeSpan.FromSeconds(10));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DemoConfigurationIsClearlyLabeledAndLocksDryRun()
    {
        var directory = CreateDirectory();
        try
        {
            RunSta(() =>
            {
                using var form = new ConfigurationForm(
                    Path.Combine(directory, "config.json"),
                    Path.Combine(directory, "window.json"),
                    IntPtr.Zero,
                    initialConfig: Config(Guid.NewGuid(), "controller"),
                    demoMode: true);

                Assert.Contains("Demo", form.Text, StringComparison.OrdinalIgnoreCase);
                var dryRun = Assert.IsType<CheckBox>(Assert.Single(
                    form.Controls.Find("ConfigurationDryRun", searchAllChildren: true)));
                Assert.True(dryRun.Checked);
                Assert.False(dryRun.Enabled);
                Assert.Contains("locked", dryRun.Text, StringComparison.OrdinalIgnoreCase);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OpeningAndCancellingSettingsKeepsTheSameControllerWorkerConnected()
    {
        var directory = CreateDirectory();
        try
        {
            var instanceGuid = Guid.NewGuid();
            var config = Config(instanceGuid, "controller");
            using var inputHost = new RuntimeInputHost();
            var source = new ContinuousJoystickSource(instanceGuid);
            var executor = new CodexActionExecutor(
                config.Safety,
                _ => { },
                new UnusedResolver(),
                config.OpenWorkingDirectory);
            await using var worker = new CompanionWorker(
                config,
                source,
                executor,
                _ => { },
                new NoOpKeyStateLifecycle(),
                deviceId: "controller",
                inputHost: inputHost);
            worker.Start();

            var originalSession = await WaitForConnectedSourceAsync(inputHost, "controller");
            var readsBeforeOpen = source.ReadCount;
            var provider = new CatalogOnlyProvider(inputHost, config, instanceGuid);
            using var client = new ConfigurationInputClient(
                inputHost,
                provider,
                config,
                connectionId: "settings-test");

            RunSta(() =>
            {
                using var form = new ConfigurationForm(
                    Path.Combine(directory, "config.json"),
                    Path.Combine(directory, "window.json"),
                    IntPtr.Zero,
                    initialConfig: config,
                    inputClient: client);
                form.Show();
                PumpMessages(TimeSpan.FromMilliseconds(100));
                form.DialogResult = DialogResult.Cancel;
                form.Close();
            });

            await WaitUntilAsync(() => source.ReadCount > readsBeforeOpen);
            var currentSession = Assert.Single(inputHost.Sources, item =>
                string.Equals(item.Descriptor.SourceId, "controller", StringComparison.OrdinalIgnoreCase));
            Assert.True(currentSession.Connected);
            Assert.Equal(originalSession.Generation, currentSession.Generation);
            Assert.Equal(0, source.DisposeCount);
            Assert.Equal(0, source.DisconnectCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CaptureUsesTheInjectedRuntimeSourceAndRequiresAFreshPress()
    {
        var configuredGuid = Guid.NewGuid();
        var observedGuid = Guid.NewGuid();
        var config = Config(configuredGuid, "configured");
        using var inputHost = new RuntimeInputHost();
        var source = new ScriptedJoystickSource(
            observedGuid,
            Snapshot(buttonOne: true, buttonTwo: false),
            Snapshot(buttonOne: false, buttonTwo: false),
            Snapshot(buttonOne: false, buttonTwo: true));
        var factory = new RecordingJoystickSourceFactory(source);
        await using var provider = new RuntimeInputSourceProvider(
            inputHost,
            factory,
            new PollingOptions
            {
                ConnectWarmupMs = 0,
                PollIntervalMs = 1,
                ReconnectIntervalMs = 1,
            },
            _ => { });
        using var client = new ConfigurationInputClient(
            inputHost,
            provider,
            config,
            connectionId: "capture-test");
        var entry = Assert.Single(client.RefreshSources());
        Assert.Null(entry.ConfiguredDeviceId);
        var completed = new TaskCompletionSource<InputCaptureChangedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var statuses = new List<InputCaptureStatus>();
        client.CaptureChanged += (_, eventArgs) =>
        {
            lock (statuses)
            {
                statuses.Add(eventArgs.Lease.Status);
            }
            if (eventArgs.Lease.Status == InputCaptureStatus.Completed)
            {
                completed.TrySetResult(eventArgs);
            }
        };

        var started = client.BeginCapture(entry.Source.SourceId, "test capture");
        Assert.True(started.Accepted, started.Error);
        Assert.Equal(InputCaptureStatus.Pending, started.Lease?.Status);
        Assert.True(client.ObserveForCapture(entry.Source.SourceId));

        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, result.CapturedInput?.DisplayIndex);
        Assert.Equal(1, factory.CreateCount);
        lock (statuses)
        {
            Assert.Contains(InputCaptureStatus.Active, statuses);
        }
    }

    [Fact]
    public async Task ClosingSettingsReleasesAnEmbeddedPickerCaptureSource()
    {
        var directory = CreateDirectory();
        try
        {
            var config = Config(Guid.NewGuid(), "configured");
            using var inputHost = new RuntimeInputHost();
            var source = new TrackingIdleJoystickSource(Guid.NewGuid());
            var factory = new TrackingJoystickSourceFactory(source);
            await using var provider = new RuntimeInputSourceProvider(
                inputHost,
                factory,
                config.Polling,
                _ => { });
            using var client = new ConfigurationInputClient(
                inputHost,
                provider,
                config,
                connectionId: "embedded-picker-close-test");

            RunSta(() =>
            {
                using var form = new ConfigurationForm(
                    Path.Combine(directory, "config.json"),
                    Path.Combine(directory, "window.json"),
                    IntPtr.Zero,
                    initialConfig: config,
                    inputClient: client);
                form.Show();
                form.SelectPage("Prompt Pickers");
                PumpMessages(TimeSpan.FromMilliseconds(50));

                foreach (var combo in Descendants<ComboBox>(form).Where(HasInjectedSourceChoice))
                {
                    combo.SelectedItem = combo.Items.Cast<object>().First(item =>
                        Convert.ToString(item)?.Contains("Injected source", StringComparison.Ordinal) == true);
                }
                var capture = Descendants<Button>(form).First(button =>
                    string.Equals(button.Text, "Capture", StringComparison.Ordinal));
                capture.PerformClick();
                PumpMessages(TimeSpan.FromMilliseconds(50));

                Assert.Equal(1, factory.CreateCount);
                Assert.NotNull(source.ConnectedDevice);
                form.DialogResult = DialogResult.Cancel;
                form.Close();
            });

            await WaitUntilAsync(() => source.DisposeCount > 0);
            Assert.True(source.DisconnectCount > 0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SeparatelyLoadedUnchangedPreferencesDoNotRequestRuntimeRestarts()
    {
        var directory = CreateDirectory();
        try
        {
            var configPath = Path.Combine(directory, "config.json");
            var voicePath = Path.Combine(directory, "voice.json");
            var pebblePath = Path.Combine(directory, "pebble.json");
            var config = Config(Guid.NewGuid(), "controller");
            ConfigStore.Save(configPath, config);
            VoicePePreferencesStore.Save(voicePath, VoicePePreferences.Default);
            PebbleIndexPreferencesStore.Save(pebblePath, PebbleIndexPreferences.Default);

            var changes = ConfigurationChangeDetector.Detect(
                ConfigStore.LoadOrCreate(configPath),
                ConfigStore.LoadOrCreate(configPath),
                VoicePePreferencesStore.LoadOrCreate(voicePath),
                VoicePePreferencesStore.LoadOrCreate(voicePath),
                PebbleIndexPreferencesStore.LoadOrCreate(pebblePath),
                PebbleIndexPreferencesStore.LoadOrCreate(pebblePath));

            Assert.False(changes.CompanionChanged);
            Assert.False(changes.VoicePreferencesChanged);
            Assert.False(changes.PebbleIndexPreferencesChanged);
            Assert.False(changes.VoiceRuntimeChanged);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static CompanionConfig Config(Guid instanceGuid, string deviceId) => new()
    {
        Device = new DeviceSelector
        {
            ProductNameContains = "Synthetic controller",
            InstanceGuid = instanceGuid.ToString("D"),
            ProductGuid = Guid.NewGuid().ToString("D"),
        },
        Devices =
        [
            new DeviceProfile
            {
                Id = deviceId,
                DisplayName = "Synthetic controller",
                Selector = new DeviceSelector
                {
                    ProductNameContains = "Synthetic controller",
                    InstanceGuid = instanceGuid.ToString("D"),
                    ProductGuid = Guid.NewGuid().ToString("D"),
                },
                ButtonMapTemplate = "cm3",
            },
        ],
        Polling = new PollingOptions
        {
            ConnectWarmupMs = 0,
            PollIntervalMs = 5,
            ReconnectIntervalMs = 250,
        },
        Safety = new SafetyOptions { DryRun = true },
    };

    private static JoystickSnapshot Snapshot(bool buttonOne, bool buttonTwo) => new(
        DateTimeOffset.UtcNow,
        [buttonOne, buttonTwo],
        [],
        []);

    private static async Task<InputSourceState> WaitForConnectedSourceAsync(
        RuntimeInputHost host,
        string sourceId)
    {
        InputSourceState? result = null;
        await WaitUntilAsync(() =>
        {
            result = host.Sources.FirstOrDefault(item =>
                string.Equals(item.Descriptor.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
            return result?.Connected == true;
        });
        return result!;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static void PumpMessages(TimeSpan duration)
    {
        var until = DateTimeOffset.UtcNow + duration;
        while (DateTimeOffset.UtcNow < until)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    private static void RunSta(Action action, TimeSpan? timeout = null)
    {
        Exception? failure = null;
        using var complete = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                complete.Set();
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(
            complete.Wait(timeout ?? TimeSpan.FromSeconds(5)),
            "The settings form did not close.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void WaitWithoutPumping(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (!predicate())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The expected runtime state did not arrive.");
            Thread.Sleep(10);
        }
    }

    private static void PumpUntil(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
        while (!predicate())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The expected UI state did not arrive.");
            Application.DoEvents();
            Thread.Sleep(5);
        }
        Application.DoEvents();
    }

    private static Dictionary<string, long?> ConnectedSources(TrayApplicationContext tray) =>
        tray.InputSourcesForTesting
            .Where(source => source.Connected)
            .ToDictionary(
                source => source.Descriptor.SourceId,
                source => source.Generation,
                StringComparer.OrdinalIgnoreCase);

    private static void AssertSameSourceGenerations(
        TrayApplicationContext tray,
        IReadOnlyDictionary<string, long?> expected)
    {
        var actual = ConnectedSources(tray);
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (sourceId, generation) in expected)
        {
            Assert.Equal(generation, actual[sourceId]);
        }
    }

    private static void AssertSameWorkers(
        TrayApplicationContext tray,
        IReadOnlyDictionary<string, CompanionWorker> expected)
    {
        Assert.Equal(expected.Keys.Order(), tray.WorkersForTesting.Keys.Order());
        foreach (var (deviceId, worker) in expected)
        {
            Assert.Same(worker, tray.WorkersForTesting[deviceId]);
        }
    }

    private static CompanionConfig DemoConfig(DemoJoystickSourceFactory factory)
    {
        var devices = factory.EnumerateDevices();
        var profiles = devices.Select((device, index) => new DeviceProfile
        {
            Id = index == 0 ? "cm3" : "warbrd",
            DisplayName = device.ProductName,
            Selector = new DeviceSelector
            {
                ProductNameContains = device.ProductName,
                InstanceGuid = device.InstanceGuid.ToString("D"),
                ProductGuid = device.ProductGuid.ToString("D"),
            },
            ButtonMapTemplate = index == 0 ? "cm3" : "alpha-warbrd",
        }).ToList();
        return new CompanionConfig
        {
            Device = profiles[0].Selector,
            Devices = profiles,
            Polling = new PollingOptions
            {
                ConnectWarmupMs = 0,
                PollIntervalMs = 5,
                ReconnectIntervalMs = 250,
            },
            Safety = new SafetyOptions { DryRun = true },
        };
    }

    private static bool HasInjectedSourceChoice(ComboBox combo) => combo.Items
        .Cast<object>()
        .Any(item => Convert.ToString(item)?.Contains("Injected source", StringComparison.Ordinal) == true);

    private static IEnumerable<T> Descendants<T>(Control parent) where T : Control
    {
        foreach (Control child in parent.Controls)
        {
            if (child is T match)
            {
                yield return match;
            }
            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "joydex-live-settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class CatalogOnlyProvider(
        RuntimeInputHost inputHost,
        CompanionConfig config,
        Guid instanceGuid) : IRuntimeInputSourceProvider
    {
        public IReadOnlyList<RuntimeInputSourceCatalogEntry> Refresh(CompanionConfig activeConfig)
        {
            var device = config.Devices[0];
            var entry = new RuntimeInputSourceCatalogEntry(
                new InputSourceDescriptor(device.Id, device.DisplayName, instanceGuid.ToString("D")),
                device.Selector,
                instanceGuid,
                Guid.Parse(device.Selector.ProductGuid!),
                device.Id);
            inputHost.PublishAvailableSources([entry.Source]);
            return [entry];
        }

        public bool ObserveForCapture(string sourceId) => true;

        public void ReleaseCaptureObservation(string sourceId)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ContinuousJoystickSource(Guid instanceGuid) : IJoystickSource
    {
        private DirectInputDeviceInfo? _connected;
        private int _readCount;
        private int _disconnectCount;
        private int _disposeCount;

        public DirectInputDeviceInfo? ConnectedDevice => _connected;
        public IReadOnlyList<JoystickEvent> LatestBufferedButtonEvents => [];
        public int ReadCount => Volatile.Read(ref _readCount);
        public int DisconnectCount => Volatile.Read(ref _disconnectCount);
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public bool TryConnect(DeviceSelector selector, out string message)
        {
            _connected = new DirectInputDeviceInfo(
                "Synthetic controller",
                "Synthetic controller",
                instanceGuid,
                Guid.Parse(selector.ProductGuid!));
            message = "Connected synthetic controller.";
            return true;
        }

        public bool TryRead(out JoystickSnapshot? snapshot, out string? error)
        {
            Interlocked.Increment(ref _readCount);
            snapshot = Snapshot(buttonOne: false, buttonTwo: false);
            error = null;
            return true;
        }

        public void Disconnect()
        {
            Interlocked.Increment(ref _disconnectCount);
            _connected = null;
        }

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class ScriptedJoystickSource : IJoystickSource
    {
        private readonly JoystickSnapshot[] _snapshots;
        private int _nextSnapshot;

        public ScriptedJoystickSource(Guid instanceGuid, params JoystickSnapshot[] snapshots)
        {
            InstanceGuid = instanceGuid;
            _snapshots = snapshots;
        }

        public Guid InstanceGuid { get; }
        public DirectInputDeviceInfo? ConnectedDevice { get; private set; }
        public IReadOnlyList<JoystickEvent> LatestBufferedButtonEvents => [];

        public bool TryConnect(DeviceSelector selector, out string message)
        {
            ConnectedDevice = new DirectInputDeviceInfo(
                "Injected source",
                "Injected source",
                InstanceGuid,
                Guid.NewGuid());
            message = "Connected injected source.";
            return true;
        }

        public bool TryRead(out JoystickSnapshot? snapshot, out string? error)
        {
            var index = Math.Min(Interlocked.Increment(ref _nextSnapshot) - 1, _snapshots.Length - 1);
            snapshot = _snapshots[index];
            error = null;
            return true;
        }

        public void Disconnect() => ConnectedDevice = null;

        public void Dispose()
        {
        }
    }

    private sealed class RecordingJoystickSourceFactory(ScriptedJoystickSource source) : IJoystickSourceFactory
    {
        private int _createCount;
        public int CreateCount => Volatile.Read(ref _createCount);

        public IReadOnlyList<DirectInputDeviceInfo> EnumerateDevices() =>
        [
            new DirectInputDeviceInfo(
                "Injected source",
                "Injected source",
                source.InstanceGuid,
                Guid.NewGuid()),
        ];

        public IJoystickSource Create()
        {
            Interlocked.Increment(ref _createCount);
            return source;
        }

    }

    private sealed class TrackingJoystickSourceFactory(TrackingIdleJoystickSource source) : IJoystickSourceFactory
    {
        private int _createCount;
        public int CreateCount => Volatile.Read(ref _createCount);

        public IReadOnlyList<DirectInputDeviceInfo> EnumerateDevices() =>
        [
            new DirectInputDeviceInfo(
                "Injected source",
                "Injected source",
                source.InstanceGuid,
                Guid.NewGuid()),
        ];

        public IJoystickSource Create()
        {
            Interlocked.Increment(ref _createCount);
            return source;
        }
    }

    private sealed class TrackingIdleJoystickSource(Guid instanceGuid) : IJoystickSource
    {
        private int _disconnectCount;
        private int _disposeCount;

        public Guid InstanceGuid { get; } = instanceGuid;
        public DirectInputDeviceInfo? ConnectedDevice { get; private set; }
        public IReadOnlyList<JoystickEvent> LatestBufferedButtonEvents => [];
        public int DisconnectCount => Volatile.Read(ref _disconnectCount);
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public bool TryConnect(DeviceSelector selector, out string message)
        {
            ConnectedDevice = new DirectInputDeviceInfo(
                "Injected source",
                "Injected source",
                InstanceGuid,
                Guid.NewGuid());
            message = "Connected injected source.";
            return true;
        }

        public bool TryRead(out JoystickSnapshot? snapshot, out string? error)
        {
            snapshot = Snapshot(buttonOne: false, buttonTwo: false);
            error = null;
            return true;
        }

        public void Disconnect()
        {
            Interlocked.Increment(ref _disconnectCount);
            ConnectedDevice = null;
        }

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class NoOpKeyStateLifecycle : IInjectedKeyStateLifecycle
    {
        public void ClearInjectedKeyState()
        {
        }

        public void ReleaseHeldKeys(InputSourceSession source)
        {
        }

        public void ReleaseAllHeldKeys()
        {
        }
    }

    private sealed class UnusedResolver : ICodexKeybindingResolver
    {
        public Task<CodexBindingResolution> ResolveAsync(
            CodexAction action,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The settings continuity test dispatches no actions.");
    }
}
