using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Joydex.WirelessPanel;

/// <summary>
/// Loads and atomically saves wireless-panel settings with a CurrentUser DPAPI password.
/// </summary>
public sealed class WirelessPanelConfigurationStore
{
    /// <summary>The only on-disk schema currently understood by Joydex.</summary>
    public const int CurrentSchemaVersion = 1;

    private const int MaximumDocumentBytes = 64 * 1024;
    private const int MaximumProtectedPasswordCharacters = 32 * 1024;

    private static readonly byte[] DpapiEntropy =
        Encoding.UTF8.GetBytes("Joydex.WirelessPanel.Password.v1");

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    /// <summary>Creates a store at the standard per-user Joydex location.</summary>
    public WirelessPanelConfigurationStore()
        : this(GetDefaultPath())
    {
    }

    /// <summary>Creates a store at an explicit path, primarily for integration and tests.</summary>
    /// <param name="configurationPath">Path to the protected JSON document.</param>
    public WirelessPanelConfigurationStore(string configurationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        ConfigurationPath = Path.GetFullPath(configurationPath);
    }

    /// <summary>Gets the full path this store reads and writes.</summary>
    public string ConfigurationPath { get; }

    /// <summary>
    /// Resolves <c>%LOCALAPPDATA%\Joydex\WirelessPanel\panel.json</c> for the current user.
    /// </summary>
    public static string GetDefaultPath()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException(
                "The current user's LocalApplicationData folder is unavailable.");
        }

        return Path.Combine(localApplicationData, "Joydex", "WirelessPanel", "panel.json");
    }

    /// <summary>
    /// Loads and validates the configuration, or returns <see langword="null"/> when none exists.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The document is malformed, unsupported, too large, or cannot be decrypted by this user.
    /// </exception>
    public WirelessPanelConfiguration? Load()
    {
        if (!File.Exists(ConfigurationPath))
        {
            return null;
        }

        var fileInfo = new FileInfo(ConfigurationPath);
        if (fileInfo.Length is <= 0 or > MaximumDocumentBytes)
        {
            throw new InvalidDataException(
                $"The wireless-panel settings file must contain 1 to {MaximumDocumentBytes} bytes.");
        }

        WirelessPanelConfigurationDocument? document;
        try
        {
            using var stream = new FileStream(
                ConfigurationPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            document = JsonSerializer.Deserialize<WirelessPanelConfigurationDocument>(
                stream,
                JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The wireless-panel settings file is not valid schema-v1 JSON.",
                exception);
        }

        if (document is null)
        {
            throw new InvalidDataException("The wireless-panel settings file was empty.");
        }

        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported wireless-panel settings schema version {document.SchemaVersion}.");
        }

        if (document.Enabled is null)
        {
            throw new InvalidDataException(
                "The wireless-panel settings do not declare whether the panel is enabled.");
        }

        if (string.IsNullOrWhiteSpace(document.ProtectedPassword)
            || document.ProtectedPassword.Length > MaximumProtectedPasswordCharacters)
        {
            throw new InvalidDataException(
                "The wireless-panel settings contain an invalid protected password.");
        }

        byte[] protectedPassword;
        try
        {
            protectedPassword = Convert.FromBase64String(document.ProtectedPassword);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "The wireless-panel protected password is not valid base64.",
                exception);
        }

        byte[]? clearPassword = null;
        try
        {
            try
            {
                clearPassword = ProtectedData.Unprotect(
                    protectedPassword,
                    DpapiEntropy,
                    DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException exception)
            {
                throw new InvalidDataException(
                    "The wireless-panel password cannot be decrypted by the current Windows user.",
                    exception);
            }

            string password;
            try
            {
                password = StrictUtf8.GetString(clearPassword);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException(
                    "The decrypted wireless-panel password is not valid UTF-8.",
                    exception);
            }

            try
            {
                return WirelessPanelConfiguration.Create(
                    document.Endpoint ?? string.Empty,
                    document.Username ?? string.Empty,
                    password,
                    document.Enabled.Value);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    "The wireless-panel settings failed validation.",
                    exception);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedPassword);
            if (clearPassword is not null)
            {
                CryptographicOperations.ZeroMemory(clearPassword);
            }
        }
    }

    /// <summary>
    /// Protects the password with CurrentUser DPAPI and atomically replaces the JSON document.
    /// </summary>
    /// <param name="configuration">A validated configuration to persist.</param>
    public void Save(WirelessPanelConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var directory = Path.GetDirectoryName(ConfigurationPath)
            ?? throw new InvalidOperationException(
                "The wireless-panel settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        TryRestrictDirectory(directory);

        var clearPassword = StrictUtf8.GetBytes(configuration.Password);
        byte[]? protectedPassword = null;
        string? temporaryPath = null;
        try
        {
            protectedPassword = ProtectedData.Protect(
                clearPassword,
                DpapiEntropy,
                DataProtectionScope.CurrentUser);

            var document = new WirelessPanelConfigurationDocument
            {
                SchemaVersion = CurrentSchemaVersion,
                Enabled = configuration.Enabled,
                Endpoint = configuration.Endpoint.AbsoluteUri,
                Username = configuration.Username,
                ProtectedPassword = Convert.ToBase64String(protectedPassword),
            };

            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(ConfigurationPath)}.{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.WriteByte((byte)'\n');
                stream.Flush(flushToDisk: true);
            }

            TryRestrictFile(temporaryPath);
            File.Move(temporaryPath, ConfigurationPath, overwrite: true);
            temporaryPath = null;
            TryRestrictFile(ConfigurationPath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearPassword);
            if (protectedPassword is not null)
            {
                CryptographicOperations.ZeroMemory(protectedPassword);
            }

            if (temporaryPath is not null)
            {
                TryDeleteTemporaryFile(temporaryPath);
            }
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryRestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User;
            if (user is null)
            {
                return;
            }

            var security = new DirectorySecurity();
            security.SetOwner(user);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(
                new FileSystemAccessRule(
                    user,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        catch (Exception exception) when (IsBestEffortAccessFailure(exception))
        {
        }
    }

    private static void TryRestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User;
            if (user is null)
            {
                return;
            }

            var security = new FileSecurity();
            security.SetOwner(user);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(
                new FileSystemAccessRule(
                    user,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        catch (Exception exception) when (IsBestEffortAccessFailure(exception))
        {
        }
    }

    private static bool IsBestEffortAccessFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException
            or NotSupportedException
            or System.Security.SecurityException
            or IdentityNotMappedException;

    private sealed class WirelessPanelConfigurationDocument
    {
        public int SchemaVersion { get; init; }

        public bool? Enabled { get; init; }

        public string? Endpoint { get; init; }

        public string? Username { get; init; }

        public string? ProtectedPassword { get; init; }
    }
}
