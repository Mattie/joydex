using System.Diagnostics;
using System.Runtime.InteropServices;
using Joydex.Core.Config;
using Joydex.Core.Mapping;
using Joydex.Core.TaskAlerts;
using Microsoft.Win32;

namespace Joydex.Windows.Actions;

public sealed record DesktopActionForeground(IntPtr WindowHandle, int ProcessId, string ExecutablePath,
    string ProcessName, bool IsT3Desktop);

public interface IDesktopActionForegroundResolver
{
    DesktopActionForeground Capture();
    bool IsCurrent(DesktopActionForeground foreground);
}

/// <summary>Proof must come from the current desktop UI's native environment and effective binding context.</summary>
public sealed record T3DesktopActionContext(
    string? EnvironmentId, string? BindingFingerprint, bool EnvironmentVerified, bool BindingsVerified,
    IReadOnlyDictionary<CodexAction, string>? Bindings = null,
    TaskAlertState? ActiveTaskState = null, string? ActiveThreadId = null, string? ActiveRunId = null,
    string? UnavailableReason = null, IntPtr WindowHandle = default, int ProcessId = 0);

public interface IT3DesktopActionContextProvider
{
    T3DesktopActionContext GetContext(DesktopActionForeground foreground);
}

/// <summary>Stock Desktop currently exposes no verified external active-environment/binding proof.</summary>
public sealed class StockT3DesktopActionContextProvider : IT3DesktopActionContextProvider
{
    public const string NotVerified = "T3 shortcuts are not verified: the stock desktop client does not expose its active environment and effective binding context. Shortcut setup alone cannot enable dispatch.";
    public T3DesktopActionContext GetContext(DesktopActionForeground foreground) =>
        new(null, null, false, false, UnavailableReason: NotVerified);
}

public sealed record DesktopActionCapability(bool Available, string Reason);

/// <summary>Routes only verified common task actions to the exact installed T3 foreground executable.</summary>
public sealed class DesktopActionRouter
{
    private readonly SafetyOptions _safety;
    private readonly Action<string> _log;
    private readonly IT3DesktopActionContextProvider _context;
    private readonly IDesktopActionForegroundResolver _foreground;
    private readonly IInputSender _input;

    public DesktopActionRouter(SafetyOptions safety, Action<string> log,
        IT3DesktopActionContextProvider? contextProvider = null,
        IDesktopActionForegroundResolver? foregroundResolver = null, IInputSender? inputSender = null)
    {
        _safety = safety ?? throw new ArgumentNullException(nameof(safety));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _context = contextProvider ?? new StockT3DesktopActionContextProvider();
        _foreground = foregroundResolver ?? new InstalledT3ForegroundResolver();
        _input = inputSender ?? new WindowsInputSender();
    }

    public DesktopActionCapability GetCapability(CodexAction action)
    {
        if (!IsCommonAction(action)) return new(false, "This action is specific to Codex and is unavailable in T3 Desktop.");
        var foreground = _foreground.Capture();
        if (!foreground.IsT3Desktop) return new(false, "The installed T3 Desktop app must be in the foreground.");
        var (_, reason) = Resolve(action, foreground, _context.GetContext(foreground));
        return new(reason is null, reason ?? "The current T3 environment and effective binding are verified.");
    }

    /// <returns>Null only when the existing Codex executor should handle the action.</returns>
    public async Task<ActionExecutionResult?> TryExecuteAsync(ActionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var foreground = _foreground.Capture();
        if (!foreground.IsT3Desktop) return null;
        string? reason = null;
        T3DesktopActionContext? context = null;
        KeySequence? sequence = null;
        if (_safety.SimulatorProcessNames.Any(name => string.Equals(Path.GetFileNameWithoutExtension(name.Trim()),
                foreground.ProcessName, StringComparison.OrdinalIgnoreCase)))
            reason = "A configured simulator is in the foreground.";
        else if (!IsCommonAction(request.Action))
            reason = "This action is specific to Codex and is unavailable in T3 Desktop.";
        else
        {
            context = _context.GetContext(foreground);
            (sequence, reason) = Resolve(request.Action, foreground, context);
        }
        if (_safety.DryRun)
            return Report(ActionExecutionResult.Simulated($"DRY RUN {CodexActionCatalog.GetId(request.Action)}; T3 Desktop; "
                + (reason is null ? "verified binding=" + sequence!.NormalizedText : "LIVE MODE WOULD BLOCK: " + reason)));
        if (reason is not null) return Block(request.Action, reason);

        // Multi-chord shortcuts yield between chords. Recheck the exact foreground and
        // native environment/binding proof before every sequence sent to Windows.
        foreach (var chord in sequence!.Chords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_foreground.IsCurrent(foreground)) return Block(request.Action, "The foreground window changed before dispatch.");
            var current = _context.GetContext(foreground);
            var (currentSequence, currentReason) = Resolve(request.Action, foreground, current);
            if (currentReason is not null || current.EnvironmentId != context!.EnvironmentId
                || current.BindingFingerprint != context.BindingFingerprint
                || current.ActiveThreadId != context.ActiveThreadId || current.ActiveRunId != context.ActiveRunId
                || currentSequence!.NormalizedText != sequence.NormalizedText)
                return Block(request.Action, currentReason ?? "The active T3 environment, task or binding changed before dispatch.");
            if (!_foreground.IsCurrent(foreground)) return Block(request.Action, "The foreground window changed before dispatch.");
            await _input.SendSequenceAsync(new([chord], chord.NormalizedText), cancellationToken).ConfigureAwait(false);
        }
        return Report(ActionExecutionResult.Success($"EXECUTED {CodexActionCatalog.GetId(request.Action)}; T3 Desktop; binding={sequence.NormalizedText}"));
    }

    private static (KeySequence? Sequence, string? Reason) Resolve(CodexAction action,
        DesktopActionForeground foreground, T3DesktopActionContext context)
    {
        if (foreground.WindowHandle == IntPtr.Zero || foreground.ProcessId <= 0)
            return (null, "No valid T3 foreground window was identified.");
        if (!context.EnvironmentVerified || !context.BindingsVerified
            || string.IsNullOrWhiteSpace(context.EnvironmentId) || string.IsNullOrWhiteSpace(context.BindingFingerprint)
            || context.WindowHandle != foreground.WindowHandle || context.ProcessId != foreground.ProcessId)
            return (null, context.UnavailableReason ?? StockT3DesktopActionContextProvider.NotVerified);
        if (action == CodexAction.StopTask && (context.ActiveTaskState != TaskAlertState.Running
                || string.IsNullOrWhiteSpace(context.ActiveThreadId) || string.IsNullOrWhiteSpace(context.ActiveRunId)))
            return (null, "Stop task requires a verified active T3 task and run.");
        if (context.Bindings is null || !context.Bindings.TryGetValue(action, out var binding)
            || !KeySequenceParser.TryParse(binding, false, out var sequence, out _))
            return (null, "No verified effective T3 shortcut is bound to this action. Use shortcut setup, then verify the desktop context.");
        return (sequence, null);
    }

    private static bool IsCommonAction(CodexAction action) => action is CodexAction.NewTask or CodexAction.PreviousTask
        or CodexAction.NextTask or CodexAction.StopTask;
    private ActionExecutionResult Block(CodexAction action, string reason) =>
        Report(ActionExecutionResult.Blocked($"BLOCKED {CodexActionCatalog.GetId(action)}; T3 Desktop; error={reason}"));
    private ActionExecutionResult Report(ActionExecutionResult result) { _log(result.Message); return result; }
}

/// <summary>Uses current-user installation metadata, never browser names or a guessed process name.</summary>
public sealed partial class InstalledT3ForegroundResolver : IDesktopActionForegroundResolver
{
    public DesktopActionForeground Capture()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || !IsWindow(window)) return new(window, 0, "", "", false);
        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0 || processId > int.MaxValue) return new(window, 0, "", "", false);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            var path = process.MainModule?.FileName ?? "";
            var installed = FindInstalledExecutable();
            var isT3 = installed is not null && string.Equals(installed, path, StringComparison.OrdinalIgnoreCase);
            return new(window, (int)processId, path, process.ProcessName, isT3);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new(window, (int)processId, "", "", false);
        }
    }

    public bool IsCurrent(DesktopActionForeground foreground)
    {
        var current = Capture();
        return current.IsT3Desktop && current.WindowHandle == foreground.WindowHandle
            && current.ProcessId == foreground.ProcessId
            && string.Equals(current.ExecutablePath, foreground.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    internal static string? FindInstalledExecutable() => FindInstalledExecutable(out _);

    internal static string? FindInstalledExecutable(out string? failureClass)
    {
        try
        {
            // The current-user protocol command is authoritative when present.
            // An invalid override must never fall through to another installation.
            using var user = Registry.CurrentUser.OpenSubKey(@"Software\Classes\t3code\shell\open\command");
            string? mergedCommand = null;
            if (user is null)
            {
                using var merged = Registry.ClassesRoot.OpenSubKey(@"t3code\shell\open\command");
                mergedCommand = merged?.GetValue(null) as string;
            }
            var executable = VerifyProtocolRegistration(user is not null, user?.GetValue(null) as string, mergedCommand,
                File.Exists, path => FileVersionInfo.GetVersionInfo(path).ProductName, out failureClass);
            if (executable is null && failureClass == "LocalCliRegistrationMissing")
            {
                failureClass = ProbeMissingRegistration();
                if (TryParseMissingRegistrationDiagnostic(failureClass, out var userStatus, out var classesStatus, out var currentStatus)
                    && userStatus == 2 && classesStatus == 2 && currentStatus == 2)
                    executable = ResolveRuntimeWhenRegistrationMissing("LocalCliRegistrationMissing", T3RuntimeExecutableResolver.FindCurrentUserExecutable);
                if (executable is not null) failureClass = null;
            }
            return executable;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException
            or ArgumentException or NotSupportedException)
        {
            failureClass = "LocalCliVerificationFailed";
            return null;
        }
    }

    internal static string? ResolveRuntimeWhenRegistrationMissing(string? failureClass, Func<string?> resolveRuntime) =>
        failureClass == "LocalCliRegistrationMissing" ? resolveRuntime() : null;

    // Managed OpenSubKey returns null for several native failures, not only a
    // missing key. Probe just these two fixed paths after that exact failure.
    private static string ProbeMissingRegistration()
    {
        var userStatus = OpenStatus(new IntPtr(unchecked((int)0x80000001)), @"Software\Classes\t3code\shell\open\command");
        var classesStatus = OpenStatus(new IntPtr(unchecked((int)0x80000000)), @"t3code\shell\open\command");
        var currentStatus = RegOpenCurrentUser(0x20019, out var currentUser);
        if (currentStatus == 0)
        {
            try { currentStatus = OpenStatus(currentUser, @"Software\Classes\t3code\shell\open\command"); }
            finally { if (currentUser != IntPtr.Zero) RegCloseKey(currentUser); }
        }
        return "LocalCliRegistrationMissing:hkcu=" + userStatus + ":hkcr=" + classesStatus + ":current=" + currentStatus;
    }

    private static int OpenStatus(IntPtr root, string path)
    {
        var status = RegOpenKeyEx(root, path, 0, 0x20019, out var opened);
        if (status == 0 && opened != IntPtr.Zero) RegCloseKey(opened);
        return status;
    }

    internal static bool TryParseMissingRegistrationDiagnostic(string? value, out int userStatus, out int classesStatus, out int currentStatus)
    {
        userStatus = classesStatus = currentStatus = 0;
        if (value is null || value.Length > 90) return false;
        var parts = value.Split(':');
        return parts.Length == 4 && parts[0] == "LocalCliRegistrationMissing"
            && parts[1].StartsWith("hkcu=", StringComparison.Ordinal) && parts[2].StartsWith("hkcr=", StringComparison.Ordinal)
            && parts[3].StartsWith("current=", StringComparison.Ordinal)
            && int.TryParse(parts[1].AsSpan(5), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out userStatus)
            && int.TryParse(parts[2].AsSpan(5), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out classesStatus)
            && int.TryParse(parts[3].AsSpan(8), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out currentStatus);
    }

    [DllImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", CharSet = CharSet.Unicode)]
    private static extern int RegOpenKeyEx(IntPtr root, string subKey, int options, int desiredAccess, out IntPtr opened);
    [DllImport("advapi32.dll")] private static extern int RegOpenCurrentUser(int desiredAccess, out IntPtr opened);
    [DllImport("advapi32.dll")] private static extern int RegCloseKey(IntPtr key);

    internal static string? VerifyProtocolRegistration(bool userKeyPresent, string? userCommand, string? mergedCommand,
        Func<string, bool> fileExists, Func<string, string?> productName, out string? failureClass)
    {
        failureClass = null;
        try
        {
            var command = userKeyPresent ? userCommand : mergedCommand;
            if (!userKeyPresent && command is null) { failureClass = "LocalCliRegistrationMissing"; return null; }
            var path = ParseRegisteredExecutable(command);
            if (path is null) { failureClass = "LocalCliRegistrationInvalid"; return null; }
            if (!fileExists(path)) { failureClass = "LocalCliExecutableMissing"; return null; }
            var product = productName(path);
            if (product is not null && (product.Equals("T3 Code (Beta)", StringComparison.OrdinalIgnoreCase)
                || product.Equals("T3 Code", StringComparison.OrdinalIgnoreCase))) return path;
            failureClass = "LocalCliProductMismatch";
            return null;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException
            or ArgumentException or NotSupportedException) { failureClass = "LocalCliVerificationFailed"; return null; }
    }

    internal static string? ParseRegisteredExecutable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var command = value.Trim();
        // The verified registration is one quoted executable and one quoted URI
        // placeholder. Ambiguous command lines fail closed without shell parsing.
        if (!command.StartsWith('"')) return null;
        var close = command.IndexOf('"', 1);
        if (close < 0 || command[(close + 1)..].Trim() != "\"%1\"") return null;
        var path = command[1..close];
        try
        {
            return Path.IsPathFullyQualified(path) && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(path) : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    [LibraryImport("user32.dll")] private static partial IntPtr GetForegroundWindow();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsWindow(IntPtr window);
    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
