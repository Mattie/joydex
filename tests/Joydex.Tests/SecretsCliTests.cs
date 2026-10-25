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
    public async Task SourcesDiscoversNamespacesAndAliasesWithoutValuesOrPaths()
    {
        await using var harness = new CliHarness(_directory);
        var otherPath = Path.Combine(_directory, "other.env");
        File.WriteAllText(otherPath, "SYNTHETIC_TOKEN=synthetic-other\n");
        var sources = new SecretsConfigurationStore(SecretsPaths.GetConfigurationPath(_directory)).UpsertEnvSource(
            null, "Other source", otherPath, new Dictionary<string, string> { ["SYNTHETIC_TOKEN"] = "SYNTHETIC_TOKEN" }).Sources;
        using var process = StartHelper(harness, null, _directory,
            ["sources", "--data-root", _directory, "--client", "codex", "--project", "joydex"]);
        var output = await process.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        using var document = JsonDocument.Parse(output);
        var listed = document.RootElement.GetProperty("sources").EnumerateArray().ToArray();
        Assert.Equal(2, listed.Length);
        Assert.Equal(sources.Select(source => source.SourceId).Order(), listed.Select(source => source.GetProperty("sourceId").GetGuid()).Order());
        Assert.All(listed, source =>
        {
            Assert.Contains(source.GetProperty("namespace").GetString(), new[] { "Test source", "Other source" });
            var alias = Assert.Single(source.GetProperty("aliases").EnumerateArray());
            Assert.Equal("SYNTHETIC_TOKEN", alias.GetProperty("alias").GetString());
            Assert.Contains("/SYNTHETIC_TOKEN", alias.GetProperty("qualifiedAlias").GetString());
        });
        Assert.DoesNotContain("synthetic-value", output);
        Assert.DoesNotContain("synthetic-other", output);
        Assert.DoesNotContain(".env", output);
        Assert.DoesNotContain(_directory, output);
        Assert.Empty(harness.Runtime.PendingRequests());
    }

    [Theory]
    [InlineData("sources")]
    [InlineData("aliases")]
    public async Task MaximumNamespacedAliasCatalogSurvivesTransportWithoutValuesOrPaths(string command)
    {
        await using var harness = new CliHarness(_directory);
        var configurationPath = SecretsPaths.GetConfigurationPath(_directory);
        var configuration = new SecretsConfigurationStore(configurationPath);
        var initial = Assert.Single(configuration.Read().Sources);
        var aliasesPerSource = SecretsConfigurationStore.MaximumAliases / SecretsConfigurationStore.MaximumSources;
        for (var source = 0; source < SecretsConfigurationStore.MaximumSources; source++)
        {
            var aliases = Enumerable.Range(0, aliasesPerSource).ToDictionary(
                index => new string('A', 93) + index.ToString("D3"), _ => "SYNTHETIC_TOKEN");
            configuration.UpsertEnvSource(source == 0 ? initial.SourceId : null,
                new string('\u754c', 93) + source.ToString("D3"), initial.FilePath, aliases);
        }
        // Exercise every alias metadata bound, including escaped stable IDs loaded from disk.
        var snapshot = configuration.Read();
        var sources = snapshot.Sources.Select((source, sourceIndex) => source with
        {
            Aliases = source.Aliases.Select((alias, index) => alias with
            {
                StableSecretId = new string('\u754c', 92) + (sourceIndex * aliasesPerSource + index).ToString("D4"),
                MappingGeneration = long.MaxValue,
            }).ToArray(),
        }).ToArray();
        File.WriteAllText(configurationPath, JsonSerializer.Serialize(new
        {
            SchemaVersion = SecretsConfigurationStore.CurrentSchemaVersion,
            snapshot.Epoch,
            Sources = sources,
            snapshot.Recipes,
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        using var process = StartHelper(harness, null, _directory,
            [command, "--data-root", _directory, "--client", "codex", "--project", "joydex"]);
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        var output = await outputTask;
        Assert.Equal(string.Empty, await errorTask);
        Assert.Equal(0, process.ExitCode);
        using var document = JsonDocument.Parse(output);
        var aliasesListed = command == "sources"
            ? document.RootElement.GetProperty("sources").EnumerateArray()
                .SelectMany(source => source.GetProperty("aliases").EnumerateArray()).ToArray()
            : document.RootElement.GetProperty("aliases").EnumerateArray().ToArray();
        Assert.Equal(SecretsConfigurationStore.MaximumAliases, aliasesListed.Length);
        Assert.All(aliasesListed, alias => Assert.True(alias.GetProperty("available").GetBoolean()));
        Assert.DoesNotContain("synthetic-value", output);
        Assert.DoesNotContain(initial.FilePath, output);
        Assert.DoesNotContain(JsonSerializer.Serialize(initial.FilePath), output);
        Assert.Empty(harness.Runtime.PendingRequests());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecSelectsSourceAndMapsAliasToDestination(bool useSourceId)
    {
        await using var harness = new CliHarness(_directory);
        var otherPath = Path.Combine(_directory, "project.env");
        File.WriteAllText(otherPath, "TOKEN=synthetic-project\n");
        var source = new SecretsConfigurationStore(SecretsPaths.GetConfigurationPath(_directory)).UpsertEnvSource(
            null, "Project", otherPath, new Dictionary<string, string>
            {
                ["SYNTHETIC_TOKEN"] = "TOKEN", ["PROJECT_TOKEN"] = "TOKEN",
            }).Sources.Last();
        using var process = StartHelper(harness, new Dictionary<string, string> { ["PROJECT_TOKEN"] = "" }, _directory,
            ["exec", "--data-root", _directory, "--client", "codex", "--project", "joydex", "--reason", "Test source selection",
             "--source", useSourceId ? source.SourceId.ToString() : "project", "--secret", "SYNTHETIC_TOKEN=PROJECT_TOKEN",
             "--", harness.PowerShell, "-NoProfile", "-Command",
             "if ($env:SYNTHETIC_TOKEN -eq 'synthetic-project' -and !$env:PROJECT_TOKEN) {exit 0} else {exit 9}"]);
        var pending = await harness.WaitForRequestAsync(process);
        var binding = Assert.Single(pending.SecretBindings!);
        Assert.Equal("Project", binding.SourceName);
        Assert.Equal(source.SourceId, binding.SourceId);
        Assert.Equal("PROJECT_TOKEN", binding.Alias);
        Assert.Equal("SYNTHETIC_TOKEN", binding.EnvironmentVariable);
        Approve(harness, pending);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, process.ExitCode);
        Assert.Empty(await process.StandardOutput.ReadToEndAsync());
        Assert.Empty(await process.StandardError.ReadToEndAsync());
    }

    [Theory]
    [InlineData("synthetic_token=SYNTHETIC_TOKEN")]
    [InlineData("BAD-NAME=SYNTHETIC_TOKEN")]
    [InlineData("DESTINATION=")]
    [InlineData("DESTINATION=not-a-source/SYNTHETIC_TOKEN")]
    public async Task ExecRejectsDuplicateDestinationsAndMalformedMappings(string mapping)
    {
        await using var harness = new CliHarness(_directory);
        using var process = StartCli(harness, "--secret", mapping, "--", harness.CommandShell, "/d", "/c", "echo must-not-run");
        var error = await process.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync();
        Assert.Equal(2, process.ExitCode);
        Assert.Contains("destination", error);
        Assert.Empty(await process.StandardOutput.ReadToEndAsync());
        Assert.Empty(harness.Runtime.PendingRequests());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Missing source")]
    public async Task ExecRejectsAmbiguousOrMissingSourceBeforeConsent(string? source)
    {
        await using var harness = new CliHarness(_directory);
        var otherPath = Path.Combine(_directory, "other.env");
        File.WriteAllText(otherPath, "TOKEN=synthetic-other\n");
        new SecretsConfigurationStore(SecretsPaths.GetConfigurationPath(_directory)).UpsertEnvSource(
            null, "Other", otherPath, new Dictionary<string, string> { ["SYNTHETIC_TOKEN"] = "TOKEN" });
        using var process = StartCli(harness,
            (source is null ? Array.Empty<string>() : new[] { "--source", source })
                .Concat(["--", harness.CommandShell, "/d", "/c", "echo must-not-run"]).ToArray());
        var error = await process.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync();
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("secret_selection_unavailable", error);
        Assert.Contains(source is null ? "ambiguous" : "source", error);
        Assert.Empty(await process.StandardOutput.ReadToEndAsync());
        Assert.Empty(harness.Runtime.PendingRequests());
        Assert.DoesNotContain(harness.Audit.ReadAll(), record => record.Kind == SecretsAuditEventKind.LaunchCommitted);
    }

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
                harness.Runtime.SetConsentAvailability(SecretsConsentAvailability.Disconnected);
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

    [Fact]
    public async Task LockedConsentFailsFastWithoutTimeoutFallbackAndCanBeDiagnosedOffline()
    {
        CliHarness harness;
        var marker = Path.Combine(_directory, "must-not-launch.txt");
        await using (harness = new CliHarness(_directory))
        {
            harness.Runtime.SetConsentAvailability(SecretsConsentAvailability.Locked);
            using var process = StartCli(harness, "--request", "locked-cli-request",
                "--on-approval-timeout", "run-without-secrets", "--", harness.CommandShell, "/d", "/c", $"echo launched>\"{marker}\"");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(16, process.ExitCode);
            Assert.Empty(await process.StandardOutput.ReadToEndAsync());
            var error = await process.StandardError.ReadToEndAsync();
            Assert.Contains("consent_unavailable", error);
            Assert.Contains("locked-cli-request", error);
            Assert.False(File.Exists(marker));
            Assert.Empty(harness.Runtime.PendingRequests());
        }

        var filesBefore = Directory.GetFiles(_directory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.GetLastWriteTimeUtc);
        using var diagnose = StartHelper(harness, null, null,
            ["diagnose", "--data-root", _directory, "--last-failure", "--output-mode", "json"]);
        var output = await diagnose.StandardOutput.ReadToEndAsync();
        await diagnose.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, diagnose.ExitCode);
        using var result = JsonDocument.Parse(output);
        Assert.Equal("locked-cli-request", result.RootElement.GetProperty("requestId").GetString());
        Assert.Equal("consent_unavailable", result.RootElement.GetProperty("failure").GetProperty("code").GetString());
        Assert.Equal("not_started", result.RootElement.GetProperty("launchState").GetString());
        Assert.DoesNotContain("synthetic-value", output);
        foreach (var file in filesBefore) Assert.Equal(file.Value, File.GetLastWriteTimeUtc(file.Key));
        Assert.Equal(filesBefore.Count, Directory.GetFiles(_directory, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task DiagnosingMissingHistoryCreatesNoRequesterOrFiles()
    {
        await using var harness = new CliHarness(_directory);
        var absent = Path.Combine(_directory, "never-created");
        using var process = StartHelper(harness, null, null,
            ["diagnose", "--data-root", absent, "--request", "unknown-request", "--output-mode", "json"]);
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, process.ExitCode);
        Assert.False(Directory.Exists(absent));
        using var result = JsonDocument.Parse(output);
        Assert.Equal("unknown", result.RootElement.GetProperty("launchState").GetString());
        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("failure").ValueKind);
    }

    [Fact]
    public async Task DiagnosisDoesNotCombineClientsThatReuseARequestId()
    {
        await using var harness = new CliHarness(_directory);
        var completed = Guid.NewGuid();
        var blocked = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var committed = new SecretsAuditRecord(completed, SecretsAuditEventKind.LaunchCommitted,
            now, "shared-id", "other-client", "joydex", [], new string('a', 64),
            ClientRegistrationId: Guid.NewGuid(), ClientGeneration: 1);
        harness.Audit.Append(committed);
        harness.Audit.Append(committed with { Kind = SecretsAuditEventKind.LaunchCompleted });
        new SecretsDiagnostics(_directory).TryAppend(new(now, "failure", Guid.NewGuid(),
            Environment.ProcessId, "test", SecretsConsentAvailability.Locked, 1,
            "shared-id", blocked, SecretsFailure.ConsentUnavailable));

        using (var process = StartHelper(harness, null, null,
            ["diagnose", "--data-root", _directory, "--request", "shared-id", "--output-mode", "json"]))
        {
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, process.ExitCode);
            using var result = JsonDocument.Parse(output);
            Assert.Equal("unknown", result.RootElement.GetProperty("launchState").GetString());
            Assert.True(result.RootElement.GetProperty("ambiguous").GetBoolean());
            Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("failure").ValueKind);
        }

        foreach (var tail in new[] { new[] { "--last-failure" }, new[] { "--request", "shared-id", "--attempt", blocked.ToString() } })
        {
            using var process = StartHelper(harness, null, null,
                new[] { "diagnose", "--data-root", _directory, "--output-mode", "json" }.Concat(tail));
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, process.ExitCode);
            using var result = JsonDocument.Parse(output);
            Assert.Equal(blocked, result.RootElement.GetProperty("attemptId").GetGuid());
            Assert.Equal("not_started", result.RootElement.GetProperty("launchState").GetString());
            Assert.Empty(result.RootElement.GetProperty("launches").EnumerateArray());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncorrelatedFailureCannotSelectAnOlderAttemptOrClaimNothingRan(bool emptyGuid)
    {
        await using var harness = new CliHarness(_directory);
        var completed = Guid.NewGuid();
        var committed = new SecretsAuditRecord(completed, SecretsAuditEventKind.LaunchCommitted,
            DateTimeOffset.UtcNow, "lost-correlation", "old-client", "joydex", [], new string('b', 64),
            ClientRegistrationId: Guid.NewGuid(), ClientGeneration: 1);
        harness.Audit.Append(committed);
        harness.Audit.Append(committed with { Kind = SecretsAuditEventKind.LaunchCompleted });
        new SecretsDiagnostics(_directory).TryAppend(new(DateTimeOffset.UtcNow, "failure", Guid.NewGuid(),
            Environment.ProcessId, "test", SecretsConsentAvailability.Available, 1,
            "lost-correlation", emptyGuid ? Guid.Empty : null,
            new SecretsFailure("identity_unverified", "identity", "The requester could not be verified.")));

        foreach (var tail in new[] { new[] { "--last-failure" }, new[] { "--request", "lost-correlation" } })
        {
            using var process = StartHelper(harness, null, null,
                new[] { "diagnose", "--data-root", _directory, "--output-mode", "json" }.Concat(tail));
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, process.ExitCode);
            using var result = JsonDocument.Parse(output);
            Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("attemptId").ValueKind);
            Assert.True(result.RootElement.GetProperty("uncorrelated").GetBoolean());
            Assert.Equal(completed, Assert.Single(result.RootElement.GetProperty("candidateAttemptIds").EnumerateArray()).GetGuid());
            Assert.Equal("unknown", result.RootElement.GetProperty("launchState").GetString());
            Assert.Equal("identity_unverified", result.RootElement.GetProperty("failure").GetProperty("code").GetString());
            Assert.Equal("unknown", result.RootElement.GetProperty("failure").GetProperty("launchState").GetString());
            Assert.Empty(result.RootElement.GetProperty("launches").EnumerateArray());
        }

        using var selected = StartHelper(harness, null, null,
            ["diagnose", "--data-root", _directory, "--request", "lost-correlation", "--attempt", completed.ToString(), "--output-mode", "json"]);
        var selectedOutput = await selected.StandardOutput.ReadToEndAsync();
        await selected.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, selected.ExitCode);
        using var original = JsonDocument.Parse(selectedOutput);
        Assert.Equal("finished", original.RootElement.GetProperty("launchState").GetString());
        Assert.Equal(JsonValueKind.Null, original.RootElement.GetProperty("failure").ValueKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosisWithUnreadableLaunchHistoryDoesNotClaimNothingRan(bool committed)
    {
        await using var harness = new CliHarness(_directory);
        var attemptId = Guid.NewGuid();
        new SecretsDiagnostics(_directory).TryAppend(new(DateTimeOffset.UtcNow, "failure", Guid.NewGuid(),
            Environment.ProcessId, "test", SecretsConsentAvailability.Locked, 1,
            "damaged-history", attemptId, SecretsFailure.ConsentUnavailable));
        if (committed)
            harness.Audit.Append(new(attemptId, SecretsAuditEventKind.LaunchCommitted, DateTimeOffset.UtcNow,
                "damaged-history", "codex", "joydex", [], new string('a', 64),
                ClientRegistrationId: Guid.NewGuid(), ClientGeneration: 1));
        File.WriteAllText(SecretsPaths.GetAuditPath(_directory), "corrupt\n");
        using var process = StartHelper(harness, null, null,
            ["diagnose", "--data-root", _directory, "--request", "damaged-history", "--output-mode", "json"]);
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, process.ExitCode);
        using var result = JsonDocument.Parse(output);
        Assert.True(result.RootElement.GetProperty("incomplete").GetBoolean());
        Assert.Equal(committed ? "unconfirmed" : "unknown", result.RootElement.GetProperty("launchState").GetString());
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
