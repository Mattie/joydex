using System.Text;
using Joydex.App;

namespace Joydex.Tests;

public sealed class DesktopTaskBridgeBrokerProcessTests
{
    [Fact]
    public void BrokerPipesUseUtf8InsteadOfTheWindowsConsoleCodePage()
    {
        var startInfo = DesktopTaskBridgeBrokerProcess.CreateStartInfo(
            @"C:\runtime\Joydex.DesktopBridgeHost.exe");

        Assert.Equal(Encoding.UTF8.CodePage, Assert.IsType<UTF8Encoding>(startInfo.StandardOutputEncoding).CodePage);
        Assert.Equal(Encoding.UTF8.CodePage, Assert.IsType<UTF8Encoding>(startInfo.StandardErrorEncoding).CodePage);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.False(startInfo.RedirectStandardInput);
    }
}
