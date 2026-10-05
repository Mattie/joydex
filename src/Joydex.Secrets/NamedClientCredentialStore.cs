using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Joydex.Secrets;

/// <summary>Stores hidden helper credentials under CurrentUser DPAPI with verified user-only ACLs.</summary>
public sealed class NamedClientCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Joydex.Secrets.NamedClient.v1");
    private readonly string _directory;

    public NamedClientCredentialStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    /// <summary>Gets the standard broker-owned credential directory.</summary>
    public static string GetDefaultDirectory()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            throw new InvalidOperationException("LocalApplicationData is unavailable.");
        }
        return Path.Combine(localData, "Joydex", "Secrets", "credentials");
    }

    /// <summary>Protects and atomically saves a 256-bit named-client credential.</summary>
    public void Save(string clientId, ReadOnlySpan<byte> credential)
    {
        var path = CredentialPath(clientId);
        if (credential.Length != 32)
        {
            throw new ArgumentException("A named-client credential must contain 32 bytes.", nameof(credential));
        }
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Named-client credentials require Windows DPAPI.");
        }

        Directory.CreateDirectory(_directory);
        EnsurePrivateDirectory(_directory);
        var clear = credential.ToArray();
        byte[]? protectedBytes = null;
        var temporaryPath = Path.Combine(
            _directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            protectedBytes = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(protectedBytes);
                stream.Flush(flushToDisk: true);
            }
            EnsurePrivateFile(temporaryPath);
            File.Move(temporaryPath, path, overwrite: true);
            EnsurePrivateFile(path);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes);
            try { File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Loads and decrypts a credential for immediate authenticated IPC use.</summary>
    public byte[] Load(string clientId)
    {
        var path = CredentialPath(clientId);
        if (!Directory.Exists(_directory) || !File.Exists(path))
        {
            throw MissingCredential(clientId, path);
        }
        EnsurePrivateDirectory(_directory);
        if (!File.Exists(path))
        {
            throw MissingCredential(clientId, path);
        }
        EnsurePrivateFile(path);
        var protectedBytes = File.ReadAllBytes(path);
        try
        {
            var credential = ProtectedData.Unprotect(
                protectedBytes,
                Entropy,
                DataProtectionScope.CurrentUser);
            if (credential.Length != 32)
            {
                CryptographicOperations.ZeroMemory(credential);
                throw new InvalidDataException("The named-client credential has an invalid size.");
            }
            return credential;
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException(
                "The named-client credential cannot be decrypted by this Windows user.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    /// <summary>Removes a local requester's protected helper credential when it exists.</summary>
    public void Delete(string clientId)
    {
        var path = CredentialPath(clientId);
        if (!File.Exists(path)) return;
        EnsurePrivateDirectory(_directory);
        EnsurePrivateFile(path);
        File.Delete(path);
    }

    private string CredentialPath(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)
            || clientId.Length > 64
            || !clientId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            throw new ArgumentException("The named-client ID is invalid.", nameof(clientId));
        }
        return Path.Combine(_directory, clientId + ".cred");
    }

    private static FileNotFoundException MissingCredential(string clientId, string path) =>
        new(
            $"Joydex has no local request credential for agent '{clientId}'. Run a fresh joydex-secrets exec request to create it.",
            path);

    private static void EnsurePrivateDirectory(string path)
    {
        var user = CurrentUser();
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
        var applied = new DirectoryInfo(path).GetAccessControl();
        if (!applied.AreAccessRulesProtected || !HasOnlyCurrentUserRules(applied, user))
        {
            throw new UnauthorizedAccessException("The credential directory ACL could not be verified.");
        }
    }

    private static void EnsurePrivateFile(string path)
    {
        var user = CurrentUser();
        var security = new FileSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
        var applied = new FileInfo(path).GetAccessControl();
        if (!applied.AreAccessRulesProtected || !HasOnlyCurrentUserRules(applied, user))
        {
            throw new UnauthorizedAccessException("The credential file ACL could not be verified.");
        }
    }

    private static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User
            ?? throw new UnauthorizedAccessException("The current Windows SID is unavailable.");
    }

    private static bool HasOnlyCurrentUserRules(FileSystemSecurity security, SecurityIdentifier user)
    {
        var rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        return rules.Length > 0
            && rules.All(rule => Equals(rule.IdentityReference, user)
                && rule.AccessControlType == AccessControlType.Allow);
    }
}
