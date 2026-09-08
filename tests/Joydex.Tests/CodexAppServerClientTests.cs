using System.Text;
using Joydex.Windows.Voice;

namespace Joydex.Tests;

public sealed class CodexAppServerClientTests
{
    [Fact]
    public void AppServerPipesUseUtf8InsteadOfTheWindowsConsoleCodePage()
    {
        var startInfo = CodexAppServerClient.CreateStartInfo(@"C:\runtime\codex.exe");

        Assert.Equal(Encoding.UTF8.CodePage, Assert.IsType<UTF8Encoding>(startInfo.StandardInputEncoding).CodePage);
        Assert.Equal(Encoding.UTF8.CodePage, Assert.IsType<UTF8Encoding>(startInfo.StandardOutputEncoding).CodePage);
        Assert.Equal(Encoding.UTF8.CodePage, Assert.IsType<UTF8Encoding>(startInfo.StandardErrorEncoding).CodePage);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
    }

    [Fact]
    public void AppServerUsesExplicitExistingWorkingDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var startInfo = CodexAppServerClient.CreateStartInfo(@"C:\runtime\codex.exe", directory);

            Assert.Equal(Path.GetFullPath(directory), startInfo.WorkingDirectory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
