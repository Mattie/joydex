using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Joydex.WirelessPanel;

namespace Joydex.WirelessPanel.Tests;

public sealed class WirelessPanelConfigurationStoreTests
{
    [Fact]
    public void Load_returns_null_when_file_does_not_exist()
    {
        using var directory = new TemporaryDirectory();
        var store = new WirelessPanelConfigurationStore(
            Path.Combine(directory.Path, "panel.json"));

        Assert.Null(store.Load());
    }

    [Fact]
    public void Save_and_load_round_trip_through_current_user_dpapi()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "panel.json");
        var store = new WirelessPanelConfigurationStore(path);
        var expected = WirelessPanelConfiguration.Create(
            "http://panel.local:6052/",
            "panel-user",
            "unique round-trip password",
            enabled: false);

        store.Save(expected);
        var actual = store.Load();

        Assert.NotNull(actual);
        Assert.Equal(expected.Endpoint, actual.Endpoint);
        Assert.Equal(expected.Username, actual.Username);
        Assert.Equal(expected.Password, actual.Password);
        Assert.Equal(expected.Enabled, actual.Enabled);
    }

    [Fact]
    public void Save_keeps_plaintext_password_out_of_json()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "panel.json");
        var store = new WirelessPanelConfigurationStore(path);
        const string password = "plain-text-marker-836ab56d";

        store.Save(
            WirelessPanelConfiguration.Create(
                "http://panel.local/",
                "user",
                password));

        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        Assert.DoesNotContain(password, json);
        Assert.Equal(
            WirelessPanelConfigurationStore.CurrentSchemaVersion,
            document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.False(
            string.IsNullOrWhiteSpace(
                document.RootElement.GetProperty("protectedPassword").GetString()));
    }

    [Fact]
    public void Save_protects_password_for_the_current_windows_user()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "panel.json");
        var store = new WirelessPanelConfigurationStore(path);
        const string password = "current-user-scope-marker";
        store.Save(
            WirelessPanelConfiguration.Create(
                "http://panel.local/",
                "user",
                password));

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var protectedPassword = Convert.FromBase64String(
            document.RootElement.GetProperty("protectedPassword").GetString()!);
        var entropy = Encoding.UTF8.GetBytes("Joydex.WirelessPanel.Password.v1");
        byte[]? clearPassword = null;
        try
        {
            clearPassword = ProtectedData.Unprotect(
                protectedPassword,
                entropy,
                DataProtectionScope.CurrentUser);

            Assert.Equal(password, Encoding.UTF8.GetString(clearPassword));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedPassword);
            CryptographicOperations.ZeroMemory(entropy);
            if (clearPassword is not null)
            {
                CryptographicOperations.ZeroMemory(clearPassword);
            }
        }
    }

    [Fact]
    public void Save_replaces_existing_document_and_cleans_temporary_file()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "panel.json");
        var store = new WirelessPanelConfigurationStore(path);
        store.Save(
            WirelessPanelConfiguration.Create(
                "http://first-panel.local/",
                "first-user",
                "first-password"));

        store.Save(
            WirelessPanelConfiguration.Create(
                "http://second-panel.local/",
                "second-user",
                "second-password",
                enabled: false));

        var configuration = store.Load();
        Assert.NotNull(configuration);
        Assert.Equal("http://second-panel.local/", configuration.Endpoint.AbsoluteUri);
        Assert.Equal("second-user", configuration.Username);
        Assert.Equal("second-password", configuration.Password);
        Assert.False(configuration.Enabled);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void Load_rejects_invalid_protected_password()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "panel.json");
        File.WriteAllText(
            path,
            """
            {
              "schemaVersion": 1,
              "enabled": true,
              "endpoint": "http://panel.local/",
              "username": "user",
              "protectedPassword": "not base64!"
            }
            """);
        var store = new WirelessPanelConfigurationStore(path);

        Assert.Throws<InvalidDataException>(() => store.Load());
    }

    [Fact]
    public void Load_rejects_dpapi_ciphertext_protected_with_different_entropy()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "panel.json");
        var clearPassword = Encoding.UTF8.GetBytes("wrong-entropy-password");
        var wrongEntropy = Encoding.UTF8.GetBytes("Joydex.WirelessPanel.WrongEntropy");
        var protectedPassword = ProtectedData.Protect(
            clearPassword,
            wrongEntropy,
            DataProtectionScope.CurrentUser);
        try
        {
            File.WriteAllText(
                path,
                $$"""
                {
                  "schemaVersion": 1,
                  "enabled": true,
                  "endpoint": "http://panel.local/",
                  "username": "user",
                  "protectedPassword": "{{Convert.ToBase64String(protectedPassword)}}"
                }
                """);
            var store = new WirelessPanelConfigurationStore(path);

            Assert.Throws<InvalidDataException>(() => store.Load());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearPassword);
            CryptographicOperations.ZeroMemory(wrongEntropy);
            CryptographicOperations.ZeroMemory(protectedPassword);
        }
    }

    [Fact]
    public void Load_rejects_unknown_properties()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "panel.json");
        File.WriteAllText(
            path,
            """
            {
              "schemaVersion": 1,
              "enabled": true,
              "endpoint": "http://panel.local/",
              "username": "user",
              "protectedPassword": "AA==",
              "surprise": true
            }
            """);
        var store = new WirelessPanelConfigurationStore(path);

        Assert.Throws<InvalidDataException>(() => store.Load());
    }

    [Fact]
    public void Load_rejects_missing_enabled_property()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "panel.json");
        File.WriteAllText(
            path,
            """
            {
              "schemaVersion": 1,
              "endpoint": "http://panel.local/",
              "username": "user",
              "protectedPassword": "AA=="
            }
            """);
        var store = new WirelessPanelConfigurationStore(path);

        Assert.Throws<InvalidDataException>(() => store.Load());
    }

    [Fact]
    public void GetDefaultPath_uses_local_app_data()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Joydex",
            "WirelessPanel",
            "panel.json");

        Assert.Equal(expected, WirelessPanelConfigurationStore.GetDefaultPath());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"joydex-wireless-panel-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
