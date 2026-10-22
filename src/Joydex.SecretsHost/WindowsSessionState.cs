using System.Diagnostics;
using System.Runtime.InteropServices;
using Joydex.Secrets;

namespace Joydex.SecretsHost;

/// <summary>Reads consent presentation availability; an unknown state cannot present a prompt.</summary>
internal static class WindowsSessionState
{
    private const int WtsSessionInfoEx = 25;
    private const int WtsInfoExLevelOne = 1;
    private const int WtsActive = 0;
    private const int WtsSessionStateUnlock = 1;

    public static SecretsConsentAvailability ReadAvailability()
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
                return SecretsConsentAvailability.Unknown;
            }

            // WTSINFOEX aligns its level-one union to 8 bytes in the win-x64 package.
            var unionOffset = IntPtr.Size == 8 ? 8 : 4;
            var connectionState = Marshal.ReadInt32(buffer, unionOffset + 4);
            var sessionFlags = Marshal.ReadInt32(buffer, unionOffset + 8);
            return connectionState != WtsActive ? SecretsConsentAvailability.Disconnected
                : sessionFlags == WtsSessionStateUnlock ? SecretsConsentAvailability.Available
                : sessionFlags == 0 ? SecretsConsentAvailability.Locked
                : SecretsConsentAvailability.Unknown;
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
