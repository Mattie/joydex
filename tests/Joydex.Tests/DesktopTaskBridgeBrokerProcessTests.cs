using System.Text;
using Joydex.App;

namespace Joydex.Tests;

public sealed class DesktopTaskBridgeBrokerProcessTests
{
    [Fact]
    public void StartupFailureClassificationPreservesStartupAndCleanupFailures()
    {
        var startupFailure = new InvalidOperationException("startup failed");
        var cleanupFailure = new IOException("cleanup failed");

        var terminal = DesktopTaskBridgeOwnershipCleanupException.ForStartupFailure(
            startupFailure,
            cleanupFailure);

        Assert.Equal([startupFailure, cleanupFailure], terminal.InnerExceptions);
        Assert.Contains("must not be replaced", terminal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalStartupCleanupFailureSuppressesAutomaticReplacement()
    {
        var terminal = DesktopTaskBridgeOwnershipCleanupException.ForStartupFailure(
            new InvalidOperationException("startup failed"),
            new IOException("cleanup failed"));
        var admission = new DesktopTaskBridgeBrokerAdmission();
        var logs = new List<string>();
        Assert.True(admission.CanStart);

        DesktopTaskBridgeBrokerFailurePolicy.HandleTerminalStartupFailure(
            terminal,
            admission,
            logs.Add);

        Assert.Contains("Automatic replacement is disabled", Assert.Single(logs));
        Assert.False(admission.CanStart);
        Assert.False(admission.CanStart);
        Assert.Same(terminal, admission.TerminalFailure);
    }

    [Fact]
    public void BrokerPipesUseUtf8InsteadOfTheWindowsConsoleCodePage()
    {
        var startInfo = DesktopTaskBridgeBrokerProcess.CreateStartInfo(
            @"C:\runtime\Joydex.DesktopBridgeHost.exe",
            "Joydex.DesktopTasks.test-capability");

        Assert.Equal(Encoding.UTF8.CodePage, Assert.IsType<UTF8Encoding>(startInfo.StandardOutputEncoding).CodePage);
        Assert.Equal(Encoding.UTF8.CodePage, Assert.IsType<UTF8Encoding>(startInfo.StandardErrorEncoding).CodePage);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.False(startInfo.RedirectStandardInput);
        Assert.Equal("Joydex.DesktopTasks.test-capability", startInfo.ArgumentList[^1]);
    }

}
