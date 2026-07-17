using System.Diagnostics;
using System.Runtime.InteropServices;
using VirpilCodexPad.Core.Config;

namespace VirpilCodexPad.Windows.Actions;

public sealed record ClipboardDirectoryResult(bool Success, string? DirectoryPath, string? Error)
{
    public static ClipboardDirectoryResult Failure(string error) => new(false, null, error);

    public static ClipboardDirectoryResult Found(string directoryPath) => new(true, directoryPath, null);
}

/// <summary>Waits for Codex to replace the clipboard with a working-directory value.</summary>
public interface IWorkingDirectoryClipboard
{
    uint GetSequenceNumber();

    Task<ClipboardDirectoryResult> WaitForNewDirectoryAsync(
        uint previousSequenceNumber,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal interface IWindowsClipboardNative
{
    uint GetSequenceNumber();

    bool TryReadUnicodeText(out string? text);
}

internal interface IWindowsClipboardTiming
{
    DateTimeOffset UtcNow { get; }

    Task DelayAsync(int milliseconds, CancellationToken cancellationToken);
}

public sealed class WindowsWorkingDirectoryClipboard : IWorkingDirectoryClipboard
{
    private readonly IWindowsClipboardNative _native;
    private readonly IWindowsClipboardTiming _timing;

    public WindowsWorkingDirectoryClipboard()
        : this(new WindowsClipboardNative(), new SystemWindowsClipboardTiming())
    {
    }

    internal WindowsWorkingDirectoryClipboard(
        IWindowsClipboardNative native,
        IWindowsClipboardTiming timing)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _timing = timing ?? throw new ArgumentNullException(nameof(timing));
    }

    public uint GetSequenceNumber() => _native.GetSequenceNumber();

    public async Task<ClipboardDirectoryResult> WaitForNewDirectoryAsync(
        uint previousSequenceNumber,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = _timing.UtcNow + timeout;
        var sawChange = false;
        while (_timing.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentSequence = _native.GetSequenceNumber();
            if (currentSequence != previousSequenceNumber)
            {
                sawChange = true;
                if (_native.TryReadUnicodeText(out var text))
                {
                    return ValidateDirectory(text);
                }
            }

            await _timing.DelayAsync(25, cancellationToken).ConfigureAwait(false);
        }

        return ClipboardDirectoryResult.Failure(
            sawChange
                ? "The changed clipboard value could not be read before the timeout."
                : "Codex did not place a fresh working directory on the clipboard before the timeout.");
    }

    internal static ClipboardDirectoryResult ValidateDirectory(string? clipboardText)
    {
        var candidate = clipboardText?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return ClipboardDirectoryResult.Failure("Codex copied an empty working directory value.");
        }

        try
        {
            if (!Path.IsPathFullyQualified(candidate))
            {
                return ClipboardDirectoryResult.Failure("Codex copied a working directory that is not an absolute path.");
            }

            if (!Directory.Exists(candidate))
            {
                return ClipboardDirectoryResult.Failure("Codex copied a working directory that does not exist.");
            }

            return ClipboardDirectoryResult.Found(Path.GetFullPath(candidate));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ClipboardDirectoryResult.Failure($"Codex copied an invalid working directory path ({exception.Message}).");
        }
    }

}

internal sealed class SystemWindowsClipboardTiming : IWindowsClipboardTiming
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(int milliseconds, CancellationToken cancellationToken) =>
        Task.Delay(milliseconds, cancellationToken);
}

internal sealed partial class WindowsClipboardNative : IWindowsClipboardNative
{
    private const uint UnicodeTextFormat = 13;

    public uint GetSequenceNumber() => GetClipboardSequenceNumber();

    public bool TryReadUnicodeText(out string? text)
    {
        text = null;
        if (!IsClipboardFormatAvailable(UnicodeTextFormat) || !OpenClipboard(IntPtr.Zero))
        {
            return false;
        }

        try
        {
            var handle = GetClipboardData(UnicodeTextFormat);
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                text = Marshal.PtrToStringUni(pointer);
                return true;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(IntPtr newOwner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetClipboardData(uint format);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport("user32.dll")]
    private static partial uint GetClipboardSequenceNumber();

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalLock(IntPtr memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(IntPtr memory);
}

public sealed record WorkingDirectoryLaunchResult(bool Success, string? Error)
{
    public static WorkingDirectoryLaunchResult Launched() => new(true, null);

    public static WorkingDirectoryLaunchResult Failure(string error) => new(false, error);
}

/// <summary>Opens a validated directory in one fixed, named application target.</summary>
public interface IWorkingDirectoryLauncher
{
    string TargetId { get; }

    WorkingDirectoryLaunchResult Launch(string directoryPath);
}

public sealed class WorkingDirectoryLauncherRegistry
{
    private readonly IReadOnlyDictionary<string, IWorkingDirectoryLauncher> _launchers;

    public WorkingDirectoryLauncherRegistry(IEnumerable<IWorkingDirectoryLauncher>? launchers = null)
    {
        _launchers = (launchers ??
            [new VisualStudioCodeWorkingDirectoryLauncher(), new FileExplorerWorkingDirectoryLauncher()])
            .ToDictionary(launcher => launcher.TargetId, StringComparer.OrdinalIgnoreCase);
    }

    public WorkingDirectoryLaunchResult Launch(string targetId, string directoryPath) =>
        _launchers.TryGetValue(targetId, out var launcher)
            ? launcher.Launch(directoryPath)
            : WorkingDirectoryLaunchResult.Failure($"Unknown working-directory target '{targetId}'.");
}

public sealed class VisualStudioCodeWorkingDirectoryLauncher : IWorkingDirectoryLauncher
{
    public string TargetId => OpenWorkingDirectoryOptions.VisualStudioCodeTarget;

    public WorkingDirectoryLaunchResult Launch(string directoryPath)
    {
        var executable = FindVisualStudioCode();
        if (executable is null)
        {
            return WorkingDirectoryLaunchResult.Failure(
                "Visual Studio Code was not found. Install it or select File Explorer in Virpil Codex Pad configuration.");
        }

        try
        {
            var startInfo = WorkingDirectoryProcessStartInfo.CreateVisualStudioCode(executable, directoryPath);
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return WorkingDirectoryLaunchResult.Failure("Visual Studio Code did not start.");
            }

            if (!VisualStudioCodeWindowSurface.TrySurface(TimeSpan.FromSeconds(5)))
            {
                return WorkingDirectoryLaunchResult.Failure(
                    "Visual Studio Code started, but its window could not be surfaced.");
            }

            return WorkingDirectoryLaunchResult.Launched();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return WorkingDirectoryLaunchResult.Failure($"Could not launch Visual Studio Code ({exception.Message}).");
        }
    }

    private static string? FindVisualStudioCode()
    {
        var candidates = new List<string?>
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "Microsoft VS Code",
                "Code.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft VS Code",
                "Code.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft VS Code",
                "Code.exe"),
        };
        return candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate));
    }

}

public sealed class FileExplorerWorkingDirectoryLauncher : IWorkingDirectoryLauncher
{
    public string TargetId => OpenWorkingDirectoryOptions.FileExplorerTarget;

    public WorkingDirectoryLaunchResult Launch(string directoryPath)
    {
        var explorerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "explorer.exe");
        try
        {
            var startInfo = WorkingDirectoryProcessStartInfo.Create(explorerPath, directoryPath);
            Process.Start(startInfo);
            return WorkingDirectoryLaunchResult.Launched();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return WorkingDirectoryLaunchResult.Failure($"Could not launch File Explorer ({exception.Message}).");
        }
    }
}

internal static class WorkingDirectoryProcessStartInfo
{
    public static ProcessStartInfo Create(string executable, string directoryPath)
    {
        var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false };
        startInfo.ArgumentList.Add(directoryPath);
        return startInfo;
    }

    public static ProcessStartInfo CreateVisualStudioCode(string executable, string directoryPath)
    {
        var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false };
        startInfo.ArgumentList.Add("--new-window");
        startInfo.ArgumentList.Add(directoryPath);
        return startInfo;
    }
}

internal static partial class VisualStudioCodeWindowSurface
{
    private const int RestoreWindow = 9;
    private const uint NoSize = 0x0001;
    private const uint NoMove = 0x0002;
    private const uint ShowWindowFlag = 0x0040;
    private static readonly IntPtr TopMost = new(-1);
    private static readonly IntPtr NotTopMost = new(-2);

    public static bool TrySurface(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        do
        {
            foreach (var process in Process.GetProcessesByName("Code"))
            {
                using (process)
                {
                    try
                    {
                        process.Refresh();
                        var window = process.MainWindowHandle;
                        if (window == IntPtr.Zero)
                        {
                            continue;
                        }

                        ShowWindow(window, RestoreWindow);
                        var flags = NoSize | NoMove | ShowWindowFlag;
                        return SetWindowPos(window, TopMost, 0, 0, 0, 0, flags)
                            && SetWindowPos(window, NotTopMost, 0, 0, 0, 0, flags);
                    }
                    catch (InvalidOperationException)
                    {
                        // A candidate process can exit while VS Code hands off to its main process.
                    }
                }
            }

            Thread.Sleep(50);
        }
        while (DateTimeOffset.UtcNow < deadline);

        return false;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr window, int command);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
