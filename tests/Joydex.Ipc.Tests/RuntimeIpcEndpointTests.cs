using Joydex.Contracts;

namespace Joydex.Ipc.Tests;

public sealed class RuntimeIpcEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "joydex-ipc-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void EquivalentDataRootsHaveTheSameIdentity()
    {
        Directory.CreateDirectory(_root);

        var first = RuntimeIpcEndpoint.CreateSynthetic(_root, PipeName());
        var second = RuntimeIpcEndpoint.CreateSynthetic(
            Path.Combine(_root, ".", ""),
            PipeName());
        var explicitDefault = RuntimeIpcEndpoint.CreateSynthetic(
            _root,
            Path.Combine(_root, "config.json"),
            PipeName());

        Assert.Equal(first.DataRootId, second.DataRootId);
        Assert.Equal(first.DataRootId, explicitDefault.DataRootId);
    }

    [Fact]
    public void ConfigurationPathParticipatesInOpaqueDataRootIdentity()
    {
        var first = RuntimeIpcEndpoint.CreateSynthetic(
            _root,
            Path.Combine(_root, "a.json"),
            PipeName());
        var second = RuntimeIpcEndpoint.CreateSynthetic(
            _root,
            Path.Combine(_root, "b.json"),
            PipeName());

        Assert.NotEqual(first.DataRootId, second.DataRootId);
        Assert.DoesNotContain(_root, first.DataRootId, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DataRootAndConfigurationIdentityUseNormalizedCaseInsensitivePaths()
    {
        var first = RuntimeIpcEndpoint.CreateSynthetic(
            _root,
            Path.Combine(_root, "nested", "settings.json"),
            PipeName());
        var second = RuntimeIpcEndpoint.CreateSynthetic(
            Path.Combine(_root.ToUpperInvariant(), "."),
            Path.Combine(_root.ToUpperInvariant(), "NESTED", ".", "SETTINGS.JSON"),
            PipeName());

        Assert.Equal(first.DataRootId, second.DataRootId);
    }

    [Fact]
    public void ProductionEndpointIsStableForAUserSessionAndKeepsRootIdentitySeparate()
    {
        var first = RuntimeIpcEndpoint.CreateProduction(Path.Combine(_root, "one"));
        var second = RuntimeIpcEndpoint.CreateProduction(Path.Combine(_root, "two"));

        Assert.Equal(RuntimeInstanceKind.Production, first.InstanceKind);
        Assert.Equal(first.PipeName, second.PipeName);
        Assert.Equal(first.SessionId, second.SessionId);
        Assert.NotEqual(first.DataRootId, second.DataRootId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("pipe/name")]
    [InlineData("pipe\\name")]
    [InlineData(" pipe")]
    public void SyntheticEndpointRejectsInvalidPipeNames(string pipeName)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            RuntimeIpcEndpoint.CreateSynthetic(_root, pipeName));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string PipeName() => $"joydex-ipc-test-{Guid.NewGuid():N}";
}
