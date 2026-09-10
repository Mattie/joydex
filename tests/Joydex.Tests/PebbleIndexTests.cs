using Joydex.Core.Voice;
using Joydex.App;
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
    public void DeliberatelyConfiguredShortPrivateTokenIsAccepted()
    {
        var path = Path.Combine(_directory, "pebble-index.secret");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "w00tw00t\n");

        Assert.Equal("w00tw00t", PebbleIndexSecretStore.LoadOrCreate(path));
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
        var bridge = new RecordingBridge(
            target,
            beforeSend: () => stateAtSend = store.Recent(1).Single().State);
        var preferences = new PebbleIndexPreferences(
            Enabled: true, Port: port, TargetTaskId: target.Id,
            TargetHostId: target.HostId, TargetTaskLabel: target.Title);
        await using var receiver = await PebbleIndexReceiverRuntime.StartAsync(
            preferences, "test-secret", inbox, bridge, _ => { }, _ => { });
        using var client = new HttpClient();
        var endpoint = new Uri($"http://127.0.0.1:{port}/pebble-index");

        using (var unauthorized = CreateForm("hello", "1000"))
        using (var response = await client.PostAsync(endpoint, unauthorized))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        Assert.True(client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "test-secret"));
        using (var first = CreateForm("hello", "1000"))
        using (var response = await client.PostAsync(endpoint, first))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await bridge.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(PebbleIndexDeliveryState.DeliveryUncertain, stateAtSend);
        await WaitForStateAsync(store, PebbleIndexDeliveryState.Sent);

        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");
        using (var duplicate = CreateForm("hello", "1000"))
        using (var response = await client.PostAsync(endpoint, duplicate))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using (var next = CreateForm("next", "1001"))
        using (var response = await client.PostAsync(endpoint, next))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await WaitForSentCountAsync(store, 2);
        Assert.Equal(2, bridge.SendCount);

        using var withFile = CreateForm("ignored", "2000");
        withFile.Add(new ByteArrayContent([1, 2, 3]), "audio", "audio.wav");
        using var fileResponse = await client.PostAsync(endpoint, withFile);
        Assert.Equal(HttpStatusCode.BadRequest, fileResponse.StatusCode);

        using var audioField = CreateForm("ignored", "2001");
        audioField.Add(new StringContent("audio-shaped data"), "audio");
        using var audioFieldResponse = await client.PostAsync(endpoint, audioField);
        Assert.Equal(HttpStatusCode.BadRequest, audioFieldResponse.StatusCode);
    }

    [Fact]
    public async Task FailedDesktopSendIsPersistedAsDeliveryUncertain()
    {
        var port = ReservePort();
        var target = new DesktopTaskSummary(
            Guid.NewGuid().ToString("D"), "local", "Target", "idle", null, null, 0);
        var bridge = new RecordingBridge(target, sendError: new IOException("confirmation lost"));
        var inbox = Path.Combine(_directory, "uncertain-inbox");
        var preferences = new PebbleIndexPreferences(
            Enabled: true, Port: port, TargetTaskId: target.Id,
            TargetHostId: target.HostId, TargetTaskLabel: target.Title);
        await using var receiver = await PebbleIndexReceiverRuntime.StartAsync(
            preferences, "test-secret", inbox, bridge, _ => { }, _ => { });
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");

        using var form = CreateForm("hello", "3000");
        using var response = await client.PostAsync($"http://127.0.0.1:{port}/pebble-index", form);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await bridge.SendAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var delivery = await WaitForStateAsync(
            new PebbleIndexDeliveryStore(inbox),
            PebbleIndexDeliveryState.DeliveryUncertain,
            detailContains: "not confirmed");
        Assert.Contains("not confirmed", delivery.Detail, StringComparison.OrdinalIgnoreCase);
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
                if (Interlocked.Increment(ref logCalls) > 1)
                    throw new IOException("log unavailable");
            });
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");
        var endpoint = new Uri($"http://127.0.0.1:{port}/pebble-index");

        using (var first = CreateForm("first", "4000"))
        using (var response = await client.PostAsync(endpoint, first))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await WaitForSentCountAsync(new PebbleIndexDeliveryStore(inbox), 1);
        using (var second = CreateForm("second", "4001"))
        using (var response = await client.PostAsync(endpoint, second))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await WaitForSentCountAsync(new PebbleIndexDeliveryStore(inbox), 2);

        Assert.Equal(2, bridge.SendCount);
        Assert.True(statusCalls > 1);
        Assert.True(logCalls > 1);
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

    private async Task<PebbleIndexDelivery> WaitForStateAsync(
        PebbleIndexDeliveryStore store,
        PebbleIndexDeliveryState state,
        string? detailContains = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!deadline.IsCancellationRequested)
        {
            var delivery = store.Recent(1).FirstOrDefault();
            if (delivery?.State == state
                && (detailContains is null
                    || delivery.Detail.Contains(detailContains, StringComparison.OrdinalIgnoreCase)))
                return delivery;
            await Task.Delay(20, deadline.Token).ConfigureAwait(false);
        }
        throw new TimeoutException($"Pebble Index delivery did not reach {state}.");
    }

    private static async Task WaitForSentCountAsync(PebbleIndexDeliveryStore store, int expected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!deadline.IsCancellationRequested)
        {
            if (store.Recent(100).Count(delivery => delivery.State == PebbleIndexDeliveryState.Sent) == expected)
                return;
            await Task.Delay(20, deadline.Token).ConfigureAwait(false);
        }
        throw new TimeoutException($"Pebble Index did not persist {expected} sent deliveries.");
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
        Action? beforeSend = null) : IDesktopTaskBridgeClient
    {
        public int SendCount;
        public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SendAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> IsAvailableAsync(string sourceThreadId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<DesktopTaskCatalog> ListTasksAsync(string sourceThreadId, string? excludedThreadId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DesktopTaskCatalog([target]));
        public Task<string> ReadTaskAsync(string sourceThreadId, DesktopTaskSummary selected, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);
        public Task<DesktopTaskDeliveryResult> SendMessageAsync(
            string sourceThreadId, DesktopTaskSummary selected, string message, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref SendCount);
            beforeSend?.Invoke();
            SendAttempted.TrySetResult();
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
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
