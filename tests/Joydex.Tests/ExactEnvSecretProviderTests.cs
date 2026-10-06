using Joydex.Secrets;

namespace Joydex.Tests;

public sealed class ExactEnvSecretProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "joydex-secrets-tests-" + Guid.NewGuid().ToString("N"));

    public ExactEnvSecretProviderTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void FetchParsesQuotesAndMultilineWithoutChangingProcessEnvironment()
    {
        var path = Write("DEPLOY_TOKEN=synthetic-one\nMULTILINE=\"line one\nline two\"\n");
        var previous = Environment.GetEnvironmentVariable("DEPLOY_TOKEN");
        Environment.SetEnvironmentVariable("DEPLOY_TOKEN", null);
        try
        {
            var provider = Provider(path,
                new("deploy-token", "DEPLOY_TOKEN", "stable-1", 1),
                new("multi", "MULTILINE", "stable-2", 1));

            using var values = provider.Fetch(["deploy-token", "multi"]);

            Assert.Equal("synthetic-one", values.GetValue("deploy-token"));
            Assert.Equal("line one\nline two", values.GetValue("multi"));
            Assert.Null(Environment.GetEnvironmentVariable("DEPLOY_TOKEN"));
            Assert.Equal(64, values.ProviderGeneration.Length);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEPLOY_TOKEN", previous);
        }
    }

    [Fact]
    public void DuplicateKeysAreRejectedInsteadOfSilentlyChoosingOne()
    {
        var path = Write("TOKEN=synthetic-one\nTOKEN=synthetic-two\n");
        var provider = Provider(path, new EnvSecretAlias("token", "TOKEN", "stable", 1));

        var exception = Assert.Throws<InvalidDataException>(() => provider.Fetch(["token"]));

        Assert.Contains("repeats key", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TOKEN=$EXTERNAL\n")]
    [InlineData("TOKEN=\"prefix ${EXTERNAL} suffix\"\n")]
    public void InterpolationIsRejectedBeforeItCanReadBrokerEnvironment(string contents)
    {
        var previous = Environment.GetEnvironmentVariable("EXTERNAL");
        Environment.SetEnvironmentVariable("EXTERNAL", "private-process-value");
        try
        {
            var provider = Provider(
                Write(contents),
                new EnvSecretAlias("token", "TOKEN", "stable", 1));

            var exception = Assert.Throws<InvalidDataException>(() => provider.Fetch(["token"]));

            Assert.Contains("interpolation is disabled", exception.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private-process-value", exception.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EXTERNAL", previous);
        }
    }

    [Fact]
    public void AliasListingContainsOnlyMetadataAndAvailability()
    {
        var provider = Provider(
            Write("TOKEN=canary-secret-value\n"),
            new("available", "TOKEN", "stable-a", 4),
            new("missing", "MISSING", "stable-b", 2));

        var aliases = provider.ListAliases();

        Assert.Collection(
            aliases,
            item =>
            {
                Assert.Equal("available", item.Alias);
                Assert.True(item.Available);
                Assert.Equal("stable-a", item.StableSecretId);
            },
            item =>
            {
                Assert.Equal("missing", item.Alias);
                Assert.False(item.Available);
            });
        Assert.DoesNotContain("canary-secret-value", string.Join('|', aliases));
    }

    [Fact]
    public void VariableDiscoveryReturnsNamesWithoutValues()
    {
        var path = Write("Z_TOKEN=canary-secret-value\nexport API_KEY='another-secret'\n");

        var names = ExactEnvSecretProvider.DiscoverKeys(path);

        Assert.Equal(["API_KEY", "Z_TOKEN"], names);
        Assert.DoesNotContain("secret", string.Join('|', names), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProviderGenerationChangesWhenFileContentChanges()
    {
        var path = Write("TOKEN=synthetic-one\n");
        var provider = Provider(path, new EnvSecretAlias("token", "TOKEN", "stable", 1));
        string first;
        using (var values = provider.Fetch(["token"])) first = values.ProviderGeneration;
        File.WriteAllText(path, "TOKEN=synthetic-two\n");

        using var changed = provider.Fetch(["token"]);

        Assert.NotEqual(first, changed.ProviderGeneration);
        Assert.Equal("synthetic-two", changed.GetValue("token"));
    }

    [Fact]
    public void CombinedProviderListsAndFetchesAliasesFromMultipleExactFiles()
    {
        var firstPath = Path.Combine(_directory, "first.env");
        var secondPath = Path.Combine(_directory, "second.env");
        File.WriteAllText(firstPath, "FIRST_TOKEN=synthetic-first\n");
        File.WriteAllText(secondPath, "SECOND_TOKEN=synthetic-second\n");
        var provider = new ExactEnvSecretProvider([
            new(firstPath, [new("first-token", "FIRST_TOKEN", "stable-first", 1)]),
            new(secondPath, [new("second-token", "SECOND_TOKEN", "stable-second", 1)]),
        ]);

        var aliases = provider.ListAliases();
        using var values = provider.Fetch(["second-token", "first-token"]);

        Assert.Equal(["first-token", "second-token"], aliases.Select(alias => alias.Alias));
        Assert.All(aliases, alias => Assert.True(alias.Available));
        Assert.Equal("synthetic-first", values.GetValue("first-token"));
        Assert.Equal("synthetic-second", values.GetValue("second-token"));
        Assert.Equal(64, values.ProviderGeneration.Length);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private string Write(string contents)
    {
        var path = Path.Combine(_directory, ".env");
        File.WriteAllText(path, contents, new System.Text.UTF8Encoding(false));
        return path;
    }

    private static ExactEnvSecretProvider Provider(string path, params EnvSecretAlias[] aliases) =>
        new(path, aliases);
}
