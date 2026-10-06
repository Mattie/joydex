using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Joydex.Secrets;

namespace Joydex.Tests;

[Collection("Detached tasks")]
public sealed class SecretsCliTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "joydex-secrets-cli-" + Guid.NewGuid().ToString("N"));

    public SecretsCliTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task DetachedCliReturnsReceiptAndTaskSurvivesCallerAndBrokerShutdown()
    {
        SecretsDetachedTask.TestLaunchHost = (exe, args) =>
        { using var helper = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true }); };
        SecretsTaskReceipt? task = null;
        try
        {
            await using (var harness = new CliHarness(_directory))
            {
                using var process = StartCli(harness, "--detach", "--", harness.PowerShell,
                    "-NoProfile", "-Command", "Start-Sleep -Seconds 120");
                var pending = await harness.WaitForRequestAsync(process);
                Assert.Equal(SecretsExecutionLifetime.Detached, pending.Lifetime);
                Approve(harness, pending);
                var output = await process.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20));
                await process.WaitForExitAsync();
                Assert.Equal(0, process.ExitCode);
                task = JsonSerializer.Deserialize<SecretsTaskReceipt>(output, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                Assert.Equal("running", task!.State);
                Assert.NotNull(task.Deadline); // StartCli supplies an explicit 15-second timeout.
                Assert.Contains(task.TaskId, await process.StandardError.ReadToEndAsync());
            }
            Assert.Equal("running", (await SecretsDetachedTask.ControlAsync(_directory, task.TaskId, false)).State);
            Assert.Equal("stopped", (await SecretsDetachedTask.ControlAsync(_directory, task.TaskId, true)).State);
        }
        finally
        {
            SecretsDetachedTask.TestLaunchHost = null;
            if (task is not null) await SecretsDetachedTask.ControlAsync(_directory, task.TaskId, true);
        }
    }

    [Fact]
    public async Task JsonCallerDisconnectKillsChildAndGrandchild()
    {
        await using var harness = new CliHarness(_directory);
        var pidFile = Path.Combine(_directory, "tree.txt");
        using var process = StartCli(harness, "--output-mode", "json", "--", harness.PowerShell, "-NoProfile", "-Command",
            "$p=Start-Process powershell.exe -WindowStyle Hidden -PassThru -ArgumentList '-NoProfile -Command Start-Sleep -Seconds 120'; "
            + $"[IO.File]::WriteAllText('{pidFile}', [string]$PID + ',' + [string]$p.Id); Start-Sleep -Seconds 120");
        Approve(harness, await harness.WaitForRequestAsync(process));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!File.Exists(pidFile)) await Task.Delay(25, deadline.Token);
        var pids = File.ReadAllText(pidFile).Split(',').Select(int.Parse).ToArray();
        var children = pids.Select(Process.GetProcessById).ToArray();
        try
        {
            process.Kill(); await process.WaitForExitAsync(deadline.Token);
            foreach (var child in children) { await child.WaitForExitAsync(deadline.Token); Assert.True(child.HasExited); }
        }
        finally { foreach (var child in children) { if (!child.HasExited) child.Kill(true); child.Dispose(); } }
    }

    [Fact]
    public async Task DefaultExecStreamsBytesBeforeExitForwardsInputAndPreservesLargeExitCode()
    {
        await using var harness = new CliHarness(_directory);
        using var process = StartCli(harness, "--", harness.PowerShell, "-NoProfile", "-Command",
            "[Console]::Out.Write('ready'); [Console]::Out.Flush(); "
            + "[Console]::OpenStandardInput().CopyTo([Console]::OpenStandardOutput()); "
            + "[Console]::Error.Write('child error'); exit 301");
        Approve(harness, await harness.WaitForRequestAsync(process));

        var ready = new byte[5];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await process.StandardOutput.BaseStream.ReadExactlyAsync(ready, timeout.Token);
        Assert.Equal("ready", Encoding.ASCII.GetString(ready));
        Assert.False(process.HasExited);

        var payload = new byte[100_000];
        new Random(42).NextBytes(payload);
        using var captured = new MemoryStream();
        var output = process.StandardOutput.BaseStream.CopyToAsync(captured, timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.StandardInput.BaseStream.WriteAsync(payload, timeout.Token);
        process.StandardInput.Close();
        await process.WaitForExitAsync(timeout.Token);
        await output;
        Assert.Equal(payload, captured.ToArray());
        Assert.Equal("child error", await error);
        Assert.Equal(301, process.ExitCode);
    }

    [Fact]
    public async Task ExecRememberedApprovalDoesNotBindScriptContents()
    {
        await using var harness = new CliHarness(_directory);
        var script = Path.Combine(_directory, "script.ps1");
        File.WriteAllText(script, "[Console]::Write('first')");
        Process Start() => StartCli(harness, (IReadOnlyDictionary<string, string>?)null, _directory,
            "--", harness.PowerShell, "-NoProfile", "-File", "script.ps1");
        using (var first = Start())
        {
            var pending = await harness.WaitForRequestAsync(first);
            harness.Runtime.Decide(pending.AttemptId, pending.DisplayChallenge, SecretsConsentChoice.YesAlways, true);
            Assert.Equal("first", await first.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20)));
            await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, first.ExitCode);
        }

        File.WriteAllText(script, "[Console]::Write('changed')");
        using var changed = Start();
        Assert.Equal("changed", await changed.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20)));
        await changed.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, changed.ExitCode);
        Assert.Empty(harness.Runtime.PendingRequests());
    }

    [Fact]
    public async Task AdvancedRequestWaitRunSupportsApprovedPassthroughStreams()
    {
        await using var harness = new CliHarness(_directory);
        var operationFile = Path.Combine(_directory, "operation.json");
        var operation = new ExecOperationProposal(harness.PowerShell,
            ["-NoProfile", "-Command", "[Console]::OpenStandardInput().CopyTo([Console]::OpenStandardOutput()); [Console]::Error.Write('child error'); exit 301"],
            _directory, new Dictionary<string, string> { ["SYNTHETIC_TOKEN"] = "SYNTHETIC_TOKEN" }, [], SecretOutputDisclosure.Passthrough);
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        File.WriteAllText(operationFile, JsonSerializer.Serialize(operation, json));
        Process Start(string command, params string[] options) => StartHelper(harness, null, _directory,
            [command, "--data-root", _directory, "--client", "codex", "--project", "joydex", "--request", "advanced-streams", ..options]);
        using (var request = Start("request", "--reason", "Test advanced streams", "--operation-file", operationFile))
        {
            var output = await request.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await request.WaitForExitAsync();
            using var response = JsonDocument.Parse(output);
            Assert.Equal("pending", response.RootElement.GetProperty("status").GetString());
        }
        Approve(harness, Assert.Single(harness.Runtime.PendingRequests()));
        string reservation;
        using (var wait = Start("wait"))
        {
            var output = await wait.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await wait.WaitForExitAsync();
            using var response = JsonDocument.Parse(output);
            Assert.Equal("allowed", response.RootElement.GetProperty("status").GetString());
            reservation = response.RootElement.GetProperty("reservation").GetString()!;
        }
        using var run = Start("run", "--reservation", reservation, "--output-mode", "passthrough");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var payload = new byte[] { 0, 255, 13, 10, 42 };
        // Capture raw bytes without text conversion or a broker JSON envelope.
        using var captured = new MemoryStream();
        var outputTask = run.StandardOutput.BaseStream.CopyToAsync(captured, timeout.Token);
        await run.StandardInput.BaseStream.WriteAsync(payload, timeout.Token);
        run.StandardInput.Close();
        await outputTask;
        await run.WaitForExitAsync(timeout.Token);
        Assert.Equal(payload, captured.ToArray());
        Assert.Equal("child error", await run.StandardError.ReadToEndAsync(timeout.Token));
        Assert.Equal(301, run.ExitCode);
    }

    [Fact]
    public async Task ClosingWrapperTerminatesApprovedChild()
    {
        await using var harness = new CliHarness(_directory);
        using var process = StartCli(harness, "--", harness.PowerShell, "-NoProfile", "-Command",
            "[Console]::Out.WriteLine($PID); [Console]::Out.Flush(); Start-Sleep -Seconds 60");
        Approve(harness, await harness.WaitForRequestAsync(process));
        var pid = int.Parse((await process.StandardOutput.ReadLineAsync()
            .WaitAsync(TimeSpan.FromSeconds(10)))!);
        using var child = Process.GetProcessById(pid);
        try
        {
            process.Kill();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(child.HasExited);
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task JsonOutputRemainsOptInAndRedactsInjectedValues()
    {
        await using var harness = new CliHarness(_directory);
        using var process = StartCli(harness, "--output-mode", "json", "--",
            harness.CommandShell, "/d", "/c", "echo %SYNTHETIC_TOKEN%");
        Approve(harness, await harness.WaitForRequestAsync(process));
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        using var result = JsonDocument.Parse(output);
        Assert.Equal("completed", result.RootElement.GetProperty("status").GetString());
        Assert.Equal("[REDACTED]", result.RootElement.GetProperty("execution")
            .GetProperty("standardOutput").GetString()!.Trim());
        Assert.Equal(string.Empty, await process.StandardError.ReadToEndAsync());
    }

    [Fact]
    public async Task ExecInheritsCallerEnvironmentAndCurrentDirectory()
    {
        await using var harness = new CliHarness(_directory);
        var marker = "caller-" + Guid.NewGuid().ToString("N");
        var callerEnvironment = new Dictionary<string, string>
        {
            ["JOYDEX_CALLER_MARKER"] = marker,
            ["JOYDEX_EXPECTED_MARKER"] = marker,
            ["JOYDEX_EXPECTED_CWD"] = _directory,
            ["JOYDEX_EXPECTED_PATH"] = Environment.SystemDirectory,
            ["APPDATA"] = _directory,
            ["PATH"] = Environment.SystemDirectory,
            ["SYNTHETIC_TOKEN"] = "caller-original",
        };
        using var process = StartCli(harness, callerEnvironment, _directory,
            "--", harness.PowerShell, "-NoProfile", "-Command",
            "if ($env:JOYDEX_CALLER_MARKER -ne $env:JOYDEX_EXPECTED_MARKER "
            + "-or $env:SYNTHETIC_TOKEN -ne 'synthetic-value' "
            + "-or $env:APPDATA -ne $env:JOYDEX_EXPECTED_CWD "
            + "-or $env:PATH -ne $env:JOYDEX_EXPECTED_PATH "
            + "-or [Environment]::CurrentDirectory -ne $env:JOYDEX_EXPECTED_CWD) "
            + "{ exit 8 }; [Console]::Write('ok')");
        Approve(harness, await harness.WaitForRequestAsync(process));

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("ok", output);
        Assert.Empty(error);
        var audit = File.ReadAllText(SecretsPaths.GetAuditPath(_directory));
        Assert.DoesNotContain(marker, audit, StringComparison.Ordinal);
        Assert.DoesNotContain("caller-original", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeniedExecLeavesStdoutEmptyAndReportsFailureOnStderr()
    {
        await using var harness = new CliHarness(_directory);
        using var process = StartCli(harness, "--", harness.CommandShell, "/d", "/c", "echo must-not-run");
        var pending = await harness.WaitForRequestAsync(process);
        harness.Runtime.Decide(pending.AttemptId, pending.DisplayChallenge, SecretsConsentChoice.No, true);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(string.Empty, await process.StandardOutput.ReadToEndAsync());
        Assert.Contains("Denied", await process.StandardError.ReadToEndAsync());
        Assert.Equal(11, process.ExitCode);
    }

    private static void Approve(CliHarness harness, SecretsPendingRequest pending) =>
        harness.Runtime.Decide(pending.AttemptId, pending.DisplayChallenge, SecretsConsentChoice.Yes, true);

    private static Process StartCli(CliHarness harness, params string[] tail) =>
        StartCli(harness, null, null, tail);

    private static Process StartCli(CliHarness harness,
        IReadOnlyDictionary<string, string>? environment,
        string? workingDirectory,
        params string[] tail) => StartHelper(harness, environment, workingDirectory,
            new[] { "exec", "--data-root", harness.DataRoot, "--client", "codex",
                "--project", "joydex", "--reason", "Test CLI streams", "--secret", "SYNTHETIC_TOKEN",
                "--approval-timeout", "10", "--execution-timeout", "15" }.Concat(tail));

    private static Process StartHelper(CliHarness harness,
        IReadOnlyDictionary<string, string>? environment, string? workingDirectory, IEnumerable<string> arguments)
    {
        // Run the CLI with its own dependency files, not the test project's copied apphost.
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var cliPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../tools/Joydex.Secrets.Cli/bin", configuration, "net8.0-windows/joydex-secrets.exe"));
        var start = new ProcessStartInfo(cliPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        if (environment is not null)
        {
            foreach (var variable in environment)
                start.Environment[variable.Key] = variable.Value;
        }
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }

    [Fact]
    public async Task ExecPreservesArgumentBoundariesAndReturnsApprovedChildExitCode()
    {
        await using var harness = new CliHarness(_directory);
        var command = harness.StartExecAsync(
            "--approval-timeout", "10",
            "--", harness.CommandShell, "/d", "/c",
            "if defined SYNTHETIC_TOKEN (exit /b 7) else (exit /b 8)");
        var pending = await harness.WaitForRequestAsync();

        var approved = harness.Runtime.Decide(
            pending.AttemptId,
            pending.DisplayChallenge,
            SecretsConsentChoice.Yes,
            requireOperation: true);

        Assert.Equal(SecretsRequestStatus.Allowed, approved.Status);
        Assert.Equal(7, await command);
        Assert.Equal(["SYNTHETIC_TOKEN"], pending.EnvironmentVariables);
        Assert.Contains("/d /c", pending.CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FirstExecCreatesItsLocalRequesterAndReachesConsentWithoutSetup()
    {
        await using var harness = new CliHarness(_directory);

        var command = harness.StartExecAsync(
            "--approval-timeout", "10",
            "--", harness.CommandShell, "/d", "/c", "exit /b 0");
        var pending = await harness.WaitForRequestAsync();

        var requester = Assert.Single(new NamedClientRegistry(
            SecretsPaths.GetClientsPath(_directory)).List());
        Assert.Equal("codex", requester.ClientId);
        Assert.Equal("Codex", requester.DisplayLabel);
        Assert.Equal(["joydex"], requester.ProjectReferences);
        var credential = new NamedClientCredentialStore(
            SecretsPaths.GetCredentialDirectory(_directory)).Load("codex");
        try
        {
            Assert.Equal(32, credential.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credential);
        }

        harness.Runtime.Decide(
            pending.AttemptId,
            pending.DisplayChallenge,
            SecretsConsentChoice.Yes,
            requireOperation: true);
        Assert.Equal(0, await command);
    }

    [Fact]
    public async Task ExecCanContinueWithCallerEnvironmentAfterApprovalTimeout()
    {
        await using var harness = new CliHarness(_directory);
        var inherited = Environment.GetEnvironmentVariable("SYNTHETIC_TOKEN");
        int exitCode;
        try
        {
            Environment.SetEnvironmentVariable("SYNTHETIC_TOKEN", "must-not-leak");
            exitCode = await harness.StartExecAsync(
                "--approval-timeout", "1",
                "--on-approval-timeout", "run-without-secrets",
                "--", harness.CommandShell, "/d", "/c",
                "if defined SYNTHETIC_TOKEN (exit /b 8) else (exit /b 9)");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SYNTHETIC_TOKEN", inherited);
        }

        var pending = Assert.Single(harness.Runtime.PendingRequests());
        Assert.Equal(8, exitCode);
        Assert.Equal(SecretsDetachReason.RunWithoutSecrets, pending.DetachedReason);
        Assert.DoesNotContain(
            harness.Audit.ReadAll(),
            record => record.Kind == SecretsAuditEventKind.LaunchCommitted);
    }

    [Fact]
    public async Task ExecCancelsByDefaultWhenApprovalTimesOut()
    {
        await using var harness = new CliHarness(_directory);
        var marker = Path.Combine(_directory, "cancelled-command.txt");

        var exitCode = await harness.StartExecAsync(
            "--approval-timeout", "1",
            "--", harness.CommandShell, "/d", "/c",
            $"echo launched>\"{marker}\"");

        Assert.Equal(11, exitCode);
        Assert.Empty(harness.Runtime.PendingRequests());
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task ExecStopsAnApprovedChildAtTheRequestedExecutionTimeout()
    {
        await using var harness = new CliHarness(_directory);
        var command = harness.StartExecAsync(
            "--approval-timeout", "10",
            "--execution-timeout", "1",
            "--", harness.PowerShell, "-NoProfile", "-Command",
            "Start-Sleep -Seconds 10");
        var pending = await harness.WaitForRequestAsync();
        harness.Runtime.Decide(
            pending.AttemptId,
            pending.DisplayChallenge,
            SecretsConsentChoice.Yes,
            requireOperation: true);

        Assert.Equal(11, await command);
        Assert.Contains(
            harness.Audit.ReadAll(),
            record => record.Kind == SecretsAuditEventKind.LaunchTerminated);
    }

    [Fact]
    public async Task ExecCanLeavePolicyReviewOpenWithoutLaunchingTheCommand()
    {
        await using var harness = new CliHarness(_directory);
        var marker = Path.Combine(_directory, "must-not-exist.txt");

        var exitCode = await harness.StartExecAsync(
            "--approval-timeout", "1",
            "--on-approval-timeout", "leave-pending",
            "--", harness.CommandShell, "/d", "/c",
            $"echo launched>\"{marker}\"");

        var pending = Assert.Single(harness.Runtime.PendingRequests());
        Assert.Equal(12, exitCode);
        Assert.Equal(SecretsDetachReason.LeavePending, pending.DetachedReason);
        Assert.False(File.Exists(marker));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class CliHarness : IAsyncDisposable
    {
        private const string ClientId = "codex";
        private const string ProjectReference = "joydex";
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _serverTask;

        public CliHarness(string dataRoot)
        {
            DataRoot = dataRoot;
            CommandShell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe");
            PowerShell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            var envPath = Path.Combine(dataRoot, ".env");
            File.WriteAllText(envPath, "SYNTHETIC_TOKEN=synthetic-value" + Environment.NewLine);
            new SecretsConfigurationStore(SecretsPaths.GetConfigurationPath(dataRoot)).UpsertEnvSource(
                null,
                "Test source",
                envPath,
                new Dictionary<string, string> { ["SYNTHETIC_TOKEN"] = "SYNTHETIC_TOKEN" });
            Runtime = new SecretsBrokerRuntime(dataRoot);
            var server = new SecretsBrokerPipeServer(SecretsBrokerEndpoint.Create(dataRoot), Runtime);
            _serverTask = server.RunAsync(_cancellation.Token);
            Audit = new SecretsAuditJournal(SecretsPaths.GetAuditPath(dataRoot));
        }

        public string DataRoot { get; }
        public string CommandShell { get; }
        public string PowerShell { get; }
        public SecretsBrokerRuntime Runtime { get; }
        public SecretsAuditJournal Audit { get; }

        public Task<int> StartExecAsync(params string[] tail) => Joydex.Secrets.Cli.Program.RunAsync([
            "exec",
            "--data-root", DataRoot,
            "--client", ClientId,
            "--project", ProjectReference,
            "--reason", "Exercise the direct CLI path.",
            "--secret", "SYNTHETIC_TOKEN",
            ..tail]);

        public async Task<SecretsPendingRequest> WaitForRequestAsync(Process? process = null)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (process?.HasExited == true) throw new InvalidOperationException(await process.StandardError.ReadToEndAsync());
                var pending = Runtime.PendingRequests().SingleOrDefault();
                if (pending is not null) return pending;
                await Task.Delay(25);
            }
            throw new TimeoutException("The CLI did not submit its request.");
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            try { await _serverTask; }
            catch (OperationCanceledException) { }
            _cancellation.Dispose();
        }
    }
}
