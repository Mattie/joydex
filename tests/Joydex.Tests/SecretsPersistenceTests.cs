using System.Security.Cryptography;
using Joydex.Secrets;

namespace Joydex.Tests;

public sealed class SecretsPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "joydex-secrets-persistence-" + Guid.NewGuid().ToString("N"));

    public SecretsPersistenceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void NamedClientAuthenticationUsesStoredIdentityAndExactProjectAndMode()
    {
        var path = Path.Combine(_directory, "clients.json");
        var registry = new NamedClientRegistry(path);
        using var enrollment = registry.Enroll(
            "release-helper",
            "Release helper",
            [Project()],
            [SecretDeliveryMode.ExecInject]);
        var credential = enrollment.CopyCredential();
        try
        {
            var reloaded = new NamedClientRegistry(path);

            var authenticated = reloaded.Authenticate(
                "release-helper", credential, "joydex", SecretDeliveryMode.ExecInject);
            var wrongProject = reloaded.Authenticate(
                "release-helper", credential, "other", SecretDeliveryMode.ExecInject);
            var wrongMode = reloaded.Authenticate(
                "release-helper", credential, "joydex", SecretDeliveryMode.RawRead);

            Assert.NotNull(authenticated);
            Assert.Equal(enrollment.Principal.RegistrationId, authenticated.Principal.RegistrationId);
            Assert.Equal("worktree-main", authenticated.Project.WorktreeId);
            Assert.Null(wrongProject);
            Assert.Null(wrongMode);
            Assert.DoesNotContain(Convert.ToBase64String(credential), File.ReadAllText(path));
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(credential);
        }
    }

    [Fact]
    public void ClientRevocationInvalidatesCredentialAndAdvancesGeneration()
    {
        var registry = new NamedClientRegistry(Path.Combine(_directory, "clients.json"));
        using var enrollment = registry.Enroll(
            "release-helper",
            "Release helper",
            [Project()],
            [SecretDeliveryMode.ExecInject]);
        var credential = enrollment.CopyCredential();
        try
        {
            Assert.NotNull(registry.Authenticate(
                "release-helper", credential, "joydex", SecretDeliveryMode.ExecInject));

            registry.Revoke("release-helper");

            Assert.Null(registry.Authenticate(
                "release-helper", credential, "joydex", SecretDeliveryMode.ExecInject));
            var metadata = Assert.Single(registry.List());
            Assert.True(metadata.Revoked);
            Assert.Equal(2, metadata.Generation);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(credential);
        }
    }

    [Fact]
    public void ClientRevocationFromAnotherStoreInstanceIsObservedImmediately()
    {
        var path = Path.Combine(_directory, "clients-shared.json");
        var brokerRegistry = new NamedClientRegistry(path);
        using var enrollment = brokerRegistry.Enroll(
            "release-helper",
            "Release helper",
            [Project()],
            [SecretDeliveryMode.ExecInject]);
        var credential = enrollment.CopyCredential();
        try
        {
            new NamedClientRegistry(path).Revoke("release-helper");

            Assert.Null(brokerRegistry.Authenticate(
                "release-helper", credential, "joydex", SecretDeliveryMode.ExecInject));
            Assert.True(Assert.Single(brokerRegistry.List()).Revoked);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credential);
        }
    }

    [Fact]
    public void RevokedClientIdCanBeEnrolledAgainWithNewAuthority()
    {
        var registry = new NamedClientRegistry(Path.Combine(_directory, "clients-reenroll.json"));
        byte[] oldCredential;
        Guid oldRegistration;
        using (var first = registry.Enroll(
                   "release-helper",
                   "Release helper",
                   [Project()],
                   [SecretDeliveryMode.ExecInject]))
        {
            oldCredential = first.CopyCredential();
            oldRegistration = first.Principal.RegistrationId;
        }
        registry.Revoke("release-helper");

        byte[] newCredential;
        using (var second = registry.Enroll(
                   "release-helper",
                   "Release helper",
                   [Project()],
                   [SecretDeliveryMode.ExecInject]))
        {
            newCredential = second.CopyCredential();
            Assert.NotEqual(oldRegistration, second.Principal.RegistrationId);
            Assert.Equal(3, second.Principal.Generation);
        }
        try
        {
            Assert.Null(registry.Authenticate(
                "release-helper", oldCredential, "joydex", SecretDeliveryMode.ExecInject));
            Assert.NotNull(registry.Authenticate(
                "release-helper", newCredential, "joydex", SecretDeliveryMode.ExecInject));
            Assert.False(Assert.Single(registry.List()).Revoked);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(oldCredential);
            CryptographicOperations.ZeroMemory(newCredential);
        }
    }

    [Fact]
    public void ReplacingAnEnrolledProjectDirectoryInvalidatesAuthentication()
    {
        var projectRoot = Path.Combine(_directory, "enrolled-project");
        var replacement = Path.Combine(_directory, "replacement-project");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(replacement);
        var project = SecretsProjectIdentityFactory.Create("replace-test", projectRoot);
        var registry = new NamedClientRegistry(Path.Combine(_directory, "clients-project-identity.json"));
        using var enrollment = registry.Enroll(
            "identity-helper",
            "Identity helper",
            [project],
            [SecretDeliveryMode.ExecInject]);
        var credential = enrollment.CopyCredential();
        try
        {
            Assert.NotNull(registry.Authenticate(
                "identity-helper", credential, "replace-test", SecretDeliveryMode.ExecInject));

            Directory.Delete(projectRoot);
            Directory.Move(replacement, projectRoot);

            Assert.Null(registry.Authenticate(
                "identity-helper", credential, "replace-test", SecretDeliveryMode.ExecInject));
            Assert.Empty(registry.ListProjects());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credential);
        }
    }

    [Fact]
    public void EnrollmentRejectsAProjectNameThatRequestsCannotUse()
    {
        var invalid = Project() with { ProjectReference = "My Project" };
        var registry = new NamedClientRegistry(Path.Combine(_directory, "invalid-project-clients.json"));

        Assert.Throws<ArgumentException>(() => registry.Enroll(
            "invalid-project-helper",
            "Invalid project helper",
            [invalid],
            [SecretDeliveryMode.ExecInject]));
    }

    [Fact]
    public void EnrollmentRejectsAnUndefinedDeliveryMode()
    {
        var registry = new NamedClientRegistry(Path.Combine(_directory, "invalid-mode-clients.json"));

        Assert.Throws<ArgumentException>(() => registry.Enroll(
            "invalid-mode-helper",
            "Invalid mode helper",
            [Project()],
            [(SecretDeliveryMode)99]));
    }

    [Fact]
    public void HelperCredentialRoundTripsThroughDpapiAndNeverPersistsClearBytes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(_directory, "credentials");
        var store = new NamedClientCredentialStore(directory);
        var credential = RandomNumberGenerator.GetBytes(32);
        try
        {
            store.Save("release-helper", credential);
            var loaded = store.Load("release-helper");
            try
            {
                Assert.Equal(credential, loaded);
                Assert.NotEqual(credential, File.ReadAllBytes(
                    Path.Combine(directory, "release-helper.cred")));
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(loaded);
            }
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(credential);
        }
    }

    [Fact]
    public void MissingHelperCredentialReportsHowFirstUseCreatesItInsteadOfAnAclFailure()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new NamedClientCredentialStore(Path.Combine(_directory, "missing-credentials"));

        var exception = Assert.Throws<FileNotFoundException>(() => store.Load("release-helper"));

        Assert.Contains("Run a fresh joydex-secrets exec request", exception.Message, StringComparison.Ordinal);
        Assert.EndsWith("release-helper.cred", exception.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalRequesterFirstUseIsAutomaticAndStable()
    {
        if (!OperatingSystem.IsWindows()) return;

        var first = LocalSecretsRequester.EnsureReady(
            _directory,
            "codex",
            "Codex",
            "joydex",
            _directory);
        var second = LocalSecretsRequester.EnsureReady(
            _directory,
            "codex",
            "Codex",
            "joydex",
            _directory);

        Assert.Equal(first, second);
        var requester = Assert.Single(new NamedClientRegistry(
            SecretsPaths.GetClientsPath(_directory)).List());
        Assert.False(requester.Revoked);
        Assert.Equal(["joydex"], requester.ProjectReferences);
    }

    [Fact]
    public void CorruptPolicyBlocksLoadingAndRulesSurviveReload()
    {
        var path = Path.Combine(_directory, "policy.json");
        var store = new SecretsPolicyStore(path);
        var rule = SecretsPolicyRule.Create(
            SecretsConsentChoice.YesAlways,
            RememberedGrantScopeKind.ExactOperation,
            Scope(),
            DateTimeOffset.UnixEpoch,
            "native-ui");

        var epoch = store.Add(rule);
        var reloaded = new SecretsPolicyStore(path).Read(DateTimeOffset.UnixEpoch);

        Assert.Equal(epoch, reloaded.Epoch);
        var reloadedRule = Assert.Single(reloaded.Rules);
        Assert.Equal(rule with { Aliases = reloadedRule.Aliases }, reloadedRule);
        Assert.Equal(rule.Aliases, reloadedRule.Aliases);
        File.WriteAllText(path, "{ malformed");
        Assert.Throws<InvalidDataException>(() => new SecretsPolicyStore(path));
    }

    [Fact]
    public void PolicyRejectsUndefinedScopeKindsAndOperationDeliveryModes()
    {
        var store = new SecretsPolicyStore(Path.Combine(_directory, "invalid-policy.json"));
        var invalidRule = SecretsPolicyRule.Create(
            SecretsConsentChoice.YesAlways,
            RememberedGrantScopeKind.ExactOperation,
            Scope(),
            DateTimeOffset.UnixEpoch,
            "native-ui") with
        {
            ScopeKind = (RememberedGrantScopeKind)99,
        };
        var invalidOperation = new SecretsOperationIdentity(
            (SecretDeliveryMode)99,
            new string('1', 64),
            1);

        Assert.Throws<InvalidDataException>(() => store.Add(invalidRule));
        Assert.Throws<ArgumentException>(() => SecretsScopeFactory.Create(
            Scope().Principal,
            Project(),
            [new("deploy-token", "env", "secret-1", 1)],
            invalidOperation));
    }

    [Fact]
    public void RememberedDecisionMetadataSurvivesActivityCleanup()
    {
        var policyPath = Path.Combine(_directory, "metadata-policy.json");
        var auditPath = Path.Combine(_directory, "metadata-audit.jsonl");
        var policy = new SecretsPolicyStore(policyPath);
        var rule = SecretsPolicyRule.Create(
            SecretsConsentChoice.Never,
            RememberedGrantScopeKind.Client,
            Scope(),
            DateTimeOffset.UnixEpoch,
            "native-ui");
        policy.Add(rule);
        var audit = new SecretsAuditJournal(auditPath);
        audit.Append(Record(Guid.NewGuid(), SecretsAuditEventKind.ConsentDecision));

        audit.ClearActivity();
        var reloaded = Assert.Single(new SecretsPolicyStore(policyPath)
            .Read(DateTimeOffset.UnixEpoch)
            .Rules);

        Assert.Empty(audit.ReadAll());
        Assert.Equal("release-helper", reloaded.ClientReference);
        Assert.Equal("Release helper", reloaded.ClientLabel);
        Assert.Equal("joydex", reloaded.ProjectReference);
        Assert.Equal(["deploy-token"], reloaded.Aliases);
    }

    [Fact]
    public void ReadingPolicyPrunesExpiredRulesAndAdvancesTheEpoch()
    {
        var path = Path.Combine(_directory, "expiring-policy.json");
        var store = new SecretsPolicyStore(path);
        var issuedAt = DateTimeOffset.Parse("2026-09-13T00:00:00Z");
        var rule = SecretsPolicyRule.Create(
            SecretsConsentChoice.Yes24Hours,
            RememberedGrantScopeKind.ExactOperation,
            Scope(),
            issuedAt,
            "native-ui");
        var addedEpoch = store.Add(rule);

        var active = store.Read(issuedAt.AddHours(23));
        var expired = store.Read(issuedAt.AddHours(25));

        Assert.Single(active.Rules);
        Assert.Empty(expired.Rules);
        Assert.Equal(addedEpoch + 1, expired.Epoch);
        Assert.Empty(new SecretsPolicyStore(path).Read(issuedAt.AddHours(25)).Rules);
    }

    [Fact]
    public void RevocationEpochInvalidatesAnUnredeemedReservation()
    {
        var policy = new SecretsPolicyStore(Path.Combine(_directory, "policy.json"));
        var scope = Scope();
        var rule = SecretsPolicyRule.Create(
            SecretsConsentChoice.YesAlways,
            RememberedGrantScopeKind.ExactOperation,
            scope,
            DateTimeOffset.UnixEpoch,
            "native-ui");
        var epoch = policy.Add(rule);
        var reservations = new SecretsReservationStore();
        var handle = reservations.Reserve(
            scope.Principal,
            "request-1",
            scope,
            epoch,
            DateTimeOffset.UnixEpoch.AddMinutes(5));

        Assert.True(policy.Revoke(rule.RuleId));
        var result = reservations.Consume(
            handle,
            scope.Principal,
            "request-1",
            scope,
            policy.Read(DateTimeOffset.UnixEpoch).Epoch,
            DateTimeOffset.UnixEpoch.AddMinutes(1));

        Assert.Equal(SecretsReservationConsumeResult.PolicyChanged, result);
        Assert.Equal(
            SecretsReservationConsumeResult.Missing,
            reservations.Consume(
                handle,
                scope.Principal,
                "request-1",
                scope,
                epoch,
                DateTimeOffset.UnixEpoch.AddMinutes(1)));
    }

    [Fact]
    public void PolicyRevocationFromAnotherStoreInstanceIsObservedImmediately()
    {
        var path = Path.Combine(_directory, "policy-shared.json");
        var brokerPolicy = new SecretsPolicyStore(path);
        var rule = SecretsPolicyRule.Create(
            SecretsConsentChoice.YesAlways,
            RememberedGrantScopeKind.ExactOperation,
            Scope(),
            DateTimeOffset.UnixEpoch,
            "native-ui");
        var initialEpoch = brokerPolicy.Add(rule);

        Assert.True(new SecretsPolicyStore(path).Revoke(rule.RuleId));
        var refreshed = brokerPolicy.Read(DateTimeOffset.UnixEpoch);

        Assert.Empty(refreshed.Rules);
        Assert.Equal(initialEpoch + 1, refreshed.Epoch);
    }

    [Fact]
    public void ReservationIsBoundToPrincipalRequestAndScopeAndConsumedOnFailure()
    {
        var scope = Scope();
        var reservations = new SecretsReservationStore();
        var handle = reservations.Reserve(
            scope.Principal,
            "request-1",
            scope,
            4,
            DateTimeOffset.UnixEpoch.AddMinutes(5));
        var wrong = scope.Principal with
        {
            RegistrationId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        };

        Assert.Equal(
            SecretsReservationConsumeResult.WrongPrincipal,
            reservations.Consume(
                handle,
                wrong,
                "request-1",
                scope,
                4,
                DateTimeOffset.UnixEpoch.AddMinutes(1)));
        Assert.Equal(
            SecretsReservationConsumeResult.Missing,
            reservations.Consume(
                handle,
                scope.Principal,
                "request-1",
                scope,
                4,
                DateTimeOffset.UnixEpoch.AddMinutes(1)));
    }

    [Fact]
    public async Task ConcurrentReservationRedemptionAllowsExactlyOneConsumer()
    {
        var scope = Scope();
        var reservations = new SecretsReservationStore();
        var handle = reservations.Reserve(
            scope.Principal,
            "request-1",
            scope,
            4,
            DateTimeOffset.UnixEpoch.AddMinutes(5));
        using var start = new ManualResetEventSlim();
        var attempts = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return reservations.Consume(
                handle,
                scope.Principal,
                "request-1",
                scope,
                4,
                DateTimeOffset.UnixEpoch.AddMinutes(1));
        })).ToArray();

        start.Set();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result == SecretsReservationConsumeResult.Consumed);
        Assert.Equal(15, results.Count(result => result == SecretsReservationConsumeResult.Missing));
    }

    [Fact]
    public void AuditPairsAreSanitizedAndCommittedLaunchWithoutOutcomeIsUnconfirmed()
    {
        var path = Path.Combine(_directory, "audit.jsonl");
        var journal = new SecretsAuditJournal(path);
        var attempt = Guid.NewGuid();
        journal.Append(Record(attempt, SecretsAuditEventKind.RequestReceived));
        journal.Append(Record(attempt, SecretsAuditEventKind.RequestOutcome) with
        {
            OperationDigest = new string('1', 64),
            Outcome = "allowed",
        });
        journal.Append(Record(attempt, SecretsAuditEventKind.LaunchCommitted) with
        {
            OperationDigest = new string('1', 64),
        });

        var records = new SecretsAuditJournal(path).ReadAll();

        Assert.Equal(3, records.Count);
        Assert.Equal([attempt], journal.FindUnconfirmedLaunches());
        var persisted = File.ReadAllText(path);
        Assert.DoesNotContain("canary-secret-value", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("reason text", persisted, StringComparison.Ordinal);

        journal.Append(Record(attempt, SecretsAuditEventKind.LaunchCompleted));
        Assert.Empty(journal.FindUnconfirmedLaunches());
    }

    [Fact]
    public void ClearingActivityPreservesDurableLaunchState()
    {
        var path = Path.Combine(_directory, "clearable-audit.jsonl");
        var journal = new SecretsAuditJournal(path);
        var attempt = Guid.NewGuid();
        var registration = Guid.NewGuid();
        journal.Append(Record(attempt, SecretsAuditEventKind.LaunchCommitted) with
        {
            OperationDigest = new string('2', 64),
            ClientRegistrationId = registration,
            ClientGeneration = 3,
        });

        journal.ClearActivity();

        Assert.Empty(journal.ReadAll());
        var durable = Assert.Single(journal.FindCommittedRequests());
        Assert.Equal(attempt, durable.AttemptId);
        Assert.True(durable.OutcomeUnconfirmed);
        journal.Append(Record(attempt, SecretsAuditEventKind.LaunchCompleted));
        Assert.False(Assert.Single(journal.FindCommittedRequests()).OutcomeUnconfirmed);
    }

    [Fact]
    public void AutoAllowModePersistsAndReturnsToNormalAfterTwentyFourHours()
    {
        var path = Path.Combine(_directory, "operating-mode.json");
        var store = new SecretsOperatingModeStore(path);
        var now = DateTimeOffset.Parse("2026-09-13T10:15:00Z");

        var enabled = store.AutoAllowFor24Hours(now);
        var reloaded = new SecretsOperatingModeStore(path).Read(now.AddHours(23));
        var expired = new SecretsOperatingModeStore(path).Read(now.AddHours(24));

        Assert.Equal(SecretsOperatingMode.AutoAllow24Hours, enabled.Mode);
        Assert.Equal(now.AddHours(24), enabled.ExpiresAt);
        Assert.Equal(enabled, reloaded);
        Assert.Equal(SecretsOperatingMode.Ask, expired.Mode);
        Assert.Null(expired.ExpiresAt);
        Assert.True(expired.Epoch > enabled.Epoch);
    }

    [Fact]
    public void DenyAllPersistsUntilTheUserChangesIt()
    {
        var path = Path.Combine(_directory, "deny-operating-mode.json");
        var store = new SecretsOperatingModeStore(path);
        var now = DateTimeOffset.Parse("2026-09-13T10:15:00Z");

        var denied = store.DenyAll(now);
        var reloaded = new SecretsOperatingModeStore(path).Read(now.AddDays(30));
        var restored = store.Ask(now.AddDays(30));

        Assert.Equal(denied, reloaded);
        Assert.Equal(SecretsOperatingMode.Ask, restored.Mode);
        Assert.True(restored.Epoch > denied.Epoch);
    }

    [Fact]
    public void UsageMetricsKeepLifetimeCountsWhenActivityIsCleared()
    {
        var path = Path.Combine(_directory, "metrics-audit.jsonl");
        var journal = new SecretsAuditJournal(path);
        var now = DateTimeOffset.Parse("2026-09-13T10:15:00Z");
        var oldAttempt = Guid.NewGuid();
        var recentAttempt = Guid.NewGuid();
        journal.Append(Record(oldAttempt, SecretsAuditEventKind.RequestReceived) with
        {
            Timestamp = now.AddHours(-25),
        });
        journal.Append(Record(oldAttempt, SecretsAuditEventKind.RequestOutcome) with
        {
            Timestamp = now.AddHours(-25),
            Outcome = "allowed",
        });
        journal.Append(Record(oldAttempt, SecretsAuditEventKind.LaunchStarted) with
        {
            Timestamp = now.AddHours(-25),
        });
        journal.Append(Record(recentAttempt, SecretsAuditEventKind.RequestReceived) with
        {
            Timestamp = now.AddMinutes(-10),
        });
        journal.Append(Record(recentAttempt, SecretsAuditEventKind.ConsentDecision) with
        {
            Timestamp = now.AddMinutes(-10),
            Outcome = "denied",
        });

        var metricsPath = Path.Combine(_directory, "metrics.json");
        var beforeClear = new SecretsMetricsStore(metricsPath).Read(now);
        journal.ClearActivity();
        var afterClear = new SecretsMetricsStore(metricsPath).Read(now);

        Assert.Equal(new SecretsMetricsSnapshot(2, 1, 1, 0, 1, 0, 1, 1), beforeClear);
        Assert.Equal(beforeClear, afterClear);
        Assert.Empty(journal.ReadAll());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static SecretsProjectIdentity Project() => new(
        "joydex",
        "joydex-project",
        Path.GetFullPath("D:\\Projects\\Joydex"),
        "worktree-main",
        Path.GetFullPath("D:\\Projects\\Joydex\\worktree"),
        1);

    private static SecretsAuthorizationScope Scope() => SecretsScopeFactory.Create(
        new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "release-helper",
            1,
            "Release helper"),
        Project(),
        [new("deploy-token", "env", "secret-1", 1)],
        new(SecretDeliveryMode.ExecInject, new string('1', 64), 1));

    private static SecretsAuditRecord Record(Guid attempt, SecretsAuditEventKind kind) => new(
        attempt,
        kind,
        DateTimeOffset.UnixEpoch,
        "request-1",
        "release-helper",
        "joydex",
        ["deploy-token"]);
}
