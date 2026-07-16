using System.Diagnostics;
using System.Runtime.InteropServices;
using VirpilCodexPad.Core.Config;

namespace VirpilCodexPad.Windows.Actions;

public sealed record ForegroundCheck(bool Allowed, string ProcessName, string Reason);

public interface IForegroundProcessGuard
{
    ForegroundCheck Check(SafetyOptions safety, bool actionMayBringCodexForward);
}

public sealed partial class ForegroundProcessGuard : IForegroundProcessGuard
{
    public ForegroundCheck Check(SafetyOptions safety, bool actionMayBringCodexForward)
    {
        ArgumentNullException.ThrowIfNull(safety);

        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return new ForegroundCheck(false, string.Empty, "Windows did not report a foreground window.");
        }

        _ = GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return new ForegroundCheck(false, string.Empty, "The foreground process could not be identified.");
        }

        string processName;
        try
        {
            processName = Process.GetProcessById((int)processId).ProcessName;
        }
        catch (ArgumentException)
        {
            return new ForegroundCheck(false, string.Empty, "The foreground process exited before it could be checked.");
        }

        return Evaluate(safety, processName, actionMayBringCodexForward);
    }

    public static ForegroundCheck Evaluate(
        SafetyOptions safety,
        string processName,
        bool actionMayBringCodexForward)
    {
        ArgumentNullException.ThrowIfNull(safety);
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);

        if (ContainsProcessName(safety.SimulatorProcessNames, processName))
        {
            return new ForegroundCheck(false, processName, $"Simulator '{processName}' is in the foreground.");
        }

        var codexIsForeground = ContainsProcessName(safety.CodexProcessNames, processName);
        if (safety.RequireCodexForeground && !codexIsForeground && !actionMayBringCodexForward)
        {
            return new ForegroundCheck(false, processName, $"Foreground process '{processName}' is not ChatGPT/Codex.");
        }

        return new ForegroundCheck(true, processName, codexIsForeground
            ? "ChatGPT/Codex is in the foreground."
            : "The explicit deep-link action may bring ChatGPT/Codex forward.");
    }

    private static bool ContainsProcessName(IEnumerable<string> configuredNames, string processName) =>
        configuredNames.Any(configured => string.Equals(
            Path.GetFileNameWithoutExtension(configured.Trim()),
            processName,
            StringComparison.OrdinalIgnoreCase));

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
