using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Joydex.SecretsHost;

/// <summary>Reads the current interactive-session lock flag and fails closed.</summary>
internal static class WindowsSessionState
{
    private const int WtsSessionInfoEx = 25;
    private const int WtsInfoExLevelOne = 1;
    private const int WtsActive = 0;
    private const int WtsSessionStateUnlock = 1;

    public static bool IsLocked()
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!WTSQuerySessionInformation(
                    IntPtr.Zero,
                    Process.GetCurrentProcess().SessionId,
                    WtsSessionInfoEx,
                    out buffer,
                    out var length)
                || buffer == IntPtr.Zero
                || length < 20
                || Marshal.ReadInt32(buffer) != WtsInfoExLevelOne)
            {
                return true;
            }

            // WTSINFOEX aligns its level-one union to 8 bytes in the win-x64 package.
            var unionOffset = IntPtr.Size == 8 ? 8 : 4;
            var connectionState = Marshal.ReadInt32(buffer, unionOffset + 4);
            var sessionFlags = Marshal.ReadInt32(buffer, unionOffset + 8);
            return connectionState != WtsActive || sessionFlags != WtsSessionStateUnlock;
        }
        finally
        {
            if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
        }
    }

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr server,
        int sessionId,
        int infoClass,
        out IntPtr buffer,
        out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
