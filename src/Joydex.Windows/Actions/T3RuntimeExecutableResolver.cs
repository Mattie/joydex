using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Joydex.Windows.Actions;

internal sealed record T3RuntimeProcessIdentity(int ProcessId, int SessionId, string? UserSid,
    DateTimeOffset StartedAt, string ExecutablePath, bool Exited);

/// <summary>Resolves one runtime-recorded PID; never scans by process name or launches a server.</summary>
internal static class T3RuntimeExecutableResolver
{
    internal static string? FindCurrentUserExecutable()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".t3", "userdata", "server-runtime.json");
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[65537];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = file.Read(bytes, length, bytes.Length - length);
                if (read == 0) break;
                length += read;
            }
            if (length == bytes.Length) return null;
            using var descriptor = JsonDocument.Parse(bytes.AsMemory(0, length));
            using var current = WindowsIdentity.GetCurrent();
            var sid = current.User?.Value;
            if (sid is null) return null;
            using var self = Process.GetCurrentProcess();
            return Verify(descriptor.RootElement, self.SessionId, sid, DateTimeOffset.UtcNow,
                pid => Capture(pid, self.SessionId, sid), File.Exists, executable => FileVersionInfo.GetVersionInfo(executable).ProductName);
        }
        catch { return null; }
    }

    internal static string? Verify(JsonElement descriptor, int sessionId, string userSid, DateTimeOffset now,
        Func<int, T3RuntimeProcessIdentity?> inspectProcess, Func<string, bool> fileExists, Func<string, string?> productName)
    {
        try
        {
            if (!descriptor.TryGetProperty("pid", out var pidValue) || !pidValue.TryGetInt32(out var pid) || pid <= 0
                || !descriptor.TryGetProperty("startedAt", out var startedValue) || !startedValue.TryGetDateTimeOffset(out var started)
                || started <= DateTimeOffset.UnixEpoch || started > now.AddSeconds(5)
                || !descriptor.TryGetProperty("origin", out var originValue)
                || !Uri.TryCreate(originValue.GetString(), UriKind.Absolute, out var origin)
                || !T3.T3LocalMetadataReader.IsSupportedLoopbackOrigin(origin)) return null;
            var process = inspectProcess(pid);
            if (process is null || process.Exited || process.ProcessId != pid || process.SessionId != sessionId
                || !string.Equals(process.UserSid, userSid, StringComparison.Ordinal)
                || process.StartedAt <= DateTimeOffset.UnixEpoch || process.StartedAt > started
                || !Path.IsPathFullyQualified(process.ExecutablePath)
                || !Path.GetExtension(process.ExecutablePath).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                || !fileExists(process.ExecutablePath)) return null;
            var product = productName(process.ExecutablePath);
            return product is not null && (product.Equals("T3 Code (Beta)", StringComparison.OrdinalIgnoreCase)
                || product.Equals("T3 Code", StringComparison.OrdinalIgnoreCase)) ? Path.GetFullPath(process.ExecutablePath) : null;
        }
        catch { return null; }
    }

    private static T3RuntimeProcessIdentity? Capture(int pid, int expectedSession, string expectedUser)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited || process.SessionId != expectedSession || !OpenProcessToken(process.SafeHandle, 8, out var token)) return null;
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            {
                if (!string.Equals(identity.User?.Value, expectedUser, StringComparison.Ordinal)) return null;
                return new(pid, process.SessionId, identity.User?.Value, process.StartTime.ToUniversalTime(),
                    process.MainModule?.FileName ?? "", process.HasExited);
            }
        }
        catch { return null; }
    }

    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
