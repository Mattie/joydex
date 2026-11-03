using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Joydex.Core.T3;
using Joydex.Windows.T3;

namespace Joydex.Tests;

public sealed class T3DesktopDiscoveryTests
{
    [Fact]
    public void WslProjectorUsesExactDistributionArgumentsAndNoShellOrServerStartupCommand()
    {
        var start = T3WslTransport.CreateStartInfo("Ubuntu");
        Assert.Equal("wsl.exe", start.FileName);
        Assert.Equal(new[] { "-d", "Ubuntu", "--exec", "python3", "-" }, start.ArgumentList);
        Assert.False(start.UseShellExecute); Assert.True(start.CreateNoWindow);
        Assert.True(start.RedirectStandardInput); Assert.True(start.RedirectStandardOutput);
        Assert.Throws<ArgumentException>(() => T3WslTransport.CreateStartInfo("Ubuntu --shutdown"));
    }

    [Fact]
    public void RemoteOnlyDesktopCanBeVerifiedAndManualFallbackRequiresExactEnabledNativeIdentity()
    {
        var discovery = new T3DesktopDiscoveryResult(true, true,
            [new("local", "Local", true, null, false),
             new("remote", "Remote", false, null, true, "Saved relay credentials are not reused", TransportKind: "relay")]);
        var options = new T3ConnectionOptions { FollowDesktop = true, SshHost = "example-host", EnvironmentId = "remote" };
        var selected = Assert.Single(T3Reader.DesktopTestCandidates(discovery, options));
        Assert.Equal("remote", selected.EnvironmentId); Assert.Equal("example-host", selected.SshHost);
        Assert.Null(selected.UnsupportedReason);
        Assert.Empty(T3Reader.DesktopTestCandidates(discovery, options with { EnvironmentId = "other" }));
        Assert.Empty(T3Reader.DesktopTestCandidates(discovery with { Environments = [discovery.Environments[1] with { Enabled = false }] }, options));
    }

    [Fact]
    public void CatalogProjectionDeduplicatesNativeSourcesAndNeverParsesCredentialFields()
    {
        using var document = JsonDocument.Parse("""
            {"schemaVersion":1,"targets":[
              {"_tag":"RelayConnectionTarget","environmentId":"native-remote","label":"Remote"},
              {"_tag":"SshConnectionTarget","environmentId":"native-remote","connectionId":"route-ssh","label":"Remote SSH"},
              {"_tag":"BearerConnectionTarget","environmentId":"disabled","connectionId":"route-bearer","label":"Disabled"}],
             "profiles":[{"_tag":"SshConnectionProfile","environmentId":"native-remote","connectionId":"route-ssh",
                 "target":{"alias":"example-host","hostname":"example.test","username":null,"port":null}}],
             "disabledEnvironmentIds":["disabled"],"credentials":"deliberately invalid secret shape",
             "remoteDpopTokens":{"not":"a token array"}}
            """);
        var result = T3Reader.ProjectDesktopCatalog(document.RootElement);
        Assert.Equal(2, result.Count);
        var remote = result[0];
        Assert.Equal("native-remote", remote.EnvironmentId);
        Assert.Equal("route-ssh", remote.ConnectionId);
        Assert.Equal("example-host", remote.SshHost);
        Assert.Null(remote.UnsupportedReason);
        Assert.False(result[1].Enabled);
        Assert.NotNull(result[1].UnsupportedReason);
    }

    [Fact]
    public void ExplicitSshOverridesAreVisibleUnsupportedRatherThanIgnored()
    {
        using var document = JsonDocument.Parse("""
            {"schemaVersion":1,"targets":[{"_tag":"SshConnectionTarget","environmentId":"remote","connectionId":"r"}],
             "profiles":[{"environmentId":"remote","connectionId":"r","target":{"alias":"example","username":"different-user","port":2222}}]}
            """);
        var source = Assert.Single(T3Reader.ProjectDesktopCatalog(document.RootElement));
        Assert.Null(source.SshHost);
        Assert.Contains("explicit user or port", source.UnsupportedReason);
    }

    [Fact]
    public void SupportedElectronEncryptionProjectsMetadataAndUnknownFormatsFailClosed()
    {
        using var scratch = new Scratch();
        var key = RandomNumberGenerator.GetBytes(32);
        var plain = Encoding.UTF8.GetBytes("""
            {"schemaVersion":1,"targets":[{"_tag":"RelayConnectionTarget","environmentId":"remote","label":"Remote"}],"profiles":[],
             "credentials":[{"token":"synthetic-never-project"}],"remoteDpopTokens":[],"disabledEnvironmentIds":[]}
            """);
        var nonce = RandomNumberGenerator.GetBytes(12); var cipher = new byte[plain.Length]; var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag);
        var envelope = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, encryptedCatalog = Convert.ToBase64String("v10"u8.ToArray().Concat(nonce).Concat(cipher).Concat(tag).ToArray()) });
        var wrapped = "DPAPI"u8.ToArray().Concat(ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser)).ToArray();
        var localState = JsonSerializer.SerializeToUtf8Bytes(new { os_crypt = new { encrypted_key = Convert.ToBase64String(wrapped) } });
        var decrypted = T3Reader.DecryptDesktopCatalog(envelope, localState);
        try
        {
            using var decoded = JsonDocument.Parse(decrypted);
            Assert.Equal("remote", Assert.Single(T3Reader.ProjectDesktopCatalog(decoded.RootElement)).EnvironmentId);
        }
        finally { CryptographicOperations.ZeroMemory(decrypted); CryptographicOperations.ZeroMemory(key); }
        var unknown = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, encryptedCatalog = Convert.ToBase64String("v20-unsupported"u8.ToArray()) });
        Assert.Throws<InvalidDataException>(() => T3Reader.DecryptDesktopCatalog(unknown, localState));
    }

    [Fact]
    public void CatalogFailureRetainsIndependentPrimaryAndWslOnlyDoesNotUseStaleWindowsIdentity()
    {
        using var scratch = new Scratch();
        File.WriteAllText(Path.Combine(scratch.Root, "environment-id"), "windows-native");
        File.WriteAllText(Path.Combine(scratch.Root, "connection-catalog.json"), "{\"version\":99}");
        var partial = T3Reader.DiscoverDesktop(scratch.Home, Path.Combine(scratch.Home, "missing-local-state"));
        Assert.True(partial.Success); Assert.False(partial.IsComplete);
        Assert.Equal("windows-native", Assert.Single(partial.Environments).EnvironmentId);
        File.WriteAllText(Path.Combine(scratch.Root, "desktop-settings.json"), "{\"wslBackendEnabled\":true,\"wslOnly\":true}");
        var wsl = T3Reader.DiscoverDesktop(scratch.Home, "missing");
        Assert.DoesNotContain(wsl.Environments, source => source.EnvironmentId == "windows-native");
        Assert.Contains(wsl.Environments, source => source.TransportKind == "wsl" && source.UnsupportedReason is not null);
    }

    private sealed class Scratch : IDisposable
    {
        public string Home { get; } = Path.Combine(Path.GetTempPath(), "joydex-t3-discovery-" + Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(Home, "userdata");
        public Scratch() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Home, true);
    }
}
