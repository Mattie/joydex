using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Joydex.Windows.Actions;

public readonly record struct CodexDictationControlResult(
    bool Success,
    IntPtr WindowHandle,
    string Detail)
{
    internal bool ButtonMissing { get; init; }

    public static CodexDictationControlResult Completed(IntPtr windowHandle, string detail) =>
        new(true, windowHandle, detail);

    public static CodexDictationControlResult Failed(IntPtr windowHandle, string detail) =>
        new(false, windowHandle, detail);

    internal static CodexDictationControlResult Missing(IntPtr windowHandle, string detail) =>
        new(false, windowHandle, detail) { ButtonMissing = true };
}

public interface ICodexDictationControl
{
    CodexDictationControlResult Start();

    CodexDictationControlResult Stop(IntPtr windowHandle);
}

public sealed partial class WindowsCodexDictationControl : ICodexDictationControl
{
    private const string StartName = "Dictate";
    private const int StopSettleAttempts = 21;
    private const int StopSettleDelayMs = 50;

    private static readonly string[] StopNames =
    [
        "Stop dictation",
        "Starting dictation; click to cancel",
        "Preparing dictation",
    ];

    public CodexDictationControlResult Start()
    {
        var windowHandle = GetForegroundWindow();
        if (windowHandle == IntPtr.Zero)
        {
            return CodexDictationControlResult.Failed(
                windowHandle,
                "Windows did not report a foreground Codex window.");
        }

        return InvokeNamedButton(
            windowHandle,
            [StartName],
            $"The foreground Codex window has no enabled '{StartName}' button.");
    }

    public CodexDictationControlResult Stop(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return CodexDictationControlResult.Failed(
                windowHandle,
                "The recorded Codex window is unavailable.");
        }

        try
        {
            return WaitForStopTransition(
                windowHandle,
                () => InvokeNamedButton(
                    windowHandle,
                    StopNames,
                    "The recorded Codex window has no enabled dictation stop or cancel button."),
                () => FindEnabledButton(AutomationElement.FromHandle(windowHandle), StartName) is not null,
                () => Thread.Sleep(StopSettleDelayMs),
                StopSettleAttempts);
        }
        catch (Exception exception) when (IsAutomationFailure(exception))
        {
            return CodexDictationControlResult.Failed(
                windowHandle,
                $"The recorded Codex window could not be inspected: {exception.Message}");
        }
    }

    internal static CodexDictationControlResult WaitForStopTransition(
        IntPtr windowHandle,
        Func<CodexDictationControlResult> tryStop,
        Func<bool> isInactive,
        Action wait,
        int maxAttempts)
    {
        ArgumentNullException.ThrowIfNull(tryStop);
        ArgumentNullException.ThrowIfNull(isInactive);
        ArgumentNullException.ThrowIfNull(wait);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        var inactiveThroughout = true;
        CodexDictationControlResult result = default;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            result = tryStop();
            if (result.Success || !result.ButtonMissing)
            {
                return result;
            }

            inactiveThroughout &= isInactive();
            if (attempt < maxAttempts - 1)
            {
                wait();
            }
        }

        return inactiveThroughout
            ? CodexDictationControlResult.Completed(windowHandle, "dictation-already-stopped")
            : result;
    }

    private static CodexDictationControlResult InvokeNamedButton(
        IntPtr windowHandle,
        IReadOnlyList<string> names,
        string missingMessage)
    {
        try
        {
            var root = AutomationElement.FromHandle(windowHandle);
            foreach (var name in names)
            {
                var button = FindEnabledButton(root, name);
                if (button is null)
                {
                    continue;
                }

                if (!button.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)
                    || pattern is not InvokePattern invokePattern)
                {
                    return CodexDictationControlResult.Failed(
                        windowHandle,
                        $"The Codex '{name}' button cannot be invoked through Windows accessibility.");
                }

                invokePattern.Invoke();
                return CodexDictationControlResult.Completed(windowHandle, $"accessibility-button={name}");
            }

            return CodexDictationControlResult.Missing(windowHandle, missingMessage);
        }
        catch (Exception exception) when (IsAutomationFailure(exception))
        {
            return CodexDictationControlResult.Failed(
                windowHandle,
                $"The Codex dictation button could not be invoked: {exception.Message}");
        }
    }

    private static AutomationElement? FindEnabledButton(AutomationElement root, string name)
    {
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, name),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
        var button = root.FindFirst(TreeScope.Descendants, condition);
        return button is not null && button.Current.IsEnabled ? button : null;
    }

    private static bool IsAutomationFailure(Exception exception) =>
        exception is ElementNotAvailableException
            or InvalidOperationException
            or ArgumentException
            or COMException;

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();
}
