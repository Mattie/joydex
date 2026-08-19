namespace Joydex.App;

/// <summary>Locates VIRPIL Controls LinkTool in its standard per-user or system install folders.</summary>
internal static class VirpilLinkToolLocator
{
    private const string DirectoryName = "VIRPIL Controls LinkTool";
    private const string ExecutableName = "VIRPIL Controls LinkTool.exe";

    /// <summary>Returns the first installed LinkTool executable in the standard locations.</summary>
    public static string? FindInstalledPath() => FindInstalledPath(DefaultCandidatePaths());

    internal static string? FindInstalledPath(
        IEnumerable<string> candidatePaths,
        Func<string, bool>? fileExists = null)
    {
        ArgumentNullException.ThrowIfNull(candidatePaths);
        fileExists ??= File.Exists;

        foreach (var candidatePath in candidatePaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var fullPath = Path.GetFullPath(candidatePath);
            if (fileExists(fullPath))
            {
                return fullPath;
            }
        }

        return null;
    }

    private static IEnumerable<string> DefaultCandidatePaths()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Programs", DirectoryName, ExecutableName);
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, DirectoryName, ExecutableName);
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            yield return Path.Combine(programFilesX86, DirectoryName, ExecutableName);
        }
    }
}
