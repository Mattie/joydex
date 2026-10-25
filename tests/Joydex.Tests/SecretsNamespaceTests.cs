using System.Security.Cryptography;
using Joydex.Secrets;

namespace Joydex.Tests;

public sealed class SecretsNamespaceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "joydex-namespaces-" + Guid.NewGuid().ToString("N"));

    public SecretsNamespaceTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RememberedApprovalsCannotCrossSourcesAndReplayCannotChangeSource(bool exactOperation)
    {
        var configuration = new SecretsConfigurationStore(SecretsPaths.GetConfigurationPath(_directory));
        var publicAlias = new string('A', 96);
        foreach (var name in new[] { "Personal", "Project" })
        {
            var path = Path.Combine(_directory, name + ".env");
            File.WriteAllText(path, "TOKEN=synthetic-" + name + "\n");
            configuration.UpsertEnvSource(null, name, path, new Dictionary<string, string> { [publicAlias] = "TOKEN" });
        }
        LocalSecretsRequester.EnsureReady(_directory, "codex", "Codex", "test", _directory);
        var credential = new NamedClientCredentialStore(SecretsPaths.GetCredentialDirectory(_directory)).Load("codex");
        var runtime = new SecretsBrokerRuntime(_directory);
        try
        {
            SecretsRequestResponse Submit(string requestId, string? source, string destination = "TOKEN") => runtime.Submit(
                new(requestId, "codex", "test", "Test namespace scopes", Operation: new(
                    Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/c", "exit /b 0"], _directory,
                    new Dictionary<string, string> { [destination] = publicAlias }, [], SecretOutputDisclosure.None, Source: source)), credential);

            var first = Submit("first", "Personal");
            var shown = Assert.Single(runtime.PendingRequests());
            Assert.Equal("Personal", Assert.Single(shown.SecretBindings!).SourceName);
            Assert.Equal(SecretsRequestStatus.Allowed,
                runtime.Decide(first.AttemptId, shown.DisplayChallenge, SecretsConsentChoice.YesAlways, exactOperation).Status);
            Assert.Equal(SecretsRequestStatus.Allowed, Submit("same-source", "Personal").Status);
            Assert.Equal(SecretsRequestStatus.Conflict, Submit("first", "Project").Status);
            Assert.Equal(SecretsRequestStatus.Pending, Submit("other-source", "Project").Status);
            Assert.Equal("Project", Assert.Single(Assert.Single(runtime.PendingRequests()).SecretBindings!).SourceName);
            if (exactOperation)
                Assert.Equal(SecretsRequestStatus.Pending, Submit("new-destination", "Personal", "OTHER_TOKEN").Status);
            var ambiguous = Submit("ambiguous", null);
            Assert.Equal(SecretsRequestStatus.ProviderUnavailable, ambiguous.Status);
            Assert.Contains("ambiguous", ambiguous.Failure!.Message);

            var policy = new SecretsPolicyStore(SecretsPaths.GetPolicyPath(_directory)).Read(DateTimeOffset.UtcNow);
            var rule = Assert.Single(policy.Rules);
            var personal = configuration.Read().Sources.Single(source => source.DisplayName == "Personal");
            Assert.Equal([$"{personal.SourceId:N}/{publicAlias}"], rule.Aliases);
        }
        finally { CryptographicOperations.ZeroMemory(credential); }
    }

    [Fact]
    public void RenamedOrRemovedSourceNeverFallsBackToAnotherCredential()
    {
        var configuration = new SecretsConfigurationStore(SecretsPaths.GetConfigurationPath(_directory));
        foreach (var name in new[] { "Personal", "Project" })
        {
            var path = Path.Combine(_directory, name + ".env");
            File.WriteAllText(path, "TOKEN=synthetic-" + name + "\n");
            configuration.UpsertEnvSource(null, name, path, new Dictionary<string, string> { ["TOKEN"] = "TOKEN" });
        }
        LocalSecretsRequester.EnsureReady(_directory, "codex", "Codex", "test", _directory);
        var credential = new NamedClientCredentialStore(SecretsPaths.GetCredentialDirectory(_directory)).Load("codex");
        var runtime = new SecretsBrokerRuntime(_directory);
        try
        {
            SecretsRequestResponse Submit(string id, string? source) => runtime.Submit(
                new(id, "codex", "test", "Test source lifecycle", Operation: new(
                    Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/c", "exit /b 0"], _directory,
                    new Dictionary<string, string> { ["TOKEN"] = "TOKEN" }, [], SecretOutputDisclosure.None, Source: source)), credential);
            var first = Submit("initial", "Personal");
            var shown = Assert.Single(runtime.PendingRequests());
            runtime.Decide(first.AttemptId, shown.DisplayChallenge, SecretsConsentChoice.YesAlways, false);
            var personal = configuration.Read().Sources.Single(source => source.DisplayName == "Personal");
            configuration.UpsertEnvSource(personal.SourceId, "Renamed", personal.FilePath,
                new Dictionary<string, string> { ["TOKEN"] = "TOKEN" });
            Assert.Equal(SecretsRequestStatus.ProviderUnavailable, Submit("old-name", "Personal").Status);
            Assert.Equal(SecretsRequestStatus.Allowed, Submit("stable-id", personal.SourceId.ToString("N")).Status);
            configuration.RemoveEnvSource(personal.SourceId);
            Assert.Equal(SecretsRequestStatus.ProviderUnavailable, Submit("removed", personal.SourceId.ToString("N")).Status);
            Assert.Equal(SecretsRequestStatus.Pending, Submit("remaining", null).Status);
            Assert.Equal("Project", Assert.Single(Assert.Single(runtime.PendingRequests()).SecretBindings!).SourceName);
        }
        finally { CryptographicOperations.ZeroMemory(credential); }
    }

    [Fact]
    public async Task QualifiedReferencesCanInjectOverlappingAliasesFromTwoSources()
    {
        var configuration = new SecretsConfigurationStore(SecretsPaths.GetConfigurationPath(_directory));
        foreach (var name in new[] { "First", "Second" })
        {
            var path = Path.Combine(_directory, name + ".env");
            File.WriteAllText(path, "TOKEN=synthetic-" + name + "\n");
            configuration.UpsertEnvSource(null, name, path, new Dictionary<string, string> { ["TOKEN"] = "TOKEN" });
        }
        LocalSecretsRequester.EnsureReady(_directory, "codex", "Codex", "test", _directory);
        var credential = new NamedClientCredentialStore(SecretsPaths.GetCredentialDirectory(_directory)).Load("codex");
        var runtime = new SecretsBrokerRuntime(_directory);
        try
        {
            var aliases = runtime.ListCatalog("codex", credential, "test").Aliases;
            var mappings = aliases.ToDictionary(alias => alias.SourceName!.ToUpperInvariant() + "_TOKEN", alias => alias.QualifiedAlias!);
            var submitted = runtime.Submit(new("mixed", "codex", "test", "Test mixed sources", Operation: new(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                ["/d", "/c", "if \"%FIRST_TOKEN%\"==\"synthetic-First\" (if \"%SECOND_TOKEN%\"==\"synthetic-Second\" (exit /b 0)) & exit /b 9"],
                _directory, mappings, [], SecretOutputDisclosure.Summary)), credential);
            var shown = Assert.Single(runtime.PendingRequests());
            Assert.Equal(2, shown.SecretBindings!.Count);
            var approved = runtime.Decide(submitted.AttemptId, shown.DisplayChallenge, SecretsConsentChoice.Yes, true);
            var result = await runtime.RunAsync("codex", credential, "test", submitted.RequestId, approved.Reservation!,
                new Dictionary<string, string>(), CancellationToken.None);
            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.StandardOutput);
        }
        finally { CryptographicOperations.ZeroMemory(credential); }
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
