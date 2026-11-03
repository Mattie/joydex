using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Joydex.Core.T3;

namespace Joydex.Windows.T3;

public sealed partial class T3Reader
{
    private static string DesktopHome => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".t3");

    public async Task<T3DesktopDiscoveryResult> DiscoverDesktopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = DiscoverDesktop(DesktopHome,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "t3code-v2", "Local State"));
        var environments = result.Environments.ToArray();
        for (var index = 0; index < environments.Length; index++)
            if (environments[index] is { Enabled: true, TransportKind: "wsl" } wsl)
                environments[index] = await T3WslTransport.DiscoverAsync(wsl, cancellationToken).ConfigureAwait(false);
        return result with { Environments = environments };
    }

    internal static T3DesktopDiscoveryResult DiscoverDesktop(string home, string localStatePath)
    {
        var environments = new List<T3DesktopEnvironment>();
        try
        {
            var root = Path.Combine(home, "userdata");
            var enabled = true;
            var wsl = false;
            var wslOnly = false;
            string? wslDistro = null;
            var settingsPath = Path.Combine(root, "desktop-settings.json");
            if (File.Exists(settingsPath))
            {
                using var settings = JsonDocument.Parse(ReadBoundedFile(settingsPath));
                enabled = !settings.RootElement.TryGetProperty("localEnvironmentEnabled", out var local) || local.ValueKind != JsonValueKind.False;
                wsl = settings.RootElement.TryGetProperty("wslBackendEnabled", out var backend) && backend.ValueKind == JsonValueKind.True;
                wslOnly = settings.RootElement.TryGetProperty("wslOnly", out var only) && only.ValueKind == JsonValueKind.True;
                wslDistro = Text(settings.RootElement, "wslDistro");
            }
            var idPath = Path.Combine(root, "environment-id");
            var localId = File.Exists(idPath) ? Encoding.UTF8.GetString(ReadBoundedFile(idPath)).Trim() : "";
            if (wsl && wslOnly)
                environments.Add(new("", "T3 local WSL primary", true, null, enabled,
                    "WSL primary runtime has not been verified; a stale Windows runtime is not treated as the primary.", TransportKind: "wsl", WslDistro: wslDistro));
            else if (ValidId(localId)) environments.Add(new(localId, "T3 local", true, null, enabled));
            else if (enabled) environments.Add(new("", "T3 local", true, null, true, "The native local environment identity is unavailable."));
            if (enabled && wsl && !wslOnly)
                environments.Add(new("", "Configured T3 WSL route", false, null, true,
                    "The configured WSL route has not yet been verified as a running environment.", TransportKind: "wsl", WslDistro: wslDistro));

            var catalogPath = Path.Combine(root, "connection-catalog.json");
            if (!File.Exists(catalogPath)) return new(true, true, environments);
            var plaintext = DecryptDesktopCatalog(ReadBoundedFile(catalogPath), ReadBoundedFile(localStatePath));
            try
            {
                using var catalog = JsonDocument.Parse(plaintext);
                environments.AddRange(ProjectDesktopCatalog(catalog.RootElement));
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
            return new(true, true, environments);
        }
        catch
        {
            return new(environments.Any(environment => environment.IsLocal && ValidId(environment.EnvironmentId)), false, environments,
                "Desktop environment discovery is incomplete; previously discovered sources are retained.");
        }
    }

    // This is deliberately a whitelist: credentials and remoteDpopTokens are never inspected.
    internal static IReadOnlyList<T3DesktopEnvironment> ProjectDesktopCatalog(JsonElement catalog)
    {
        if (catalog.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException();
        var targets = catalog.GetProperty("targets");
        var profiles = catalog.GetProperty("profiles");
        if (targets.GetArrayLength() > 128 || profiles.GetArrayLength() > 256) throw new InvalidDataException();
        var disabled = new HashSet<string>(StringComparer.Ordinal);
        if (catalog.TryGetProperty("disabledEnvironmentIds", out var disabledIds))
            foreach (var value in disabledIds.EnumerateArray()) if (value.GetString() is { } id) disabled.Add(id);
        var output = new List<T3DesktopEnvironment>();
        foreach (var group in targets.EnumerateArray().GroupBy(target => Text(target, "environmentId"), StringComparer.Ordinal))
        {
            if (!ValidId(group.Key)) throw new InvalidDataException();
            T3DesktopEnvironment? selected = null;
            foreach (var target in group)
            {
                var tag = Text(target, "_tag");
                var connectionId = Text(target, "connectionId");
                var profile = profiles.EnumerateArray().FirstOrDefault(profile => Text(profile, "connectionId") == connectionId
                    && Text(profile, "environmentId") == group.Key);
                var name = DisplayLabel(Text(target, "label")) ?? "T3 environment";
                var isSsh = tag == "SshConnectionTarget";
                var host = isSsh && profile.ValueKind == JsonValueKind.Object && profile.TryGetProperty("target", out var ssh)
                    ? Text(ssh, "alias") : null;
                var hasOverrides = isSsh && profile.ValueKind == JsonValueKind.Object && profile.TryGetProperty("target", out var sshTarget)
                    && (Text(sshTarget, "username") is not null || sshTarget.TryGetProperty("port", out var port) && port.ValueKind != JsonValueKind.Null);
                var transport = isSsh ? "ssh" : tag == "RelayConnectionTarget" ? "relay" : "bearer";
                var reason = hasOverrides ? "The SSH route has explicit user or port settings; its effective alias must be verified before it can be monitored." :
                    isSsh && T3SshTransport.IsValidHost(host) ? null :
                    isSsh ? "The saved SSH route has no supported current-user alias." :
                    "This enabled desktop route needs an independent read-only grant or a verified matching SSH route; saved desktop credentials are not reused.";
                var candidate = new T3DesktopEnvironment(group.Key!, name, false,
                    reason is null ? host : null, !disabled.Contains(group.Key!), reason, connectionId, transport);
                // Alternate routes share one native source. Prefer a route we can actually read.
                selected = selected is null || selected.UnsupportedReason is not null && reason is null ? candidate : selected;
            }
            if (selected is not null) output.Add(selected);
        }
        return output;
    }

    // Electron 44.10 safeStorage uses Chromium OSCrypt sync v10. Its Windows
    // layout is v10 | 12-byte nonce | AES-256-GCM ciphertext | 16-byte tag;
    // Local State's os_crypt.encrypted_key is DPAPI-prefixed, current-user protected.
    // Unknown formats fail closed. No credential values are projected or persisted.
    internal static byte[] DecryptDesktopCatalog(byte[] envelope, byte[] localState)
    {
        using var outer = JsonDocument.Parse(envelope);
        if (outer.RootElement.GetProperty("version").GetInt32() != 1) throw new InvalidDataException();
        var cipher = Convert.FromBase64String(outer.RootElement.GetProperty("encryptedCatalog").GetString()!);
        if (cipher.Length < 31 || !cipher.AsSpan(0, 3).SequenceEqual("v10"u8)) throw new InvalidDataException();
        using var state = JsonDocument.Parse(localState);
        var wrapped = Convert.FromBase64String(state.RootElement.GetProperty("os_crypt").GetProperty("encrypted_key").GetString()!);
        if (wrapped.Length < 6 || !wrapped.AsSpan(0, 5).SequenceEqual("DPAPI"u8)) throw new InvalidDataException();
        var key = ProtectedData.Unprotect(wrapped.AsSpan(5).ToArray(), null, DataProtectionScope.CurrentUser);
        try
        {
            if (key.Length != 32) throw new InvalidDataException();
            var plain = new byte[cipher.Length - 31];
            try
            {
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(cipher.AsSpan(3, 12), cipher.AsSpan(15, plain.Length), cipher.AsSpan(15 + plain.Length, 16), plain);
                return plain;
            }
            catch { CryptographicOperations.ZeroMemory(plain); throw; }
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(wrapped); }
    }

    internal static byte[] ReadBoundedFile(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > T3SshTransport.MaximumOutputBytes) throw new InvalidDataException();
        using var buffer = new MemoryStream();
        var block = new byte[16384];
        while (buffer.Length <= T3SshTransport.MaximumOutputBytes)
        {
            var read = file.Read(block, 0, Math.Min(block.Length, T3SshTransport.MaximumOutputBytes + 1 - (int)buffer.Length));
            if (read == 0) break;
            buffer.Write(block, 0, read);
        }
        if (buffer.Length > T3SshTransport.MaximumOutputBytes) throw new InvalidDataException();
        return buffer.ToArray();
    }

    private static string? Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

}
