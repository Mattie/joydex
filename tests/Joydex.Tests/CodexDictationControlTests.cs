using Joydex.Windows.Actions;

namespace Joydex.Tests;

public sealed class CodexDictationControlTests
{
    [Fact]
    public void StopTransitionRetriesUntilStartupCancelAppears()
    {
        var windowHandle = new IntPtr(1234);
        var attempts = 0;
        var waits = 0;

        var result = WindowsCodexDictationControl.WaitForStopTransition(
            windowHandle,
            () => ++attempts < 3
                ? CodexDictationControlResult.Missing(windowHandle, "missing")
                : CodexDictationControlResult.Completed(
                    windowHandle,
                    "accessibility-button=Starting dictation; click to cancel"),
            () => attempts == 1,
            () => waits++,
            maxAttempts: 3);

        Assert.True(result.Success);
        Assert.Equal("accessibility-button=Starting dictation; click to cancel", result.Detail);
        Assert.Equal(3, attempts);
        Assert.Equal(2, waits);
    }

    [Fact]
    public void StopTransitionKeepsAmbiguousStartupAsAFailure()
    {
        var windowHandle = new IntPtr(1234);
        var attempts = 0;

        var result = WindowsCodexDictationControl.WaitForStopTransition(
            windowHandle,
            () =>
            {
                attempts++;
                return CodexDictationControlResult.Missing(windowHandle, "missing");
            },
            () => attempts == 1,
            () => { },
            maxAttempts: 3);

        Assert.False(result.Success);
        Assert.Equal("missing", result.Detail);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void StopTransitionRequiresAFullInactiveConfirmationWindow()
    {
        var windowHandle = new IntPtr(1234);
        var attempts = 0;
        var waits = 0;

        var result = WindowsCodexDictationControl.WaitForStopTransition(
            windowHandle,
            () =>
            {
                attempts++;
                return CodexDictationControlResult.Missing(windowHandle, "missing");
            },
            () => true,
            () => waits++,
            maxAttempts: 3);

        Assert.True(result.Success);
        Assert.Equal("dictation-already-stopped", result.Detail);
        Assert.Equal(3, attempts);
        Assert.Equal(2, waits);
    }
}
