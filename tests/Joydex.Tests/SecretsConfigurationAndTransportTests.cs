using System.Security.Cryptography;
using Joydex.Secrets;

namespace Joydex.Tests;

public sealed class SecretsConfigurationAndTransportTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "joydex-secrets-runtime-" + Guid.NewGuid().ToString("N"));

    public SecretsConfigurationAndTransportTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void ConfigurationPreservesUnchangedAliasIdentityAndVersionsBehaviorChanges()
    {
        var envPath = Path.Combine(_directory, ".env");
        File.WriteAllText(envPath, "TOKEN=synthetic-secret\n");
        var store = new SecretsConfigurationStore(Path.Combine(_directory, "configuration.json"));

        var created = store.UpsertEnvSource(
            null,
            "Local secrets",
            envPath,
            new Dictionary<string, string> { ["api-token"] = "TOKEN" });
        var first = Assert.Single(created.Sources);
        var firstAlias = Assert.Single(first.Aliases);
        var renamed = store.UpsertEnvSource(
            first.SourceId,
            "Project secrets",
            envPath,
            new Dictionary<string, string> { ["api-token"] = "TOKEN" });
        var renamedAlias = Assert.Single(Assert.Single(renamed.Sources).Aliases);
        var remapped = store.UpsertEnvSource(
            first.SourceId,
            "Project secrets",
            envPath,
            new Dictionary<string, string> { ["api-token"] = "OTHER_TOKEN" });
        var remappedAlias = Assert.Single(Assert.Single(remapped.Sources).Aliases);

        Assert.Equal(firstAlias.StableSecretId, renamedAlias.StableSecretId);
        Assert.Equal(firstAlias.MappingGeneration, renamedAlias.MappingGeneration);
        Assert.NotEqual(firstAlias.StableSecretId, remappedAlias.StableSecretId);
        Assert.Equal(firstAlias.MappingGeneration + 1, remappedAlias.MappingGeneration);
        Assert.True(created.Epoch < renamed.Epoch && renamed.Epoch < remapped.Epoch);
    }

    [Fact]
    public void ConfigurationKeepsMultipleSourcesWithUnambiguousPublicNames()
    {
        var firstPath = Path.Combine(_directory, "first.env");
        var secondPath = Path.Combine(_directory, "second.env");
        File.WriteAllText(firstPath, "FIRST_TOKEN=synthetic-first\n");
        File.WriteAllText(secondPath, "SECOND_TOKEN=synthetic-second\n");
        var store = new SecretsConfigurationStore(Path.Combine(_directory, "multiple-sources.json"));
        var first = store.UpsertEnvSource(
            null,
            "First source",
            firstPath,
            new Dictionary<string, string> { ["FIRST_TOKEN"] = "FIRST_TOKEN" });

        var second = store.UpsertEnvSource(
            null,
            "Second source",
            secondPath,
            new Dictionary<string, string> { ["SECOND_TOKEN"] = "SECOND_TOKEN" });

        Assert.Equal(2, second.Sources.Count);
        Assert.Equal(first.Sources[0].SourceId, second.Sources[0].SourceId);
        Assert.Equal(first.Sources[0].Generation, second.Sources[0].Generation);
        Assert.Equal(
            first.Sources[0].Aliases.Select(alias => (alias.Alias, alias.Key, alias.StableSecretId)),
            second.Sources[0].Aliases.Select(alias => (alias.Alias, alias.Key, alias.StableSecretId)));
        var duplicate = Assert.Throws<InvalidOperationException>(() => store.UpsertEnvSource(
            null,
            "Duplicate variable source",
            secondPath,
            new Dictionary<string, string> { ["FIRST_TOKEN"] = "SECOND_TOKEN" }));
        Assert.Contains("already provided", duplicate.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, store.Read().Sources.Count);
    }

    [Fact]
    public void SavedRecipeOnlyAcceptsDeclaredParameterValuesAndCarriesItsGeneration()
    {
        var store = new SecretsConfigurationStore(Path.Combine(_directory, "configuration.json"));
        var recipe = Assert.Single(store.UpsertRecipe(new SecretsRecipeDraft(
            "publish-preview",
            "Publish preview",
            "joydex",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            ["/d", "/c", "echo {environment}"],
            _directory,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["environment"] = ["dev", "preview"],
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["API_TOKEN"] = "api-token",
            },
            [],
            SecretOutputDisclosure.None)).Recipes);

        var resolved = recipe.Resolve(new Dictionary<string, string> { ["environment"] = "preview" });

        Assert.Equal("echo preview", resolved.Arguments[2]);
        Assert.Equal(recipe.Generation, resolved.OperationGeneration);
        Assert.Throws<ArgumentException>(() => recipe.Resolve(
            new Dictionary<string, string> { ["environment"] = "production" }));
    }

    [Fact]
    public void RecipeRejectsEnvironmentNamesThatCollideOnWindows()
    {
        var store = new SecretsConfigurationStore(Path.Combine(_directory, "configuration.json"));
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["API_TOKEN"] = "first-token",
            ["api_token"] = "second-token",
        };

        Assert.Throws<ArgumentException>(() => store.UpsertRecipe(new SecretsRecipeDraft(
            "case-collision",
            "Case collision",
            "joydex",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            ["/d", "/c", "exit /b 0"],
            _directory,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            mappings,
            [],
            SecretOutputDisclosure.None)));
    }

    [Fact]
    public async Task NamedPipeRequestApprovalAndExecutionKeepSecretOutOfTransportArtifacts()
    {
        const string secret = "synthetic-secret-for-transport";
        const string clientId = "transport-helper";
        const string projectReference = "joydex-test";
        var envPath = Path.Combine(_directory, ".env");
        File.WriteAllText(envPath, "TOKEN=" + secret + Environment.NewLine);
        var configuration = new SecretsConfigurationStore(SecretsPaths.GetConfigurationPath(_directory));
        configuration.UpsertEnvSource(
            null,
            "Test source",
            envPath,
            new Dictionary<string, string> { ["synthetic-token"] = "TOKEN" });
        var secondaryEnvPath = Path.Combine(_directory, "secondary.env");
        File.WriteAllText(secondaryEnvPath, "SECONDARY=synthetic-secondary" + Environment.NewLine);
        configuration.UpsertEnvSource(
            null,
            "Secondary source",
            secondaryEnvPath,
            new Dictionary<string, string> { ["secondary-token"] = "SECONDARY" });
        var project = new SecretsProjectIdentity(
            projectReference,
            "project-test",
            _directory,
            "worktree-test",
            _directory,
            1,
            WorktreeReference: "worktree-test");
        var registry = new NamedClientRegistry(SecretsPaths.GetClientsPath(_directory));
        byte[] credential;
        using (var enrollment = registry.Enroll(
                   clientId,
                   "Transport helper",
                   [project],
                   [SecretDeliveryMode.ExecInject]))
        {
            credential = enrollment.CopyCredential();
        }
        new NamedClientCredentialStore(SecretsPaths.GetCredentialDirectory(_directory))
            .Save(clientId, credential);

        var runtime = new SecretsBrokerRuntime(_directory);
        var wrongCredential = RandomNumberGenerator.GetBytes(32);
        try
        {
            Assert.Equal(
                SecretsRequestStatus.IdentityUnverified,
                runtime.Cancel(
                    clientId,
                    wrongCredential,
                    projectReference,
                    "unknown-request").Status);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => runtime.RunAsync(
                clientId,
                wrongCredential,
                projectReference,
                "unknown-request",
                "unknown-reservation",
                new Dictionary<string, string>(),
                CancellationToken.None));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrongCredential);
        }
        var endpoint = SecretsBrokerEndpoint.Create(_directory);
        var server = new SecretsBrokerPipeServer(
            endpoint,
            runtime,
            maximumRunDuration: TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource();
        var serverTask = server.RunAsync(cancellation.Token);
        var client = new SecretsBrokerPipeClient(_directory);
        try
        {
            var catalog = await client.SendAsync(
                new(SecretsBrokerCommandKind.Catalog, clientId, projectReference),
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
            Assert.Equal(SecretsRequestStatus.Completed, catalog.Status);
            Assert.Equal(
                ["secondary-token", "synthetic-token"],
                catalog.Aliases!.Select(alias => alias.Alias));

            var request = await client.SendAsync(new(
                    SecretsBrokerCommandKind.Request,
                    clientId,
                    projectReference,
                    RequestId: "transport-request",
                    Reason: "Exercise the local broker transport.",
                    Operation: new ExecOperationProposal(
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                        ["/d", "/c", "echo %SYNTHETIC_TOKEN%"],
                        _directory,
                        new Dictionary<string, string> { ["SYNTHETIC_TOKEN"] = "synthetic-token" },
                        [],
                        SecretOutputDisclosure.Summary)),
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
            Assert.Equal(SecretsRequestStatus.Pending, request.Status);
            var shown = Assert.Single(runtime.PendingRequests());
            var approved = runtime.Decide(
                shown.AttemptId,
                shown.DisplayChallenge,
                SecretsConsentChoice.Yes,
                requireOperation: true);
            Assert.Equal(SecretsRequestStatus.Allowed, approved.Status);

            var polled = await client.SendAsync(
                new(SecretsBrokerCommandKind.Poll, clientId, projectReference, RequestId: request.RequestId),
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
            Assert.Equal(SecretsRequestStatus.Allowed, polled.Status);
            Assert.False(string.IsNullOrWhiteSpace(polled.Reservation));
            var invalidEnvironment = await client.SendAsync(
                new(
                    SecretsBrokerCommandKind.Run,
                    clientId,
                    projectReference,
                    RequestId: request.RequestId,
                    Reservation: polled.Reservation,
                    CallerEnvironment: new Dictionary<string, string> { ["BAD=NAME"] = "value" }),
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
            Assert.Equal(SecretsRequestStatus.InvalidRequest, invalidEnvironment.Status);
            var executed = await client.SendAsync(
                new(
                    SecretsBrokerCommandKind.Run,
                    clientId,
                    projectReference,
                    RequestId: request.RequestId,
                    Reservation: polled.Reservation,
                    CallerEnvironment: new Dictionary<string, string>()),
                TimeSpan.FromSeconds(5),
                CancellationToken.None);

            Assert.Equal(SecretsRequestStatus.Completed, executed.Status);
            Assert.True(executed.SecretsInjected);
            Assert.Equal(0, executed.Execution!.ExitCode);
            Assert.Equal("[REDACTED]", executed.Execution.StandardOutput.Trim());
            Assert.Equal(
                SecretsRequestStatus.Completed,
                runtime.GetStatus(clientId, credential, projectReference, request.RequestId!).Status);
            Assert.Equal(
                SecretsRequestStatus.Completed,
                new SecretsBrokerRuntime(_directory)
                    .GetStatus(clientId, credential, projectReference, request.RequestId!).Status);
            Assert.DoesNotContain(secret, File.ReadAllText(SecretsPaths.GetAuditPath(_directory)), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, File.ReadAllText(SecretsPaths.GetConfigurationPath(_directory)), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, File.ReadAllText(SecretsPaths.GetClientsPath(_directory)), StringComparison.Ordinal);

            var staleRequest = await client.SendAsync(new(
                    SecretsBrokerCommandKind.Request,
                    clientId,
                    projectReference,
                    RequestId: "stale-configuration-request",
                    Reason: "Verify configuration generation checks.",
                    Operation: new ExecOperationProposal(
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                        ["/d", "/c", "exit /b 0"],
                        _directory,
                        new Dictionary<string, string> { ["SYNTHETIC_TOKEN"] = "synthetic-token" },
                        [],
                        SecretOutputDisclosure.None)),
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
            var staleShown = Assert.Single(runtime.PendingRequests());
            var staleApproved = runtime.Decide(
                staleShown.AttemptId,
                staleShown.DisplayChallenge,
                SecretsConsentChoice.Yes,
                requireOperation: true);
            var oldGenerationPending = await client.SendAsync(new(
                    SecretsBrokerCommandKind.Request,
                    clientId,
                    projectReference,
                    RequestId: "old-generation-pending",
                    Reason: "Verify old queues are invalidated.",
                    Operation: new ExecOperationProposal(
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                        ["/d", "/c", "exit /b 0"],
                        _directory,
                        new Dictionary<string, string> { ["SYNTHETIC_TOKEN"] = "synthetic-token" },
                        [],
                        SecretOutputDisclosure.None)),
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
            Assert.Equal(SecretsRequestStatus.Pending, oldGenerationPending.Status);
            var source = Assert.Single(
                configuration.Read().Sources,
                source => source.DisplayName == "Test source");
            configuration.UpsertEnvSource(
                source.SourceId,
                "Renamed source",
                envPath,
                new Dictionary<string, string> { ["synthetic-token"] = "TOKEN" });
            Assert.Empty(runtime.PendingRequests());
            Assert.Equal(
                SecretsRequestStatus.Revoked,
                runtime.GetStatus(
                    clientId,
                    credential,
                    projectReference,
                    oldGenerationPending.RequestId!).Status);
            var staleRun = await client.SendAsync(
                new(
                    SecretsBrokerCommandKind.Run,
                    clientId,
                    projectReference,
                    RequestId: staleRequest.RequestId,
                    Reservation: staleApproved.Reservation,
                    CallerEnvironment: new Dictionary<string, string>()),
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
            Assert.Equal(SecretsRequestStatus.Revoked, staleRun.Status);
            Assert.Equal("The Secrets request is no longer authorized.", staleRun.Error);
            Assert.Equal(
                SecretsRequestStatus.Revoked,
                runtime.GetStatus(clientId, credential, projectReference, staleRequest.RequestId!).Status);
            Assert.Contains(
                new SecretsAuditJournal(SecretsPaths.GetAuditPath(_directory)).ReadAll(),
                record => record.AttemptId == staleRequest.AttemptId
                    && record.Kind == SecretsAuditEventKind.RequestOutcome
                    && record.Outcome == "revoked");

            var lockedRequest = await client.SendAsync(new(
                    SecretsBrokerCommandKind.Request,
                    clientId,
                    projectReference,
                    RequestId: "locked-session-request",
                    Reason: "Verify session lock checks.",
                    Operation: new ExecOperationProposal(
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                        ["/d", "/c", "exit /b 0"],
                        _directory,
                        new Dictionary<string, string> { ["SYNTHETIC_TOKEN"] = "synthetic-token" },
                        [],
                        SecretOutputDisclosure.None)),
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
            Assert.Equal(SecretsRequestStatus.Pending, lockedRequest.Status);
            runtime.SetSessionLocked(true);
            Assert.Empty(runtime.PendingRequests());
            runtime.SetSessionLocked(false);
            var lockedShown = Assert.Single(runtime.PendingRequests());
            var lockedApproved = runtime.Decide(
                lockedShown.AttemptId,
                lockedShown.DisplayChallenge,
                SecretsConsentChoice.Yes,
                requireOperation: true);
            runtime.SetSessionLocked(true);
            var lockedRun = await client.SendAsync(
                new(
                    SecretsBrokerCommandKind.Run,
                    clientId,
                    projectReference,
                    RequestId: lockedRequest.RequestId,
                    Reservation: lockedApproved.Reservation,
                    CallerEnvironment: new Dictionary<string, string>()),
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
            Assert.Equal(SecretsRequestStatus.Revoked, lockedRun.Status);
            Assert.Equal(
                SecretsRequestStatus.Revoked,
                runtime.GetStatus(clientId, credential, projectReference, lockedRequest.RequestId!).Status);
            runtime.SetSessionLocked(false);

            var timedRequest = await client.SendAsync(new(
                    SecretsBrokerCommandKind.Request,
                    clientId,
                    projectReference,
                    RequestId: "execution-time-limit",
                    Reason: "Verify the server execution bound.",
                    Operation: new ExecOperationProposal(
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                        ["/d", "/c", $"{Path.Combine(Environment.SystemDirectory, "PING.EXE")} 127.0.0.1 -n 30 >nul"],
                        _directory,
                        new Dictionary<string, string> { ["SYNTHETIC_TOKEN"] = "synthetic-token" },
                        [],
                        SecretOutputDisclosure.None)),
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
            var timedShown = Assert.Single(runtime.PendingRequests());
            var timedAllowed = runtime.Decide(
                timedShown.AttemptId,
                timedShown.DisplayChallenge,
                SecretsConsentChoice.Yes,
                requireOperation: true);
            var timedRun = await client.SendAsync(
                new(
                    SecretsBrokerCommandKind.Run,
                    clientId,
                    projectReference,
                    RequestId: timedRequest.RequestId,
                    Reservation: timedAllowed.Reservation,
                    CallerEnvironment: new Dictionary<string, string>()),
                TimeSpan.FromSeconds(5),
                CancellationToken.None);

            Assert.Equal(SecretsRequestStatus.Revoked, timedRun.Status);
            Assert.Contains("time limit", timedRun.Error, StringComparison.Ordinal);
            Assert.Contains(
                new SecretsAuditJournal(SecretsPaths.GetAuditPath(_directory)).ReadAll(),
                record => record.AttemptId == timedRequest.AttemptId
                    && record.Kind == SecretsAuditEventKind.LaunchTerminated);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credential);
            await cancellation.CancelAsync();
            await serverTask;
        }
    }

    [Fact]
    public async Task ReenrolledClientCannotReuseAnOldRequestRouteOrRecipeGeneration()
    {
        const string clientId = "route-helper";
        const string projectReference = "route-project";
        const string requestId = "reused-request";
        var envPath = Path.Combine(_directory, "route.env");
        File.WriteAllText(envPath, "TOKEN=synthetic-secret" + Environment.NewLine);
        var configuration = new SecretsConfigurationStore(SecretsPaths.GetConfigurationPath(_directory));
        configuration.UpsertEnvSource(
            null,
            "Route source",
            envPath,
            new Dictionary<string, string> { ["synthetic-token"] = "TOKEN" });
        var command = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        SecretsRecipeDraft Recipe(string exitCode) => new(
            "route-recipe",
            "Route recipe",
            projectReference,
            command,
            ["/d", "/c", $"exit /b {exitCode}"],
            _directory,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SYNTHETIC_TOKEN"] = "synthetic-token",
            },
            [],
            SecretOutputDisclosure.None);
        configuration.UpsertRecipe(Recipe("0"));
        var project = SecretsProjectIdentityFactory.Create(projectReference, _directory);
        var registry = new NamedClientRegistry(SecretsPaths.GetClientsPath(_directory));
        byte[] oldCredential;
        using (var enrollment = registry.Enroll(
                   clientId,
                   "Route helper",
                   [project],
                   [SecretDeliveryMode.ExecInject]))
        {
            oldCredential = enrollment.CopyCredential();
        }
        var runtime = new SecretsBrokerRuntime(_directory);
        byte[]? newCredential = null;
        try
        {
            var oldRequest = runtime.Submit(new(
                requestId,
                clientId,
                projectReference,
                "Create an old-generation route.",
                RecipeId: "route-recipe"), oldCredential);
            Assert.Equal(SecretsRequestStatus.Pending, oldRequest.Status);

            configuration.UpsertRecipe(Recipe("7"));
            registry.Revoke(clientId);
            using (var enrollment = registry.Enroll(
                       clientId,
                       "Route helper",
                       [project],
                       [SecretDeliveryMode.ExecInject]))
            {
                newCredential = enrollment.CopyCredential();
            }

            var replacement = runtime.Submit(new(
                requestId,
                clientId,
                projectReference,
                "Use the current recipe generation.",
                RecipeId: "route-recipe"), newCredential);
            var shown = Assert.Single(runtime.PendingRequests());
            var allowed = runtime.Decide(
                replacement.AttemptId,
                shown.DisplayChallenge,
                SecretsConsentChoice.Yes,
                requireOperation: true);
            var result = await runtime.RunAsync(
                clientId,
                newCredential,
                projectReference,
                requestId,
                allowed.Reservation!,
                new Dictionary<string, string>(),
                CancellationToken.None);

            Assert.Equal(SecretsRequestStatus.Pending, replacement.Status);
            Assert.Equal(7, result.ExitCode);
            Assert.Equal(
                SecretsRequestStatus.IdentityUnverified,
                runtime.GetStatus(clientId, oldCredential, projectReference, requestId).Status);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(oldCredential);
            if (newCredential is not null) CryptographicOperations.ZeroMemory(newCredential);
        }
    }

    [Fact]
    public void MissingSourceDoesNotRevealProviderStateToAnUnenrolledClient()
    {
        var runtime = new SecretsBrokerRuntime(_directory);
        var credential = RandomNumberGenerator.GetBytes(32);
        try
        {
            var response = runtime.Submit(new SecretsBrokerSubmission(
                "missing-source",
                "unknown-client",
                "unknown-project",
                "Test authentication order.",
                Operation: new ExecOperationProposal(
                    Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    ["/d", "/c", "exit /b 0"],
                    _directory,
                    new Dictionary<string, string> { ["TOKEN"] = "token" },
                    [],
                    SecretOutputDisclosure.None)), credential);

            Assert.Equal(SecretsRequestStatus.IdentityUnverified, response.Status);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credential);
        }
    }

    [Fact]
    public void TimeoutReplyDoesNotClaimAnUnconfirmedLaunchStopped()
    {
        var reply = SecretsBrokerPipeServer.TimeoutReply(
            SecretsRequestStatus.LaunchUnconfirmed,
            "uncertain-request");
        var authenticationChanged = SecretsBrokerPipeServer.TimeoutReply(
            SecretsRequestStatus.IdentityUnverified,
            "revoked-during-timeout");

        Assert.Equal(SecretsRequestStatus.LaunchUnconfirmed, reply.Status);
        Assert.Contains("could not confirm", reply.Error, StringComparison.Ordinal);
        Assert.Contains("Do not retry", reply.Error, StringComparison.Ordinal);
        Assert.Equal(SecretsRequestStatus.LaunchUnconfirmed, authenticationChanged.Status);
        Assert.Contains("Do not retry", authenticationChanged.Error, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
