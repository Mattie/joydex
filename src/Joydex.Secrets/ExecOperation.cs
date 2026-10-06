using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace Joydex.Secrets;

public enum SecretOutputDisclosure
{
    None,
    Summary,
    Passthrough,
}

public enum SecretsExecutionLifetime { Attached, Detached }

/// <summary>Metadata-only proposal for a child process that may receive approved secrets.</summary>
public sealed record ExecOperationProposal(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> SecretEnvironment,
    IReadOnlyList<string> FingerprintInputs,
    SecretOutputDisclosure OutputDisclosure,
    long OperationGeneration = 1,
    SecretsExecutionLifetime Lifetime = SecretsExecutionLifetime.Attached,
    int? DetachedTimeoutSeconds = null,
    string? TaskId = null);

/// <summary>A validated operation frozen to exact paths, arguments, mappings and content hashes.</summary>
public sealed record ResolvedExecOperation(
    SecretsOperationIdentity Identity,
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string WorkingDirectoryIdentity,
    IReadOnlyDictionary<string, string> SecretEnvironment,
    IReadOnlyDictionary<string, string> Fingerprints,
    SecretOutputDisclosure OutputDisclosure,
    SecretsExecutionLifetime Lifetime = SecretsExecutionLifetime.Attached,
    int? DetachedTimeoutSeconds = null,
    string? TaskId = null);

/// <summary>Validates an inline exec proposal and derives its broker-owned operation identity.</summary>
public static class ExecOperationCanonicalizer
{
    public static ResolvedExecOperation Resolve(
        SecretsProjectIdentity project,
        IReadOnlyCollection<string> configuredAliases,
        ExecOperationProposal proposal,
        long operationGeneration = 1)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(configuredAliases);
        ArgumentNullException.ThrowIfNull(proposal);
        if (proposal.Arguments.Count > 128
            || proposal.Arguments.Any(argument => argument.Length > 4096 || argument.IndexOf('\0') >= 0)
            || proposal.SecretEnvironment.Count is < 1 or > 64
            || proposal.FingerprintInputs.Count > 64
            || !Enum.IsDefined(proposal.OutputDisclosure)
            || !Enum.IsDefined(proposal.Lifetime)
            || proposal.DetachedTimeoutSeconds is < 1 or > 2147483
            || (proposal.Lifetime == SecretsExecutionLifetime.Attached && (proposal.TaskId is not null || proposal.DetachedTimeoutSeconds is not null))
            || (proposal.Lifetime == SecretsExecutionLifetime.Detached && (!Guid.TryParseExact(proposal.TaskId, "N", out _) || proposal.OutputDisclosure != SecretOutputDisclosure.None))
            || operationGeneration < 1)
        {
            throw new ArgumentException("The exec proposal exceeds its bounded metadata limits.", nameof(proposal));
        }

        var executable = ResolveExistingFile(proposal.Executable, "executable");
        var workingDirectory = ResolveExistingDirectory(proposal.WorkingDirectory, "working directory");
        var projectRoot = ResolveExistingDirectory(project.CanonicalWorktreeRoot, "project worktree");
        if (!IsWithin(workingDirectory, projectRoot))
        {
            throw new ArgumentException("The exec working directory is outside the current project worktree.", nameof(proposal));
        }

        var aliasSet = configuredAliases.ToHashSet(StringComparer.Ordinal);
        var secretEnvironment = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in proposal.SecretEnvironment)
        {
            if (!IsEnvironmentName(mapping.Key)
                || !aliasSet.Contains(mapping.Value)
                || !secretEnvironment.TryAdd(mapping.Key, mapping.Value))
            {
                throw new ArgumentException("The exec secret-to-environment mapping is invalid.", nameof(proposal));
            }
        }

        var fingerprints = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [executable] = HashFile(executable),
        };
        foreach (var input in proposal.FingerprintInputs)
        {
            var path = ResolveExistingFile(input, "fingerprint input");
            if (!IsWithin(path, projectRoot))
            {
                throw new ArgumentException("A fingerprint input is outside the current project worktree.", nameof(proposal));
            }
            if (!fingerprints.TryAdd(path, HashFile(path)))
            {
                throw new ArgumentException("The exec proposal repeats a fingerprint input.", nameof(proposal));
            }
        }

        var arguments = proposal.Arguments.ToArray();
        var workingDirectoryIdentity = WindowsDirectoryHandle.GetIdentity(workingDirectory);
        var digest = ComputeDigest(
            executable,
            arguments,
            workingDirectory,
            workingDirectoryIdentity,
            secretEnvironment,
            fingerprints,
            proposal.OutputDisclosure, proposal.Lifetime, proposal.DetachedTimeoutSeconds);
        return new ResolvedExecOperation(
            new(SecretDeliveryMode.ExecInject, digest, operationGeneration, proposal.Lifetime),
            executable,
            Array.AsReadOnly(arguments),
            workingDirectory,
            workingDirectoryIdentity,
            secretEnvironment,
            fingerprints,
            proposal.OutputDisclosure, proposal.Lifetime, proposal.DetachedTimeoutSeconds, proposal.TaskId);
    }

    /// <summary>
    /// Rechecks every fingerprint and holds read handles that prevent writes or replacement until
    /// process creation has crossed its committed boundary.
    /// </summary>
    public static ExecOperationFileLease AcquireCurrentFiles(ResolvedExecOperation operation) =>
        ExecOperationFileLease.Acquire(operation);

    private static string ComputeDigest(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string workingDirectoryIdentity,
        IReadOnlyDictionary<string, string> secretEnvironment,
        IReadOnlyDictionary<string, string> fingerprints,
        SecretOutputDisclosure disclosure,
        SecretsExecutionLifetime lifetime,
        int? detachedTimeout)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("executable", NormalizePath(executable));
            writer.WriteStartArray("arguments");
            foreach (var argument in arguments) writer.WriteStringValue(argument);
            writer.WriteEndArray();
            writer.WriteString("workingDirectory", NormalizePath(workingDirectory));
            writer.WriteString("workingDirectoryIdentity", workingDirectoryIdentity);
            writer.WriteStartObject("secretEnvironment");
            foreach (var mapping in secretEnvironment.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteString(mapping.Key, mapping.Value);
            }
            writer.WriteEndObject();
            writer.WriteStartObject("fingerprints");
            foreach (var fingerprint in fingerprints.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                writer.WriteString(NormalizePath(fingerprint.Key), fingerprint.Value);
            }
            writer.WriteEndObject();
            writer.WriteString("outputDisclosure", disclosure.ToString());
            if (lifetime == SecretsExecutionLifetime.Detached)
            {
                writer.WriteString("executionLifetime", "detached-v1");
                if (detachedTimeout is { } seconds) writer.WriteNumber("detachedTimeoutSeconds", seconds);
                else writer.WriteNull("detachedTimeoutSeconds");
            }
            writer.WriteString("childEnvironment", "caller-inherited-v1");
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(
            stream.GetBuffer().AsSpan(0, checked((int)stream.Length)))).ToLowerInvariant();
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ResolveExistingFile(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException($"The exec {label} is required.");
        }
        var fullPath = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(fullPath) || !File.Exists(fullPath))
        {
            throw new ArgumentException($"The exec {label} does not exist.");
        }
        RejectReparsePoints(fullPath, label, isDirectory: false);
        return fullPath;
    }

    private static string ResolveExistingDirectory(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException($"The exec {label} is required.");
        }
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Path.IsPathFullyQualified(fullPath) || !Directory.Exists(fullPath))
        {
            throw new ArgumentException($"The exec {label} does not exist.");
        }
        RejectReparsePoints(fullPath, label, isDirectory: true);
        return fullPath;
    }

    internal static void RejectReparsePoints(string path, string label, bool isDirectory)
    {
        if (!isDirectory && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArgumentException($"The exec {label} cannot be a reparse point.");
        }
        var directory = isDirectory ? path : Path.GetDirectoryName(path)!;
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException(
                    $"The exec {label} cannot have a reparse point in its path.");
            }
        }
    }

    private static bool IsWithin(string candidate, string root)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative == "."
            || (!Path.IsPathFullyQualified(relative)
                && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant();

    private static bool IsEnvironmentName(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && !char.IsAsciiDigit(value[0])
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
}

/// <summary>Open fingerprint handles that keep approved executable inputs stable through launch.</summary>
public sealed class ExecOperationFileLease : IDisposable
{
    private List<FileStream>? _streams;
    private List<SafeFileHandle>? _directoryHandles;

    private ExecOperationFileLease(
        List<FileStream> streams,
        List<SafeFileHandle> directoryHandles)
    {
        _streams = streams;
        _directoryHandles = directoryHandles;
    }

    internal static ExecOperationFileLease Acquire(ResolvedExecOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!Directory.Exists(operation.WorkingDirectory))
        {
            throw new InvalidOperationException("The approved working directory is unavailable.");
        }

        var streams = new List<FileStream>();
        var directoryHandles = new List<SafeFileHandle>();
        try
        {
            ExecOperationCanonicalizer.RejectReparsePoints(
                operation.WorkingDirectory,
                "working directory",
                isDirectory: true);
            var workingDirectoryHandle = WindowsDirectoryHandle.OpenStable(operation.WorkingDirectory);
            directoryHandles.Add(workingDirectoryHandle);
            if (!string.Equals(
                    WindowsDirectoryHandle.GetIdentity(workingDirectoryHandle),
                    operation.WorkingDirectoryIdentity,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The approved working directory changed before launch.");
            }

            foreach (var fingerprint in operation.Fingerprints)
            {
                ExecOperationCanonicalizer.RejectReparsePoints(
                    fingerprint.Key,
                    "fingerprint input",
                    isDirectory: false);
                var stream = new FileStream(
                    fingerprint.Key,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.SequentialScan);
                streams.Add(stream);
                var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (!string.Equals(actual, fingerprint.Value, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("An approved executable input changed before launch.");
                }
            }
            ExecOperationCanonicalizer.RejectReparsePoints(
                operation.WorkingDirectory,
                "working directory",
                isDirectory: true);
            return new ExecOperationFileLease(streams, directoryHandles);
        }
        catch
        {
            foreach (var stream in streams) stream.Dispose();
            foreach (var handle in directoryHandles) handle.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_streams is null) return;
        foreach (var stream in _streams) stream.Dispose();
        _streams = null;
        foreach (var handle in _directoryHandles!) handle.Dispose();
        _directoryHandles = null;
    }
}

internal static class WindowsDirectoryHandle
{
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileReadAttributes = 0x0080;

    public static string GetIdentity(string path)
    {
        using var handle = Open(path, FileShare.Read | FileShare.Write | FileShare.Delete);
        return GetIdentity(handle);
    }

    public static SafeFileHandle OpenStable(string path) =>
        Open(path, FileShare.Read | FileShare.Write);

    public static string GetIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
        {
            throw new IOException(
                "The working directory identity could not be read.",
                Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        }
        return $"{info.VolumeSerialNumber:x8}:{info.FileIndexHigh:x8}{info.FileIndexLow:x8}";
    }

    private static SafeFileHandle Open(string path, FileShare share)
    {
        var handle = CreateFile(
            path,
            FileReadAttributes,
            share,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException(
                "The working directory could not be held stable for launch.",
                Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        }
        return handle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
