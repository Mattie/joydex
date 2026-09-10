using System.Security.Cryptography;

namespace Joydex.Windows.Voice;

/// <summary>
/// The exact Codex build validated for the experimental ChatGPT-authenticated WebRTC boundary.
/// </summary>
public static class CodexAppServerBinaryPolicy
{
    public const string PinnedVersion = "0.153.4";
    public const string PinnedSha256 = "e5aa76d19c7c94e2e9ef9b707d590206a73ac0e97c8ddc8382181242494bef75";
    public const string CodeModeHostFileName = "codex-code-mode-host.exe";
    public const string PinnedCodeModeHostSha256 = "3eb2083b58f0982506e5c3cb7a550fb6538d718c29f0a75ca4848852a0aff0c7";

    public static async Task<CodexAppServerBinary> VerifyAsync(
        string executablePath,
        CancellationToken cancellationToken = default) =>
        await VerifyAsync(
                executablePath,
                PinnedSha256,
                PinnedCodeModeHostSha256,
                cancellationToken)
            .ConfigureAwait(false);

    internal static async Task<CodexAppServerBinary> VerifyAsync(
        string executablePath,
        string expectedExecutableSha256,
        string expectedCodeModeHostSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new InvalidDataException("The Codex App Server executable path must be fully qualified.");
        }

        var fullPath = Path.GetFullPath(executablePath);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("The pinned Codex App Server executable was not found.", fullPath);
        }

        var actualHash = await CalculateSha256Async(fullPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualHash, expectedExecutableSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The Codex App Server executable hash is {actualHash}; expected validated {PinnedVersion} hash {expectedExecutableSha256}.");
        }

        var codeModeHostPath = Path.Combine(file.DirectoryName!, CodeModeHostFileName);
        if (!File.Exists(codeModeHostPath))
        {
            throw new FileNotFoundException(
                "The pinned Codex runtime is incomplete: its code-mode host was not found beside codex.exe.",
                codeModeHostPath);
        }

        var actualCodeModeHostHash = await CalculateSha256Async(codeModeHostPath, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                actualCodeModeHostHash,
                expectedCodeModeHostSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The Codex code-mode host hash is {actualCodeModeHostHash}; expected validated {PinnedVersion} hash {expectedCodeModeHostSha256}.");
        }

        return new CodexAppServerBinary(
            fullPath,
            PinnedVersion,
            actualHash,
            codeModeHostPath,
            actualCodeModeHostHash);
    }

    private static async Task<string> CalculateSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        byte[] digest;
        await using (var stream = new FileStream(
                         path,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         bufferSize: 128 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}

public sealed class CodexAppServerBinary
{
    internal CodexAppServerBinary(
        string executablePath,
        string version,
        string sha256,
        string codeModeHostPath,
        string codeModeHostSha256)
    {
        ExecutablePath = executablePath;
        Version = version;
        Sha256 = sha256;
        CodeModeHostPath = codeModeHostPath;
        CodeModeHostSha256 = codeModeHostSha256;
    }

    public string ExecutablePath { get; }

    public string Version { get; }

    public string Sha256 { get; }

    public string CodeModeHostPath { get; }

    public string CodeModeHostSha256 { get; }
}
