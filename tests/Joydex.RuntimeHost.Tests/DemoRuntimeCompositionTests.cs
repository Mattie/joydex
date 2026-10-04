using System.Diagnostics;
using Joydex.Contracts;
using Joydex.Core.Config;
using Joydex.Core.Input;
using Joydex.Core.Runtime;
using Joydex.Core.TaskAlerts;
using Joydex.Core.Voice;

namespace Joydex.RuntimeHost.Tests;

public sealed class DemoRuntimeCompositionTests
{
    private static readonly TimeSpan AsyncTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task TwoSimulatedSourcesDispatchTheirConfiguredBindings()
    {
        var time = new ManualTimeProvider();
        using var inputHost = new RuntimeInputHost(time);
        await using var composition = DemoRuntimeComposition.Create(inputHost, time, default);

        var sources = composition.Refresh(Bundle("new-task", "toggle-sidebar"));

        Assert.Collection(
            sources.OrderBy(source => source.SourceId),
            source => AssertSource(source, "cm3", "Simulated Joydex CM3"),
            source => AssertSource(source, "warbrd", "Simulated Joydex WarBRD"));
        await WaitForConnectedSourcesAsync(inputHost);

        await AdvanceUntilObservedAsync(
            inputHost,
            time,
            "cm3",
            TimeSpan.FromMilliseconds(1500),
            snapshot => snapshot.Buttons[11]);
        await WaitForActivityAsync(composition, 0, "DRY RUN new-task press from always/button 12;");

        await AdvanceUntilObservedAsync(
            inputHost,
            time,
            "cm3",
            TimeSpan.FromMilliseconds(2000),
            snapshot => !snapshot.Buttons[11]);
        await AdvanceUntilObservedAsync(
            inputHost,
            time,
            "warbrd",
            TimeSpan.FromMilliseconds(3500),
            snapshot => snapshot.Buttons[0]);
        await WaitForActivityAsync(composition, 0, "DRY RUN toggle-sidebar press from always/button 1;");
    }

    [Fact]
    public async Task CompanionApplyUsesTheChangedBindingOnTheNextEligiblePress()
    {
        var time = new ManualTimeProvider();
        using var inputHost = new RuntimeInputHost(time);
        await using var composition = DemoRuntimeComposition.Create(inputHost, time, default);
        var active = Bundle("new-task", "agent-1");
        composition.Refresh(active);
        await WaitForConnectedSourcesAsync(inputHost);

        await AdvanceUntilObservedAsync(
            inputHost,
            time,
            "cm3",
            TimeSpan.FromMilliseconds(1500),
            snapshot => snapshot.Buttons[11]);
        await WaitForActivityAsync(composition, 0, "DRY RUN new-task press from always/button 12;");
        await AdvanceUntilObservedAsync(
            inputHost,
            time,
            "cm3",
            TimeSpan.FromMilliseconds(2000),
            snapshot => !snapshot.Buttons[11]);

        var applied = await composition.ActivateAsync(
            SettingsAggregateId.Companion,
            Bundle("toggle-sidebar", "agent-1"),
            desiredRevision: 2,
            default);
        var activityBeforeNextPress = LatestActivitySequence(composition);
        await WaitForConnectedSourcesAsync(inputHost);

        await AdvanceUntilObservedAsync(
            inputHost,
            time,
            "cm3",
            TimeSpan.FromMilliseconds(3500),
            snapshot => snapshot.Buttons[11]);
        await WaitForActivityAsync(
            composition,
            activityBeforeNextPress,
            "DRY RUN toggle-sidebar press from always/button 12;");

        Assert.Equal(SettingsActivationState.Applied, applied.State);
        Assert.DoesNotContain(
            ActivityAfter(composition, activityBeforeNextPress),
            activity => activity.Message.Contains(
                "DRY RUN new-task press",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnrelatedAggregateApplyPreservesBothSourceGenerations()
    {
        var time = new ManualTimeProvider();
        using var inputHost = new RuntimeInputHost(time);
        await using var composition = DemoRuntimeComposition.Create(inputHost, time, default);
        var active = Bundle("new-task", "agent-1");
        composition.Refresh(active);
        var before = await WaitForConnectedSourcesAsync(inputHost);

        var candidate = active with
        {
            Voice = VoicePePreferences.Default with { RealtimeVoice = "sol" },
        };
        var applied = await composition.ActivateAsync(
            SettingsAggregateId.Voice,
            candidate,
            desiredRevision: 2,
            default);
        composition.Refresh(candidate);
        var after = await WaitForConnectedSourcesAsync(inputHost);

        Assert.Equal(SettingsActivationState.Applied, applied.State);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (sourceId, generation) in before)
        {
            Assert.Equal(generation, after[sourceId]);
        }
    }

    [Fact]
    public async Task CaptureIgnoresHeldControlAndSuppressesItsCapturedPressUntilRelease()
    {
        var time = new ManualTimeProvider();
        using var inputHost = new RuntimeInputHost(time);
        await using var composition = DemoRuntimeComposition.Create(inputHost, time, default);
        composition.Refresh(Bundle("new-task", "agent-1"));
        var sources = await WaitForConnectedSourcesAsync(inputHost);

        await AdvanceUntilObservedAsync(
            inputHost,
            time,
            "cm3",
            TimeSpan.FromMilliseconds(1500),
            snapshot => snapshot.Buttons[11]);
        var initialAction = await WaitForActivityAsync(
            composition,
            0,
            "DRY RUN new-task press from always/button 12;");

        var started = inputHost.BeginCapture(new InputCaptureRequest(
            "settings-client",
            "cm3",
            "binding",
            sources["cm3"],
            TimeSpan.FromSeconds(10)));
        Assert.True(started.Accepted);
        var captureId = started.Lease!.CaptureId;
        var activeCapture = WaitForCaptureAsync(inputHost, captureId, InputCaptureStatus.Active);

        Assert.True(composition.ObserveForCapture(captureId, "cm3"));
        await activeCapture;

        await AdvanceUntilObservedAsync(
            inputHost,
            time,
            "cm3",
            TimeSpan.FromMilliseconds(2000),
            snapshot => !snapshot.Buttons[11]);
        var captured = WaitForCaptureAsync(inputHost, captureId, InputCaptureStatus.Completed);
        var capturedObservation = await AdvanceUntilObservedAsync(
            inputHost,
            time,
            "cm3",
            TimeSpan.FromMilliseconds(6500),
            snapshot => snapshot.Buttons[11]);
        var completion = await captured;
        var observationAfterCapture = WaitForObservationAfterAsync(
            inputHost,
            "cm3",
            capturedObservation.Sequence);
        composition.ReleaseCaptureObservation(captureId);
        await observationAfterCapture;

        Assert.Equal(12, completion.CapturedInput?.DisplayIndex);
        Assert.Equal(sources["cm3"], inputHost.Sources.Single(source =>
            string.Equals(
                source.Descriptor.SourceId,
                "cm3",
                StringComparison.OrdinalIgnoreCase)).Generation);
        Assert.Equal(2, inputHost.Sources.Count(source => source.Connected));
        Assert.DoesNotContain(
            ActivityAfter(composition, initialAction.Sequence),
            activity => activity.Message.Contains(
                "DRY RUN new-task press",
                StringComparison.Ordinal));

        await AdvanceUntilObservedAsync(
            inputHost,
            time,
            "cm3",
            TimeSpan.FromMilliseconds(7000),
            snapshot => !snapshot.Buttons[11]);
        await AdvanceUntilObservedAsync(
            inputHost,
            time,
            "cm3",
            TimeSpan.FromMilliseconds(11500),
            snapshot => snapshot.Buttons[11]);
        await WaitForActivityAsync(
            composition,
            initialAction.Sequence,
            "DRY RUN new-task press from always/button 12;");
    }

    [Fact]
    public async Task RuntimeCancellationRejectsActivationAndDisposalDisconnectsSources()
    {
        var time = new ManualTimeProvider();
        using var inputHost = new RuntimeInputHost(time);
        using var runtimeCancellation = new CancellationTokenSource();
        var composition = DemoRuntimeComposition.Create(
            inputHost,
            time,
            runtimeCancellation.Token);
        composition.Refresh(Bundle("new-task", "agent-1"));
        await WaitForConnectedSourcesAsync(inputHost);

        runtimeCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            composition.ActivateAsync(
                SettingsAggregateId.Companion,
                Bundle("toggle-sidebar", "agent-1"),
                desiredRevision: 2,
                default));
        await composition.DisposeAsync().AsTask().WaitAsync(AsyncTimeout);

        Assert.Equal(2, inputHost.Sources.Count);
        Assert.All(inputHost.Sources, source => Assert.False(source.Connected));
        await composition.DisposeAsync();
    }

    private static SettingsBundle Bundle(string cm3Action, string warbrdAction) => new(
        new CompanionConfig
        {
            Devices =
            [
                new DeviceProfile
                {
                    Id = "cm3",
                    DisplayName = "CM3",
                    Selector = new DeviceSelector { ProductNameContains = "CM3" },
                    ButtonMapTemplate = "cm3",
                },
                new DeviceProfile
                {
                    Id = "warbrd",
                    DisplayName = "WarBRD",
                    Selector = new DeviceSelector { ProductNameContains = "WarBRD" },
                    ButtonMapTemplate = "alpha-warbrd",
                },
            ],
            Bindings =
            [
                new ButtonBinding
                {
                    Name = "cm3-demo-action",
                    DeviceId = "cm3",
                    Bank = CompanionConfig.AlwaysBank,
                    Button = 12,
                    Action = cm3Action,
                },
                new ButtonBinding
                {
                    Name = "warbrd-demo-action",
                    DeviceId = "warbrd",
                    Bank = CompanionConfig.AlwaysBank,
                    Button = 1,
                    Action = warbrdAction,
                },
            ],
        },
        VoicePePreferences.Default,
        PebbleIndexPreferences.Default,
        TaskAlertPreferences.Default);

    private static void AssertSource(
        RuntimeInputSource source,
        string sourceId,
        string displayName)
    {
        Assert.Equal(sourceId, source.SourceId);
        Assert.Equal(displayName, source.DisplayName);
        Assert.Equal(sourceId, source.ConfiguredDeviceId);
        Assert.True(source.Connected);
        Assert.NotNull(source.Generation);
    }

    private static async Task<IReadOnlyDictionary<string, long>> WaitForConnectedSourcesAsync(
        RuntimeInputHost inputHost)
    {
        IReadOnlyDictionary<string, long>? result = null;
        await WaitUntilAsync(() =>
        {
            var sources = inputHost.Sources;
            if (sources.Count != 2 || sources.Any(source => !source.Connected || source.Generation is null))
            {
                return false;
            }
            result = sources.ToDictionary(
                source => source.Descriptor.SourceId,
                source => source.Generation!.Value,
                StringComparer.OrdinalIgnoreCase);
            return true;
        });
        return result!;
    }

    private static async Task<InputObservation> AdvanceUntilObservedAsync(
        RuntimeInputHost inputHost,
        ManualTimeProvider time,
        string sourceId,
        TimeSpan elapsed,
        Func<JoystickSnapshot, bool> predicate)
    {
        var observed = new TaskCompletionSource<InputObservation>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnObserved(object? sender, InputObservationEventArgs update)
        {
            if (string.Equals(
                    update.Observation.Source.Descriptor.SourceId,
                    sourceId,
                    StringComparison.OrdinalIgnoreCase)
                && predicate(update.Observation.Snapshot))
            {
                observed.TrySetResult(update.Observation);
            }
        }

        inputHost.InputObserved += OnObserved;
        try
        {
            time.SetElapsed(elapsed);
            return await observed.Task.WaitAsync(AsyncTimeout);
        }
        finally
        {
            inputHost.InputObserved -= OnObserved;
        }
    }

    private static async Task<InputCaptureChangedEventArgs> WaitForCaptureAsync(
        RuntimeInputHost inputHost,
        Guid captureId,
        InputCaptureStatus status)
    {
        var reached = new TaskCompletionSource<InputCaptureChangedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, InputCaptureChangedEventArgs update)
        {
            if (update.Lease.CaptureId == captureId && update.Lease.Status == status)
            {
                reached.TrySetResult(update);
            }
        }

        inputHost.CaptureChanged += OnChanged;
        try
        {
            return await reached.Task.WaitAsync(AsyncTimeout);
        }
        finally
        {
            inputHost.CaptureChanged -= OnChanged;
        }
    }

    private static async Task<InputObservation> WaitForObservationAfterAsync(
        RuntimeInputHost inputHost,
        string sourceId,
        long sequence)
    {
        var observed = new TaskCompletionSource<InputObservation>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnObserved(object? sender, InputObservationEventArgs update)
        {
            if (update.Observation.Sequence > sequence
                && string.Equals(
                    update.Observation.Source.Descriptor.SourceId,
                    sourceId,
                    StringComparison.OrdinalIgnoreCase))
            {
                observed.TrySetResult(update.Observation);
            }
        }

        inputHost.InputObserved += OnObserved;
        try
        {
            return await observed.Task.WaitAsync(AsyncTimeout);
        }
        finally
        {
            inputHost.InputObserved -= OnObserved;
        }
    }

    private static async Task<RuntimeActionActivity> WaitForActivityAsync(
        IRuntimeComposition composition,
        long afterSequence,
        string messageFragment)
    {
        RuntimeActionActivity? match = null;
        await WaitUntilAsync(() =>
        {
            match = ActivityAfter(composition, afterSequence).FirstOrDefault(activity =>
                activity.Message.Contains(messageFragment, StringComparison.Ordinal));
            return match is not null;
        });
        return match!;
    }

    private static RuntimeActionActivity[] ActivityAfter(
        IRuntimeComposition composition,
        long sequence) =>
        (composition.GetUiSnapshot().RecentActivity ?? [])
        .Where(activity => activity.Sequence > sequence)
        .ToArray();

    private static long LatestActivitySequence(IRuntimeComposition composition) =>
        composition.GetUiSnapshot().RecentActivity?.LastOrDefault()?.Sequence ?? 0;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < AsyncTimeout)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.Fail("The expected demo runtime state was not observed before the timeout.");
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private static readonly DateTimeOffset Origin =
            DateTimeOffset.Parse("2026-09-11T00:00:00Z");
        private long _elapsedTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() =>
            Origin + TimeSpan.FromTicks(Volatile.Read(ref _elapsedTicks));

        public override long GetTimestamp() => Volatile.Read(ref _elapsedTicks);

        public void SetElapsed(TimeSpan elapsed) =>
            Interlocked.Exchange(ref _elapsedTicks, elapsed.Ticks);
    }
}
