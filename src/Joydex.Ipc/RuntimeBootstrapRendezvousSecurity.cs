using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Joydex.Ipc;

internal sealed record RuntimeBootstrapImageIdentity(
    string CanonicalPath,
    uint VolumeSerialNumber,
    ulong FileIndex)
{
    public string CanonicalDirectory => Path.GetDirectoryName(CanonicalPath)
        ?? throw new InvalidOperationException("The frozen executable path has no parent directory.");
}

internal interface IRuntimeBootstrapImageVerifier
{
    RuntimeBootstrapImageIdentity CaptureFrozenImage(string path);

    void VerifyCurrentProcess(RuntimeBootstrapImageIdentity expectedImage);

    void VerifyPeerProcess(
        Process process,
        RuntimeIpcPeer peer,
        RuntimeBootstrapImageIdentity expectedImage,
        string peerName);
}

internal sealed partial class WindowsRuntimeBootstrapImageVerifier : IRuntimeBootstrapImageVerifier
{
    private const uint TokenQuery = 0x0008;
    private const uint FileNameNormalized = 0;
    private const uint VolumeNameDos = 0;

    public static WindowsRuntimeBootstrapImageVerifier Instance { get; } = new();

    private WindowsRuntimeBootstrapImageVerifier()
    {
    }

    public RuntimeBootstrapImageIdentity CaptureFrozenImage(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path.Trim());
        using var handle = File.OpenHandle(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return CaptureOpenedImage(handle);
    }

    public void VerifyCurrentProcess(RuntimeBootstrapImageIdentity expectedImage)
    {
        ArgumentNullException.ThrowIfNull(expectedImage);
        using var process = Process.GetCurrentProcess();
        if (IsElevated(process.SafeHandle))
        {
            throw new RuntimeIpcAuthenticationException(
                "An elevated RuntimeHost cannot expose the production bootstrap rendezvous.");
        }

        var peer = new RuntimeIpcPeer(
            process.Id,
            process.StartTime.ToUniversalTime(),
            process.SessionId,
            RuntimeIpcEndpoint.GetCurrentUserSid(),
            IsElevated: false);
        VerifyPeerProcess(process, peer, expectedImage, "RuntimeHost");
    }

    public void VerifyPeerProcess(
        Process process,
        RuntimeIpcPeer peer,
        RuntimeBootstrapImageIdentity expectedImage,
        string peerName)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(expectedImage);
        ArgumentException.ThrowIfNullOrWhiteSpace(peerName);

        if (process.Id != peer.ProcessId
            || process.StartTime.ToUniversalTime().Ticks != peer.ProcessStartTimeUtc.UtcTicks
            || process.SessionId != peer.SessionId)
        {
            throw new RuntimeIpcAuthenticationException(
                $"The {peerName} process identity changed during bootstrap authentication.");
        }

        // The opened process supplies the stable PID/start/session and its executable path. The
        // file identity comparison then checks the current entry at that path against the entry
        // frozen by this rendezvous. It is cooperative application identity, not mapped-byte
        // attestation against hostile code already running as the same user.
        var imagePath = QueryImagePath(process.SafeHandle, peerName);
        RuntimeBootstrapImageIdentity currentDeploymentEntry;
        try
        {
            currentDeploymentEntry = CaptureFrozenImage(imagePath);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or Win32Exception)
        {
            throw new RuntimeIpcAuthenticationException(
                $"The {peerName} executable image could not be opened for identity verification.");
        }

        if (!string.Equals(
                currentDeploymentEntry.CanonicalPath,
                expectedImage.CanonicalPath,
                StringComparison.OrdinalIgnoreCase)
            || currentDeploymentEntry.VolumeSerialNumber != expectedImage.VolumeSerialNumber
            || currentDeploymentEntry.FileIndex != expectedImage.FileIndex)
        {
            throw new RuntimeIpcAuthenticationException(
                $"The {peerName} executable path or current deployment file does not match the frozen entry.");
        }
    }

    private static RuntimeBootstrapImageIdentity CaptureOpenedImage(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw Win32Failure("The executable file identity could not be read.");
        }

        var canonicalPath = GetCanonicalPath(handle);
        var fileIndex = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        return new RuntimeBootstrapImageIdentity(
            canonicalPath,
            information.VolumeSerialNumber,
            fileIndex);
    }

    private static string GetCanonicalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        while (capacity <= 32768)
        {
            var builder = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandle(
                handle,
                builder,
                checked((uint)builder.Capacity),
                FileNameNormalized | VolumeNameDos);
            if (length == 0)
            {
                throw Win32Failure("The executable's final path could not be resolved.");
            }
            if (length < builder.Capacity)
            {
                return NormalizeFinalPath(builder.ToString());
            }
            capacity = checked((int)length + 1);
        }

        throw new IOException("The executable's final path exceeds the supported Windows path length.");
    }

    private static string NormalizeFinalPath(string path)
    {
        const string extendedUncPrefix = @"\\?\UNC\";
        const string extendedPrefix = @"\\?\";
        if (path.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            path = @"\\" + path[extendedUncPrefix.Length..];
        }
        else if (path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            path = path[extendedPrefix.Length..];
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string QueryImagePath(SafeProcessHandle processHandle, string peerName)
    {
        var capacity = 512;
        while (capacity <= 32768)
        {
            var builder = new StringBuilder(capacity);
            var length = checked((uint)builder.Capacity);
            if (QueryFullProcessImageName(processHandle, 0, builder, ref length))
            {
                return builder.ToString();
            }
            var error = Marshal.GetLastWin32Error();
            if (error != 122)
            {
                throw new Win32Exception(
                    error,
                    $"The {peerName} executable path could not be read. Win32 error {error}.");
            }
            capacity *= 2;
        }

        throw new IOException($"The {peerName} executable path exceeds the supported Windows path length.");
    }

    private static bool IsElevated(SafeProcessHandle processHandle)
    {
        if (!OpenProcessToken(processHandle, TokenQuery, out var tokenHandle))
        {
            throw Win32Failure("The current RuntimeHost token could not be inspected.");
        }

        using (tokenHandle)
        {
            var elevation = new TokenElevation();
            if (!GetTokenInformation(
                    tokenHandle,
                    TokenInformationClass.TokenElevation,
                    ref elevation,
                    Marshal.SizeOf<TokenElevation>(),
                    out _))
            {
                throw Win32Failure("The current RuntimeHost elevation could not be inspected.");
            }
            return elevation.TokenIsElevated != 0;
        }
    }

    private static Win32Exception Win32Failure(string message)
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{message} Win32 error {error}.");
    }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevation
    {
        public int TokenIsElevated;
    }

    private enum TokenInformationClass
    {
        TokenElevation = 20,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathCharacters,
        uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        StringBuilder executableName,
        ref uint executableNameCharacters);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(
        SafeProcessHandle process,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        TokenInformationClass tokenInformationClass,
        ref TokenElevation tokenInformation,
        int tokenInformationLength,
        out int returnLength);
}
