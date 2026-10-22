using System.Security.Cryptography;
using Joydex.Secrets;

namespace Joydex.Tests;

/// <summary>Exercises unavailable desktops with real broker policy, reservations and harmless child processes.</summary>
public sealed class SecretsConsentAvailabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "joydex-consent-tests", Guid.NewGuid().ToString("N"));

    public static IEnumerable<object[]> ApprovedCases()
    {
        foreach (var state in Enum.GetValues<SecretsConsentAvailability>().Where(state => state != SecretsConsentAvailability.Available))
        foreach (var choice in new[] { SecretsConsentChoice.Yes, SecretsConsentChoice.YesAlways, SecretsConsentChoice.Yes24Hours })
        foreach (var exact in choice == SecretsConsentChoice.Yes ? new[] { true } : new[] { true, false })
            yield return [state, choice, exact];
    }

    [Theory]
    [MemberData(nameof(ApprovedCases))]
    public async Task ValidApprovalsExecuteWithoutAnInteractiveDesktop(SecretsConsentAvailability state, SecretsConsentChoice choice, bool exact)
    {
        using var harness = new Harness(_root);
        var approved = harness.Approve(choice, exact);
        harness.Runtime.SetConsentAvailability(state);
        if (choice != SecretsConsentChoice.Yes)
            approved = harness.Submit("preapproved-while-unavailable");

        Assert.Equal(SecretsRequestStatus.Allowed, approved.Status);
        var result = await harness.Run(approved);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(harness.Runtime.PendingRequests());
        Assert.DoesNotContain(harness.Diagnostics.Read().Records, record => record.Kind == "failure");
    }

    [Theory]
    [InlineData(SecretsConsentAvailability.Locked)]
    [InlineData(SecretsConsentAvailability.Disconnected)]
    [InlineData(SecretsConsentAvailability.LoggedOff)]
    [InlineData(SecretsConsentAvailability.Unknown)]
    public void UnapprovedRequestsFailPromptlyAndNeverRevive(SecretsConsentAvailability state)
    {
        using var harness = new Harness(_root);
        harness.Runtime.SetConsentAvailability(state);
        var blocked = harness.Submit("no-grant");
        Assert.Equal(SecretsRequestStatus.ConsentUnavailable, blocked.Status);
        Assert.Equal("consent_unavailable", blocked.Failure!.Code);
        Assert.Null(blocked.Reservation);
        Assert.Empty(harness.Runtime.PendingRequests());
        Assert.Empty(harness.Policy.Read(DateTimeOffset.UtcNow).Rules);
        Assert.DoesNotContain(harness.Audit.ReadAll(), record => record.Kind == SecretsAuditEventKind.LaunchCommitted);

        harness.Runtime.SetConsentAvailability(SecretsConsentAvailability.Available);
        Assert.Equal(SecretsRequestStatus.ConsentUnavailable, harness.Submit("no-grant").Status);
        Assert.Equal(SecretsRequestStatus.Pending, harness.Submit("new-request").Status);
    }

    [Fact]
    public void PendingConsentEndsOnLockAndRejectsTheOldChallenge()
    {
        using var harness = new Harness(_root);
        var pending = harness.Submit("pending");
        var shown = Assert.Single(harness.Runtime.PendingRequests());
        harness.Runtime.SetConsentAvailability(SecretsConsentAvailability.Locked);
        harness.Runtime.SetConsentAvailability(SecretsConsentAvailability.Available);
        Assert.Empty(harness.Runtime.PendingRequests());
        Assert.Equal(SecretsRequestStatus.ConsentUnavailable, harness.Runtime.GetStatus("codex", harness.Credential, "joydex", pending.RequestId).Status);
        Assert.Equal(SecretsRequestStatus.Expired, harness.Runtime.Decide(shown.AttemptId, shown.DisplayChallenge, SecretsConsentChoice.Yes, true).Status);
        Assert.Empty(harness.Policy.Read(DateTimeOffset.UtcNow).Rules);
    }

    [Fact]
    public async Task AutoAllowWorksWhileDisconnectedAndDenyAllStillWins()
    {
        using var harness = new Harness(_root);
        var modes = new SecretsOperatingModeStore(SecretsPaths.GetOperatingModePath(harness.Directory));
        modes.AutoAllowFor24Hours(DateTimeOffset.UtcNow);
        harness.Runtime.SetConsentAvailability(SecretsConsentAvailability.Disconnected);
        var approved = harness.Submit("auto-allowed");
        Assert.Equal(SecretsRequestStatus.Allowed, approved.Status);
        Assert.Equal(0, (await harness.Run(approved)).ExitCode);
        modes.DenyAll(DateTimeOffset.UtcNow);
        Assert.Equal(SecretsRequestStatus.Denied, harness.Submit("deny-all").Status);
    }

    [Fact]
    public void RememberedDenialsAndChangedOperationsDoNotBecomeAllowsWhileLocked()
    {
        using var harness = new Harness(_root);
        harness.Approve(SecretsConsentChoice.Never, false);
        harness.Runtime.SetConsentAvailability(SecretsConsentAvailability.Locked);
        Assert.Equal(SecretsRequestStatus.Denied, harness.Submit("remembered-deny").Status);

        using var other = new Harness(_root);
        other.Approve(SecretsConsentChoice.YesAlways, true);
        other.Runtime.SetConsentAvailability(SecretsConsentAvailability.Locked);
        Assert.Equal(SecretsRequestStatus.ConsentUnavailable, other.Submit("changed-operation", "exit /b 7").Status);
    }

    [Fact]
    public async Task ConfigurationFailurePreservesTheReasonAndEpochsWithoutSecretValues()
    {
        using var harness = new Harness(_root);
        var approved = harness.Approve(SecretsConsentChoice.Yes, true);
        harness.Configuration.UpsertEnvSource(harness.SourceId, "Changed source", harness.EnvPath,
            new Dictionary<string, string> { ["TOKEN"] = "TOKEN" });
        var exception = await Assert.ThrowsAsync<SecretsRequestException>(() => harness.Run(approved));
        Assert.Equal("configuration_changed", exception.Failure.Code);
        Assert.Equal("not_started", exception.Failure.LaunchState);
        Assert.NotEqual(exception.Failure.ExpectedConfigurationEpoch, exception.Failure.CurrentConfigurationEpoch);
        var failure = Assert.Single(harness.Diagnostics.Read().Records, record => record.Kind == "failure");
        Assert.Equal(approved.RequestId, failure.RequestId);
        Assert.Equal(approved.AttemptId, failure.AttemptId);
        Assert.DoesNotContain("synthetic-consent-secret", File.ReadAllText(SecretsPaths.GetDiagnosticsPath(harness.Directory)));
    }

    [Fact]
    public async Task BrokenDiagnosticStorageDoesNotBlockKnownApproval()
    {
        using var harness = new Harness(_root);
        var approved = harness.Approve(SecretsConsentChoice.Yes, true);
        var path = SecretsPaths.GetDiagnosticsPath(harness.Directory);
        File.Delete(path);
        Directory.CreateDirectory(path);
        harness.Runtime.SetConsentAvailability(SecretsConsentAvailability.Locked);
        Assert.Equal(0, (await harness.Run(approved)).ExitCode);
    }

    [Fact]
    public async Task RememberedApprovalSurvivesBrokerRestartIntoALockedSession()
    {
        using var harness = new Harness(_root);
        harness.Approve(SecretsConsentChoice.YesAlways, false);
        harness.Runtime = new SecretsBrokerRuntime(harness.Directory, SecretsConsentAvailability.Locked);
        var approved = harness.Submit("after-restart");
        Assert.Equal(SecretsRequestStatus.Allowed, approved.Status);
        Assert.Equal(0, (await harness.Run(approved)).ExitCode);
    }

    [Fact]
    public async Task UnexpiredOneUseApprovalDoesNotSurviveBrokerRestart()
    {
        using var harness = new Harness(_root);
        var approved = harness.Approve(SecretsConsentChoice.Yes, true);
        Assert.True(approved.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1));
        harness.Runtime = new SecretsBrokerRuntime(harness.Directory, SecretsConsentAvailability.Locked);

        var rejected = await Assert.ThrowsAsync<SecretsRequestException>(() => harness.Run(approved));

        Assert.Equal("request_unavailable", rejected.Failure.Code);
        Assert.Equal("unknown", rejected.Failure.LaunchState);
        Assert.True(approved.ExpiresAt > DateTimeOffset.UtcNow);
        Assert.DoesNotContain(harness.Audit.ReadAll(), record => record.Kind == SecretsAuditEventKind.LaunchCommitted);
        Assert.Equal(SecretsRequestStatus.ConsentUnavailable, harness.Submit("fresh-after-restart").Status);
    }

    [Theory]
    [InlineData(SecretsConsentAvailability.Locked)]
    [InlineData(SecretsConsentAvailability.Disconnected)]
    [InlineData(SecretsConsentAvailability.Unknown)]
    public async Task DetachedPolicyReviewReturnsWithoutAuthorityToRunTheOldCommand(SecretsConsentAvailability state)
    {
        using var harness = new Harness(_root);
        var pending = harness.Submit("policy-review");
        var detached = harness.Runtime.Detach("codex", harness.Credential, "joydex", pending.RequestId,
            SecretsDetachReason.LeavePending);
        var shown = Assert.Single(harness.Runtime.PendingRequests());
        Assert.Equal(SecretsRequestStatus.AgentDetached, detached.Status);
        Assert.Null(detached.Reservation);
        harness.Runtime.SetConsentAvailability(state);
        Assert.Empty(harness.Runtime.PendingRequests());
        Assert.Equal(SecretsRequestStatus.AgentDetached,
            harness.Runtime.GetStatus("codex", harness.Credential, "joydex", pending.RequestId).Status);
        harness.Runtime.SetConsentAvailability(SecretsConsentAvailability.Available);
        var returned = Assert.Single(harness.Runtime.PendingRequests());
        Assert.Equal(shown.AttemptId, returned.AttemptId);
        Assert.Equal(SecretsDetachReason.LeavePending, returned.DetachedReason);
        Assert.Equal(SecretsRequestStatus.AgentDetached,
            harness.Runtime.Decide(returned.AttemptId, returned.DisplayChallenge, SecretsConsentChoice.Yes, true).Status);
        await Assert.ThrowsAsync<SecretsRequestException>(() => harness.Runtime.RunAsync("codex", harness.Credential,
            "joydex", pending.RequestId, "not-a-reservation", new Dictionary<string, string>(), CancellationToken.None));
        Assert.Equal(SecretsRequestStatus.Denied,
            harness.Runtime.Decide(returned.AttemptId, returned.DisplayChallenge, SecretsConsentChoice.YesAlways, false).Status);
        Assert.Equal(SecretsRequestStatus.Allowed, harness.Submit("future-policy-use").Status);
        Assert.DoesNotContain(harness.Audit.ReadAll(), record => record.AttemptId == pending.AttemptId
            && record.Kind == SecretsAuditEventKind.LaunchCommitted);
    }

    [Fact]
    public async Task RejectedReplayPreservesTheCompletedExecutionOutcome()
    {
        using var harness = new Harness(_root);
        var approved = harness.Approve(SecretsConsentChoice.Yes, true);
        Assert.Equal(0, (await harness.Run(approved)).ExitCode);
        var replay = await Assert.ThrowsAsync<SecretsRequestException>(() => harness.Run(approved));
        Assert.Equal("finished", replay.Failure.LaunchState);
        Assert.Equal(SecretsRequestStatus.Completed, harness.Runtime.GetStatus("codex", harness.Credential, "joydex", approved.RequestId).Status);
        Assert.Single(harness.Audit.ReadAll(), record => record.Kind == SecretsAuditEventKind.LaunchStarted);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class Harness : IDisposable
    {
        public Harness(string root)
        {
            Directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            EnvPath = Path.Combine(Directory, ".env");
            File.WriteAllText(EnvPath, "TOKEN=synthetic-consent-secret\n");
            Configuration = new SecretsConfigurationStore(SecretsPaths.GetConfigurationPath(Directory));
            SourceId = Configuration.UpsertEnvSource(null, "Test", EnvPath, new Dictionary<string, string> { ["TOKEN"] = "TOKEN" }).Sources.Single().SourceId;
            LocalSecretsRequester.EnsureReady(Directory, "codex", "Codex", "joydex", Directory);
            Credential = new NamedClientCredentialStore(SecretsPaths.GetCredentialDirectory(Directory)).Load("codex");
            Runtime = new SecretsBrokerRuntime(Directory);
            Policy = new SecretsPolicyStore(SecretsPaths.GetPolicyPath(Directory));
            Audit = new SecretsAuditJournal(SecretsPaths.GetAuditPath(Directory));
            Diagnostics = new SecretsDiagnostics(Directory);
        }

        public string Directory { get; }
        public string EnvPath { get; }
        public Guid SourceId { get; }
        public byte[] Credential { get; }
        public SecretsConfigurationStore Configuration { get; }
        public SecretsBrokerRuntime Runtime { get; set; }
        public SecretsPolicyStore Policy { get; }
        public SecretsAuditJournal Audit { get; }
        public SecretsDiagnostics Diagnostics { get; }

        public SecretsRequestResponse Submit(string id, string? command = null) => Runtime.Submit(new(id, "codex", "joydex", "Test",
            Operation: new(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                ["/d", "/c", command ?? "if defined TOKEN (exit /b 0) else (exit /b 33)"], Directory,
                new Dictionary<string, string> { ["TOKEN"] = "TOKEN" }, [], SecretOutputDisclosure.None)), Credential);

        public SecretsRequestResponse Approve(SecretsConsentChoice choice, bool exact)
        {
            var pending = Submit("initial");
            var shown = Assert.Single(Runtime.PendingRequests());
            return Runtime.Decide(pending.AttemptId, shown.DisplayChallenge, choice, exact);
        }

        public Task<SecretsExecResult> Run(SecretsRequestResponse response) => Runtime.RunAsync("codex", Credential, "joydex",
            response.RequestId, response.Reservation!, new Dictionary<string, string>(), CancellationToken.None);

        public void Dispose() => CryptographicOperations.ZeroMemory(Credential);
    }
}
