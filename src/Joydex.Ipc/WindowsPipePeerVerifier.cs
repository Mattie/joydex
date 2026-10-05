using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Joydex.Ipc;

internal static partial class WindowsPipePeerVerifier
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int ErrorPipeLocal = 229;
    private static int _treatedAsUnelevatedTestProcessId;

    internal static void TreatCurrentProcessAsUnelevatedForTests() =>
        Volatile.Write(ref _treatedAsUnelevatedTestProcessId, Environment.ProcessId);

    public static RuntimeIpcPeer VerifyClient(NamedPipeServerStream pipe, int expectedSessionId)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        VerifyLocalClient(pipe);
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId))
        {
            throw Win32Failure("The runtime could not identify the named-pipe client.");
        }
        return VerifyProcess(checked((int)processId), expectedSessionId);
    }

    public static RuntimeIpcPeer VerifyClient(
        NamedPipeServerStream pipe,
        int expectedSessionId,
        int expectedProcessId,
        long expectedStartTimeUtcTicks)
    {
        var peer = VerifyClient(pipe, expectedSessionId);
        VerifyExactProcess(peer, expectedProcessId, expectedStartTimeUtcTicks);
        return peer;
    }

    public static RuntimeIpcPeer VerifyServer(NamedPipeClientStream pipe, int expectedSessionId)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId))
        {
            throw Win32Failure("The runtime client could not identify the named-pipe server.");
        }
        return VerifyProcess(checked((int)processId), expectedSessionId);
    }

    public static RuntimeIpcPeer VerifyServer(
        NamedPipeClientStream pipe,
        int expectedSessionId,
        int expectedProcessId,
        long expectedStartTimeUtcTicks)
    {
        var peer = VerifyServer(pipe, expectedSessionId);
        VerifyExactProcess(peer, expectedProcessId, expectedStartTimeUtcTicks);
        return peer;
    }

    private static void VerifyExactProcess(
        RuntimeIpcPeer peer,
        int expectedProcessId,
        long expectedStartTimeUtcTicks)
    {
        if (peer.ProcessId != expectedProcessId
            || peer.ProcessStartTimeUtc.UtcTicks != expectedStartTimeUtcTicks)
        {
            throw new RuntimeIpcAuthenticationException(
                "The named-pipe peer is not the expected process generation.");
        }
    }

    private static RuntimeIpcPeer VerifyProcess(int processId, int expectedSessionId)
    {
        if (processId <= 0)
        {
            throw new RuntimeIpcAuthenticationException("The named-pipe peer did not have a local process identity.");
        }

        using var process = Process.GetProcessById(processId);
        if (process.SessionId != expectedSessionId)
        {
            throw new RuntimeIpcAuthenticationException("The named-pipe peer belongs to another Windows session.");
        }

        using var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle.IsInvalid)
        {
            throw Win32Failure("The runtime could not inspect the named-pipe peer process.");
        }
        if (!OpenProcessToken(processHandle, TokenQuery, out var tokenHandle))
        {
            throw Win32Failure("The runtime could not inspect the named-pipe peer token.");
        }

        using (tokenHandle)
        using (var identity = new WindowsIdentity(tokenHandle.DangerousGetHandle()))
        {
            var peerSid = identity.User?.Value
                ?? throw new RuntimeIpcAuthenticationException("The named-pipe peer has no Windows user identity.");
            if (!string.Equals(peerSid, RuntimeIpcEndpoint.GetCurrentUserSid(), StringComparison.Ordinal))
            {
                throw new RuntimeIpcAuthenticationException("The named-pipe peer belongs to another Windows user.");
            }
            if (IsElevated(tokenHandle)
                && processId != Volatile.Read(ref _treatedAsUnelevatedTestProcessId))
            {
                throw new RuntimeIpcAuthenticationException("Elevated named-pipe peers are not accepted.");
            }

            return new RuntimeIpcPeer(
                processId,
                process.StartTime.ToUniversalTime(),
                process.SessionId,
                peerSid,
                IsElevated: false);
        }
    }

    private static void VerifyLocalClient(NamedPipeServerStream pipe)
    {
        const int maximumComputerNameCharacters = 256;
        var computerName = new StringBuilder(maximumComputerNameCharacters);
        if (!GetNamedPipeClientComputerName(
                pipe.SafePipeHandle,
                computerName,
                maximumComputerNameCharacters * sizeof(char)))
        {
            if (Marshal.GetLastWin32Error() == ErrorPipeLocal)
            {
                return;
            }
            throw Win32Failure("The runtime could not identify the named-pipe client computer.");
        }

        var normalized = computerName.ToString().Trim().TrimStart('\\');
        if (!string.Equals(normalized, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(normalized, ".", StringComparison.Ordinal)
            && !string.Equals(normalized, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new RuntimeIpcAuthenticationException(
                $"Remote named-pipe clients are not accepted (reported computer '{normalized}').");
        }
    }

    private static bool IsElevated(SafeAccessTokenHandle tokenHandle)
    {
        var elevation = new TokenElevation();
        if (!GetTokenInformation(
                tokenHandle,
                TokenInformationClass.TokenElevation,
                ref elevation,
                Marshal.SizeOf<TokenElevation>(),
                out _))
        {
            throw Win32Failure("The runtime could not inspect the named-pipe peer elevation.");
        }
        return elevation.TokenIsElevated != 0;
    }

    private static Win32Exception Win32Failure(string message)
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{message} Win32 error {error}.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevation
    {
        public int TokenIsElevated;
    }

    private enum TokenInformationClass
    {
        TokenUser = 1,
        TokenElevation = 20,
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(
        SafePipeHandle pipe,
        out uint serverProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientComputerName(
        SafePipeHandle pipe,
        StringBuilder clientComputerName,
        int clientComputerNameLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(
        SafeProcessHandle processHandle,
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
