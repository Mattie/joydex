using System.Security.Cryptography;
using Joydex.Secrets;

namespace Joydex.Tests;

public sealed class SecretsBrokerCoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "joydex-secrets-broker-" + Guid.NewGuid().ToString("N"));

    public SecretsBrokerCoreTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void InvalidCredentialIsAuditedBeforeIdentityRejection()
    {
        using var harness = new Harness(_directory);
        var wrong = RandomNumberGenerator.GetBytes(32);
        try
        {
            var response = harness.Broker.Submit(harness.Submission("request-1"), wrong);

            Assert.Equal(SecretsRequestStatus.IdentityUnverified, response.Status);
            var records = harness.Audit.ReadAll();
            Assert.Collection(
                records,
                record => Assert.Equal(SecretsAuditEventKind.RequestReceived, record.Kind),
                record => Assert.Equal(SecretsAuditEventKind.RequestOutcome, record.Kind));
            Assert.All(records, record => Assert.Equal(response.AttemptId, record.AttemptId));
            Assert.DoesNotContain("synthetic-secret", File.ReadAllText(harness.AuditPath));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrong);
        }
    }

    [Fact]
    public async Task YesOnceCreatesOneAtomicReservationAndRunsExactOperation()
    {
        using var harness = new Harness(_directory);
        var pending = harness.Submit("request-1");
        var shown = Assert.Single(harness.Broker.PendingRequests());

        var allowed = harness.Broker.Decide(
            pending.AttemptId,
            shown.DisplayChallenge,
            SecretsConsentChoice.Yes,
            requireOperation: true);
        var result = await harness.RunAsync(allowed);

        Assert.Equal(SecretsRequestStatus.Allowed, allowed.Status);
        Assert.Equal(0, result.ExitCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RunAsync(allowed));
    }

    [Fact]
    public async Task CommittedExecutionStaysActivePastApprovalExpiryUntilCancelled()
    {
        using var harness = new Harness(_directory);
        var ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        var pending = harness.Submit("long-running", $"{ping} -n 60 127.0.0.1 > nul");
        var shown = Assert.Single(harness.Broker.PendingRequests());
        var allowed = harness.Broker.Decide(pending.AttemptId, shown.DisplayChallenge,
            SecretsConsentChoice.Yes, requireOperation: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var execution = harness.RunAsync(allowed, cancellation.Token);
        try
        {
            while (!harness.Audit.ReadAll().Any(record => record.AttemptId == pending.AttemptId
                && record.Kind == SecretsAuditEventKind.LaunchStarted))
                await Task.Delay(10, cancellation.Token);
            harness.Clock.Advance(TimeSpan.FromMinutes(3));
            Assert.Equal(SecretsRequestStatus.Allowed, harness.Status(pending.RequestId).Status);
            Assert.True(harness.Broker.ContainsActiveRequest("release-helper", "joydex", pending.RequestId));
            Assert.DoesNotContain(harness.Audit.ReadAll(), record => record.AttemptId == pending.AttemptId
                && record.Outcome == "expired");
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        }
        Assert.False(harness.Broker.ContainsActiveRequest("release-helper", "joydex", pending.RequestId));
    }

    [Fact]
    public async Task DetachedRequestCanRecordFuturePolicyButCanNeverRun()
    {
        using var harness = new Harness(_directory);
        var pending = harness.Submit("detached-request");

        var detached = harness.Broker.Detach(
            "release-helper",
            harness.Credential,
            "joydex",
            pending.RequestId,
            SecretsDetachReason.LeavePending);
        var shown = Assert.Single(harness.Broker.PendingRequests());
        var remembered = harness.Broker.Decide(
            detached.AttemptId,
            shown.DisplayChallenge,
            SecretsConsentChoice.YesAlways,
            requireOperation: false);

        Assert.Equal(SecretsRequestStatus.AgentDetached, detached.Status);
        Assert.Equal(SecretsDetachReason.LeavePending, shown.DetachedReason);
        Assert.Equal(SecretsRequestStatus.Denied, remembered.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Broker.RunAsync(
            "release-helper",
            harness.Credential,
            "joydex",
            pending.RequestId,
            "fabricated-reservation",
            new Dictionary<string, string>(),
            CancellationToken.None));
        Assert.Equal(SecretsRequestStatus.Allowed, harness.Submit("future-request").Status);
        Assert.DoesNotContain(
            harness.Audit.ReadAll(),
            record => record.AttemptId == pending.AttemptId
                && record.Kind == SecretsAuditEventKind.LaunchCommitted);
    }

    [Fact]
    public void FirstUseIndicatorClearsAfterTheAliasHasBeenRequestedForThatAgentAndProject()
    {
        using var harness = new Harness(_directory);
        var first = harness.Submit("first-use");
        var firstShown = Assert.Single(harness.Broker.PendingRequests());
        harness.Broker.Decide(
            first.AttemptId,
            firstShown.DisplayChallenge,
            SecretsConsentChoice.No,
            requireOperation: true);

        harness.ReopenBroker();
        harness.Submit("second-use", command: "exit /b 1");
        var secondShown = Assert.Single(harness.Broker.PendingRequests());

        Assert.Equal(["deploy-token"], firstShown.NewAliases);
        Assert.Empty(secondShown.NewAliases);
    }

    [Fact]
    public void ReenrolledRequesterWithSameLabelsGetsFirstUseWarning()
    {
        using var harness = new Harness(_directory);
        harness.Submit("old-registration");
        harness.Clients.Revoke("release-helper");
        using var enrollment = harness.Clients.Enroll("release-helper", "Release helper",
            [harness.Project], [SecretDeliveryMode.ExecInject]);
        var credential = enrollment.CopyCredential();
        try
        {
            harness.ReopenBroker();
            harness.Broker.Submit(harness.Submission("new-registration"), credential);
            Assert.Equal(["deploy-token"], Assert.Single(harness.Broker.PendingRequests()).NewAliases);
        }
        finally { CryptographicOperations.ZeroMemory(credential); }
    }

    [Fact]
    public void ChangedCanonicalProjectWithSameLabelGetsFirstUseWarning()
    {
        using var harness = new Harness(_directory);
        harness.Submit("old-project");
        var root = Path.Combine(_directory, "new-project");
        Directory.CreateDirectory(root);
        var project = harness.Project with { ProjectId = "new-project", CanonicalProjectRoot = root,
            CanonicalWorktreeRoot = root, WorktreeId = "new-worktree" };
        var registration = harness.Clients.EnsureLocalRequester("release-helper", "Release helper",
            project, SecretDeliveryMode.ExecInject, harness.Credential);
        Assert.Equal(harness.Principal.RegistrationId, registration.Principal.RegistrationId);
        harness.ReopenBroker();
        var submission = harness.Submission("new-project");
        harness.Submit(submission with { Operation = submission.Operation! with { WorkingDirectory = root } });
        Assert.Equal(["deploy-token"], Assert.Single(harness.Broker.PendingRequests()).NewAliases);
    }

    [Fact]
    public void LegacyAuditWithoutAuthenticatedIdentityCannotHideFirstUse()
    {
        using var harness = new Harness(_directory);
        harness.Audit.Append(new(Guid.NewGuid(), SecretsAuditEventKind.RequestOutcome,
            harness.Clock.GetUtcNow(), "legacy", "release-helper", "joydex", ["deploy-token"],
            OperationDigest: new string('a', 64), Outcome: "pending"));
        harness.Submit("current");
        Assert.Equal(["deploy-token"], Assert.Single(harness.Broker.PendingRequests()).NewAliases);
    }

    [Fact]
    public async Task RememberedApprovalThatExpiresBeforeRedemptionFailsClosed()
    {
        using var harness = new Harness(_directory);
        var seed = harness.Submit("seed");
        var shown = Assert.Single(harness.Broker.PendingRequests());
        harness.Broker.Decide(
            seed.AttemptId,
            shown.DisplayChallenge,
            SecretsConsentChoice.Yes24Hours,
            requireOperation: true);
        harness.Clock.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(58.5));
        var allowed = harness.Submit("request-1");
        harness.Clock.Advance(TimeSpan.FromMinutes(1.55));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.RunAsync(allowed));

        Assert.Contains("remembered approval expired", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            harness.Audit.ReadAll(),
            record => record.Kind == SecretsAuditEventKind.LaunchCommitted
                && record.AttemptId == allowed.AttemptId);
    }

    [Fact]
    public async Task RedemptionFromAnotherEnrolledProjectIsRejected()
    {
        using var harness = new Harness(_directory, enrollSecondProject: true);
        var pending = harness.Submit("request-1");
        var shown = Assert.Single(harness.Broker.PendingRequests());
        var allowed = harness.Broker.Decide(
            pending.AttemptId,
            shown.DisplayChallenge,
            SecretsConsentChoice.Yes,
            requireOperation: true);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            harness.RunAsync(allowed, projectReference: "joydex-alt"));
    }

    [Fact]
    public async Task CancellationBeforeLaunchWritesNoLaunchCommit()
    {
        using var harness = new Harness(_directory);
        var pending = harness.Submit("request-1");
        var shown = Assert.Single(harness.Broker.PendingRequests());
        var allowed = harness.Broker.Decide(
            pending.AttemptId,
            shown.DisplayChallenge,
            SecretsConsentChoice.Yes,
            requireOperation: true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.RunAsync(allowed, cancellation.Token));

        Assert.DoesNotContain(
            harness.Audit.ReadAll(),
            record => record.Kind == SecretsAuditEventKind.LaunchCommitted
                && record.AttemptId == allowed.AttemptId);
    }

    [Fact]
    public void OperationBoundAlwaysCachesExactRepeatAndPromptsForChangedOperation()
    {
        using var harness = new Harness(_directory);
        var first = harness.Submit("request-1");
        var shown = Assert.Single(harness.Broker.PendingRequests());
        harness.Broker.Decide(
            first.AttemptId,
            shown.DisplayChallenge,
            SecretsConsentChoice.YesAlways,
            requireOperation: true);

        var repeat = harness.Submit("request-2");
        var changed = harness.Submit("request-3", command: "echo changed");

        Assert.Equal(SecretsRequestStatus.Allowed, repeat.Status);
        Assert.Equal(SecretsRequestStatus.Pending, changed.Status);
        Assert.Single(harness.Broker.PendingRequests());
    }

    [Fact]
    public void CallerSuppliedOperationGenerationCannotMissAnExactApproval()
    {
        using var harness = new Harness(_directory);
        var first = harness.Submit("generation-seed");
        var shown = Assert.Single(harness.Broker.PendingRequests());
        harness.Broker.Decide(
            first.AttemptId,
            shown.DisplayChallenge,
            SecretsConsentChoice.YesAlways,
            requireOperation: true);
        var retry = harness.Submission("generation-retry");
        retry = retry with
        {
            Operation = retry.Operation with { OperationGeneration = 999 },
        };

        var response = harness.Submit(retry);

        Assert.Equal(SecretsRequestStatus.Allowed, response.Status);
        Assert.Empty(harness.Broker.PendingRequests());
    }

    [Fact]
    public void ClientScopedAlwaysCachesChangedOperationWithinSameBoundedScope()
    {
        using var harness = new Harness(_directory);
        var first = harness.Submit("request-1");
        var shown = Assert.Single(harness.Broker.PendingRequests());
        harness.Broker.Decide(
            first.AttemptId,
            shown.DisplayChallenge,
            SecretsConsentChoice.YesAlways,
            requireOperation: false);

        var changed = harness.Submit("request-2", command: "echo changed");

        Assert.Equal(SecretsRequestStatus.Allowed, changed.Status);
        Assert.Empty(harness.Broker.PendingRequests());
    }

    [Fact]
    public void PendingRequestUsesEnvironmentNamesAndBoundedOperationDetailsForDisplay()
    {
        using var harness = new Harness(_directory);
        harness.Submit("request-1");

        var shown = Assert.Single(harness.Broker.PendingRequests());

        Assert.Equal("worktree-test", shown.WorktreeReference);
        Assert.Equal(["CANARY_TOKEN"], shown.EnvironmentVariables);
        Assert.Equal(
            Path.Combine(Environment.SystemDirectory, "cmd.exe") + " /d /c \"exit /b 0\"",
            shown.CommandLine);
        Assert.False(shown.CommandLineTruncated);
        Assert.Equal("worktree-test", shown.WorktreeReference);
        Assert.Equal(SecretOutputDisclosure.Summary, shown.OutputDisclosure);
    }

    [Fact]
    public void PendingRequestNormalizesCallerReasonAndDisclosesCommandTruncation()
    {
        using var harness = new Harness(_directory);
        harness.Submit(
            "bounded-display-request",
            command: "echo " + new string('x', 900),
            reason: "Confirm\r\n  this\trequest.");

        var shown = Assert.Single(harness.Broker.PendingRequests());

        Assert.Equal("Confirm this request.", shown.Reason);
        Assert.True(shown.CommandLineTruncated);
        Assert.Equal(64, shown.OperationDigest.Length);
    }

    [Fact]
    public void PendingRequestExpiresAfterTwoMinutes()
    {
        using var harness = new Harness(_directory);
        var pending = harness.Submit("two-minute-request");

        Assert.Equal(harness.Clock.GetUtcNow().AddSeconds(120), pending.ExpiresAt);
        harness.Clock.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal(SecretsRequestStatus.Pending, harness.Status(pending.RequestId).Status);
        harness.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(SecretsRequestStatus.Expired, harness.Status(pending.RequestId).Status);
        Assert.Contains(
            harness.Audit.ReadAll(),
            record => record.AttemptId == pending.AttemptId
                && record.Kind == SecretsAuditEventKind.RequestOutcome
                && record.Outcome == "expired");
    }

    [Fact]
    public void ClientScopedNeverDeniesChangedOperationWithinSameBoundedScope()
    {
        using var harness = new Harness(_directory);
        var first = harness.Submit("request-1");
        var shown = Assert.Single(harness.Broker.PendingRequests());
        harness.Broker.Decide(
            first.AttemptId,
            shown.DisplayChallenge,
            SecretsConsentChoice.Never,
            requireOperation: false);

        var changed = harness.Submit("request-2", command: "echo changed");

        Assert.Equal(SecretsRequestStatus.Denied, changed.Status);
        Assert.Empty(harness.Broker.PendingRequests());
    }

    [Fact]
    public void NeverPersistsExactDenyAheadOfBroaderAllow()
    {
        using var harness = new Harness(_directory);
        var denySeed = harness.Submit("deny-seed", command: "echo denied-operation");
        var denyShown = Assert.Single(harness.Broker.PendingRequests());
        harness.Broker.Decide(
            denySeed.AttemptId,
            denyShown.DisplayChallenge,
            SecretsConsentChoice.Never,
            requireOperation: true);

        var scopeSeed = harness.Submit("allow-seed");
        var allowShown = Assert.Single(harness.Broker.PendingRequests());
        harness.Broker.Decide(
            scopeSeed.AttemptId,
            allowShown.DisplayChallenge,
            SecretsConsentChoice.YesAlways,
            requireOperation: false);

        var exactDenied = harness.Submit("request-3", command: "echo denied-operation");
        var differentAllowed = harness.Submit("request-4", command: "echo other-operation");

        Assert.Equal(SecretsRequestStatus.Denied, exactDenied.Status);
        Assert.Equal(SecretsRequestStatus.Allowed, differentAllowed.Status);
    }

    [Fact]
    public void SameRequestIdWithChangedAuthorityFieldsReturnsConflict()
    {
        using var harness = new Harness(_directory);

        Assert.Equal(SecretsRequestStatus.Pending, harness.Submit("request-1").Status);
        var conflict = harness.Submit("request-1", command: "echo changed");

        Assert.Equal(SecretsRequestStatus.Conflict, conflict.Status);
        Assert.Single(harness.Broker.PendingRequests());
    }

    [Fact]
    public void PendingQueueIsBoundedPerClient()
    {
        using var harness = new Harness(_directory, maximumPendingPerClient: 1);

        Assert.Equal(SecretsRequestStatus.Pending, harness.Submit("request-1").Status);
        Assert.Equal(SecretsRequestStatus.QueueFull, harness.Submit("request-2").Status);
    }

    [Fact]
    public void ProviderAliasIdentityMismatchFailsBeforeConsent()
    {
        using var harness = new Harness(_directory, brokerStableSecretId: "different-secret");

        var response = harness.Submit("request-1");

        Assert.Equal(SecretsRequestStatus.ProviderUnavailable, response.Status);
        Assert.Empty(harness.Broker.PendingRequests());
    }

    [Fact]
    public void UnknownConfiguredAliasReportsProviderUnavailableBeforeConsent()
    {
        using var harness = new Harness(_directory);
        var submission = harness.Submission("request-1");
        var operation = submission.Operation with
        {
            SecretEnvironment = new Dictionary<string, string>
            {
                ["SYNTHETIC_TOKEN"] = "SYNTHETIC_TOKEN",
            },
        };

        var response = harness.Submit(submission with { Operation = operation });

        Assert.Equal(SecretsRequestStatus.ProviderUnavailable, response.Status);
        Assert.Empty(harness.Broker.PendingRequests());
    }

    [Fact]
    public async Task ProviderFailureAfterApprovalRecordsATerminalProviderOutcome()
    {
        using var harness = new Harness(_directory);
        var pending = harness.Submit("provider-disappeared");
        var shown = Assert.Single(harness.Broker.PendingRequests());
        var allowed = harness.Broker.Decide(
            pending.AttemptId,
            shown.DisplayChallenge,
            SecretsConsentChoice.Yes,
            requireOperation: true);
        File.Delete(harness.EnvPath);

        await Assert.ThrowsAnyAsync<IOException>(() => harness.RunAsync(allowed));

        Assert.Equal(
            SecretsRequestStatus.ProviderUnavailable,
            harness.Status(pending.RequestId).Status);
        Assert.Contains(
            harness.Audit.ReadAll(),
            record => record.AttemptId == pending.AttemptId
                && record.Kind == SecretsAuditEventKind.RequestOutcome
                && record.Outcome == "providerunavailable");
    }

    [Fact]
    public void RestartRefusesToRelaunchAnUnconfirmedCommittedRequest()
    {
        using var harness = new Harness(_directory);
        var first = harness.Submit("request-1");
        var shown = Assert.Single(harness.Broker.PendingRequests());
        harness.Audit.Append(new(
            first.AttemptId,
            SecretsAuditEventKind.LaunchCommitted,
            harness.Clock.GetUtcNow(),
            first.RequestId,
            "release-helper",
            "joydex",
            ["deploy-token"],
            shown.OperationDigest,
            ClientRegistrationId: harness.Principal.RegistrationId,
            ClientGeneration: harness.Principal.Generation));
        harness.ReopenBroker();

        var retried = harness.Submit("request-1");

        Assert.Equal(SecretsRequestStatus.LaunchUnconfirmed, retried.Status);
        Assert.Null(retried.Reservation);
        Assert.Empty(harness.Broker.PendingRequests());
    }

    [Fact]
    public async Task RestartKeepsACompletedRequestIdTerminal()
    {
        using var harness = new Harness(_directory);
        var pending = harness.Submit("request-1");
        var shown = Assert.Single(harness.Broker.PendingRequests());
        var allowed = harness.Broker.Decide(
            pending.AttemptId,
            shown.DisplayChallenge,
            SecretsConsentChoice.YesAlways,
            requireOperation: true);
        var result = await harness.RunAsync(allowed);
        Assert.Equal(0, result.ExitCode);
        harness.ReopenBroker();

        var retried = harness.Submit("request-1");

        Assert.Equal(SecretsRequestStatus.Completed, retried.Status);
        Assert.Null(retried.Reservation);
        Assert.Empty(harness.Broker.PendingRequests());
    }

    [Fact]
    public void RestartKeepsAFailedBeforeLaunchRequestTerminalWithoutCallingItCompleted()
    {
        using var harness = new Harness(_directory);
        var first = harness.Submit("failed-launch");
        var shown = Assert.Single(harness.Broker.PendingRequests());
        harness.Audit.Append(new(
            first.AttemptId,
            SecretsAuditEventKind.LaunchCommitted,
            harness.Clock.GetUtcNow(),
            first.RequestId,
            "release-helper",
            "joydex",
            ["deploy-token"],
            shown.OperationDigest,
            ClientRegistrationId: harness.Principal.RegistrationId,
            ClientGeneration: harness.Principal.Generation));
        harness.Audit.Append(new(
            first.AttemptId,
            SecretsAuditEventKind.FailedBeforeLaunch,
            harness.Clock.GetUtcNow(),
            first.RequestId,
            "release-helper",
            "joydex",
            ["deploy-token"]));
        harness.ReopenBroker();

        var retried = harness.Submit("failed-launch");

        Assert.Equal(SecretsRequestStatus.Revoked, retried.Status);
        Assert.Equal(
            SecretsCommittedOutcome.FailedBeforeLaunch,
            Assert.Single(harness.Audit.FindCommittedRequests()).Outcome);
    }

    [Fact]
    public void ReservationCapacityReturnsQueueFullForCachedAndInteractiveApprovals()
    {
        using var harness = new Harness(_directory, maximumReservations: 1);
        var seed = harness.Submit("seed");
        var seedShown = Assert.Single(harness.Broker.PendingRequests());
        var seedAllowed = harness.Broker.Decide(
            seed.AttemptId,
            seedShown.DisplayChallenge,
            SecretsConsentChoice.YesAlways,
            requireOperation: true);

        var cached = harness.Submit("cached");
        var pending = harness.Submit("interactive", command: "echo changed");
        var shown = Assert.Single(harness.Broker.PendingRequests());
        var interactive = harness.Broker.Decide(
            pending.AttemptId,
            shown.DisplayChallenge,
            SecretsConsentChoice.Yes,
            requireOperation: true);

        Assert.Equal(SecretsRequestStatus.Allowed, seedAllowed.Status);
        Assert.Equal(SecretsRequestStatus.QueueFull, cached.Status);
        Assert.Equal(SecretsRequestStatus.QueueFull, interactive.Status);
        Assert.Single(harness.Policy.Read(harness.Clock.GetUtcNow()).Rules);
    }

    [Fact]
    public void DenyAllResolvesPendingRequestsAndRejectsNewOnes()
    {
        using var harness = new Harness(_directory);
        var pending = harness.Submit("pending-before-deny");

        harness.OperatingMode.DenyAll(harness.Clock.GetUtcNow());

        Assert.Empty(harness.Broker.PendingRequests());
        Assert.Equal(SecretsRequestStatus.Denied, harness.Status(pending.RequestId).Status);
        Assert.Equal(SecretsRequestStatus.Denied, harness.Submit("request-during-deny").Status);
    }

    [Fact]
    public async Task ChangingToDenyAllInvalidatesAnUnredeemedApproval()
    {
        using var harness = new Harness(_directory);
        var pending = harness.Submit("allowed-before-deny");
        var shown = Assert.Single(harness.Broker.PendingRequests());
        var allowed = harness.Broker.Decide(
            pending.AttemptId,
            shown.DisplayChallenge,
            SecretsConsentChoice.Yes,
            requireOperation: true);

        harness.OperatingMode.DenyAll(harness.Clock.GetUtcNow());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.RunAsync(allowed));
        Assert.Contains("denying all", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            harness.Audit.ReadAll(),
            record => record.AttemptId == allowed.AttemptId
                && record.Kind == SecretsAuditEventKind.LaunchCommitted);
    }

    [Fact]
    public async Task AutoAllowOverridesRememberedDenyOnlyUntilItsFixedExpiry()
    {
        using var harness = new Harness(_directory);
        var seed = harness.Submit("deny-seed");
        var shown = Assert.Single(harness.Broker.PendingRequests());
        Assert.Equal(
            SecretsRequestStatus.Denied,
            harness.Broker.Decide(
                seed.AttemptId,
                shown.DisplayChallenge,
                SecretsConsentChoice.Never,
                requireOperation: false).Status);
        harness.OperatingMode.AutoAllowFor24Hours(harness.Clock.GetUtcNow());
        var autoAllowed = harness.Submit("auto-allowed");

        Assert.Equal(SecretsRequestStatus.Allowed, autoAllowed.Status);
        harness.Clock.Advance(TimeSpan.FromHours(24));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.RunAsync(autoAllowed));
        var deniedAgain = harness.Submit("after-auto-allow");

        Assert.Contains("auto-allow window ended", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SecretsRequestStatus.Denied, deniedAgain.Status);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class Harness : IDisposable
    {
        private readonly byte[] _credential;
        private readonly string _directory;

        public Harness(
            string root,
            int maximumPendingPerClient = 8,
            bool enrollSecondProject = false,
            string brokerStableSecretId = "stable-token",
            int maximumReservations = 2048)
        {
            _directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            EnvPath = Path.Combine(_directory, ".env");
            File.WriteAllText(EnvPath, "TOKEN=synthetic-secret\n");
            Project = new SecretsProjectIdentity(
                "joydex",
                "joydex-project",
                _directory,
                "worktree-test",
                _directory,
                1,
                WorktreeReference: "worktree-test");
            var projects = new List<SecretsProjectIdentity> { Project };
            if (enrollSecondProject)
            {
                projects.Add(Project with
                {
                    ProjectReference = "joydex-alt",
                    ProjectId = "joydex-project-alt",
                    WorktreeId = "worktree-alt",
                });
            }
            Clients = new NamedClientRegistry(Path.Combine(_directory, "clients.json"));
            using (var enrollment = Clients.Enroll(
                       "release-helper",
                       "Release helper",
                       projects,
                       [SecretDeliveryMode.ExecInject]))
            {
                _credential = enrollment.CopyCredential();
                Principal = enrollment.Principal;
            }
            Provider = new ExactEnvSecretProvider(
                EnvPath,
                [new("deploy-token", "TOKEN", "stable-token", 1)]);
            Aliases = [new("deploy-token", "env", brokerStableSecretId, 1)];
            AuditPath = Path.Combine(_directory, "audit.jsonl");
            Audit = new SecretsAuditJournal(AuditPath);
            Policy = new SecretsPolicyStore(Path.Combine(_directory, "policy.json"));
            OperatingMode = new SecretsOperatingModeStore(Path.Combine(_directory, "operating-mode.json"));
            Clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-13T00:00:00Z"));
            _maximumPendingPerClient = maximumPendingPerClient;
            _maximumReservations = maximumReservations;
            Broker = CreateBroker();
        }

        private readonly int _maximumPendingPerClient;
        private readonly int _maximumReservations;
        public NamedClientRegistry Clients { get; }
        public SecretsPolicyStore Policy { get; }
        public SecretsOperatingModeStore OperatingMode { get; }
        public ExactEnvSecretProvider Provider { get; }
        public SecretsAliasIdentity[] Aliases { get; }
        public SecretsClientPrincipal Principal { get; }
        public SecretsProjectIdentity Project { get; }
        public ManualTimeProvider Clock { get; }
        public SecretsBrokerCore Broker { get; private set; }
        public SecretsAuditJournal Audit { get; }
        public string AuditPath { get; }
        public string EnvPath { get; }
        public byte[] Credential => _credential;

        public void ReopenBroker() => Broker = CreateBroker();

        private SecretsBrokerCore CreateBroker() => new(
                Clients,
                Policy,
                Audit,
                Provider,
                Aliases,
                Clock,
                maximumPendingPerClient: _maximumPendingPerClient,
                maximumReservations: _maximumReservations,
                operatingMode: OperatingMode);

        public SecretsRequestSubmission Submission(
            string requestId,
            string command = "exit /b 0",
            string reason = "Synthetic test request") => new(
            requestId,
            "release-helper",
            "joydex",
            new ExecOperationProposal(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                ["/d", "/c", command],
                _directory,
                new Dictionary<string, string> { ["CANARY_TOKEN"] = "deploy-token" },
                [],
                SecretOutputDisclosure.Summary),
            reason);

        public SecretsRequestResponse Submit(
            string requestId,
            string command = "exit /b 0",
            string reason = "Synthetic test request") =>
            Broker.Submit(Submission(requestId, command, reason), _credential);

        public SecretsRequestResponse Submit(SecretsRequestSubmission submission) =>
            Broker.Submit(submission, _credential);

        public SecretsRequestResponse Status(string requestId) => Broker.GetStatus(
            "release-helper",
            _credential,
            "joydex",
            requestId);

        public Task<SecretsExecResult> RunAsync(
            SecretsRequestResponse response,
            CancellationToken cancellationToken = default,
            string projectReference = "joydex") => Broker.RunAsync(
            "release-helper",
            _credential,
            projectReference,
            response.RequestId,
            response.Reservation!,
            new Dictionary<string, string>(),
            cancellationToken);

        public void Dispose() => CryptographicOperations.ZeroMemory(_credential);
    }

    public sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
