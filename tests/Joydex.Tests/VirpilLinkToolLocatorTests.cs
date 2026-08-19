using Joydex.App;

namespace Joydex.Tests;

public sealed class VirpilLinkToolLocatorTests
{
    [Fact]
    public void FindsTheFirstInstalledCandidate()
    {
        var missing = Path.GetFullPath(@"C:\Missing\VIRPIL Controls LinkTool.exe");
        var installed = Path.GetFullPath(@"D:\Apps\VIRPIL Controls LinkTool.exe");

        var result = VirpilLinkToolLocator.FindInstalledPath(
            [missing, installed],
            path => string.Equals(path, installed, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(installed, result);
    }

    [Fact]
    public void ReturnsNullWhenNoCandidateExists()
    {
        var result = VirpilLinkToolLocator.FindInstalledPath(
            [@"C:\Missing\VIRPIL Controls LinkTool.exe"],
            _ => false);

        Assert.Null(result);
    }
}
