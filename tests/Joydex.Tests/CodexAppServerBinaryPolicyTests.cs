using System.Security.Cryptography;
using Joydex.Windows.Voice;

namespace Joydex.Tests;

public sealed class CodexAppServerBinaryPolicyTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "joydex-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RejectsAnExecutableThatDoesNotMatchTheValidatedBinary()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "codex.exe");
        await File.WriteAllTextAsync(path, "not codex");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => CodexAppServerBinaryPolicy.VerifyAsync(path));

        Assert.Contains(CodexAppServerBinaryPolicy.PinnedVersion, exception.Message, StringComparison.Ordinal);
        Assert.Contains(CodexAppServerBinaryPolicy.PinnedSha256, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsMissingExecutableWithoutSearchingOtherLocations()
    {
        var path = Path.Combine(_directory, "missing.exe");

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(
            () => CodexAppServerBinaryPolicy.VerifyAsync(path));

        Assert.Equal(Path.GetFullPath(path), exception.FileName);
    }

    [Fact]
    public async Task RejectsPinnedExecutableWithoutItsMatchingCodeModeHost()
    {
        Directory.CreateDirectory(_directory);
        var executablePath = Path.Combine(_directory, "codex.exe");
        await File.WriteAllTextAsync(executablePath, "test executable");
        var executableHash = await Sha256Async(executablePath);

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(
            () => CodexAppServerBinaryPolicy.VerifyAsync(
                executablePath,
                executableHash,
                new string('0', 64)));

        Assert.Equal(
            Path.Combine(_directory, CodexAppServerBinaryPolicy.CodeModeHostFileName),
            exception.FileName);
        Assert.Contains("runtime is incomplete", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsCodeModeHostFromAnotherCodexBuild()
    {
        Directory.CreateDirectory(_directory);
        var executablePath = Path.Combine(_directory, "codex.exe");
        var codeModeHostPath = Path.Combine(_directory, CodexAppServerBinaryPolicy.CodeModeHostFileName);
        await File.WriteAllTextAsync(executablePath, "test executable");
        await File.WriteAllTextAsync(codeModeHostPath, "wrong helper");
        var executableHash = await Sha256Async(executablePath);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => CodexAppServerBinaryPolicy.VerifyAsync(
                executablePath,
                executableHash,
                new string('0', 64)));

        Assert.Contains("code-mode host hash", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AcceptsACompleteMatchingCodexRuntime()
    {
        Directory.CreateDirectory(_directory);
        var executablePath = Path.Combine(_directory, "codex.exe");
        var codeModeHostPath = Path.Combine(_directory, CodexAppServerBinaryPolicy.CodeModeHostFileName);
        await File.WriteAllTextAsync(executablePath, "test executable");
        await File.WriteAllTextAsync(codeModeHostPath, "test helper");

        var verified = await CodexAppServerBinaryPolicy.VerifyAsync(
            executablePath,
            await Sha256Async(executablePath),
            await Sha256Async(codeModeHostPath));

        Assert.Equal(Path.GetFullPath(executablePath), verified.ExecutablePath);
        Assert.Equal(Path.GetFullPath(codeModeHostPath), verified.CodeModeHostPath);
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var digest = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
