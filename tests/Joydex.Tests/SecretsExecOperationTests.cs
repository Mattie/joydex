using Joydex.Secrets;

namespace Joydex.Tests;

public sealed class SecretsExecOperationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "joydex-secrets-exec-" + Guid.NewGuid().ToString("N"));

    public SecretsExecOperationTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void CanonicalOperationChangesForArgumentsFingerprintsMappingsAndOutputMode()
    {
        var input = Path.Combine(_directory, "input.txt");
        File.WriteAllText(input, "generation-one");
        var original = Resolve(Arguments("one"), input, SecretOutputDisclosure.Summary, "deploy-token");
        var repeat = Resolve(Arguments("one"), input, SecretOutputDisclosure.Summary, "deploy-token");
        var changedArgument = Resolve(Arguments("two"), input, SecretOutputDisclosure.Summary, "deploy-token");
        var changedMapping = Resolve(Arguments("one"), input, SecretOutputDisclosure.Summary, "other-token");
        var changedOutput = Resolve(Arguments("one"), input, SecretOutputDisclosure.None, "deploy-token");
        File.WriteAllText(input, "generation-two");
        var changedInput = Resolve(Arguments("one"), input, SecretOutputDisclosure.Summary, "deploy-token");

        Assert.Equal(original.Identity.Digest, repeat.Identity.Digest);
        Assert.NotEqual(original.Identity.Digest, changedArgument.Identity.Digest);
        Assert.NotEqual(original.Identity.Digest, changedMapping.Identity.Digest);
        Assert.NotEqual(original.Identity.Digest, changedOutput.Identity.Digest);
        Assert.NotEqual(original.Identity.Digest, changedInput.Identity.Digest);
    }

    [Fact]
    public void WorkingDirectoryOutsideCurrentProjectWorktreeIsRejectedBeforeConsent()
    {
        var proposal = new ExecOperationProposal(
            CommandInterpreter(),
            Arguments("one"),
            Path.GetTempPath(),
            new Dictionary<string, string> { ["TOKEN"] = "deploy-token" },
            [],
            SecretOutputDisclosure.Summary);

        var exception = Assert.Throws<ArgumentException>(() =>
            ExecOperationCanonicalizer.Resolve(Project(), ["deploy-token"], proposal));

        Assert.Contains("outside the current project worktree", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UndefinedOutputDisclosureIsRejectedBeforeConsent()
    {
        var proposal = new ExecOperationProposal(
            CommandInterpreter(),
            Arguments("one"),
            _directory,
            new Dictionary<string, string> { ["TOKEN"] = "deploy-token" },
            [],
            (SecretOutputDisclosure)42);

        Assert.Throws<ArgumentException>(() =>
            ExecOperationCanonicalizer.Resolve(Project(), ["deploy-token"], proposal));
    }

    [Fact]
    public async Task ApprovedLaunchUsesMinimalEnvironmentRedactsOutputAndClosesJournal()
    {
        var envPath = Path.Combine(_directory, ".env");
        File.WriteAllText(envPath, "TOKEN=synthetic-secret\n");
        var provider = new ExactEnvSecretProvider(
            envPath,
            [new("deploy-token", "TOKEN", "stable-token", 1)]);
        using var values = provider.Fetch(["deploy-token"]);
        var command = "if \"%CANARY_TOKEN%\"==\"synthetic-secret\" "
            + "(echo synthetic-secret & exit /b 0) else exit /b 9";
        var operation = Resolve(["/d", "/c", command], null, SecretOutputDisclosure.Summary, "deploy-token");
        var journal = new SecretsAuditJournal(Path.Combine(_directory, "audit.jsonl"));
        var launcher = new SecretsExecLauncher(journal);
        var attempt = Guid.NewGuid();

        var result = await launcher.RunAsync(
            attempt,
            "request-1",
            "release-helper",
            "joydex",
            ["deploy-token"],
            operation,
            values,
            new Dictionary<string, string>(),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("[REDACTED]", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-secret", result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(journal.FindUnconfirmedLaunches());
        Assert.DoesNotContain("synthetic-secret", File.ReadAllText(Path.Combine(_directory, "audit.jsonl")));
    }

    [Fact]
    public async Task RedactionHandlesOverlappingSecretValuesWithoutLeakingSuffixes()
    {
        var envPath = Path.Combine(_directory, ".env");
        File.WriteAllText(envPath, "SHORT=abc\nLONG=abcdef\n");
        var provider = new ExactEnvSecretProvider(envPath,
        [
            new("short-token", "SHORT", "stable-short", 1),
            new("long-token", "LONG", "stable-long", 1),
        ]);
        using var values = provider.Fetch(["short-token", "long-token"]);
        var operation = ExecOperationCanonicalizer.Resolve(
            Project(),
            ["short-token", "long-token"],
            new ExecOperationProposal(
                CommandInterpreter(),
                ["/d", "/c", "echo %LONG_TOKEN%"],
                _directory,
                new Dictionary<string, string>
                {
                    ["SHORT_TOKEN"] = "short-token",
                    ["LONG_TOKEN"] = "long-token",
                },
                [],
                SecretOutputDisclosure.Summary));

        var result = await new SecretsExecLauncher(
                new SecretsAuditJournal(Path.Combine(_directory, "overlap-audit.jsonl")))
            .RunAsync(
                Guid.NewGuid(),
                "overlap-request",
                "release-helper",
                "joydex",
                ["short-token", "long-token"],
                operation,
                values,
                new Dictionary<string, string>(),
                CancellationToken.None);

        Assert.Equal("[REDACTED]", result.StandardOutput.Trim());
        Assert.DoesNotContain("def", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchUsesCallerEnvironmentAndOverridesApprovedSecret()
    {
        var envPath = Path.Combine(_directory, ".env");
        File.WriteAllText(envPath, "TOKEN=synthetic-secret\n");
        var provider = new ExactEnvSecretProvider(
            envPath,
            [new("deploy-token", "TOKEN", "stable-token", 1)]);
        using var values = provider.Fetch(["deploy-token"]);
        var brokerOnlyName = "JOYDEX_BROKER_ONLY_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        var operation = Resolve(
            ["/d", "/c", "if not \"%JOYDEX_PARENT_MARKER%\"==\"caller-value\" exit /b 8 "
                + "& if not \"%CANARY_TOKEN%\"==\"synthetic-secret\" exit /b 9 "
                + $"& if defined {brokerOnlyName} exit /b 10"],
            null,
            SecretOutputDisclosure.Summary,
            "deploy-token");
        var launcher = new SecretsExecLauncher(
            new SecretsAuditJournal(Path.Combine(_directory, "minimal-env-audit.jsonl")));
        var previous = Environment.GetEnvironmentVariable(brokerOnlyName);
        Environment.SetEnvironmentVariable(brokerOnlyName, "broker-only");
        try
        {
            var result = await launcher.RunAsync(
                Guid.NewGuid(),
                "request-caller-env",
                "release-helper",
                "joydex",
                ["deploy-token"],
                operation,
                values,
                new Dictionary<string, string>
                {
                    ["JOYDEX_PARENT_MARKER"] = "caller-value",
                    ["CANARY_TOKEN"] = "caller-original",
                },
                CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(brokerOnlyName, previous);
        }
    }

    [Fact]
    public async Task TruncatedOutputReturnsOnlyAConstantMarker()
    {
        var envPath = Path.Combine(_directory, ".env");
        File.WriteAllText(envPath, "TOKEN=synthetic-secret\n");
        var provider = new ExactEnvSecretProvider(
            envPath,
            [new("deploy-token", "TOKEN", "stable-token", 1)]);
        using var values = provider.Fetch(["deploy-token"]);
        var command = "for /L %i in (1,1,17000) do @<nul set /p =A & echo synthetic-secret";
        var operation = Resolve(
            ["/d", "/c", command],
            null,
            SecretOutputDisclosure.Summary,
            "deploy-token");
        var launcher = new SecretsExecLauncher(
            new SecretsAuditJournal(Path.Combine(_directory, "truncation-audit.jsonl")));

        var result = await launcher.RunAsync(
            Guid.NewGuid(),
            "request-truncation",
            "release-helper",
            "joydex",
            ["deploy-token"],
            operation,
            values,
            new Dictionary<string, string>(),
            CancellationToken.None);

        Assert.Equal("[OUTPUT TRUNCATED]", result.StandardOutput);
        Assert.DoesNotContain("synthetic", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacedWorkingDirectoryIsRejectedBeforeLaunch()
    {
        var working = Path.Combine(_directory, "working");
        var moved = Path.Combine(_directory, "moved");
        Directory.CreateDirectory(working);
        var operation = Resolve(
            Arguments("one"),
            null,
            SecretOutputDisclosure.None,
            "deploy-token",
            working);
        Directory.Move(working, moved);
        Directory.CreateDirectory(working);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ExecOperationCanonicalizer.AcquireCurrentFiles(operation));

        Assert.Contains("working directory changed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FinalAuthorizationCanVetoBeforeLaunchIsCommitted()
    {
        var marker = Path.Combine(_directory, "must-not-launch.txt");
        var envPath = Path.Combine(_directory, ".env");
        File.WriteAllText(envPath, "TOKEN=synthetic-secret\n");
        var provider = new ExactEnvSecretProvider(
            envPath,
            [new("deploy-token", "TOKEN", "stable-token", 1)]);
        using var values = provider.Fetch(["deploy-token"]);
        var operation = Resolve(
            ["/d", "/c", "echo launched>\"" + marker + "\""],
            null,
            SecretOutputDisclosure.None,
            "deploy-token");
        var journal = new SecretsAuditJournal(Path.Combine(_directory, "veto-audit.jsonl"));
        var launcher = new SecretsExecLauncher(journal);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => launcher.RunAsync(
            Guid.NewGuid(),
            "request-veto",
            "release-helper",
            "joydex",
            ["deploy-token"],
            operation,
            values,
            new Dictionary<string, string>(),
            CancellationToken.None,
            authorizeAndCommit: _ => throw new UnauthorizedAccessException("revoked")));

        Assert.False(File.Exists(marker));
        Assert.Empty(journal.ReadAll());
    }

    [Fact]
    public async Task CancellationObservedAfterCommitRecordsConclusivePreLaunchFailure()
    {
        var marker = Path.Combine(_directory, "cancelled-must-not-launch.txt");
        var envPath = Path.Combine(_directory, ".env");
        File.WriteAllText(envPath, "TOKEN=synthetic-secret\n");
        var provider = new ExactEnvSecretProvider(
            envPath,
            [new("deploy-token", "TOKEN", "stable-token", 1)]);
        using var values = provider.Fetch(["deploy-token"]);
        var operation = Resolve(
            ["/d", "/c", "echo launched>\"" + marker + "\""],
            null,
            SecretOutputDisclosure.None,
            "deploy-token");
        var journal = new SecretsAuditJournal(Path.Combine(_directory, "cancel-audit.jsonl"));
        var launcher = new SecretsExecLauncher(journal);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launcher.RunAsync(
            Guid.NewGuid(),
            "request-cancel-after-commit",
            "release-helper",
            "joydex",
            ["deploy-token"],
            operation,
            values,
            new Dictionary<string, string>(),
            cancellation.Token,
            authorizeAndCommit: audit =>
            {
                journal.Append(audit);
                cancellation.Cancel();
            }));

        Assert.False(File.Exists(marker));
        Assert.Collection(
            journal.ReadAll(),
            record => Assert.Equal(SecretsAuditEventKind.LaunchCommitted, record.Kind),
            record =>
            {
                Assert.Equal(SecretsAuditEventKind.FailedBeforeLaunch, record.Kind);
                Assert.Equal("cancelled-before-start", record.Outcome);
            });
        Assert.Empty(journal.FindUnconfirmedLaunches());
    }

    [Fact]
    public async Task CancellationAfterStartKillsTheChildTreeAndRecordsATerminalOutcome()
    {
        var envPath = Path.Combine(_directory, "cancel-after-start.env");
        File.WriteAllText(envPath, "TOKEN=synthetic-secret\n");
        var provider = new ExactEnvSecretProvider(
            envPath,
            [new("deploy-token", "TOKEN", "stable-token", 1)]);
        using var values = provider.Fetch(["deploy-token"]);
        var marker = Path.Combine(_directory, "cancelled-child-finished.txt");
        var ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        var operation = Resolve(
            ["/d", "/c", $"{ping} 127.0.0.1 -n 30 >nul & echo finished > \"{marker}\""],
            null,
            SecretOutputDisclosure.None,
            "deploy-token");
        var attempt = Guid.NewGuid();
        var journal = new SecretsAuditJournal(Path.Combine(_directory, "cancel-after-start-audit.jsonl"));
        var launcher = new SecretsExecLauncher(journal);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launcher.RunAsync(
            attempt,
            "request-cancel-after-start",
            "release-helper",
            "joydex",
            ["deploy-token"],
            operation,
            values,
            new Dictionary<string, string>(),
            cancellation.Token));

        Assert.False(File.Exists(marker));
        Assert.Empty(journal.FindUnconfirmedLaunches());
        Assert.Contains(
            journal.ReadAll(),
            record => record.AttemptId == attempt
                && record.Kind == SecretsAuditEventKind.LaunchTerminated
                && record.Outcome == "cancelled-after-start");
    }

    [Fact]
    public async Task UnconfirmedCancellationKeepsTheLaunchInAnUncertainTerminalState()
    {
        var envPath = Path.Combine(_directory, "unconfirmed-cancel.env");
        File.WriteAllText(envPath, "TOKEN=synthetic-secret\n");
        var provider = new ExactEnvSecretProvider(
            envPath,
            [new("deploy-token", "TOKEN", "stable-token", 1)]);
        using var values = provider.Fetch(["deploy-token"]);
        var ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        var operation = Resolve(
            ["/d", "/c", $"{ping} 127.0.0.1 -n 30 >nul"],
            null,
            SecretOutputDisclosure.None,
            "deploy-token");
        var attempt = Guid.NewGuid();
        var journal = new SecretsAuditJournal(Path.Combine(_directory, "unconfirmed-cancel-audit.jsonl"));
        var launcher = new SecretsExecLauncher(journal, process =>
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            return false;
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launcher.RunAsync(
            attempt,
            "request-unconfirmed-cancel",
            "release-helper",
            "joydex",
            ["deploy-token"],
            operation,
            values,
            new Dictionary<string, string>(),
            cancellation.Token));

        Assert.Contains(attempt, journal.FindUnconfirmedLaunches());
        Assert.Contains(
            journal.ReadAll(),
            record => record.AttemptId == attempt
                && record.Kind == SecretsAuditEventKind.LaunchUnconfirmed
                && record.Outcome == "cancellation-kill-unconfirmed");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private ResolvedExecOperation Resolve(
        IReadOnlyList<string> arguments,
        string? fingerprintInput,
        SecretOutputDisclosure output,
        string alias,
        string? workingDirectory = null) => ExecOperationCanonicalizer.Resolve(
            Project(),
            ["deploy-token", "other-token"],
            new ExecOperationProposal(
                CommandInterpreter(),
                arguments,
                workingDirectory ?? _directory,
                new Dictionary<string, string> { ["CANARY_TOKEN"] = alias },
                fingerprintInput is null ? [] : [fingerprintInput],
                output));

    private SecretsProjectIdentity Project() => new(
        "joydex",
        "joydex-project",
        _directory,
        "worktree-test",
        _directory,
        1);

    private static string CommandInterpreter() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "cmd.exe");

    private static IReadOnlyList<string> Arguments(string value) => ["/d", "/c", "echo " + value];
}
