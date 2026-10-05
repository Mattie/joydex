using Joydex.Contracts;
using Joydex.App;
using Joydex.Core.Config;
using Joydex.Core.Runtime;
using Joydex.Core.TaskAlerts;
using Joydex.Core.Voice;
using Joydex.RuntimeHost.Production;
using Joydex.RuntimeHost.Settings;
using Joydex.WirelessPanel;
using Joydex.Windows.Voice;
using System.Text.Json;
using System.Windows.Forms;

namespace Joydex.RuntimeHost.Tests;

public sealed class ProductionRuntimeCompositionTests
{
    [Fact]
    public void ActiveVoiceProjectionIsOutsideCanonicalSettingsDocuments()
    {
        using var scratch = new ScratchDirectory();
        var companionPath = Path.Combine(scratch.Root, "config.json");
        var production = ProductionRuntimePaths.FromCompanionConfiguration(companionPath);
        var canonical = RuntimeSettingsPaths.ForConfiguration(companionPath);

        Assert.Equal(canonical.Voice, production.VoicePreferences);
        Assert.DoesNotContain(
            production.VoiceActivePreferences,
            new[]
            {
                canonical.Companion,
                canonical.Voice,
                canonical.PebbleIndex,
                canonical.TaskAlerts,
                canonical.Journal,
            });
    }

    [Fact]
    public async Task RefreshStartsEveryOwnerInDependencyOrder()
    {
        var factory = new FakeFactory();
        await using var composition = new ProductionRuntimeComposition(factory, default);

        var sources = composition.Refresh(Bundle());

        Assert.Equal(
            [
                SettingsAggregateId.TaskAlerts,
                SettingsAggregateId.Companion,
                SettingsAggregateId.Voice,
                SettingsAggregateId.PebbleIndex,
            ],
            factory.Created);
        Assert.Single(sources);
        Assert.Equal("controller", sources[0].SourceId);
        Assert.Single(factory.PluginRefreshes);
    }

    [Fact]
    public async Task PadLossAndRetryStayOutsideCompositionAndUnrelatedOwnerLifetimes()
    {
        var factory = new FakeFactory();
        var padHost = new PadPluginTests.FakeHost();
        var first = new PadPluginTests.FakeInstance();
        var replacement = new PadPluginTests.FakeInstance();
        var padInstances = new PadPluginTests.FakeInstanceFactory(first, replacement);
        var restart = new PadPluginTests.ManualRestartDelay();
        factory.Plugin = new Joydex.RuntimeHost.Plugins.PadPlugin(
            padHost,
            () => WirelessPanelConfiguration.Create(
                "http://panel.local/",
                "user",
                "secret"),
            padInstances,
            restart.DelayAsync,
            default);
        await using var composition = new ProductionRuntimeComposition(factory, default);
        composition.Refresh(Bundle());
        var owners = factory.LatestOwners();

        first.CompleteUnexpectedly();
        await restart.Scheduled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(composition.Completion.IsCompleted);
        Assert.All(owners.Values, owner => Assert.False(owner.Disposed));
        Assert.True(first.Disposed);
        restart.Release.TrySetResult();
        await padInstances.WaitForStartCountAsync(2).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(composition.Completion.IsCompleted);
        Assert.All(owners.Values, owner => Assert.False(owner.Disposed));
    }

    [Fact]
    public async Task PadPolicyRefreshFollowsOnlySuccessfulCompanionCommitAndRollback()
    {
        var factory = new FakeFactory();
        await using var composition = new ProductionRuntimeComposition(factory, default);
        var active = Bundle();
        composition.Refresh(active);
        var committed = active with
        {
            Companion = new CompanionConfig
            {
                Safety = new SafetyOptions { DryRun = false },
                Polling = new PollingOptions { PollIntervalMs = 20 },
            },
        };

        var applied = await composition.ActivateAsync(
            SettingsAggregateId.Companion,
            committed,
            2,
            default);
        var rejected = committed with
        {
            Companion = new CompanionConfig
            {
                Safety = new SafetyOptions { DryRun = false },
                Polling = new PollingOptions { PollIntervalMs = 21 },
            },
        };
        factory.FailNextCreate[SettingsAggregateId.Companion] = new IOException("candidate failed");
        var failed = await composition.ActivateAsync(
            SettingsAggregateId.Companion,
            rejected,
            3,
            default);

        Assert.Equal(SettingsActivationState.Applied, applied.State);
        Assert.Equal(SettingsActivationState.Failed, failed.State);
        Assert.Equal([active, committed, committed], factory.PluginRefreshes);
        Assert.DoesNotContain(rejected, factory.PluginRefreshes);
    }

    [Fact]
    public async Task StartupFailureRetriesOnlyThroughTheSettingsAuthority()
    {
        using var scratch = new ScratchDirectory();
        var factory = new FakeFactory();
        factory.FailNextCreate[SettingsAggregateId.Companion] = new IOException("device busy");
        await using var composition = new ProductionRuntimeComposition(factory, default);
        await using var coordinator = RuntimeSettingsCoordinator.Create(
            RuntimeSettingsPaths.InDataRoot(scratch.Root),
            new DefaultSettingsImpactPlanner(() => composition.VoiceSessionActive),
            composition);

        var startup = await coordinator.ReconcileStartupAsync(CancellationToken.None);
        var failed = Assert.Single(
            startup.Aggregates,
            state => state.Aggregate == SettingsAggregateId.Companion);

        Assert.Equal(SettingsActivationState.Failed, failed.Activation);
        Assert.Empty(composition.Refresh(startup.Active));
        Assert.Equal(1, factory.Created.Count(item => item == SettingsAggregateId.Companion));

        var retry = await coordinator.ActivateDesiredAsync(
            SettingsAggregateId.Companion,
            CancellationToken.None);
        var attached = await coordinator.AttachAsync(
            "settings-check",
            previousEpoch: null,
            afterSequence: null,
            _ => { },
            CancellationToken.None);
        using var subscription = attached.Subscription;
        var applied = Assert.Single(
            attached.Snapshot.Aggregates,
            state => state.Aggregate == SettingsAggregateId.Companion);

        Assert.Equal(SettingsActivationState.Applied, retry.State);
        Assert.Equal(SettingsActivationState.Applied, applied.Activation);
        Assert.Equal(applied.DesiredRevision, applied.ActiveRevision);
        Assert.Single(composition.Refresh(attached.Snapshot.Active));
        Assert.Equal(2, factory.Created.Count(item => item == SettingsAggregateId.Companion));
    }

    [Fact]
    public async Task AggregateReplacementPreservesUnrelatedOwners()
    {
        var factory = new FakeFactory();
        await using var composition = new ProductionRuntimeComposition(factory, default);
        var active = Bundle();
        composition.Refresh(active);
        var originals = factory.LatestOwners();

        var result = await composition.ActivateAsync(
            SettingsAggregateId.PebbleIndex,
            active with { PebbleIndex = active.PebbleIndex with { Port = 6123 } },
            desiredRevision: 2,
            default);

        Assert.Equal(SettingsActivationState.Applied, result.State);
        Assert.True(originals[SettingsAggregateId.PebbleIndex].Disposed);
        Assert.Same(originals[SettingsAggregateId.TaskAlerts], factory.Latest(SettingsAggregateId.TaskAlerts));
        Assert.Same(originals[SettingsAggregateId.Companion], factory.Latest(SettingsAggregateId.Companion));
        Assert.Same(originals[SettingsAggregateId.Voice], factory.Latest(SettingsAggregateId.Voice));
    }

    [Fact]
    public async Task ActivationFailureRestoresPriorOwner()
    {
        var factory = new FakeFactory();
        await using var composition = new ProductionRuntimeComposition(factory, default);
        var active = Bundle();
        composition.Refresh(active);
        var original = factory.Latest(SettingsAggregateId.PebbleIndex);
        factory.FailNextCreate[SettingsAggregateId.PebbleIndex] = new IOException("port busy");

        var result = await composition.ActivateAsync(
            SettingsAggregateId.PebbleIndex,
            active with { PebbleIndex = active.PebbleIndex with { Port = 6123 } },
            desiredRevision: 2,
            default);

        Assert.Equal(SettingsActivationState.Failed, result.State);
        Assert.True(original.Disposed);
        Assert.Equal(3, factory.Created.Count(item => item == SettingsAggregateId.PebbleIndex));
        Assert.False(factory.Latest(SettingsAggregateId.PebbleIndex).Disposed);
    }

    [Fact]
    public async Task CleanupFailureMakesAggregateTerminalAndShutdownRemainFailed()
    {
        var factory = new FakeFactory();
        var composition = new ProductionRuntimeComposition(factory, default);
        var active = Bundle();
        composition.Refresh(active);
        var original = factory.Latest(SettingsAggregateId.PebbleIndex);
        original.DisposeFailure = new IOException("listener did not stop");

        var first = await composition.ActivateAsync(
            SettingsAggregateId.PebbleIndex,
            active with { PebbleIndex = active.PebbleIndex with { Port = 6123 } },
            2,
            default);
        var second = await composition.ActivateAsync(
            SettingsAggregateId.PebbleIndex,
            active with { PebbleIndex = active.PebbleIndex with { Port = 6124 } },
            3,
            default);

        Assert.Equal(SettingsActivationState.Failed, first.State);
        Assert.Equal(SettingsActivationState.Failed, second.State);
        Assert.Equal(1, factory.Created.Count(item => item == SettingsAggregateId.PebbleIndex));
        var shutdown = await Assert.ThrowsAsync<AggregateException>(async () =>
            await composition.DisposeAsync());
        Assert.Contains("previously failed to confirm cleanup", shutdown.ToString());
        Assert.True(factory.Disposed);
    }

    [Fact]
    public async Task IncompleteVoiceStartupCleanupIsTerminalAndNeverRetried()
    {
        var factory = new FakeFactory();
        var composition = new ProductionRuntimeComposition(factory, default);
        factory.FailNextCreate[SettingsAggregateId.Voice] = new VoiceOwnershipCleanupException(
            "STA did not join",
            [new IOException("thread alive")]);

        composition.Refresh(Bundle());
        var result = await composition.ActivateAsync(
            SettingsAggregateId.Voice,
            Bundle() with { Voice = new VoicePePreferences(Enabled: true) },
            2,
            default);

        Assert.Equal(SettingsActivationState.Failed, result.State);
        Assert.Equal(1, factory.Created.Count(item => item == SettingsAggregateId.Voice));
        await Assert.ThrowsAsync<AggregateException>(async () => await composition.DisposeAsync());
    }

    [Fact]
    public async Task IncompletePhysicalOwnerStartupCleanupIsTerminalAndNeverRetried()
    {
        var factory = new FakeFactory();
        var composition = new ProductionRuntimeComposition(factory, default);
        factory.FailNextCreate[SettingsAggregateId.Companion] = new ProductionOwnershipCleanupException(
            "DirectInput cleanup failed",
            [new IOException("device still acquired")]);

        composition.Refresh(Bundle());
        var result = await composition.ActivateAsync(
            SettingsAggregateId.Companion,
            Bundle() with { Companion = new CompanionConfig() },
            2,
            default);

        Assert.Equal(SettingsActivationState.Failed, result.State);
        Assert.Equal(1, factory.Created.Count(item => item == SettingsAggregateId.Companion));
        await Assert.ThrowsAsync<AggregateException>(async () => await composition.DisposeAsync());
    }

    [Fact]
    public async Task ActiveVoiceDefersActivationAndIdlePublishesBoundary()
    {
        var factory = new FakeFactory { VoiceStartsActive = true };
        await using var composition = new ProductionRuntimeComposition(factory, default);
        var active = Bundle();
        composition.Refresh(active);
        var boundaries = 0;
        composition.ActivationBoundaryAvailable += (_, _) => boundaries++;

        var result = await composition.ActivateAsync(
            SettingsAggregateId.Voice,
            active with { Voice = active.Voice with { DeviceEndpoint = "http://127.0.0.1" } },
            2,
            default);
        ((FakeVoiceOwner)factory.Latest(SettingsAggregateId.Voice)).IsSessionActive = false;
        factory.RaiseVoiceIdle();

        Assert.Equal(SettingsActivationState.PendingIdle, result.State);
        Assert.Equal(1, factory.Created.Count(item => item == SettingsAggregateId.Voice));
        Assert.Equal(1, boundaries);
    }

    [Fact]
    public async Task ActiveVoiceAppliesOnlyItsTargetImmediatelyAndDefersMixedChanges()
    {
        using var scratch = new ScratchDirectory();
        var projectionPath = Path.Combine(scratch.Root, "runtime-active-voice-pe.json");
        var factory = new FakeFactory
        {
            VoiceStartsActive = true,
            VoiceProjectionPath = projectionPath,
        };
        await using var composition = new ProductionRuntimeComposition(factory, default);
        var active = Bundle();
        composition.Refresh(active);
        var originalVoiceOwner = factory.Latest(SettingsAggregateId.Voice);
        await using var coordinator = RuntimeSettingsCoordinator.Create(
            RuntimeSettingsPaths.InDataRoot(scratch.Root),
            new DefaultSettingsImpactPlanner(() => composition.VoiceSessionActive),
            composition);
        var attached = await coordinator.AttachAsync(
            "active-voice-settings",
            previousEpoch: null,
            afterSequence: null,
            _ => { },
            CancellationToken.None);
        using var subscription = attached.Subscription;
        var firstTargetId = Guid.NewGuid().ToString("D");
        var firstVoice = attached.Snapshot.Desired.Voice with
        {
            VoiceTargetTaskId = firstTargetId,
            VoiceTargetHostId = "local",
            VoiceTargetTaskLabel = "First target",
        };

        var preparedTarget = await coordinator.PrepareAsync(
            "active-voice-settings",
            new PrepareSettingsRequest(
                attached.Snapshot.Revision,
                new SettingsPatch(Voice: firstVoice)),
            CancellationToken.None);
        Assert.Equal(SettingsPrepareStatus.Prepared, preparedTarget.Status);
        Assert.Equal(SettingsEffectKind.ApplyLive, Assert.Single(preparedTarget.Effects).Kind);
        var appliedTarget = await coordinator.ApplyAsync(
            "active-voice-settings",
            new ApplySettingsRequest(Guid.NewGuid(), preparedTarget.PreparationToken!),
            CancellationToken.None);

        Assert.Equal(SettingsApplyStatus.Applied, appliedTarget.Status);
        Assert.Equal(firstTargetId, appliedTarget.Snapshot.Active.Voice.VoiceTargetTaskId);
        Assert.Same(originalVoiceOwner, factory.Latest(SettingsAggregateId.Voice));
        Assert.False(originalVoiceOwner.Disposed);
        Assert.Equal(firstVoice.Normalize(), Assert.Single(factory.VoiceMessagingRefreshes));
        Assert.Equal(
            firstTargetId,
            VoicePePreferencesStore.LoadExisting(projectionPath).VoiceTargetTaskId);

        var secondTargetId = Guid.NewGuid().ToString("D");
        var mixedVoice = firstVoice with
        {
            VoiceTargetTaskId = secondTargetId,
            VoiceTargetTaskLabel = "Second target",
            ConversationSpeakerGain = firstVoice.ConversationSpeakerGain + 1,
        };
        var preparedMixed = await coordinator.PrepareAsync(
            "active-voice-settings",
            new PrepareSettingsRequest(
                appliedTarget.Snapshot.Revision,
                new SettingsPatch(Voice: mixedVoice)),
            CancellationToken.None);
        Assert.Equal(SettingsEffectKind.PendingIdle, Assert.Single(preparedMixed.Effects).Kind);
        var deferredMixed = await coordinator.ApplyAsync(
            "active-voice-settings",
            new ApplySettingsRequest(Guid.NewGuid(), preparedMixed.PreparationToken!),
            CancellationToken.None);

        Assert.Equal(SettingsApplyStatus.PendingIdle, deferredMixed.Status);
        Assert.Equal(secondTargetId, deferredMixed.Snapshot.Desired.Voice.VoiceTargetTaskId);
        Assert.Equal(firstTargetId, deferredMixed.Snapshot.Active.Voice.VoiceTargetTaskId);
        Assert.Same(originalVoiceOwner, factory.Latest(SettingsAggregateId.Voice));
        Assert.Single(factory.VoiceMessagingRefreshes);
        Assert.Equal(
            firstTargetId,
            VoicePePreferencesStore.LoadExisting(projectionPath).VoiceTargetTaskId);
    }

    [Fact]
    public async Task ActiveVoiceProjectionFailureLeavesTheOldTargetActive()
    {
        using var scratch = new ScratchDirectory();
        var factory = new FakeFactory { VoiceStartsActive = true };
        await using var composition = new ProductionRuntimeComposition(factory, default);
        composition.Refresh(Bundle());
        var originalVoiceOwner = factory.Latest(SettingsAggregateId.Voice);
        await using var coordinator = RuntimeSettingsCoordinator.Create(
            RuntimeSettingsPaths.InDataRoot(scratch.Root),
            new DefaultSettingsImpactPlanner(() => composition.VoiceSessionActive),
            composition);
        var attached = await coordinator.AttachAsync(
            "failed-active-voice-settings",
            previousEpoch: null,
            afterSequence: null,
            _ => { },
            CancellationToken.None);
        using var subscription = attached.Subscription;
        var targetId = Guid.NewGuid().ToString("D");
        var candidate = attached.Snapshot.Desired.Voice with
        {
            VoiceTargetTaskId = targetId,
            VoiceTargetHostId = "local",
            VoiceTargetTaskLabel = "New target",
        };
        var prepared = await coordinator.PrepareAsync(
            "failed-active-voice-settings",
            new PrepareSettingsRequest(
                attached.Snapshot.Revision,
                new SettingsPatch(Voice: candidate)),
            CancellationToken.None);
        factory.VoiceMessagingFailure = new IOException("active projection unavailable");

        var applied = await coordinator.ApplyAsync(
            "failed-active-voice-settings",
            new ApplySettingsRequest(Guid.NewGuid(), prepared.PreparationToken!),
            CancellationToken.None);

        Assert.Equal(SettingsApplyStatus.ActivationFailed, applied.Status);
        Assert.Equal(targetId, applied.Snapshot.Desired.Voice.VoiceTargetTaskId);
        Assert.Empty(applied.Snapshot.Active.Voice.VoiceTargetTaskId);
        Assert.Same(originalVoiceOwner, factory.Latest(SettingsAggregateId.Voice));
        Assert.Empty(factory.VoiceMessagingRefreshes);
    }

    [Fact]
    public async Task VoiceBoundaryCompanionChangeRestartsOnlyCompanionAndVoice()
    {
        var factory = new FakeFactory();
        await using var composition = new ProductionRuntimeComposition(factory, default);
        var active = Bundle();
        composition.Refresh(active);
        var originals = factory.LatestOwners();
        var candidate = active with
        {
            Companion = new CompanionConfig
            {
                Safety = new SafetyOptions { DryRun = false },
            },
        };

        var result = await composition.ActivateAsync(
            SettingsAggregateId.Companion,
            candidate,
            2,
            default);

        Assert.Equal(SettingsActivationState.Applied, result.State);
        Assert.True(originals[SettingsAggregateId.Companion].Disposed);
        Assert.True(originals[SettingsAggregateId.Voice].Disposed);
        Assert.Same(originals[SettingsAggregateId.TaskAlerts], factory.Latest(SettingsAggregateId.TaskAlerts));
        Assert.Same(originals[SettingsAggregateId.PebbleIndex], factory.Latest(SettingsAggregateId.PebbleIndex));
        Assert.Equal(2, factory.Created.Count(item => item == SettingsAggregateId.Companion));
        Assert.Equal(2, factory.Created.Count(item => item == SettingsAggregateId.Voice));
    }

    [Fact]
    public async Task DependentVoiceFailureRollsBackCompanionAndVoice()
    {
        var factory = new FakeFactory();
        await using var composition = new ProductionRuntimeComposition(factory, default);
        var active = Bundle();
        composition.Refresh(active);
        factory.FailCreateOnAttempts[SettingsAggregateId.Voice] = [2];

        var result = await composition.ActivateAsync(
            SettingsAggregateId.Companion,
            active with
            {
                Companion = new CompanionConfig
                {
                    Safety = new SafetyOptions { DryRun = false },
                },
            },
            2,
            default);

        Assert.Equal(SettingsActivationState.Failed, result.State);
        Assert.Equal(3, factory.Created.Count(item => item == SettingsAggregateId.Companion));
        Assert.Equal(3, factory.Created.Count(item => item == SettingsAggregateId.Voice));
        Assert.False(factory.Latest(SettingsAggregateId.Companion).Disposed);
        Assert.False(factory.Latest(SettingsAggregateId.Voice).Disposed);
    }

    [Fact]
    public async Task VoiceExclusionAuthorityCommitsOnlySuccessfulGenerationAndSurvivesRollback()
    {
        var first = VoicePePreferences.Default with
        {
            Enabled = true,
            DeviceEndpoint = "http://127.0.0.1/",
            PinnedTaskId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            ConversationSpeakerGain = 2,
        };
        var candidate = first with
        {
            PinnedTaskId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            ConversationSpeakerGain = 3,
        };
        var factory = new FakeFactory();
        await using var composition = new ProductionRuntimeComposition(factory, default);
        composition.Refresh(Bundle() with { Voice = first });
        factory.FailCreateOnAttempts[SettingsAggregateId.Voice] = [2];

        var result = await composition.ActivateAsync(
            SettingsAggregateId.Voice,
            Bundle() with { Voice = candidate },
            2,
            default);

        Assert.Equal(SettingsActivationState.Failed, result.State);
        Assert.Equal([first.Normalize(), first.Normalize()], factory.VoiceActivationCommits);
        Assert.Equal([null, first.Normalize(), first.Normalize()], factory.VoiceActivationSeenAtCreate);
    }

    [Fact]
    public async Task FailedCompanionRollbackDoesNotRestoreDependentVoice()
    {
        var factory = new FakeFactory();
        await using var composition = new ProductionRuntimeComposition(factory, default);
        var active = Bundle();
        composition.Refresh(active);
        factory.FailCreateOnAttempts[SettingsAggregateId.Companion] = [2, 3];

        var result = await composition.ActivateAsync(
            SettingsAggregateId.Companion,
            active with
            {
                Companion = new CompanionConfig
                {
                    Safety = new SafetyOptions { DryRun = false },
                },
            },
            2,
            default);

        Assert.Equal(SettingsActivationState.Failed, result.State);
        Assert.Equal(3, factory.Created.Count(item => item == SettingsAggregateId.Companion));
        Assert.Equal(1, factory.Created.Count(item => item == SettingsAggregateId.Voice));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            composition.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task TerminalCompanionCleanupDoesNotRestartDependentVoice()
    {
        var factory = new FakeFactory();
        var composition = new ProductionRuntimeComposition(factory, default);
        var active = Bundle();
        composition.Refresh(active);
        factory.Latest(SettingsAggregateId.Companion).DisposeFailure =
            new IOException("DirectInput release was not confirmed");

        var result = await composition.ActivateAsync(
            SettingsAggregateId.Companion,
            active with
            {
                Companion = new CompanionConfig
                {
                    Safety = new SafetyOptions { DryRun = false },
                },
            },
            2,
            default);

        Assert.Equal(SettingsActivationState.Failed, result.State);
        Assert.Equal(1, factory.Created.Count(item => item == SettingsAggregateId.Companion));
        Assert.Equal(1, factory.Created.Count(item => item == SettingsAggregateId.Voice));
        await Assert.ThrowsAsync<AggregateException>(async () => await composition.DisposeAsync());
    }

    [Fact]
    public async Task TerminalCompanionStartupDoesNotRestoreDependentVoice()
    {
        var factory = new FakeFactory();
        var composition = new ProductionRuntimeComposition(factory, default);
        var active = Bundle();
        composition.Refresh(active);
        factory.FailNextCreate[SettingsAggregateId.Companion] =
            new ProductionOwnershipCleanupException(
                "DirectInput startup cleanup was not confirmed",
                [new IOException("device still acquired")]);

        var result = await composition.ActivateAsync(
            SettingsAggregateId.Companion,
            active with
            {
                Companion = new CompanionConfig
                {
                    Safety = new SafetyOptions { DryRun = false },
                },
            },
            2,
            default);

        Assert.Equal(SettingsActivationState.Failed, result.State);
        Assert.Equal(2, factory.Created.Count(item => item == SettingsAggregateId.Companion));
        Assert.Equal(1, factory.Created.Count(item => item == SettingsAggregateId.Voice));
        await Assert.ThrowsAsync<AggregateException>(async () => await composition.DisposeAsync());
    }

    [Fact]
    public async Task ShutdownContinuesInReverseOrderAfterOneOwnerFails()
    {
        var factory = new FakeFactory();
        var composition = new ProductionRuntimeComposition(factory, default);
        composition.Refresh(Bundle());
        factory.Latest(SettingsAggregateId.Voice).DisposeFailure = new IOException("media thread alive");
        factory.DisposeOrder.Clear();

        await Assert.ThrowsAsync<AggregateException>(async () => await composition.DisposeAsync());

        Assert.Equal(
            [
                SettingsAggregateId.PebbleIndex,
                SettingsAggregateId.Voice,
                SettingsAggregateId.Companion,
                SettingsAggregateId.TaskAlerts,
            ],
            factory.DisposeOrder);
        Assert.True(factory.Disposed);
    }

    [Fact]
    public async Task CaptureReleaseIsReentrantAndNeverTargetsReplacementGeneration()
    {
        var factory = new FakeFactory();
        await using var composition = new ProductionRuntimeComposition(factory, default);
        var active = Bundle();
        composition.Refresh(active);
        var first = (FakeInputOwner)factory.Latest(SettingsAggregateId.Companion);
        var firstCaptureId = Guid.NewGuid();
        first.ObserveCallback = () => composition.ReleaseCaptureObservation(firstCaptureId);

        var observed = await Task.Run(() => composition.ObserveForCapture(firstCaptureId, "controller"))
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(observed);
        Assert.Equal(1, first.ReleaseCount);

        first.ObserveCallback = null;
        var retiredCaptureId = Guid.NewGuid();
        Assert.True(composition.ObserveForCapture(retiredCaptureId, "controller"));
        first.DisposeCallback = () => composition.ReleaseCaptureObservation(retiredCaptureId);
        var result = await composition.ActivateAsync(
            SettingsAggregateId.Companion,
            active with { Companion = new CompanionConfig() },
            2,
            default);
        var replacement = (FakeInputOwner)factory.Latest(SettingsAggregateId.Companion);
        composition.ReleaseCaptureObservation(retiredCaptureId);

        Assert.Equal(SettingsActivationState.Applied, result.State);
        Assert.Equal(1, first.ReleaseCount);
        Assert.Equal(0, replacement.ReleaseCount);
    }

    [Fact]
    public async Task SharedCaptureObservationStopsOnlyAfterItsLastLeaseEnds()
    {
        var factory = new FakeFactory();
        await using var composition = new ProductionRuntimeComposition(factory, default);
        composition.Refresh(Bundle());
        var input = (FakeInputOwner)factory.Latest(SettingsAggregateId.Companion);
        var firstCaptureId = Guid.NewGuid();
        var secondCaptureId = Guid.NewGuid();

        Assert.True(composition.ObserveForCapture(firstCaptureId, "controller"));
        Assert.True(composition.ObserveForCapture(secondCaptureId, "controller"));
        composition.ReleaseCaptureObservation(firstCaptureId);

        Assert.Equal(2, input.ObserveCount);
        Assert.Equal(0, input.ReleaseCount);

        composition.ReleaseCaptureObservation(secondCaptureId);

        Assert.Equal(1, input.ReleaseCount);
    }

    [Fact]
    public async Task StaleCaptureReleaseCannotStopTheReplacementOwnersObservation()
    {
        var factory = new FakeFactory();
        await using var composition = new ProductionRuntimeComposition(factory, default);
        var active = Bundle();
        composition.Refresh(active);
        var staleCaptureId = Guid.NewGuid();
        Assert.True(composition.ObserveForCapture(staleCaptureId, "controller"));

        var result = await composition.ActivateAsync(
            SettingsAggregateId.Companion,
            active with { Companion = new CompanionConfig() },
            2,
            default);
        var replacement = (FakeInputOwner)factory.Latest(SettingsAggregateId.Companion);
        var replacementCaptureId = Guid.NewGuid();
        Assert.True(composition.ObserveForCapture(replacementCaptureId, "controller"));

        composition.ReleaseCaptureObservation(staleCaptureId);

        Assert.Equal(SettingsActivationState.Applied, result.State);
        Assert.Equal(0, replacement.ReleaseCount);

        composition.ReleaseCaptureObservation(replacementCaptureId);

        Assert.Equal(1, replacement.ReleaseCount);
    }

    [Fact]
    public async Task UiSnapshotAndEventsRelayFromFactory()
    {
        var factory = new FakeFactory();
        await using var composition = new ProductionRuntimeComposition(factory, default);
        var events = new List<RuntimeEventKind>();
        composition.UiChanged += (_, update) => events.Add(update.Kind);

        factory.PublishUi(new RuntimeUiEvent(
            RuntimeEventKind.ControllerStatusChanged,
            controllerStatus: new RuntimeControllerStatus("one", "One", "Connected", true)));

        Assert.Equal([RuntimeEventKind.ControllerStatusChanged], events);
        Assert.Same(factory.UiSnapshot, composition.GetUiSnapshot());
    }

    [Fact]
    public void VoiceContinuationTokenRejectsAReplacedConversation()
    {
        var token = VoiceProductionOwner.EncodeContinuationToken(conversationVersion: 4, offset: 25);

        Assert.Equal(25, VoiceProductionOwner.DecodeContinuationToken(token, conversationVersion: 4));
        Assert.Throws<InvalidDataException>(() =>
            VoiceProductionOwner.DecodeContinuationToken(token, conversationVersion: 5));
    }

    [Fact]
    public void ReplacingCompanionClearsRetiredControllersAndButtonMaps()
    {
        var projector = new ProductionRuntimeUiProjector();
        projector.PublishController("retired", "Old device", "Stopped", true);
        projector.PublishButtonMap("retired", true);
        var events = new List<RuntimeUiEvent>();
        projector.Changed += (_, update) => events.Add(update);

        projector.ResetCompanion();
        projector.PublishController("replacement", "New device", "Connected", false);

        var snapshot = projector.GetSnapshot();
        Assert.Equal("replacement", Assert.Single(snapshot.Controllers!).DeviceId);
        Assert.Empty(snapshot.ButtonMaps!);
        Assert.Contains(events, update => update.Kind == RuntimeEventKind.UiResynchronizationRequired);
        projector.ResetCompanion();
        Assert.Empty(projector.GetSnapshot().Controllers!);
    }

    [Fact]
    public void DisablingVoiceClearsSessionAndHistoryAndRequestsClientResynchronization()
    {
        var projector = new ProductionRuntimeUiProjector();
        projector.SetActiveVoicePreferences(VoicePePreferences.Default);
        projector.PublishVoice(new ProductionVoiceState(
            new RuntimeVoiceSnapshot(RuntimeVoiceSessionState.Listening, true, true, true, false, "Listening", null, 1),
            [new RuntimeVoiceTimelineEntry("entry", DateTimeOffset.UnixEpoch, RuntimeVoiceTimelineKind.User, "old")]));
        RuntimeUiEvent? update = null;
        projector.Changed += (_, value) => update = value;

        projector.ClearVoice();

        Assert.Null(projector.GetSnapshot().Voice);
        Assert.Equal(RuntimeEventKind.UiResynchronizationRequired, update?.Kind);
        projector.PublishDesktopTasks([]);
        projector.RefreshVoiceMessaging(VoicePePreferences.Default with { Enabled = false });
        Assert.Null(projector.GetSnapshot().Voice);
    }

    [Fact]
    public void ProjectorPreservesDesktopTasksAcrossConversationUpdatesAndResetsReplacedHistory()
    {
        var projector = new ProductionRuntimeUiProjector();
        var voiceEvents = new List<RuntimeVoiceEvent>();
        projector.Changed += (_, update) =>
        {
            if (update.Voice is not null)
            {
                voiceEvents.Add(update.Voice);
            }
        };
        var preferences = VoicePePreferences.Default with { DesktopTaskMessagingEnabled = true };
        var session = new RuntimeVoiceSnapshot(
            RuntimeVoiceSessionState.Armed,
            true,
            false,
            true,
            false,
            "Armed",
            null,
            1);
        var first = new RuntimeVoiceTimelineEntry(
            "a",
            DateTimeOffset.UtcNow,
            RuntimeVoiceTimelineKind.User,
            "first");
        projector.SetActiveVoicePreferences(preferences);
        projector.PublishVoice(new ProductionVoiceState(session, [first]));
        projector.PublishDesktopTasks(
            [new DesktopTaskSummary("01990323-bbdf-7bc0-a359-a90f0777c9f1", "local", "Task", "idle", null, null, 1)]);

        var second = first with { Id = "b", Text = "replacement" };
        projector.PublishVoice(new ProductionVoiceState(session with { ConversationVersion = 2 }, [second]));

        var snapshot = projector.GetSnapshot();
        Assert.Single(snapshot.Voice!.Messaging.Tasks);
        Assert.True(voiceEvents[^1].TimelineReset);
        Assert.Null(voiceEvents[^1].TimelineEntry);
    }

    [Fact]
    public void VoiceUpdateReloadsDraftsCreatedAfterThePreviousProjection()
    {
        using var scratch = new ScratchDirectory();
        var projector = new ProductionRuntimeUiProjector();
        var preferences = VoicePePreferences.Default with
        {
            AgentWorkspacePath = scratch.Root,
            DesktopTaskMessagingEnabled = true,
        };
        var session = new RuntimeVoiceSnapshot(
            RuntimeVoiceSessionState.Armed,
            true,
            false,
            true,
            false,
            "Armed",
            null,
            1);
        var state = new ProductionVoiceState(session, []);
        projector.SetActiveVoicePreferences(preferences);
        projector.PublishVoice(state);
        Assert.Empty(projector.GetSnapshot().Voice!.Messaging.Drafts);

        var target = new DesktopTaskSummary(
            Guid.NewGuid().ToString("D"),
            "local",
            "Target",
            "idle",
            null,
            null,
            1);
        var draft = new VoiceTaskOutbox(scratch.Root).Hold(
            target,
            "Review this delivery",
            "voice-session",
            "Desktop bridge unavailable");

        projector.PublishVoice(state);

        var projected = Assert.Single(projector.GetSnapshot().Voice!.Messaging.Drafts);
        Assert.Equal(draft.Id, projected.Id);
        Assert.Equal(draft.Message, projected.MessagePreview);
    }

    [Fact]
    public void VoiceProjectionIncludesNewestDraftWhenOutboxExceedsLimit()
    {
        using var scratch = new ScratchDirectory();
        var outbox = new VoiceTaskOutbox(scratch.Root);
        var target = new DesktopTaskSummary(Guid.NewGuid().ToString("D"), "local", "Target", "idle", null, null, 1);
        for (var index = 0; index <= RuntimeUiLimits.MaximumVoiceOutboxDrafts; index++)
        {
            var draft = outbox.Hold(target, $"Message {index}", "session", "Unavailable");
            outbox.RecordFailedAttempt(draft with { CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(index) }, "Unavailable");
        }
        var projector = new ProductionRuntimeUiProjector();
        projector.SetActiveVoicePreferences(VoicePePreferences.Default with { AgentWorkspacePath = scratch.Root });
        projector.PublishVoice(new ProductionVoiceState(
            new RuntimeVoiceSnapshot(RuntimeVoiceSessionState.Armed, true, false, true, false, "Armed", null, 1), []));

        var messaging = projector.GetSnapshot().Voice!.Messaging;
        Assert.True(messaging.DraftsTruncated);
        Assert.Equal(RuntimeUiLimits.MaximumVoiceOutboxDrafts, messaging.Drafts.Length);
        Assert.Equal("Message 1", messaging.Drafts[0].MessagePreview);
        Assert.Equal($"Message {RuntimeUiLimits.MaximumVoiceOutboxDrafts}", messaging.Drafts[^1].MessagePreview);
    }

    [Fact]
    public void OversizedTaskAlertUpdatePublishesResynchronizationAndRetainsSnapshot()
    {
        var projector = new ProductionRuntimeUiProjector();
        var updates = new List<RuntimeUiEvent>();
        projector.Changed += (_, update) => updates.Add(update);
        var suppressions = Enumerable.Range(0, 100).Select(index =>
            new TaskAlertSuppressionRule(TaskAlertSuppressionScope.Workspace, $"C:\\{index}\\" + new string('a', 4000))).ToArray();
        projector.PublishTaskAlerts(
            new Joydex.Windows.TaskAlerts.TaskAlertSnapshot(true, [], 7, Suppressions: suppressions),
            new RuntimeTaskAlertHookStatus(RuntimeTaskAlertHookState.Installed));

        var update = Assert.Single(updates);
        Assert.Equal(RuntimeEventKind.UiResynchronizationRequired, update.Kind);
        Assert.Null(update.TaskAlerts);
        var restored = projector.GetSnapshot().TaskAlerts!;
        Assert.Equal(suppressions, restored.Suppressions);
        Assert.Equal(7, restored.DroppedEventCount);
    }

    [Fact]
    public void SnapshotOmitsDuplicateTaskAlertSuppressionsWhenNonAsciiPathsExceedWireBudget()
    {
        var projector = new ProductionRuntimeUiProjector();
        var suppressions = Enumerable.Range(0, 100).Select(index =>
            new TaskAlertSuppressionRule(
                TaskAlertSuppressionScope.Workspace,
                $"C:\\{index}\\" + new string('\u754c', 4_000))).ToArray();
        projector.PublishTaskAlerts(
            new Joydex.Windows.TaskAlerts.TaskAlertSnapshot(true, [], 9, Suppressions: suppressions),
            new RuntimeTaskAlertHookStatus(RuntimeTaskAlertHookState.Installed));

        var restored = projector.GetSnapshot().TaskAlerts!;

        Assert.Empty(restored.Suppressions);
        Assert.Equal(9, restored.DroppedEventCount);
        var encoded = JsonSerializer.SerializeToUtf8Bytes(
            projector.GetSnapshot(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(encoded.Length <= RuntimeUiLimits.MaximumEncodedSnapshotBytes);
    }

    [Fact]
    public void DelayedVoiceStatusAndTaskCatalogKeepTheCurrentActiveTarget()
    {
        var projector = new ProductionRuntimeUiProjector();
        var firstTask = new DesktopTaskSummary(
            Guid.NewGuid().ToString("D"),
            "local",
            "First target",
            "idle",
            null,
            null,
            1);
        var secondTask = firstTask with
        {
            Id = Guid.NewGuid().ToString("D"),
            Title = "Second target",
        };
        var firstPreferences = VoicePePreferences.Default with
        {
            DesktopTaskMessagingEnabled = true,
            VoiceTargetTaskId = firstTask.Id,
            VoiceTargetHostId = firstTask.HostId,
            VoiceTargetTaskLabel = firstTask.Title,
        };
        var secondPreferences = firstPreferences with
        {
            VoiceTargetTaskId = secondTask.Id,
            VoiceTargetTaskLabel = secondTask.Title,
        };
        var session = new RuntimeVoiceSnapshot(
            RuntimeVoiceSessionState.Armed,
            true,
            false,
            true,
            false,
            "Armed",
            null,
            1);
        projector.SetActiveVoicePreferences(firstPreferences);
        projector.PublishVoice(new ProductionVoiceState(session, []));
        projector.PublishDesktopTasks([firstTask, secondTask]);

        projector.RefreshVoiceMessaging(secondPreferences);
        projector.PublishVoice(new ProductionVoiceState(
            session with { Status = "Delayed owner status" },
            []));
        projector.PublishDesktopTasks([firstTask, secondTask]);

        var voice = projector.GetSnapshot().Voice;
        Assert.NotNull(voice);
        var selected = voice.Messaging.SelectedTask;
        Assert.NotNull(selected);
        Assert.Equal(secondTask.Id, selected.TaskId);
        Assert.Equal(secondTask.Title, voice.Messaging.SelectedLabel);
    }

    [Fact]
    public void ProjectorResetsWhenAWindowShiftAlsoChangesRetainedHistory()
    {
        var projector = new ProductionRuntimeUiProjector();
        RuntimeVoiceEvent? latest = null;
        projector.Changed += (_, update) => latest = update.Voice ?? latest;
        var session = new RuntimeVoiceSnapshot(
            RuntimeVoiceSessionState.Armed,
            true,
            false,
            true,
            false,
            "Armed",
            null,
            1);
        var timeline = Enumerable.Range(0, RuntimeUiLimits.MaximumVoiceTimelineEntries)
            .Select(index => new RuntimeVoiceTimelineEntry(
                index.ToString(),
                DateTimeOffset.UnixEpoch.AddSeconds(index),
                RuntimeVoiceTimelineKind.Assistant,
                $"entry {index}"))
            .ToArray();
        projector.SetActiveVoicePreferences(VoicePePreferences.Default);
        projector.PublishVoice(
            new ProductionVoiceState(session, timeline));
        var shifted = timeline.Skip(1).Append(new RuntimeVoiceTimelineEntry(
            "next",
            DateTimeOffset.UnixEpoch.AddSeconds(timeline.Length),
            RuntimeVoiceTimelineKind.Assistant,
            "next"))
            .ToArray();
        shifted[0] = shifted[0] with { Text = "changed retained history" };

        projector.PublishVoice(
            new ProductionVoiceState(session with { ConversationVersion = 2 }, shifted));

        Assert.NotNull(latest);
        Assert.True(latest.TimelineReset);
        Assert.Null(latest.TimelineEntry);
    }

    [Fact]
    public async Task ProductionWindowsStaOwnsHiddenWindowAndJoinsItsThread()
    {
        var callerThreadId = Environment.CurrentManagedThreadId;
        var host = await ProductionWindowsStaHost.StartAsync(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2));

        var observed = await host.InvokeAsync(() => new
        {
            ThreadId = Environment.CurrentManagedThreadId,
            Apartment = Thread.CurrentThread.GetApartmentState(),
            Context = SynchronizationContext.Current,
            Handle = host.WindowHandle,
        });

        Assert.NotEqual(callerThreadId, observed.ThreadId);
        Assert.Equal(host.ThreadId, observed.ThreadId);
        Assert.Equal(ApartmentState.STA, observed.Apartment);
        Assert.IsType<WindowsFormsSynchronizationContext>(observed.Context);
        Assert.NotEqual(IntPtr.Zero, observed.Handle);

        await host.DisposeAsync();

        Assert.True(host.Completion.IsCompletedSuccessfully);
        Assert.True(host.ThreadExited.IsCompletedSuccessfully);
        Assert.False(host.IsThreadAlive);
    }

    [Fact]
    public async Task ProductionWindowsStaPreservesAffinityAcrossAsynchronousCleanupSteps()
    {
        await using var host = await ProductionWindowsStaHost.StartAsync(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2));
        var firstCleanupReached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var continueCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupThreadIds = new List<int>();

        var cleanup = host.InvokeAsync(async () =>
        {
            cleanupThreadIds.Add(Environment.CurrentManagedThreadId);
            firstCleanupReached.TrySetResult();
            await continueCleanup.Task;
            cleanupThreadIds.Add(Environment.CurrentManagedThreadId);
        });
        await firstCleanupReached.Task.WaitAsync(TimeSpan.FromSeconds(2));
        continueCleanup.TrySetResult();
        await cleanup.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, cleanupThreadIds.Count);
        Assert.All(cleanupThreadIds, threadId => Assert.Equal(host.ThreadId, threadId));
    }

    [Fact]
    public async Task ProductionWindowsStaReturnsValuesAfterAsynchronousOwningThreadWork()
    {
        await using var host = await ProductionWindowsStaHost.StartAsync(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2));
        var operationThreadIds = new List<int>();

        var result = await host.InvokeAsync(async () =>
        {
            operationThreadIds.Add(Environment.CurrentManagedThreadId);
            await Task.Yield();
            operationThreadIds.Add(Environment.CurrentManagedThreadId);
            return 42;
        }).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(42, result);
        Assert.Equal(2, operationThreadIds.Count);
        Assert.All(operationThreadIds, threadId => Assert.Equal(host.ThreadId, threadId));
    }

    [Fact]
    public async Task UnexpectedProductionWindowsStaExitFaultsCompletion()
    {
        var host = await ProductionWindowsStaHost.StartAsync(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2));

        var dispatchFailure = Record.Exception(() => host.Invoke(Application.ExitThread));
        if (dispatchFailure is not null)
        {
            var unavailable = Assert.IsType<InvalidOperationException>(dispatchFailure);
            Assert.Contains(
                "stopped unexpectedly",
                unavailable.ToString(),
                StringComparison.OrdinalIgnoreCase);
        }

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
        await host.ThreadExited.WaitAsync(TimeSpan.FromSeconds(2));
        await host.DisposeAsync();

        Assert.Contains("stopped unexpectedly", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(host.IsThreadAlive);
    }

    [Fact]
    public async Task UnexpectedOwnerLossFaultsProductionCompositionCompletion()
    {
        var factory = new FakeFactory();
        await using var composition = new ProductionRuntimeComposition(factory, default);
        composition.Refresh(Bundle());
        var failure = new IOException("controller worker stopped");

        factory.Latest(SettingsAggregateId.Companion).Fail(failure);

        var observed = await Assert.ThrowsAsync<IOException>(() =>
            composition.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Same(failure, observed);
    }

    [Fact]
    public void ExplicitDesktopTaskListSourceDoesNotRequireVoiceAndRemainsInTheCatalog()
    {
        var sourceTaskId = Guid.NewGuid();
        var request = new RuntimeCommandRequest(
            Guid.NewGuid(),
            RuntimeCommandKind.ListDesktopTasks,
            new RuntimeCommandArguments(
                Task: new RuntimeTaskReference(
                    $"codex://threads/{sourceTaskId:D}",
                    " LOCAL ")));

        var normalized = RuntimeCommandCanonicalizer.TryNormalize(
            request,
            out var normalizedRequest,
            out var error);
        Assert.True(normalized, error);
        var source = WindowsProductionRuntimeOwnerFactory.ResolveDesktopTaskListSource(
            normalizedRequest,
            Bundle());

        Assert.Equal(sourceTaskId.ToString("D"), normalizedRequest.Arguments!.Task!.TaskId);
        Assert.Equal("LOCAL", normalizedRequest.Arguments.Task.HostId);
        Assert.Equal(sourceTaskId.ToString("D"), source.SourceTaskId);
        Assert.Null(source.ExcludedTaskId);
        Assert.False(RuntimeCommandCanonicalizer.TryNormalize(
            request with
            {
                Arguments = new RuntimeCommandArguments(
                    Task: new RuntimeTaskReference(sourceTaskId.ToString("D"), "remote")),
            },
            out _,
            out _));
    }

    [Fact]
    public void DesktopTaskListWithoutAnExplicitSourcePreservesVoiceRoutingAndExclusion()
    {
        var sourceTaskId = Guid.NewGuid().ToString("D");
        var request = new RuntimeCommandRequest(
            Guid.NewGuid(),
            RuntimeCommandKind.ListDesktopTasks);
        var activeSettings = Bundle() with
        {
            Voice = VoicePePreferences.Default with { DedicatedTaskId = sourceTaskId },
        };

        Assert.True(RuntimeCommandCanonicalizer.TryNormalize(
            request,
            out var normalizedRequest,
            out var error), error);
        var source = WindowsProductionRuntimeOwnerFactory.ResolveDesktopTaskListSource(
            normalizedRequest,
            activeSettings);

        Assert.Equal(sourceTaskId, source.SourceTaskId);
        Assert.Equal(sourceTaskId, source.ExcludedTaskId);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            WindowsProductionRuntimeOwnerFactory.ResolveDesktopTaskListSource(request, Bundle()));
        Assert.Equal(
            "A valid Dedicated Voice Task is required for Desktop task messaging.",
            exception.Message);
    }

    [Fact]
    public async Task FactoryBoundaryLossBeforeOwnerInstallationFaultsCompositionCompletion()
    {
        var factory = new FakeFactory();
        await using var composition = new ProductionRuntimeComposition(factory, default);
        var failure = new IOException("production STA stopped");

        factory.Fail(failure);

        var observed = await Assert.ThrowsAsync<IOException>(() =>
            composition.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Same(failure, observed);
        Assert.Empty(factory.Created);
    }

    [Fact]
    public async Task BrokerReleaseCleanupFailurePermanentlyClosesAdmission()
    {
        var cleanupFailure = TerminalBrokerCleanup("release did not stop child");
        var process = new FakeDesktopBrokerProcess
        {
            DisposeFailure = cleanupFailure,
            CompleteOnDispose = false,
        };
        var starter = new FakeDesktopBrokerStarter(process);
        var manager = CreateDesktopBrokerManager(starter);
        var lease = await manager.AcquireAsync(default);

        var release = await Assert.ThrowsAsync<DesktopTaskBridgeOwnershipCleanupException>(() =>
            lease.DisposeAsync().AsTask());
        var reacquire = await Assert.ThrowsAsync<DesktopTaskBridgeOwnershipCleanupException>(() =>
            manager.AcquireAsync(default));
        var shutdown = await Assert.ThrowsAsync<DesktopTaskBridgeOwnershipCleanupException>(() =>
            manager.DisposeAsync().AsTask());

        Assert.Same(cleanupFailure, release);
        Assert.Same(cleanupFailure, reacquire);
        Assert.Same(cleanupFailure, shutdown);
        Assert.Equal(1, starter.StartCount);
    }

    [Fact]
    public async Task BrokerObserverCleanupFailurePreventsAutomaticOrDirectReplacement()
    {
        var cleanupFailure = TerminalBrokerCleanup("observer could not confirm cleanup");
        var process = new FakeDesktopBrokerProcess
        {
            DisposeFailure = cleanupFailure,
            CompleteOnDispose = false,
        };
        var starter = new FakeDesktopBrokerStarter(process);
        var manager = CreateDesktopBrokerManager(starter);
        var lease = await manager.AcquireAsync(default);

        process.Complete();

        var terminal = await Assert.ThrowsAsync<DesktopTaskBridgeOwnershipCleanupException>(() =>
            manager.TerminalCompletion.WaitAsync(TimeSpan.FromSeconds(2)));
        var reacquire = await Assert.ThrowsAsync<DesktopTaskBridgeOwnershipCleanupException>(() =>
            manager.AcquireAsync(default));

        Assert.Same(cleanupFailure, terminal);
        Assert.Same(cleanupFailure, reacquire);
        Assert.Equal(1, starter.StartCount);
        await Assert.ThrowsAsync<DesktopTaskBridgeOwnershipCleanupException>(() =>
            lease.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<DesktopTaskBridgeOwnershipCleanupException>(() =>
            manager.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task BrokerRestartCleanupFailurePreventsEveryLaterStart()
    {
        var cleanupFailure = TerminalBrokerCleanup("failed restart retained child");
        var process = new FakeDesktopBrokerProcess();
        var starter = new FakeDesktopBrokerStarter(process, cleanupFailure);
        var manager = CreateDesktopBrokerManager(starter);
        var lease = await manager.AcquireAsync(default);

        process.Complete();

        var terminal = await Assert.ThrowsAsync<DesktopTaskBridgeOwnershipCleanupException>(() =>
            manager.TerminalCompletion.WaitAsync(TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAsync<DesktopTaskBridgeOwnershipCleanupException>(() =>
            manager.AcquireAsync(default));

        Assert.Same(cleanupFailure, terminal);
        Assert.Equal(2, starter.StartCount);
        await Assert.ThrowsAsync<DesktopTaskBridgeOwnershipCleanupException>(() =>
            lease.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<DesktopTaskBridgeOwnershipCleanupException>(() =>
            manager.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task BrokerShutdownCleanupFailureRemainsVisibleAfterBackgroundJoin()
    {
        var cleanupFailure = TerminalBrokerCleanup("shutdown did not stop child");
        var process = new FakeDesktopBrokerProcess
        {
            DisposeFailure = cleanupFailure,
            CompleteOnDispose = false,
        };
        var starter = new FakeDesktopBrokerStarter(process);
        var manager = CreateDesktopBrokerManager(starter);
        var lease = await manager.AcquireAsync(default);

        var shutdown = await Assert.ThrowsAsync<DesktopTaskBridgeOwnershipCleanupException>(() =>
            manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Same(cleanupFailure, shutdown);
        Assert.False(process.Completion.IsCompleted);
        Assert.Equal(1, starter.StartCount);
        await lease.DisposeAsync();
    }

    private static ProductionDesktopBrokerManager CreateDesktopBrokerManager(
        IProductionDesktopBrokerStarter starter) => new(
        Path.Combine(Path.GetTempPath(), "Joydex.DesktopBridgeHost.exe"),
        "Joydex.Test.DesktopBridge." + Guid.NewGuid().ToString("N"),
        _ => { },
        default,
        starter,
        TimeSpan.FromMilliseconds(10));

    private static DesktopTaskBridgeOwnershipCleanupException TerminalBrokerCleanup(string message) =>
        new(message, [new IOException(message)]);

    private static SettingsBundle Bundle() => new(
        CompanionConfig.CreateSafeDefault(),
        VoicePePreferences.Default,
        PebbleIndexPreferences.Default,
        TaskAlertPreferences.Default);

    private sealed class ScratchDirectory : IDisposable
    {
        public ScratchDirectory()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "joydex-production-composition-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class FakeFactory : IProductionRuntimeOwnerFactory
    {
        private readonly Dictionary<SettingsAggregateId, List<FakeOwner>> _owners = [];
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action? VoiceBecameIdle;
        public event EventHandler<RuntimeUiEvent>? UiChanged;

        public List<SettingsAggregateId> Created { get; } = [];
        public List<SettingsAggregateId> DisposeOrder { get; } = [];
        public Dictionary<SettingsAggregateId, Exception> FailNextCreate { get; } = [];
        public Dictionary<SettingsAggregateId, int[]> FailCreateOnAttempts { get; } = [];
        public List<VoicePePreferences> VoiceMessagingRefreshes { get; } = [];
        public List<SettingsBundle> PluginRefreshes { get; } = [];
        public List<VoicePePreferences> VoiceActivationCommits { get; } = [];
        public List<VoicePePreferences?> VoiceActivationSeenAtCreate { get; } = [];
        public Joydex.RuntimeHost.Plugins.PadPlugin? Plugin { get; set; }
        public Exception? VoiceMessagingFailure { get; set; }
        public string? VoiceProjectionPath { get; init; }
        public bool VoiceStartsActive { get; init; }
        public bool Disposed { get; private set; }
        public RuntimeUiSnapshot UiSnapshot { get; } = new();
        public Task Completion => _completion.Task;

        public Task<IProductionRuntimeOwner?> CreateAsync(
            SettingsAggregateId aggregate,
            SettingsBundle activeSettings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Created.Add(aggregate);
            if (aggregate == SettingsAggregateId.Voice)
            {
                VoiceActivationSeenAtCreate.Add(VoiceActivationCommits.LastOrDefault());
            }
            var attempt = Created.Count(item => item == aggregate);
            if (FailNextCreate.Remove(aggregate, out var failure))
            {
                throw failure;
            }
            if (FailCreateOnAttempts.TryGetValue(aggregate, out var failedAttempts)
                && failedAttempts.Contains(attempt))
            {
                throw new IOException($"{aggregate} attempt {attempt} failed");
            }
            if (aggregate == SettingsAggregateId.Voice && VoiceProjectionPath is not null)
            {
                VoicePePreferencesStore.Save(VoiceProjectionPath, activeSettings.Voice);
            }

            FakeOwner owner = aggregate switch
            {
                SettingsAggregateId.Companion => new FakeInputOwner(this),
                SettingsAggregateId.Voice => new FakeVoiceOwner(this) { IsSessionActive = VoiceStartsActive },
                _ => new FakeOwner(aggregate, this),
            };
            if (!_owners.TryGetValue(aggregate, out var generations))
            {
                generations = [];
                _owners.Add(aggregate, generations);
            }
            generations.Add(owner);
            return Task.FromResult<IProductionRuntimeOwner?>(owner);
        }

        public Task<RuntimeCommandResult> ExecuteAsync(
            RuntimeCommandRequest request,
            SettingsBundle activeSettings,
            CancellationToken cancellationToken) => Task.FromResult(new RuntimeCommandResult(
                request.OperationId,
                request.Kind,
                RuntimeCommandStatus.Completed));

        public RuntimeUiSnapshot GetUiSnapshot() => UiSnapshot;

        public async Task RefreshPluginsAsync(
            SettingsBundle activeSettings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PluginRefreshes.Add(activeSettings);
            if (Plugin is not null)
            {
                await Plugin.RefreshSharedConfigurationAsync(
                    activeSettings.Companion,
                    cancellationToken);
            }
        }

        public void RefreshVoiceMessaging(VoicePePreferences preferences)
        {
            if (VoiceMessagingFailure is { } failure)
            {
                throw failure;
            }
            if (VoiceProjectionPath is not null)
            {
                VoicePePreferencesStore.Save(VoiceProjectionPath, preferences);
            }
            VoiceMessagingRefreshes.Add(preferences);
        }

        public void CommitVoiceActivation(VoicePePreferences preferences) =>
            VoiceActivationCommits.Add(preferences.Normalize());

        public void ReportFailure(SettingsAggregateId aggregate, Exception exception) { }

        public async ValueTask DisposeAsync()
        {
            Disposed = true;
            _completion.TrySetResult();
            if (Plugin is not null)
            {
                await Plugin.DisposeAsync();
            }
        }

        public FakeOwner Latest(SettingsAggregateId aggregate) => _owners[aggregate][^1];

        public Dictionary<SettingsAggregateId, FakeOwner> LatestOwners() =>
            _owners.ToDictionary(item => item.Key, item => item.Value[^1]);

        public void RaiseVoiceIdle() => VoiceBecameIdle?.Invoke();

        public void PublishUi(RuntimeUiEvent update) => UiChanged?.Invoke(this, update);

        public void Fail(Exception exception) => _completion.TrySetException(exception);

    }

    private class FakeOwner(SettingsAggregateId aggregate, FakeFactory factory) : IProductionRuntimeOwner
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SettingsAggregateId Aggregate { get; } = aggregate;
        public Task Completion => _completion.Task;
        public Exception? DisposeFailure { get; set; }
        public Action? DisposeCallback { get; set; }
        public bool Disposed { get; private set; }

        public virtual ValueTask DisposeAsync()
        {
            if (Disposed)
            {
                return ValueTask.CompletedTask;
            }
            Disposed = true;
            factory.DisposeOrder.Add(Aggregate);
            DisposeCallback?.Invoke();
            _completion.TrySetResult();
            return DisposeFailure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(DisposeFailure);
        }

        public void Fail(Exception exception) => _completion.TrySetException(exception);
    }

    private sealed class FakeInputOwner(FakeFactory factory)
        : FakeOwner(SettingsAggregateId.Companion, factory), IProductionInputOwner
    {
        public Action? ObserveCallback { get; set; }
        public int ObserveCount { get; private set; }
        public int ReleaseCount { get; private set; }

        public RuntimeInputSource[] Refresh(SettingsBundle activeSettings) =>
            [new RuntimeInputSource("controller", "Controller", "hardware", null, null, null, true)];

        public bool ObserveForCapture(string sourceId)
        {
            ObserveCount++;
            ObserveCallback?.Invoke();
            return true;
        }

        public void ReleaseCaptureObservation(string sourceId) => ReleaseCount++;

        public void DismissPromptPicker() { }
    }

    private sealed class FakeVoiceOwner(FakeFactory factory)
        : FakeOwner(SettingsAggregateId.Voice, factory), IProductionVoiceOwner
    {
        public bool IsSessionActive { get; set; }

        public Task EndSessionAsync(CancellationToken cancellationToken)
        {
            IsSessionActive = false;
            return Task.CompletedTask;
        }

        public Task RefreshConversationAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ProductionVoiceState GetState() => new(
            new RuntimeVoiceSnapshot(
                RuntimeVoiceSessionState.Armed,
                false,
                IsSessionActive,
                false,
                false,
                "Armed",
                null,
                0),
            []);

        public Task<RuntimeVoiceConversationPage> GetConversationPageAsync(
            string? continuationToken,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RuntimeVoiceConversationPage([]));
    }

    private sealed class FakeDesktopBrokerStarter(params object[] outcomes)
        : IProductionDesktopBrokerStarter
    {
        private readonly Queue<object> _outcomes = new(outcomes);

        public int StartCount { get; private set; }

        public Task<IProductionDesktopBrokerProcess> StartAsync(
            string executablePath,
            string pipeName,
            Action<string> log,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            var outcome = _outcomes.Dequeue();
            return outcome is Exception exception
                ? Task.FromException<IProductionDesktopBrokerProcess>(exception)
                : Task.FromResult((IProductionDesktopBrokerProcess)outcome);
        }
    }

    private sealed class FakeDesktopBrokerProcess : IProductionDesktopBrokerProcess
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completion => _completion.Task;

        public Exception? DisposeFailure { get; init; }

        public bool CompleteOnDispose { get; init; } = true;

        public void Complete() => _completion.TrySetResult();

        public ValueTask DisposeAsync()
        {
            if (CompleteOnDispose)
            {
                Complete();
            }
            return DisposeFailure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(DisposeFailure);
        }
    }
}
