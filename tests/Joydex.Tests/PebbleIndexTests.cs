using Joydex.App;
using Joydex.Core.Config;
using Joydex.Core.Voice;
using Joydex.Windows.Voice;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace Joydex.Tests;

public sealed class PebbleIndexTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "joydex-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void PreferencesRoundTripAndNormalizeTask()
    {
        var path = Path.Combine(_directory, "pebble-index.json");
        var id = Guid.NewGuid();
        PebbleIndexPreferencesStore.Save(path, new PebbleIndexPreferences(
            Enabled: true, Port: 5190, TargetTaskId: $"codex://threads/{id:D}",
            TargetHostId: " local ", TargetTaskLabel: " Target "));

        var loaded = PebbleIndexPreferencesStore.LoadOrCreate(path);
        Assert.Equal(id.ToString("D"), loaded.TargetTaskId);
        Assert.Equal("local", loaded.TargetHostId);
        Assert.Equal("Target", loaded.TargetTaskLabel);
        Assert.Equal(5190, loaded.Port);
    }

    [Fact]
    public void NullPreferenceStringsNormalizeToEmpty()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "pebble-index.json");
        File.WriteAllText(path,
            """
            {
              "schemaVersion": 1,
              "enabled": false,
              "port": 5187,
              "targetTaskId": null,
              "targetHostId": null,
              "targetTaskLabel": null
            }
            """);

        var loaded = PebbleIndexPreferencesStore.LoadOrCreate(path);

        Assert.Equal(string.Empty, loaded.TargetTaskId);
        Assert.Equal(string.Empty, loaded.TargetHostId);
        Assert.Equal(string.Empty, loaded.TargetTaskLabel);
    }

    [Fact]
    public void DuplicateWebhookIsAcceptedOnceAcrossStoreInstances()
    {
        var inbox = Path.Combine(_directory, "inbox");
        var task = Guid.NewGuid().ToString("D");
        var preferences = new PebbleIndexPreferences(
            Enabled: true, Port: 5187, TargetTaskId: task, TargetHostId: "local", TargetTaskLabel: "Target");
        var first = new PebbleIndexDeliveryStore(inbox).Accept("do the thing", "123", "ring", "tap", null, preferences);
        var second = new PebbleIndexDeliveryStore(inbox).Accept("do the thing", "123", "ring", "tap", null, preferences);

        Assert.False(first.IsDuplicate);
        Assert.True(second.IsDuplicate);
        Assert.Equal(first.Delivery.Id, second.Delivery.Id);
        Assert.Single(Directory.EnumerateFiles(inbox, "*.json"));
    }

    [Fact]
    public async Task ConcurrentDuplicateWebhookIsPublishedOnceAcrossStoreInstances()
    {
        var inbox = Path.Combine(_directory, "concurrent-inbox");
        var task = Guid.NewGuid().ToString("D");
        var preferences = new PebbleIndexPreferences(
            Enabled: true, Port: 5187, TargetTaskId: task, TargetHostId: "local", TargetTaskLabel: "Target");
        using var readyToPublish = new Barrier(2);
        Action beforePublish = () =>
            Assert.True(readyToPublish.SignalAndWait(TimeSpan.FromSeconds(5)));
        var stores = new[]
        {
            new PebbleIndexDeliveryStore(inbox, beforePublish),
            new PebbleIndexDeliveryStore(inbox, beforePublish),
        };
        var attempts = stores
            .Select(store => Task.Run(() => store.Accept(
                    "do the thing",
                    "123",
                    "ring",
                    "tap",
                    "delivery-one",
                    preferences)))
            .ToArray();

        var results = await Task.WhenAll(attempts);

        Assert.Single(results, result => !result.IsDuplicate);
        Assert.Single(results, result => result.IsDuplicate);
        Assert.Single(results.Select(result => result.Delivery.Id).Distinct(StringComparer.Ordinal));
        Assert.Single(Directory.EnumerateFiles(inbox, "*.json"));
        Assert.Empty(Directory.EnumerateFiles(inbox, "*.tmp"));
        Assert.Single(new PebbleIndexDeliveryStore(inbox).Recent());
    }

    [Fact]
    public void ReusedProvidedDeliveryIdMustMatchTheOriginalPayload()
    {
        var store = new PebbleIndexDeliveryStore(Path.Combine(_directory, "inbox"));
        var preferences = new PebbleIndexPreferences(
            Enabled: true, TargetTaskId: Guid.NewGuid().ToString("D"),
            TargetHostId: "local", TargetTaskLabel: "Target");
        store.Accept("first", "123", "ring", "tap", "delivery-one", preferences);

        var exception = Assert.Throws<InvalidDataException>(() =>
            store.Accept("second", "123", "ring", "tap", "delivery-one", preferences));

        Assert.Contains("different Pebble Index payload", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeliveryStateIsPersisted()
    {
        var store = new PebbleIndexDeliveryStore(Path.Combine(_directory, "inbox"));
        var preferences = new PebbleIndexPreferences(
            Enabled: true, TargetTaskId: Guid.NewGuid().ToString("D"), TargetHostId: "local", TargetTaskLabel: "Target");
        var accepted = store.Accept("hello", "456", "ring", "", "delivery-one", preferences);
        store.Update(accepted.Delivery.Id, PebbleIndexDeliveryState.Sent, "Delivered.");

        var loaded = Assert.Single(new PebbleIndexDeliveryStore(Path.Combine(_directory, "inbox")).Recent());
        Assert.Equal(PebbleIndexDeliveryState.Sent, loaded.State);
        Assert.NotNull(loaded.CompletedAt);
    }

    [Fact]
    public void SettingsControlPreservesFixedTarget()
    {
        var preferences = new PebbleIndexPreferences(
            Enabled: true, Port: 5191, TargetTaskId: Guid.NewGuid().ToString("D"),
            TargetHostId: "local", TargetTaskLabel: "Chosen task");
        using var control = new PebbleIndexSettingsControl(
            preferences,
            Path.Combine(_directory, "secret"),
            Path.Combine(_directory, "inbox"),
            (_, _) => Task.FromResult(new DesktopTaskCatalog([])),
            new PebbleIndexReceiverStatus(false, "off"));

        var read = control.ReadPreferences();

        Assert.Equal(preferences, read);
    }

    [Fact]
    public void SettingsControlAcceptsTypedTaskIdForStandaloneBootstrap()
    {
        var taskId = Guid.NewGuid().ToString("D");
        using var control = new PebbleIndexSettingsControl(
            PebbleIndexPreferences.Default,
            Path.Combine(_directory, "secret"),
            Path.Combine(_directory, "inbox"),
            (_, _) => Task.FromResult(new DesktopTaskCatalog([])),
            new PebbleIndexReceiverStatus(false, "off"));
        var target = Assert.IsType<ComboBox>(
            Assert.Single(control.Controls.Find("PebbleIndexTargetTask", searchAllChildren: true)));
        target.Text = taskId;

        var read = control.ReadPreferences();

        Assert.Equal(taskId, read.TargetTaskId);
        Assert.Equal("local", read.TargetHostId);
        Assert.Equal($"Task {taskId[..8]}", read.TargetTaskLabel);
    }

    [Fact]
    public void RefreshWithNoTargetRequiresAnExplicitSelection()
    {
        var task = new DesktopTaskSummary(
            Guid.NewGuid().ToString("D"), "local", "Incidental task", "idle", null, null, 0);
        using var control = new PebbleIndexSettingsControl(
            PebbleIndexPreferences.Default,
            Path.Combine(_directory, "secret"),
            Path.Combine(_directory, "inbox"),
            (_, _) => Task.FromResult(new DesktopTaskCatalog([task])),
            new PebbleIndexReceiverStatus(false, "off"));
        var refresh = Assert.IsType<RoundedButton>(
            Assert.Single(control.Controls.Find("PebbleIndexRefreshTasks", searchAllChildren: true)));

        refresh.PerformClick();

        var target = Assert.IsType<ComboBox>(
            Assert.Single(control.Controls.Find("PebbleIndexTargetTask", searchAllChildren: true)));
        Assert.Equal(-1, target.SelectedIndex);
        Assert.Equal(string.Empty, control.ReadPreferences().TargetTaskId);
    }

    [Fact]
    public void SettingsSurfaceStoredDeliveriesThatNeedManualReview()
    {
        var inbox = Path.Combine(_directory, "recovery-inbox");
        var store = new PebbleIndexDeliveryStore(inbox);
        var deliveryPreferences = new PebbleIndexPreferences(
            TargetTaskId: Guid.NewGuid().ToString("D"),
            TargetHostId: "local",
            TargetTaskLabel: "Target");
        var accepted = store.Accept("private transcript", "123", "ring", "tap", null, deliveryPreferences);
        store.Update(accepted.Delivery.Id, PebbleIndexDeliveryState.DeliveryUncertain, "Confirmation was lost.");
        var status = PebbleIndexReceiverRuntime.ReadStoredStatus(false, "Receiver is off.", inbox);

        using var control = new PebbleIndexSettingsControl(
            PebbleIndexPreferences.Default,
            Path.Combine(_directory, "secret"),
            inbox,
            (_, _) => Task.FromResult(new DesktopTaskCatalog([])),
            status);

        var label = Assert.IsType<Label>(
            Assert.Single(control.Controls.Find("PebbleIndexStatus", searchAllChildren: true)));
        Assert.Contains("1 stored delivery needs manual review", label.Text, StringComparison.Ordinal);
        Assert.Contains("Confirmation was lost", label.Text, StringComparison.Ordinal);
        Assert.Single(control.Controls.Find("PebbleIndexOpenInbox", searchAllChildren: true));
    }

    [Theory]
    [InlineData("PebbleIndexCopyEndpoint")]
    [InlineData("PebbleIndexCopyAuthorization")]
    public void SettingsCopyFailuresAreShownAsRecoverableStatus(string buttonName)
    {
        using var control = new PebbleIndexSettingsControl(
            PebbleIndexPreferences.Default,
            Path.Combine(_directory, "secret"),
            Path.Combine(_directory, "inbox"),
            (_, _) => Task.FromResult(new DesktopTaskCatalog([])),
            new PebbleIndexReceiverStatus(false, "off"),
            _ => throw new InvalidOperationException("clipboard unavailable"));
        var button = Assert.IsType<RoundedButton>(
            Assert.Single(control.Controls.Find(buttonName, searchAllChildren: true)));

        button.PerformClick();

        var status = Assert.IsType<Label>(
            Assert.Single(control.Controls.Find("PebbleIndexStatus", searchAllChildren: true)));
        Assert.Contains("Could not copy", status.Text, StringComparison.Ordinal);
        Assert.Contains("clipboard unavailable", status.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidAuthorizationSecretIsShownAsRecoverableCopyStatus()
    {
        var secretPath = Path.Combine(_directory, "secret");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(secretPath, "pässw0rd\n");
        using var control = new PebbleIndexSettingsControl(
            PebbleIndexPreferences.Default,
            secretPath,
            Path.Combine(_directory, "inbox"),
            (_, _) => Task.FromResult(new DesktopTaskCatalog([])),
            new PebbleIndexReceiverStatus(false, "off"),
            _ => throw new InvalidOperationException("copy should not be reached"));
        var button = Assert.IsType<RoundedButton>(Assert.Single(
            control.Controls.Find("PebbleIndexCopyAuthorization", searchAllChildren: true)));

        button.PerformClick();

        var status = Assert.IsType<Label>(
            Assert.Single(control.Controls.Find("PebbleIndexStatus", searchAllChildren: true)));
        Assert.Contains("Could not copy the Pebble Index Authorization header", status.Text, StringComparison.Ordinal);
        Assert.Contains("authorization secret is invalid", status.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StoredStatusReadFailureIsContainedAsAnUnavailableStatus()
    {
        var status = PebbleIndexReceiverRuntime.ReadStoredStatus(
            false,
            "Receiver is off.",
            Path.Combine(_directory, "unavailable-inbox"),
            () => throw new DirectoryNotFoundException("The inbox disappeared."));

        Assert.False(status.Running);
        Assert.Contains("Receiver is off.", status.Message, StringComparison.Ordinal);
        Assert.Contains("stored delivery status is unavailable", status.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("inbox disappeared", status.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(status.Latest);
        Assert.Equal(0, status.OutstandingCount);
    }

    [Fact]
    public void LockedInboxRecordIsReportedAsUnavailableStoredStatus()
    {
        var inbox = Path.Combine(_directory, "locked-status-inbox");
        var store = new PebbleIndexDeliveryStore(inbox);
        var preferences = new PebbleIndexPreferences(
            TargetTaskId: Guid.NewGuid().ToString("D"),
            TargetHostId: "local",
            TargetTaskLabel: "Target");
        store.Accept("private transcript", "123", "ring", "tap", null, preferences);
        var path = Assert.Single(Directory.EnumerateFiles(inbox, "*.json"));
        using var locked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var status = PebbleIndexReceiverRuntime.ReadStoredStatus(false, "Receiver is off.", inbox);

        Assert.False(status.Running);
        Assert.Contains("Receiver is off.", status.Message, StringComparison.Ordinal);
        Assert.Contains("stored delivery status is unavailable", status.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, status.OutstandingCount);
        Assert.Null(status.Latest);
    }

    [Fact]
    public void DeliveryStatusTrackerMaintainsOutstandingSummaryInMemory()
    {
        var receivedAt = new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
        var older = new PebbleIndexDelivery(
            "older", "first", "1", "ring", "", "task", "local", "Target",
            PebbleIndexDeliveryState.Received, receivedAt);
        var newer = new PebbleIndexDelivery(
            "newer", "second", "2", "ring", "", "task", "local", "Target",
            PebbleIndexDeliveryState.DeliveryUncertain, receivedAt.AddSeconds(1));
        var latestSent = new PebbleIndexDelivery(
            "latest", "third", "3", "ring", "", "task", "local", "Target",
            PebbleIndexDeliveryState.Sent, receivedAt.AddSeconds(2));
        var tracker = new PebbleIndexDeliveryStatusTracker(
            new PebbleIndexDeliveryStoreSnapshot([older, newer], newer, latestSent));

        var initial = tracker.BuildStatus(true, "Listening.");
        tracker.Apply(newer with { State = PebbleIndexDeliveryState.Sent });
        var afterSend = tracker.BuildStatus(true, "Delivered.");

        Assert.Equal(2, initial.OutstandingCount);
        Assert.Equal(newer.Id, initial.Latest?.Id);
        Assert.Equal(1, afterSend.OutstandingCount);
        Assert.Equal(older.Id, afterSend.Latest?.Id);
        Assert.Contains("1 stored delivery", afterSend.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidPreferencesFallBackWithoutBlockingConfigurationRecovery()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "pebble-index.json");
        File.WriteAllText(path, "{ this is not valid JSON }");
        var messages = new List<string>();

        var preferences = TrayApplicationContext.LoadPebbleIndexPreferences(
            path,
            messages.Add,
            out var error);

        Assert.Equal(PebbleIndexPreferences.Default, preferences);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Contains(messages, message =>
            message.Contains("normal Joydex features will continue", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidPreferencesStillFallBackWhenRecoveryLoggingFails()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "pebble-index.json");
        File.WriteAllText(path, "{ this is not valid JSON }");

        var preferences = TrayApplicationContext.LoadPebbleIndexPreferences(
            path,
            _ => throw new IOException("log unavailable"),
            out var error);

        Assert.Equal(PebbleIndexPreferences.Default, preferences);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("pebble-index.json")]
    [InlineData("voice-pe.json")]
    [InlineData("config.json")]
    public void ConfigurationSaveFailureRollsBackEveryFile(string lockedFileName)
    {
        Directory.CreateDirectory(_directory);
        var configPath = Path.Combine(_directory, "config.json");
        var voicePath = Path.Combine(_directory, "voice-pe.json");
        var pebblePath = Path.Combine(_directory, "pebble-index.json");
        ConfigStore.Save(configPath, CompanionConfig.CreateSafeDefault());
        VoicePePreferencesStore.Save(voicePath, VoicePePreferences.Default);
        PebbleIndexPreferencesStore.Save(pebblePath, PebbleIndexPreferences.Default);
        var originalConfig = File.ReadAllBytes(configPath);
        var originalVoice = File.ReadAllBytes(voicePath);
        var originalPebble = File.ReadAllBytes(pebblePath);
        using var lockedFile = File.Open(
            Path.Combine(_directory, lockedFileName),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        var exception = Record.Exception(() => TrayApplicationContext.SaveConfigurationFiles(
            configPath,
            voicePath,
            pebblePath,
            new CompanionConfig { Safety = new SafetyOptions { DryRun = false } },
            VoicePePreferences.Default with { ConversationSpeakerGain = 3 },
            PebbleIndexPreferences.Default with { Port = PebbleIndexPreferences.Default.Port + 1 }));

        Assert.True(
            exception is IOException or UnauthorizedAccessException,
            exception?.ToString() ?? "The locked Pebble Index settings save unexpectedly succeeded.");
        Assert.Equal(originalConfig, File.ReadAllBytes(configPath));
        Assert.Equal(originalVoice, File.ReadAllBytes(voicePath));
        Assert.Equal(originalPebble, File.ReadAllBytes(pebblePath));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void TypedTaskIdTakesPriorityAsDesktopBridgeSource()
    {
        var typed = Guid.NewGuid().ToString("D");
        var saved = Guid.NewGuid().ToString("D");
        var voice = Guid.NewGuid().ToString("D");

        var resolved = TrayApplicationContext.ResolvePebbleIndexSourceTaskId(
            typed,
            new PebbleIndexPreferences(TargetTaskId: saved, TargetHostId: "local"),
            new VoicePePreferences(DedicatedTaskId: voice));

        Assert.Equal(typed, resolved);
    }

    [Fact]
    public void DesktopBridgeRunsOnlyForConfigurationOrEnabledMessaging()
    {
        Assert.False(TrayApplicationContext.ShouldStartDesktopTaskBroker(
            configuring: false,
            VoicePePreferences.Default,
            PebbleIndexPreferences.Default));
        Assert.True(TrayApplicationContext.ShouldStartDesktopTaskBroker(
            configuring: true,
            VoicePePreferences.Default,
            PebbleIndexPreferences.Default));
        Assert.True(TrayApplicationContext.ShouldStartDesktopTaskBroker(
            configuring: false,
            VoicePePreferences.Default with { DesktopTaskMessagingEnabled = true },
            PebbleIndexPreferences.Default));
        Assert.True(TrayApplicationContext.ShouldStartDesktopTaskBroker(
            configuring: false,
            VoicePePreferences.Default,
            PebbleIndexPreferences.Default with { Enabled = true }));
    }

    [Fact]
    public async Task RuntimeCoordinatorRejectsAStaleStartupAfterStop()
    {
        var coordinator = new CancellableRuntimeCoordinator<RecordingRuntime>();
        var startupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStartup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var candidate = new RecordingRuntime();
        var failures = new List<Exception>();
        var startup = coordinator.Start(
            async _ =>
            {
                startupEntered.TrySetResult();
                await releaseStartup.Task.ConfigureAwait(false);
                return candidate;
            },
            failures.Add);
        await startupEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stopping = coordinator.StopAsync(failures.Add);
        Assert.False(stopping.IsCompleted);
        releaseStartup.TrySetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(2));
        await startup.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(coordinator.IsRunning);
        Assert.True(candidate.Disposed);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task RuntimeCoordinatorCanRetryAfterCancellationCallbackFailure()
    {
        var coordinator = new CancellableRuntimeCoordinator<RecordingRuntime>();
        var startupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStartup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = new List<Exception>();
        var startup = coordinator.Start(
            async cancellationToken =>
            {
                using var registration = cancellationToken.Register(
                    () => throw new InvalidOperationException("cancellation callback failed"));
                startupEntered.TrySetResult();
                await releaseStartup.Task.ConfigureAwait(false);
                return new RecordingRuntime();
            },
            failures.Add);
        await startupEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stopping = coordinator.StopAsync(failures.Add);
        releaseStartup.TrySetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(2));
        await startup.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(coordinator.IsRunning);
        Assert.Contains(failures, exception =>
            exception.ToString().Contains("cancellation callback failed", StringComparison.Ordinal));
        var recovered = new RecordingRuntime();
        await coordinator.Start(_ => Task.FromResult(recovered), failures.Add);
        Assert.True(coordinator.IsRunning);
        await coordinator.StopAsync(failures.Add);
        Assert.True(recovered.Disposed);
    }

    [Fact]
    public async Task RuntimeCoordinatorCanRetryAfterStartupFailure()
    {
        var coordinator = new CancellableRuntimeCoordinator<RecordingRuntime>();
        var failures = new List<Exception>();
        await coordinator.Start(
            _ => Task.FromException<RecordingRuntime>(new IOException("broker unavailable")),
            failures.Add);
        Assert.False(coordinator.IsRunning);
        Assert.Single(failures);

        var recovered = new RecordingRuntime();
        await coordinator.Start(_ => Task.FromResult(recovered), failures.Add);

        Assert.True(coordinator.IsRunning);
        await coordinator.StopAsync(failures.Add);
        Assert.True(recovered.Disposed);
    }

    [Fact]
    public async Task RuntimeCoordinatorRetiresCompletedRuntimeAndCanRestart()
    {
        var coordinator = new CancellableRuntimeCoordinator<RecordingRuntime>();
        var failures = new List<Exception>();
        var stopped = new RecordingRuntime();
        await coordinator.Start(
            _ => Task.FromResult(stopped),
            failures.Add,
            runtime => runtime.Completion);
        Assert.True(coordinator.IsRunning);

        stopped.Complete();
        await stopped.Disposal.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(coordinator.IsRunning);
        var recovered = new RecordingRuntime();
        await coordinator.Start(
            _ => Task.FromResult(recovered),
            failures.Add,
            runtime => runtime.Completion);
        Assert.True(coordinator.IsRunning);

        await coordinator.StopAsync(failures.Add);
        Assert.True(recovered.Disposed);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task ConcurrentCoordinatorStopsShareCleanupAndBlockRestart()
    {
        var coordinator = new CancellableRuntimeCoordinator<RecordingRuntime>();
        var allowDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = new RecordingRuntime(allowDispose.Task);
        var failures = new List<Exception>();
        await coordinator.Start(_ => Task.FromResult(running), failures.Add);

        var firstStop = coordinator.StopAsync(failures.Add);
        await running.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var secondStop = coordinator.StopAsync(failures.Add);
        var replacementStarted = false;
        var blockedStart = coordinator.Start(
            _ =>
            {
                replacementStarted = true;
                return Task.FromResult(new RecordingRuntime());
            },
            failures.Add);

        Assert.Same(firstStop, secondStop);
        Assert.Same(firstStop, blockedStart);
        Assert.False(secondStop.IsCompleted);
        Assert.False(replacementStarted);
        allowDispose.TrySetResult();
        await Task.WhenAll(firstStop, secondStop).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(coordinator.IsRunning);
        var recovered = new RecordingRuntime();
        await coordinator.Start(_ => Task.FromResult(recovered), failures.Add);
        Assert.True(coordinator.IsRunning);
        await coordinator.StopAsync(failures.Add);
        Assert.True(recovered.Disposed);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task CoordinatorStartDuringStoppingStartupAwaitsFullCleanup()
    {
        var coordinator = new CancellableRuntimeCoordinator<RecordingRuntime>();
        var allowDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = new RecordingRuntime(allowDispose.Task);
        var observerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseObserver = new ManualResetEventSlim(false);
        var failures = new List<Exception>();
        var startup = coordinator.Start(
            _ => Task.FromResult(running),
            failures.Add,
            runtime =>
            {
                observerEntered.TrySetResult();
                releaseObserver.Wait();
                return runtime.Completion;
            });
        await observerEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(coordinator.IsRunning);

        var stop = coordinator.StopAsync(failures.Add);
        var replacementStarted = false;
        var blockedStart = coordinator.Start(
            _ =>
            {
                replacementStarted = true;
                return Task.FromResult(new RecordingRuntime());
            },
            failures.Add);

        Assert.Same(stop, blockedStart);
        Assert.False(replacementStarted);
        releaseObserver.Set();
        await running.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(startup.IsCompleted);
        Assert.False(stop.IsCompleted);
        allowDispose.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(coordinator.IsRunning);
        Assert.Empty(failures);
    }

    [Fact]
    public void DeliberatelyConfiguredShortPrivateTokenIsAccepted()
    {
        var path = Path.Combine(_directory, "pebble-index.secret");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "w00tw00t\n");

        Assert.Equal("w00tw00t", PebbleIndexSecretStore.LoadOrCreate(path));
    }

    [Fact]
    public void NonAsciiPrivateTokenIsRejected()
    {
        var path = Path.Combine(_directory, "pebble-index.secret");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "pässw0rd\n");

        Assert.Throws<InvalidDataException>(() => PebbleIndexSecretStore.LoadOrCreate(path));
    }

    [Fact]
    public async Task ConcurrentSecretCreationReturnsTheAtomicallyPublishedValue()
    {
        var path = Path.Combine(_directory, "pebble-index.secret");
        using var readyToPublish = new Barrier(2);
        Action beforePublish = () =>
            Assert.True(readyToPublish.SignalAndWait(TimeSpan.FromSeconds(5)));
        var attempts = Enumerable.Range(0, 2)
            .Select(_ => Task.Run(() => PebbleIndexSecretStore.LoadOrCreate(path, beforePublish)))
            .ToArray();

        var secrets = await Task.WhenAll(attempts);

        Assert.Equal(secrets[0], secrets[1]);
        Assert.Equal(secrets[0], File.ReadAllText(path).Trim());
        Assert.Equal(43, secrets[0].Length);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task UnauthorizedRequestIsRejectedBeforeItsDeclaredBodyArrives()
    {
        var port = ReservePort();
        var target = new DesktopTaskSummary(
            Guid.NewGuid().ToString("D"), "local", "Target", "idle", null, null, 0);
        await using var receiver = await PebbleIndexReceiverRuntime.StartAsync(
            new PebbleIndexPreferences(
                Enabled: true, Port: port, TargetTaskId: target.Id,
                TargetHostId: target.HostId, TargetTaskLabel: target.Title),
            "test-secret",
            Path.Combine(_directory, "authenticate-before-body-inbox"),
            new RecordingBridge(target),
            _ => { },
            _ => { });
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "POST /pebble-index HTTP/1.1\r\n"
            + "Host: localhost\r\n"
            + "Authorization: Bearer wrong-secret\r\n"
            + "Content-Type: multipart/form-data; boundary=test\r\n"
            + "Content-Length: 100\r\n"
            + "Connection: close\r\n\r\n"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

        var response = await reader.ReadToEndAsync(deadline.Token);

        Assert.StartsWith("HTTP/1.1 401 Unauthorized", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReceiverAuthenticatesDeliversOnceAndRejectsFiles()
    {
        var port = ReservePort();
        var target = new DesktopTaskSummary(
            Guid.NewGuid().ToString("D"), "local", "Target", "idle", null, null, 0);
        var inbox = Path.Combine(_directory, "receiver-inbox");
        var store = new PebbleIndexDeliveryStore(inbox);
        PebbleIndexDeliveryState? stateAtSend = null;
        var firstSent = new TaskCompletionSource<PebbleIndexDelivery>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSent = new TaskCompletionSource<PebbleIndexDelivery>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new RecordingBridge(
            target,
            beforeSend: () => stateAtSend = store.Recent(1).Single().State);
        var preferences = new PebbleIndexPreferences(
            Enabled: true, Port: port, TargetTaskId: target.Id,
            TargetHostId: target.HostId, TargetTaskLabel: target.Title);
        await using var receiver = await PebbleIndexReceiverRuntime.StartAsync(
            preferences,
            "test-secret",
            inbox,
            bridge,
            status =>
            {
                var sent = status.Latest;
                if (sent?.State != PebbleIndexDeliveryState.Sent) return;
                if (sent.Transcription == "hello") firstSent.TrySetResult(sent);
                if (sent.Transcription == "next") secondSent.TrySetResult(sent);
            },
            _ => { });
        using var client = new HttpClient();
        var endpoint = new Uri($"http://127.0.0.1:{port}/pebble-index");

        using (var unauthorized = CreateForm("hello", "1000"))
        using (var response = await client.PostAsync(endpoint, unauthorized))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        Assert.True(client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "test-secret"));
        using (var first = CreateForm("hello", "1000"))
        using (var response = await client.PostAsync(endpoint, first))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var firstDelivery = await firstSent.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(PebbleIndexDeliveryState.DeliveryUncertain, stateAtSend);
        Assert.Equal(PebbleIndexDeliveryState.Sent, firstDelivery.State);

        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");
        using (var duplicate = CreateForm("hello", "1000"))
        using (var response = await client.PostAsync(endpoint, duplicate))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using (var next = CreateForm("next", "1001"))
        using (var response = await client.PostAsync(endpoint, next))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var secondDelivery = await secondSent.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(PebbleIndexDeliveryState.Sent, secondDelivery.State);
        Assert.Equal(2, bridge.SendCount);
        Assert.Equal(
            2,
            store.Recent(100).Count(delivery => delivery.State == PebbleIndexDeliveryState.Sent));

        using var withFile = CreateForm("ignored", "2000");
        withFile.Add(new ByteArrayContent([1, 2, 3]), "audio", "audio.wav");
        using var fileResponse = await client.PostAsync(endpoint, withFile);
        Assert.Equal(HttpStatusCode.BadRequest, fileResponse.StatusCode);

        using var audioField = CreateForm("ignored", "2001");
        audioField.Add(new StringContent("audio-shaped data"), "audio");
        using var audioFieldResponse = await client.PostAsync(endpoint, audioField);
        Assert.Equal(HttpStatusCode.BadRequest, audioFieldResponse.StatusCode);
    }

    [Theory]
    [InlineData("form-data; name = \"attachment\"; filename = \"audio.wav\"")]
    [InlineData("form-data; name = \"attachment\"; filename* = UTF-8''audio.wav")]
    [InlineData("form-data; filename = \"audio.wav\"")]
    [InlineData("attachment; filename = \"audio.wav\"")]
    [InlineData("form-data; name=\"attachment\"\r\nContent-Disposition: attachment; filename = \"audio.wav\"")]
    [InlineData("form-data; name=\"attachment\"; filename")]
    [InlineData("form-data; name=\"attachment\"; filename*")]
    [InlineData("form-data; name=\"attachment\"; filename; filename=\"audio.wav\"")]
    [InlineData("form-data; name=\"attachment\"; filename*; filename*=UTF-8''audio.wav")]
    public async Task ReceiverRejectsUnsupportedFileDispositions(string fileDisposition)
    {
        var port = ReservePort();
        var target = new DesktopTaskSummary(
            Guid.NewGuid().ToString("D"), "local", "Target", "idle", null, null, 0);
        var bridge = new RecordingBridge(target);
        await using var receiver = await PebbleIndexReceiverRuntime.StartAsync(
            new PebbleIndexPreferences(
                Enabled: true, Port: port, TargetTaskId: target.Id,
                TargetHostId: target.HostId, TargetTaskLabel: target.Title),
            "test-secret",
            Path.Combine(_directory, "spaced-file-disposition-inbox"),
            bridge,
            _ => { },
            _ => { });
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");
        const string boundary = "spaced-file-boundary";
        var body = "--" + boundary + "\r\n"
            + "Content-Disposition: form-data; name=\"transcription\"\r\n\r\n"
            + "valid transcription\r\n"
            + "--" + boundary + "\r\n"
            + "Content-Disposition: form-data; name=\"recordedAt\"\r\n\r\n"
            + "1000\r\n"
            + "--" + boundary + "\r\n"
            + "Content-Disposition: form-data; name=\"client\"\r\n\r\n"
            + "ring\r\n"
            + "--" + boundary + "\r\n"
            + $"Content-Disposition: {fileDisposition}\r\n"
            + "Content-Type: audio/wav\r\n\r\n"
            + "file bytes\r\n"
            + "--" + boundary + "--\r\n";
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=" + boundary);

        using var response = await client.PostAsync($"http://127.0.0.1:{port}/pebble-index", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, bridge.SendCount);
    }

    [Fact]
    public async Task ReceiverRejectsAudioContentTypeWithoutFilename()
    {
        var port = ReservePort();
        var target = new DesktopTaskSummary(
            Guid.NewGuid().ToString("D"), "local", "Target", "idle", null, null, 0);
        var inbox = Path.Combine(_directory, "filename-free-audio-inbox");
        var bridge = new RecordingBridge(target);
        await using var receiver = await PebbleIndexReceiverRuntime.StartAsync(
            new PebbleIndexPreferences(
                Enabled: true, Port: port, TargetTaskId: target.Id,
                TargetHostId: target.HostId, TargetTaskLabel: target.Title),
            "test-secret",
            inbox,
            bridge,
            _ => { },
            _ => { });
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");
        using var form = new MultipartFormDataContent();
        using var audio = new ByteArrayContent(Encoding.UTF8.GetBytes("audio-shaped data"));
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "transcription");
        form.Add(new StringContent("1500"), "recordedAt");
        form.Add(new StringContent("ring"), "client");

        using var response = await client.PostAsync($"http://127.0.0.1:{port}/pebble-index", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, bridge.SendCount);
        Assert.Empty(new PebbleIndexDeliveryStore(inbox).Recent(1));
    }

    [Theory]
    [InlineData("keep --embedded-boundary inside the transcription")]
    [InlineData("keep\r\nprefix --embedded-boundary inside the transcription")]
    [InlineData("keep\r\n--embedded-boundary-not-a-delimiter\r\ninside the transcription")]
    public async Task ReceiverPreservesBoundaryLikeTextInsideTranscription(string transcription)
    {
        var port = ReservePort();
        var target = new DesktopTaskSummary(
            Guid.NewGuid().ToString("D"), "local", "Target", "idle", null, null, 0);
        var sent = new TaskCompletionSource<PebbleIndexDelivery>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new RecordingBridge(target);
        await using var receiver = await PebbleIndexReceiverRuntime.StartAsync(
            new PebbleIndexPreferences(
                Enabled: true, Port: port, TargetTaskId: target.Id,
                TargetHostId: target.HostId, TargetTaskLabel: target.Title),
            "test-secret",
            Path.Combine(_directory, "boundary-like-transcription-inbox"),
            bridge,
            status =>
            {
                if (status.Latest?.State == PebbleIndexDeliveryState.Sent)
                {
                    sent.TrySetResult(status.Latest);
                }
            },
            _ => { });
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");
        const string boundary = "embedded-boundary";
        using var form = new MultipartFormDataContent(boundary);
        form.Add(new StringContent(transcription), "transcription");
        form.Add(new StringContent("1500"), "recordedAt");
        form.Add(new StringContent("ring"), "client");

        using var response = await client.PostAsync($"http://127.0.0.1:{port}/pebble-index", form);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var delivery = await sent.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(transcription, delivery.Transcription);
        Assert.Equal(1, bridge.SendCount);
    }

    [Fact]
    public async Task ReceiverDeliversToExactSavedTaskWithoutRecentCatalogEntry()
    {
        var port = ReservePort();
        var target = new DesktopTaskSummary(
            Guid.NewGuid().ToString("D"), "local", "Older saved target", "idle", null, null, 0);
        var inbox = Path.Combine(_directory, "exact-target-inbox");
        var bridge = new RecordingBridge(
            target,
            listTasks: () => Task.FromException<DesktopTaskCatalog>(
                new InvalidOperationException("The target is outside the recent catalog.")));
        await using var receiver = await PebbleIndexReceiverRuntime.StartAsync(
            new PebbleIndexPreferences(
                Enabled: true, Port: port, TargetTaskId: target.Id,
                TargetHostId: target.HostId, TargetTaskLabel: target.Title),
            "test-secret",
            inbox,
            bridge,
            _ => { },
            _ => { });
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");

        using var form = CreateForm("hello", "2500");
        using var response = await client.PostAsync($"http://127.0.0.1:{port}/pebble-index", form);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await bridge.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1, bridge.SendCount);
    }

    [Fact]
    public async Task FailedDesktopSendIsPersistedAsDeliveryUncertain()
    {
        var port = ReservePort();
        var target = new DesktopTaskSummary(
            Guid.NewGuid().ToString("D"), "local", "Target", "idle", null, null, 0);
        var bridge = new RecordingBridge(target, sendError: new IOException("confirmation lost"));
        var inbox = Path.Combine(_directory, "uncertain-inbox");
        var uncertainPersisted = new TaskCompletionSource<PebbleIndexDelivery>(TaskCreationOptions.RunContinuationsAsynchronously);
        var preferences = new PebbleIndexPreferences(
            Enabled: true, Port: port, TargetTaskId: target.Id,
            TargetHostId: target.HostId, TargetTaskLabel: target.Title);
        await using var receiver = await PebbleIndexReceiverRuntime.StartAsync(
            preferences,
            "test-secret",
            inbox,
            bridge,
            status =>
            {
                if (status.Latest is { State: PebbleIndexDeliveryState.DeliveryUncertain } delivery
                    && delivery.Detail.Contains("not confirmed", StringComparison.OrdinalIgnoreCase))
                {
                    uncertainPersisted.TrySetResult(delivery);
                }
            },
            _ => { });
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");

        using var form = CreateForm("hello", "3000");
        using var response = await client.PostAsync($"http://127.0.0.1:{port}/pebble-index", form);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var delivery = await uncertainPersisted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var stored = Assert.Single(new PebbleIndexDeliveryStore(inbox).Recent());
        Assert.Equal(PebbleIndexDeliveryState.DeliveryUncertain, stored.State);
        Assert.Contains("not confirmed", delivery.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not confirmed", stored.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReceiverStartupIgnoresDiagnosticCallbackFailures()
    {
        var port = ReservePort();
        var target = new DesktopTaskSummary(
            Guid.NewGuid().ToString("D"), "local", "Target", "idle", null, null, 0);
        var statusCalls = 0;
        var logCalls = 0;
        await using var receiver = await PebbleIndexReceiverRuntime.StartAsync(
            new PebbleIndexPreferences(
                Enabled: true, Port: port, TargetTaskId: target.Id,
                TargetHostId: target.HostId, TargetTaskLabel: target.Title),
            "test-secret",
            Path.Combine(_directory, "callback-startup-inbox"),
            new RecordingBridge(target),
            _ =>
            {
                Interlocked.Increment(ref statusCalls);
                throw new IOException("status unavailable");
            },
            _ =>
            {
                Interlocked.Increment(ref logCalls);
                throw new IOException("log unavailable");
            });
        using var client = new HttpClient();

        using var response = await client.GetAsync($"http://127.0.0.1:{port}/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(statusCalls > 0);
        Assert.True(logCalls > 0);
    }

    [Fact]
    public async Task DiagnosticCallbackFailuresDoNotStopDeliveryLoop()
    {
        var port = ReservePort();
        var target = new DesktopTaskSummary(
            Guid.NewGuid().ToString("D"), "local", "Target", "idle", null, null, 0);
        var bridge = new RecordingBridge(target);
        var inbox = Path.Combine(_directory, "callback-delivery-inbox");
        var statusCalls = 0;
        var logCalls = 0;
        var firstDeliveryLogged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDeliveryLogged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var receiver = await PebbleIndexReceiverRuntime.StartAsync(
            new PebbleIndexPreferences(
                Enabled: true, Port: port, TargetTaskId: target.Id,
                TargetHostId: target.HostId, TargetTaskLabel: target.Title),
            "test-secret",
            inbox,
            bridge,
            _ =>
            {
                if (Interlocked.Increment(ref statusCalls) > 1)
                    throw new IOException("status unavailable");
            },
            _ =>
            {
                var call = Interlocked.Increment(ref logCalls);
                if (call == 2) firstDeliveryLogged.TrySetResult();
                if (call == 3) secondDeliveryLogged.TrySetResult();
                if (call > 1)
                    throw new IOException("log unavailable");
            });
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");
        var endpoint = new Uri($"http://127.0.0.1:{port}/pebble-index");

        using (var first = CreateForm("first", "4000"))
        using (var response = await client.PostAsync(endpoint, first))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await firstDeliveryLogged.Task.WaitAsync(TimeSpan.FromSeconds(15));
        using (var second = CreateForm("second", "4001"))
        using (var response = await client.PostAsync(endpoint, second))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await secondDeliveryLogged.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(2, bridge.SendCount);
        Assert.Equal(
            2,
            new PebbleIndexDeliveryStore(inbox).Recent(100)
                .Count(delivery => delivery.State == PebbleIndexDeliveryState.Sent));
        Assert.True(statusCalls > 1);
        Assert.True(logCalls >= 3);
    }

    [Fact]
    public async Task PersistenceRecoveryFailureDoesNotStopDeliveryLoop()
    {
        var port = ReservePort();
        var target = new DesktopTaskSummary(
            Guid.NewGuid().ToString("D"), "local", "Target", "idle", null, null, 0);
        var inbox = Path.Combine(_directory, "persistence-recovery-inbox");
        FileStream? lockedDelivery = null;
        var firstResolutionAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondConfirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolutionCalls = 0;
        var bridge = new RecordingBridge(
            target,
            resolveTask: () =>
            {
                if (Interlocked.Increment(ref resolutionCalls) == 1)
                {
                    var path = Directory.EnumerateFiles(inbox, "*.json").Single();
                    lockedDelivery = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    firstResolutionAttempted.TrySetResult();
                    throw new IOException("task lookup unavailable");
                }
                return Task.FromResult(target);
            });
        await using var receiver = await PebbleIndexReceiverRuntime.StartAsync(
            new PebbleIndexPreferences(
                Enabled: true, Port: port, TargetTaskId: target.Id,
                TargetHostId: target.HostId, TargetTaskLabel: target.Title),
            "test-secret",
            inbox,
            bridge,
            _ => { },
            message =>
            {
                if (message.Contains("was confirmed", StringComparison.OrdinalIgnoreCase))
                {
                    secondConfirmed.TrySetResult();
                }
            });
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");
        var endpoint = new Uri($"http://127.0.0.1:{port}/pebble-index");

        try
        {
            using (var first = CreateForm("first", "5000"))
            using (var response = await client.PostAsync(endpoint, first))
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            await firstResolutionAttempted.Task.WaitAsync(TimeSpan.FromSeconds(15));

            using (var second = CreateForm("second", "5001"))
            using (var response = await client.PostAsync(endpoint, second))
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            await secondConfirmed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var sent = Assert.Single(new PebbleIndexDeliveryStore(inbox).Recent());

            Assert.Equal(2, resolutionCalls);
            Assert.Equal(1, bridge.SendCount);
            Assert.Equal(PebbleIndexDeliveryState.Sent, sent.State);
            Assert.Equal("second", sent.Transcription);
        }
        finally
        {
            lockedDelivery?.Dispose();
        }
    }

    [Fact]
    public async Task ReceiverWaitsForActiveClientsDuringShutdown()
    {
        var port = ReservePort();
        var target = new DesktopTaskSummary(
            Guid.NewGuid().ToString("D"), "local", "Target", "idle", null, null, 0);
        var releaseEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = await PebbleIndexReceiverRuntime.StartAsync(
            new PebbleIndexPreferences(
                Enabled: true, Port: port, TargetTaskId: target.Id,
                TargetHostId: target.HostId, TargetTaskLabel: target.Title),
            "test-secret",
            Path.Combine(_directory, "shutdown-inbox"),
            new RecordingBridge(target),
            _ => { },
            _ => { },
            beforeClientRelease: async () =>
            {
                releaseEntered.TrySetResult();
                await allowRelease.Task.ConfigureAwait(false);
            });
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(
            "POST /pebble-index HTTP/1.1\r\nHost: localhost\r\n"));
        await WaitForActiveClientCountAsync(receiver, 1);

        var disposing = receiver.DisposeAsync().AsTask();
        try
        {
            await releaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(disposing.IsCompleted);
        }
        finally
        {
            allowRelease.TrySetResult();
            await disposing.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.Equal(0, receiver.ActiveClientCount);
    }

    [Fact]
    public async Task ImmediateListenerFailureLeavesTerminalUnavailableStatus()
    {
        var port = ReservePort();
        var target = new DesktopTaskSummary(
            Guid.NewGuid().ToString("D"), "local", "Target", "idle", null, null, 0);
        var statuses = new List<PebbleIndexReceiverStatus>();
        var statusGate = new object();
        var logs = new List<string>();
        await using var receiver = await PebbleIndexReceiverRuntime.StartAsync(
            new PebbleIndexPreferences(
                Enabled: true, Port: port, TargetTaskId: target.Id,
                TargetHostId: target.HostId, TargetTaskLabel: target.Title),
            "test-secret",
            Path.Combine(_directory, "listener-failure-inbox"),
            new RecordingBridge(target),
            status =>
            {
                lock (statusGate) statuses.Add(status);
            },
            logs.Add,
            acceptClient: _ => ValueTask.FromException<TcpClient>(
                new SocketException((int)SocketError.NoBufferSpaceAvailable)));

        await receiver.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        PebbleIndexReceiverStatus[] snapshot;
        lock (statusGate) snapshot = [.. statuses];
        var stoppedAt = Array.FindIndex(snapshot, status => !status.Running);

        Assert.True(stoppedAt >= 0);
        Assert.DoesNotContain(snapshot.Skip(stoppedAt + 1), status => status.Running);
        Assert.False(snapshot[^1].Running);
        Assert.Contains("stopped accepting connections", snapshot[^1].Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(logs);
        Assert.Contains("stopped accepting", logs[^1], StringComparison.OrdinalIgnoreCase);
    }

    private static MultipartFormDataContent CreateForm(string transcript, string recordedAt)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(transcript), "transcription");
        form.Add(new StringContent(recordedAt), "recordedAt");
        form.Add(new StringContent("ring"), "client");
        return form;
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForActiveClientCountAsync(PebbleIndexReceiverRuntime receiver, int expected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!deadline.IsCancellationRequested)
        {
            if (receiver.ActiveClientCount == expected) return;
            await Task.Delay(20, deadline.Token).ConfigureAwait(false);
        }
        throw new TimeoutException($"Pebble Index did not reach {expected} active client.");
    }

    private sealed class RecordingBridge(
        DesktopTaskSummary target,
        Exception? sendError = null,
        Action? beforeSend = null,
        Func<Task<DesktopTaskCatalog>>? listTasks = null,
        Func<Task<DesktopTaskSummary>>? resolveTask = null) : IDesktopTaskBridgeClient
    {
        public int SendCount;
        public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> IsAvailableAsync(string sourceThreadId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<DesktopTaskCatalog> ListTasksAsync(string sourceThreadId, string? excludedThreadId = null, CancellationToken cancellationToken = default) =>
            listTasks?.Invoke() ?? Task.FromResult(new DesktopTaskCatalog([target]));
        public Task<DesktopTaskSummary> ResolveTaskAsync(string sourceThreadId, string targetThreadId, string targetHostId, CancellationToken cancellationToken = default) =>
            resolveTask?.Invoke() ?? Task.FromResult(target);
        public Task<string> ReadTaskAsync(string sourceThreadId, DesktopTaskSummary selected, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);
        public Task<DesktopTaskDeliveryResult> SendMessageAsync(
            string sourceThreadId, DesktopTaskSummary selected, string message, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref SendCount);
            beforeSend?.Invoke();
            if (sendError is not null)
            {
                return Task.FromException<DesktopTaskDeliveryResult>(sendError);
            }
            Delivered.TrySetResult();
            return Task.FromResult(new DesktopTaskDeliveryResult(selected.Id, selected.HostId, selected.Title, false, "Delivered."));
        }
    }

    private sealed class RecordingRuntime : IAsyncDisposable
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task? _allowDispose;

        public RecordingRuntime(Task? allowDispose = null)
        {
            _allowDispose = allowDispose;
        }

        public bool Disposed { get; private set; }
        public Task Completion => _completion.Task;
        public TaskCompletionSource Disposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete() => _completion.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            Disposed = true;
            DisposalStarted.TrySetResult();
            if (_allowDispose is not null) await _allowDispose.ConfigureAwait(false);
            _completion.TrySetResult();
            Disposal.TrySetResult();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
