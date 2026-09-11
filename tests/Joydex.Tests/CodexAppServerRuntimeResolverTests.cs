using Joydex.Windows.Voice;

namespace Joydex.Tests;

public sealed class CodexAppServerRuntimeResolverTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "joydex-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FindsTheNewestCompleteManagedRuntimeWhenNoOverrideIsConfigured()
    {
        var managedRoot = Path.Combine(_directory, "managed");
        var older = CreateCompleteRuntime(Path.Combine(managedRoot, "older"));
        var newer = CreateCompleteRuntime(Path.Combine(managedRoot, "newer"));
        SetRuntimeFreshness(older, new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc));
        SetRuntimeFreshness(newer, new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc));

        var resolved = await CodexAppServerRuntimeResolver.ResolveAsync(
            string.Empty,
            managedRoot);

        Assert.Equal(Path.GetFullPath(newer), resolved.ExecutablePath);
        Assert.True(resolved.IsManagedRuntime);
    }

    [Fact]
    public async Task ExistingManagedPathFollowsANewerCompleteRuntime()
    {
        var managedRoot = Path.Combine(_directory, "managed");
        var configured = CreateCompleteRuntime(Path.Combine(managedRoot, "configured"));
        var current = CreateCompleteRuntime(Path.Combine(managedRoot, "current"));
        SetRuntimeFreshness(configured, new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc));
        SetRuntimeFreshness(current, new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc));

        var resolved = await CodexAppServerRuntimeResolver.ResolveAsync(
            configured,
            managedRoot);

        Assert.Equal(Path.GetFullPath(current), resolved.ExecutablePath);
    }

    [Fact]
    public async Task MissingManagedPathRecoversToTheCurrentRuntime()
    {
        var managedRoot = Path.Combine(_directory, "managed");
        var current = CreateCompleteRuntime(Path.Combine(managedRoot, "current"));
        var removedConfiguredPath = Path.Combine(managedRoot, "removed", "codex.exe");

        var resolved = await CodexAppServerRuntimeResolver.ResolveAsync(
            removedConfiguredPath,
            managedRoot);

        Assert.Equal(Path.GetFullPath(current), resolved.ExecutablePath);
    }

    [Fact]
    public async Task SkipsANewerIncompleteManagedDirectory()
    {
        var managedRoot = Path.Combine(_directory, "managed");
        var complete = CreateCompleteRuntime(Path.Combine(managedRoot, "complete"));
        var incompleteDirectory = Path.Combine(managedRoot, "incomplete");
        Directory.CreateDirectory(incompleteDirectory);
        var incomplete = Path.Combine(incompleteDirectory, CodexAppServerRuntimeResolver.ExecutableFileName);
        File.WriteAllText(incomplete, "incomplete executable");
        SetRuntimeFreshness(complete, new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(incomplete, new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc));

        var resolved = await CodexAppServerRuntimeResolver.ResolveAsync(
            string.Empty,
            managedRoot);

        Assert.Equal(Path.GetFullPath(complete), resolved.ExecutablePath);
    }

    [Fact]
    public async Task SkipsManagedCandidatesWithEmptyRuntimeFiles()
    {
        var managedRoot = Path.Combine(_directory, "managed");
        var complete = CreateCompleteRuntime(Path.Combine(managedRoot, "complete"));
        var emptyDirectory = Path.Combine(managedRoot, "empty");
        Directory.CreateDirectory(emptyDirectory);
        File.WriteAllBytes(
            Path.Combine(emptyDirectory, CodexAppServerRuntimeResolver.ExecutableFileName),
            []);
        File.WriteAllBytes(
            Path.Combine(emptyDirectory, CodexAppServerRuntimeResolver.CodeModeHostFileName),
            []);
        SetRuntimeFreshness(complete, new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc));
        Directory.SetLastWriteTimeUtc(
            emptyDirectory,
            new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc));

        var resolved = await CodexAppServerRuntimeResolver.ResolveAsync(
            string.Empty,
            managedRoot);

        Assert.Equal(Path.GetFullPath(complete), resolved.ExecutablePath);
    }

    [Fact]
    public async Task HonorsACompleteOverrideOutsideTheManagedFolder()
    {
        var managedRoot = Path.Combine(_directory, "managed");
        _ = CreateCompleteRuntime(Path.Combine(managedRoot, "current"));
        var explicitOverride = CreateCompleteRuntime(Path.Combine(_directory, "override"));

        var resolved = await CodexAppServerRuntimeResolver.ResolveAsync(
            explicitOverride,
            managedRoot);

        Assert.Equal(Path.GetFullPath(explicitOverride), resolved.ExecutablePath);
        Assert.False(resolved.IsManagedRuntime);
        Assert.Equal(
            Path.Combine(Path.GetDirectoryName(explicitOverride)!, CodexAppServerRuntimeResolver.CodeModeHostFileName),
            resolved.CodeModeHostPath);
    }

    [Fact]
    public async Task MissingExternalOverrideDoesNotSilentlySelectAManagedRuntime()
    {
        var managedRoot = Path.Combine(_directory, "managed");
        _ = CreateCompleteRuntime(Path.Combine(managedRoot, "current"));
        var explicitOverride = Path.Combine(_directory, "override", "missing.exe");

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(
            () => CodexAppServerRuntimeResolver.ResolveAsync(explicitOverride, managedRoot));

        Assert.Equal(Path.GetFullPath(explicitOverride), exception.FileName);
    }

    [Fact]
    public async Task RejectsAnIncompleteExternalOverride()
    {
        var managedRoot = Path.Combine(_directory, "managed");
        var overrideDirectory = Path.Combine(_directory, "override");
        Directory.CreateDirectory(overrideDirectory);
        var explicitOverride = Path.Combine(overrideDirectory, "codex.exe");
        File.WriteAllText(explicitOverride, "executable");

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(
            () => CodexAppServerRuntimeResolver.ResolveAsync(explicitOverride, managedRoot));

        Assert.Equal(
            Path.Combine(overrideDirectory, CodexAppServerRuntimeResolver.CodeModeHostFileName),
            exception.FileName);
        Assert.Contains("runtime is incomplete", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReportsWhenNoCompleteManagedRuntimeExists()
    {
        var managedRoot = Path.Combine(_directory, "managed");

        var exception = await Assert.ThrowsAsync<CodexManagedRuntimeUnavailableException>(
            () => CodexAppServerRuntimeResolver.ResolveAsync(string.Empty, managedRoot));

        Assert.Equal(Path.GetFullPath(managedRoot), exception.ManagedRuntimeRoot);
        Assert.Contains("complete Codex Desktop", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManagedDirectoryLossDuringMetadataProbeIsRetryable()
    {
        var managedRoot = Path.Combine(_directory, "managed");
        _ = CreateCompleteRuntime(Path.Combine(managedRoot, "current"));
        var directoryLoss = new DirectoryNotFoundException("The selected runtime directory disappeared.");

        var exception = await Assert.ThrowsAsync<CodexManagedRuntimeUnavailableException>(
            () => CodexAppServerRuntimeResolver.ResolveAsync(
                string.Empty,
                managedRoot,
                createRuntime: (_, _) => throw directoryLoss));

        Assert.Same(directoryLoss, exception.InnerException);
        Assert.Equal(Path.GetFullPath(managedRoot), exception.ManagedRuntimeRoot);
        Assert.Contains("retry", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DoesNotTreatLooseFilesAtTheManagedRootAsAnInstalledBuild()
    {
        var managedRoot = Path.Combine(_directory, "managed");
        _ = CreateCompleteRuntime(managedRoot);

        await Assert.ThrowsAsync<CodexManagedRuntimeUnavailableException>(
            () => CodexAppServerRuntimeResolver.ResolveAsync(string.Empty, managedRoot));
    }

    [Fact]
    public async Task RejectsARelativeOverride()
    {
        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => CodexAppServerRuntimeResolver.ResolveAsync(
                Path.Combine("relative", "codex.exe"),
                Path.Combine(_directory, "managed")));

        Assert.Contains("fully qualified", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateCompleteRuntime(string directory)
    {
        Directory.CreateDirectory(directory);
        var executablePath = Path.Combine(directory, CodexAppServerRuntimeResolver.ExecutableFileName);
        var codeModeHostPath = Path.Combine(directory, CodexAppServerRuntimeResolver.CodeModeHostFileName);
        File.WriteAllText(executablePath, "test executable");
        File.WriteAllText(codeModeHostPath, "test helper");
        return executablePath;
    }

    private static void SetRuntimeFreshness(string executablePath, DateTime freshness)
    {
        var directory = Path.GetDirectoryName(executablePath)!;
        File.SetLastWriteTimeUtc(executablePath, freshness);
        File.SetLastWriteTimeUtc(
            Path.Combine(directory, CodexAppServerRuntimeResolver.CodeModeHostFileName),
            freshness);
        Directory.SetLastWriteTimeUtc(directory, freshness);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
