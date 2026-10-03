using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Joydex.Contracts;

namespace Joydex.Ipc;

/// <summary>Identifies one local runtime pipe and the data root that runtime owns.</summary>
public sealed class RuntimeIpcEndpoint
{
    private const string IdentityVersion = "v1";
    private const string DataRootIdentityVersion = "v2";
    private const string DefaultConfigurationFileName = "config.json";

    private RuntimeIpcEndpoint(
        string pipeName,
        string dataRootId,
        RuntimeInstanceKind instanceKind,
        int sessionId)
    {
        PipeName = pipeName;
        DataRootId = dataRootId;
        InstanceKind = instanceKind;
        SessionId = sessionId;
    }

    public string PipeName { get; }

    public string DataRootId { get; }

    public RuntimeInstanceKind InstanceKind { get; }

    public int SessionId { get; }

    /// <summary>Creates the stable current-user/session endpoint used by the production runtime.</summary>
    public static RuntimeIpcEndpoint CreateProduction(string dataRoot) =>
        CreateProduction(dataRoot, GetDefaultConfigurationPath(dataRoot));

    /// <summary>Creates the production endpoint for one exact data-root/configuration selection.</summary>
    public static RuntimeIpcEndpoint CreateProduction(string dataRoot, string configurationPath)
    {
        var sessionId = Process.GetCurrentProcess().SessionId;
        var sid = GetCurrentUserSid();
        var userKey = Hash($"{IdentityVersion}\0{sid}")[..16];
        return new RuntimeIpcEndpoint(
            $"joydex-runtime-{IdentityVersion}-{userKey}-{sessionId}",
            GetDataRootId(dataRoot, configurationPath),
            RuntimeInstanceKind.Production,
            sessionId);
    }

    /// <summary>Creates an explicitly named endpoint for isolated tests and synthetic runtimes.</summary>
    public static RuntimeIpcEndpoint CreateSynthetic(string dataRoot, string pipeName) =>
        CreateSynthetic(dataRoot, GetDefaultConfigurationPath(dataRoot), pipeName);

    /// <summary>Creates a named synthetic endpoint for one exact data-root/configuration selection.</summary>
    public static RuntimeIpcEndpoint CreateSynthetic(
        string dataRoot,
        string configurationPath,
        string pipeName)
    {
        ValidatePipeName(pipeName);
        return new RuntimeIpcEndpoint(
            pipeName,
            GetDataRootId(dataRoot, configurationPath),
            RuntimeInstanceKind.Synthetic,
            Process.GetCurrentProcess().SessionId);
    }

    internal static string GetCurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return identity.User?.Value
            ?? throw new InvalidOperationException("The current Windows user has no security identifier.");
    }

    internal static string GetDataRootId(string dataRoot, string configurationPath)
    {
        var normalizedDataRoot = NormalizePath(dataRoot, nameof(dataRoot));
        var normalizedConfigurationPath = NormalizePath(configurationPath, nameof(configurationPath));
        var identity = string.Join(
            '\0',
            DataRootIdentityVersion,
            normalizedDataRoot,
            normalizedConfigurationPath);
        return $"root-{DataRootIdentityVersion}-{Hash(identity)}";
    }

    internal static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string GetDefaultConfigurationPath(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        return Path.Combine(dataRoot.Trim(), DefaultConfigurationFileName);
    }

    private static string NormalizePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim())).ToUpperInvariant();
    }

    private static void ValidatePipeName(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (pipeName.Length > 200
            || !string.Equals(pipeName, pipeName.Trim(), StringComparison.Ordinal)
            || pipeName.IndexOfAny(['\\', '/', ':']) >= 0)
        {
            throw new ArgumentException("The synthetic pipe name is invalid.", nameof(pipeName));
        }
    }
}
