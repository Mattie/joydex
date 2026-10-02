using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Joydex.Windows.Actions;

public readonly record struct CodexDictationControlResult(
    bool Success,
    IntPtr WindowHandle,
    string Detail)
{
    public static CodexDictationControlResult Completed(IntPtr windowHandle, string detail) =>
        new(true, windowHandle, detail);

    public static CodexDictationControlResult Failed(IntPtr windowHandle, string detail) =>
        new(false, windowHandle, detail);
}

public interface ICodexDictationControl
{
    CodexDictationControlResult Start();

    CodexDictationControlResult Stop(IntPtr windowHandle);
}

public sealed partial class WindowsCodexDictationControl : ICodexDictationControl
{
    private const string StartName = "Dictate";

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

        var result = InvokeNamedButton(
            windowHandle,
            StopNames,
            "The recorded Codex window has no enabled dictation stop or cancel button.");
        if (result.Success)
        {
            return result;
        }

        try
        {
            var root = AutomationElement.FromHandle(windowHandle);
            if (FindEnabledButton(root, StartName) is not null)
            {
                return CodexDictationControlResult.Completed(windowHandle, "dictation-already-stopped");
            }
        }
        catch (Exception exception) when (IsAutomationFailure(exception))
        {
            return CodexDictationControlResult.Failed(
                windowHandle,
                $"The recorded Codex window could not be inspected: {exception.Message}");
        }

        return result;
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

            return CodexDictationControlResult.Failed(windowHandle, missingMessage);
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
