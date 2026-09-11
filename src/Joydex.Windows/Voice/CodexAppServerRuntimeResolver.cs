namespace Joydex.Windows.Voice;

/// <summary>
/// Finds a structurally complete Codex App Server runtime. Paths inside Codex Desktop's managed
/// runtime folder follow its most recently written candidate; paths elsewhere remain explicit
/// overrides.
/// </summary>
public static class CodexAppServerRuntimeResolver
{
    public const string ExecutableFileName = "codex.exe";
    public const string CodeModeHostFileName = "codex-code-mode-host.exe";

    public static Task<CodexAppServerBinary> ResolveAsync(
        string? executablePath,
        CancellationToken cancellationToken = default) =>
        ResolveAsync(executablePath, DefaultManagedRuntimeRoot(), cancellationToken);

    public static bool IsManagedRuntimePath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        try
        {
            return IsWithin(
                Path.GetFullPath(executablePath.Trim()),
                Path.GetFullPath(DefaultManagedRuntimeRoot()));
        }
        catch (Exception exception) when (exception is
            ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }

    internal static Task<CodexAppServerBinary> ResolveAsync(
        string? executablePath,
        string managedRuntimeRoot,
        CancellationToken cancellationToken = default,
        Func<string, bool, CodexAppServerBinary>? createRuntime = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRuntimeRoot);
        createRuntime ??= CreateRuntime;

        var fullManagedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(managedRuntimeRoot));
        var configuredPath = NormalizeConfiguredPath(executablePath);
        if (configuredPath.Length == 0 || IsWithin(configuredPath, fullManagedRoot))
        {
            string? managedRuntime;
            try
            {
                managedRuntime = FindMostRecentlyWrittenCompleteManagedRuntime(
                    fullManagedRoot,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new CodexManagedRuntimeUnavailableException(
                    "Codex Desktop's managed runtime folder changed while Joydex was inspecting it; "
                    + "automatic selection will retry.",
                    fullManagedRoot,
                    exception);
            }
            if (managedRuntime is null)
            {
                throw new CodexManagedRuntimeUnavailableException(
                    "A structurally complete Codex Desktop App Server runtime candidate was not found. "
                    + "Start or reinstall Codex Desktop, or configure an executable override outside "
                    + "its managed runtime folder.",
                    fullManagedRoot);
            }

            try
            {
                return Task.FromResult(createRuntime(managedRuntime, true));
            }
            catch (Exception exception) when (exception is
                FileNotFoundException
                or DirectoryNotFoundException
                or InvalidDataException
                or UnauthorizedAccessException)
            {
                throw new CodexManagedRuntimeUnavailableException(
                    "The selected Codex Desktop runtime changed while Joydex was inspecting it; "
                    + "automatic selection will retry.",
                    fullManagedRoot,
                    exception);
            }
        }

        return Task.FromResult(createRuntime(configuredPath, false));
    }

    private static string DefaultManagedRuntimeRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenAI",
        "Codex",
        "bin");

    private static string NormalizeConfiguredPath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return string.Empty;
        }

        var candidate = executablePath.Trim();
        if (!Path.IsPathFullyQualified(candidate))
        {
            throw new InvalidDataException(
                "The Codex App Server executable override must be fully qualified.");
        }

        return Path.GetFullPath(candidate);
    }

    private static string? FindMostRecentlyWrittenCompleteManagedRuntime(
        string managedRuntimeRoot,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(managedRuntimeRoot))
        {
            return null;
        }

        var candidates = new List<ManagedRuntimeCandidate>();
        foreach (var directory in EnumerateCandidateDirectories(managedRuntimeRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var executable = new FileInfo(Path.Combine(directory, ExecutableFileName));
                var codeModeHost = new FileInfo(Path.Combine(directory, CodeModeHostFileName));
                if (!executable.Exists
                    || executable.Length == 0
                    || !codeModeHost.Exists
                    || codeModeHost.Length == 0)
                {
                    continue;
                }

                var freshness = new[]
                {
                    executable.LastWriteTimeUtc,
                    codeModeHost.LastWriteTimeUtc,
                }.Max();
                candidates.Add(new ManagedRuntimeCandidate(executable.FullName, freshness));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // An updater may be replacing one candidate while the stable bundle remains usable.
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Freshness)
            .ThenByDescending(candidate => candidate.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.ExecutablePath)
            .FirstOrDefault();
    }

    private static IEnumerable<string> EnumerateCandidateDirectories(string managedRuntimeRoot) =>
        Directory.EnumerateDirectories(
            managedRuntimeRoot,
            "*",
            new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            });

    private static CodexAppServerBinary CreateRuntime(
        string executablePath,
        bool isManagedRuntime)
    {
        var fullPath = Path.GetFullPath(executablePath);
        var executable = new FileInfo(fullPath);
        if (!executable.Exists)
        {
            throw new FileNotFoundException(
                "The Codex App Server executable override was not found.",
                fullPath);
        }
        if (executable.Length == 0)
        {
            throw new InvalidDataException("The Codex App Server executable override is empty.");
        }

        var codeModeHostPath = Path.Combine(
            Path.GetDirectoryName(fullPath)
                ?? throw new InvalidDataException("The Codex App Server executable has no parent folder."),
            CodeModeHostFileName);
        var codeModeHost = new FileInfo(codeModeHostPath);
        if (!codeModeHost.Exists)
        {
            throw new FileNotFoundException(
                "The Codex runtime is incomplete: its code-mode host was not found beside codex.exe.",
                codeModeHostPath);
        }
        if (codeModeHost.Length == 0)
        {
            throw new InvalidDataException(
                "The Codex runtime is incomplete: its code-mode host is empty.");
        }

        return new CodexAppServerBinary(fullPath, codeModeHostPath, isManagedRuntime);
    }

    private static bool IsWithin(string path, string directory)
    {
        var directoryPrefix = Path.TrimEndingDirectorySeparator(directory)
            + Path.DirectorySeparatorChar;
        return path.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ManagedRuntimeCandidate(
        string ExecutablePath,
        DateTime Freshness);
}

public sealed class CodexManagedRuntimeUnavailableException : IOException
{
    internal CodexManagedRuntimeUnavailableException(
        string message,
        string managedRuntimeRoot,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ManagedRuntimeRoot = managedRuntimeRoot;
    }

    public string ManagedRuntimeRoot { get; }
}

public sealed class CodexAppServerBinary
{
    internal CodexAppServerBinary(
        string executablePath,
        string codeModeHostPath,
        bool isManagedRuntime)
    {
        ExecutablePath = executablePath;
        CodeModeHostPath = codeModeHostPath;
        IsManagedRuntime = isManagedRuntime;
    }

    public string ExecutablePath { get; }

    public string CodeModeHostPath { get; }

    public bool IsManagedRuntime { get; }
}
