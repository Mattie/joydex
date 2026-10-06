using System.Diagnostics;
using Joydex.Secrets;

namespace Joydex.Tests;

[CollectionDefinition("Detached tasks", DisableParallelization = true)]
public sealed class DetachedTaskCollection;

[Collection("Detached tasks")]
public sealed class SecretsDetachedTaskTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "joydex-task-test-" + Guid.NewGuid().ToString("N"));
    private readonly List<SecretsTaskReceipt> _tasks = [];

    public SecretsDetachedTaskTests()
    {
        Directory.CreateDirectory(_root);
        SecretsDetachedTask.TestLaunchHost = (exe, args) =>
        {
            using var process = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true });
        };
    }

    [Fact]
    public async Task AbruptAttachedOwnerTerminationKillsChildAndGrandchild()
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Joydex.Secrets.ProcessFixture.exe"))
            { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(_root);
        using var owner = Process.Start(info)!;
        try
        {
            var tree = Path.Combine(_root, "tree.txt");
            await WaitUntil(() => Task.FromResult(File.Exists(tree)));
            var pids = File.ReadAllText(tree).Split(',').Select(int.Parse).ToArray();
            Assert.Equal(2, pids.Length);
            Assert.All(pids, pid => Assert.True(Alive(pid)));
            owner.Kill(); await owner.WaitForExitAsync();
            await WaitUntil(() => Task.FromResult(pids.All(pid => !Alive(pid))));
        }
        finally { if (!owner.HasExited) { owner.Kill(); await owner.WaitForExitAsync(); } }
    }

    [Fact]
    public void InvalidPreparedJobPreventsAnyCommandExecution()
    {
        var marker = Path.Combine(_root, "must-not-exist.txt");
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
            { WorkingDirectory = _root };
        info.ArgumentList.Add("/c"); info.ArgumentList.Add($"echo ran > {marker}");
        using var invalid = new Microsoft.Win32.SafeHandles.SafeFileHandle(IntPtr.Zero, false);
        Assert.Throws<System.ComponentModel.Win32Exception>(() => SecretsOwnedProcess.Start(info, invalid));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task ForgedReceiptIdentityCannotAuthorizeStop()
    {
        var task = await Launch("Start-Sleep -Seconds 120");
        var path = Path.Combine(Path.GetDirectoryName(task.StandardOutputPath)!, "receipt.json");
        var original = File.ReadAllText(path);
        var json = System.Text.Json.Nodes.JsonNode.Parse(original)!;
        json["helperProcessId"] = Environment.ProcessId;
        File.WriteAllText(path, json.ToJsonString());
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SecretsDetachedTask.ControlAsync(_root, task.TaskId, true));
            Assert.True(Alive(task.ChildProcessId!.Value));
        }
        finally { File.WriteAllText(path, original); }
    }

    [Fact]
    public async Task DetachedSilentTaskCanBeStoppedWithoutBrokerAndRepeatedStopIsHarmless()
    {
        var task = await Launch("Start-Sleep -Seconds 120");
        Assert.Equal("running", task.State);
        Assert.Equal("running", (await SecretsDetachedTask.ControlAsync(_root, task.TaskId, false)).State);
        var stopped = await SecretsDetachedTask.ControlAsync(_root, task.TaskId, true).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("stopped", stopped.State);
        Assert.Equal("stopped", (await SecretsDetachedTask.ControlAsync(_root, task.TaskId, true)).State);
        Assert.False(Alive(task.ChildProcessId!.Value));
    }

    [Fact]
    public async Task TimeoutKillsSilentTaskAndHelperDeathKillsItsChild()
    {
        var timed = await Launch("Start-Sleep -Seconds 120", 1);
        await WaitUntil(async () => (await SecretsDetachedTask.ControlAsync(_root, timed.TaskId, false)).State == "stopped");
        Assert.False(Alive(timed.ChildProcessId!.Value));
        var killed = await Launch("Start-Sleep -Seconds 120");
        using (var helper = Process.GetProcessById(killed.HelperProcessId!.Value)) { helper.Kill(); await helper.WaitForExitAsync(); }
        await WaitUntil(() => Task.FromResult(!Alive(killed.ChildProcessId!.Value)));
        Assert.Equal("unknown", (await SecretsDetachedTask.ControlAsync(_root, killed.TaskId, false)).State);
    }

    [Fact]
    public async Task CommitFailureLaunchesNothingAndTaskIdentityCannotBeReused()
    {
        var marker = Path.Combine(_root, "ran.txt");
        var id = Guid.NewGuid().ToString("N");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Launch($"Set-Content '{marker}' ran", id: id,
            commit: () => throw new UnauthorizedAccessException("injected authorization failure")));
        Assert.False(File.Exists(marker));
        Assert.Equal("failedBeforeLaunch", (await SecretsDetachedTask.ControlAsync(_root, id, false)).State);
        await Assert.ThrowsAsync<IOException>(() => Launch($"Set-Content '{marker}' ran", id: id));
        await Assert.ThrowsAsync<IOException>(() => Launch($"Set-Content '{marker}' ran", requestId: id));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task PostCommitStartFailureRecordsTerminalAudit()
    {
        var cwd = Path.Combine(_root, "removed-cwd");
        Directory.CreateDirectory(cwd);
        var task = await Launch("exit 0", workingDirectory: cwd, commit: () => Directory.Delete(cwd));
        await WaitUntil(async () => (await SecretsDetachedTask.ControlAsync(_root, task.TaskId, false)).State == "failedBeforeLaunch");
        var journal = new SecretsAuditJournal(Path.Combine(SecretsPaths.GetSecretsRoot(_root), "audit.jsonl"));
        await WaitUntil(() => Task.FromResult(journal.ReadAll().Any(record => record.RequestId == task.TaskId
            && record.Kind == SecretsAuditEventKind.FailedBeforeLaunch)));
        Assert.Empty(journal.FindUnconfirmedLaunches());
    }

    [Fact]
    public async Task PostStartLogFailureStopsChildAndRecordsTerminalAudit()
    {
        var id = Guid.NewGuid().ToString("N");
        var task = await Launch("Start-Sleep -Seconds 120", id: id, commit: () =>
            Directory.CreateDirectory(Path.Combine(SecretsPaths.GetSecretsRoot(_root), "tasks", id, "stdout.log")));
        await WaitUntil(async () => (await SecretsDetachedTask.ControlAsync(_root, id, false)).State == "stopped");
        var journal = new SecretsAuditJournal(Path.Combine(SecretsPaths.GetSecretsRoot(_root), "audit.jsonl"));
        await WaitUntil(() => Task.FromResult(journal.ReadAll().Any(record => record.RequestId == id
            && record.Kind == SecretsAuditEventKind.LaunchTerminated)));
        Assert.False(Alive(task.ChildProcessId!.Value));
        Assert.Empty(journal.FindUnconfirmedLaunches());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FingerprintedInputStaysLockedUntilDetachedTaskFinishes(bool stop)
    {
        var input = Path.Combine(_root, "approved.ps1");
        var ready = Path.Combine(_root, "ready");
        var proceed = Path.Combine(_root, "proceed");
        File.WriteAllText(input, "[Console]::WriteLine('approved-content')");
        var task = await Launch($"[IO.File]::WriteAllText('{ready}', 'ready'); "
            + $"while (!(Test-Path '{proceed}')) {{ Start-Sleep -Milliseconds 50 }}; & '{input}'",
            fingerprintInputs: [input]);
        await WaitUntil(() => Task.FromResult(File.Exists(ready)));

        // The child is running but has not opened the approved input yet.
        Assert.Throws<IOException>(() => File.WriteAllText(input, "throw 'changed'"));
        Assert.Throws<IOException>(() => File.Move(input, input + ".old"));
        if (stop)
        {
            Assert.Equal("stopped", (await SecretsDetachedTask.ControlAsync(_root, task.TaskId, true)).State);
        }
        else
        {
            File.WriteAllText(proceed, "continue");
            await WaitUntil(async () => (await SecretsDetachedTask.ControlAsync(_root, task.TaskId, false)).State == "completed");
            Assert.Contains("approved-content", File.ReadAllText(task.StandardOutputPath));
        }
        await WaitUntil(() => Task.FromResult(!Alive(task.HelperProcessId!.Value)));
        File.WriteAllText(input, "released");
        Assert.Equal("released", File.ReadAllText(input));
    }

    [Fact]
    public async Task LogsPreserveUnicodeEnvironmentCwdAndNonzeroExitWithoutPersistingEnvironment()
    {
        var task = await Launch("[Console]::OutputEncoding=[Text.Encoding]::UTF8; [Console]::WriteLine($env:TASK_CANARY); [Console]::Error.WriteLine((Get-Location).Path); exit 7",
            environment: new() { ["TASK_CANARY"] = "synthetic-雪" });
        SecretsTaskReceipt? result = null;
        await WaitUntil(async () => { result = await SecretsDetachedTask.ControlAsync(_root, task.TaskId, false); return result.State == "completed"; });
        Assert.Equal(7, result!.ExitCode);
        Assert.Contains("synthetic-雪", File.ReadAllText(task.StandardOutputPath));
        Assert.Contains(_root, File.ReadAllText(task.StandardErrorPath));
        Assert.DoesNotContain("synthetic-雪", File.ReadAllText(Path.Combine(Path.GetDirectoryName(task.StandardOutputPath)!, "receipt.json")));
    }

    [Fact]
    public async Task HelperOwnsGrandchildrenAndRootExitRemovesRemainingDescendants()
    {
        var pidFile = Path.Combine(_root, "descendant.pid");
        var script = "$p=Start-Process powershell.exe -WindowStyle Hidden -PassThru -ArgumentList '-NoProfile -NonInteractive -Command Start-Sleep -Seconds 120'; "
            + $"[IO.File]::WriteAllText('{pidFile}', [string]$p.Id); Start-Sleep -Seconds 2";
        var task = await Launch(script);
        await WaitUntil(() => Task.FromResult(File.Exists(pidFile)));
        var descendant = int.Parse(File.ReadAllText(pidFile));
        Assert.True(Alive(descendant));
        await WaitUntil(async () => (await SecretsDetachedTask.ControlAsync(_root, task.TaskId, false)).State == "completed");
        Assert.False(Alive(descendant));
    }

    [AttendedFact]
    public async Task ExplorerLaunchHasExplorerParentAndCanBeStopped()
    {
        SecretsDetachedTask.TestLaunchHost = null;
        var task = await Launch("Start-Sleep -Seconds 120");
        using var helper = Process.GetProcessById(task.HelperProcessId!.Value);
        var info = new IntPtr[6];
        Assert.Equal(0, NtQueryInformationProcess(helper.Handle, 0, info, IntPtr.Size * info.Length, out _));
        using var parent = Process.GetProcessById(info[5].ToInt32());
        Assert.Equal("explorer", parent.ProcessName, ignoreCase: true);
        Assert.Equal("stopped", (await SecretsDetachedTask.ControlAsync(_root, task.TaskId, true)).State);
    }

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int kind, [System.Runtime.InteropServices.Out] IntPtr[] info, int size, out int returned);

    [Fact]
    public async Task LostLaunchReplyRemainsDiscoverableAndConcurrentTasksAreIndependent()
    {
        SecretsDetachedTask.TestLoseLaunchReply = true;
        var lost = await Launch("Start-Sleep -Seconds 120");
        SecretsDetachedTask.TestLoseLaunchReply = false;
        Assert.Equal("unknown", lost.State);
        await WaitUntil(async () => (await SecretsDetachedTask.ControlAsync(_root, lost.TaskId, false)).State == "running");
        var other = await Launch("Start-Sleep -Seconds 120");
        Assert.Equal("stopped", (await SecretsDetachedTask.ControlAsync(_root, lost.TaskId, true)).State);
        Assert.Equal("running", (await SecretsDetachedTask.ControlAsync(_root, other.TaskId, false)).State);
    }

    [Fact]
    public void LogRotationRetainsExactlyThreeBoundedFiles()
    {
        var path = Path.Combine(_root, "rotation.log");
        using (var log = new RotatingTaskLog(path))
        {
            var bytes = new byte[1024 * 1024];
            for (var i = 0; i < 35; i++) { Array.Fill(bytes, (byte)i); log.Write(bytes, 0, bytes.Length); }
        }
        Assert.Equal(5 * 1024 * 1024, new FileInfo(path).Length);
        Assert.Equal(10 * 1024 * 1024, new FileInfo(path + ".1").Length);
        Assert.Equal(10 * 1024 * 1024, new FileInfo(path + ".2").Length);
        Assert.False(File.Exists(path + ".3"));
        Assert.Equal(30, File.ReadAllBytes(path)[0]);
    }

    private async Task<SecretsTaskReceipt> Launch(string script, int? seconds = null, string? id = null,
        Action? commit = null, Dictionary<string, string>? environment = null, string? requestId = null,
        string? workingDirectory = null, IReadOnlyList<string>? fingerprintInputs = null)
    {
        id ??= Guid.NewGuid().ToString("N");
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        var operation = ExecOperationCanonicalizer.Resolve(new("test", "test-project", _root, "test-worktree", _root, 1), ["synthetic"],
            new ExecOperationProposal(exe, ["-NoProfile", "-NonInteractive", "-Command", script], workingDirectory ?? _root,
                new Dictionary<string, string> { ["SYNTHETIC_TOKEN"] = "synthetic" }, fingerprintInputs ?? [], SecretOutputDisclosure.None,
                Lifetime: SecretsExecutionLifetime.Detached, DetachedTimeoutSeconds: seconds, TaskId: id));
        var auditPath = Path.Combine(SecretsPaths.GetSecretsRoot(_root), "audit.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(auditPath)!);
        var audit = new SecretsAuditRecord(Guid.NewGuid(), SecretsAuditEventKind.LaunchCommitted, DateTimeOffset.UtcNow, requestId ?? id, "test", "test", []);
        var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(p => (string)p.Key, p => (string)p.Value!, StringComparer.OrdinalIgnoreCase);
        if (environment is not null) foreach (var pair in environment) env[pair.Key] = pair.Value;
        var journal = new SecretsAuditJournal(auditPath);
        var receipt = await SecretsDetachedTask.LaunchAsync(auditPath, operation, env, audit,
            () => { commit?.Invoke(); journal.Append(audit); }, CancellationToken.None);
        _tasks.Add(receipt);
        return receipt;
    }

    private static bool Alive(int pid) { try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch (ArgumentException) { return false; } }
    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await condition()) await Task.Delay(50, timeout.Token);
    }
    public void Dispose()
    {
        SecretsDetachedTask.TestLaunchHost = null;
        SecretsDetachedTask.TestLoseLaunchReply = false;
        foreach (var task in _tasks)
        {
            try { SecretsDetachedTask.ControlAsync(_root, task.TaskId, true).GetAwaiter().GetResult(); } catch { }
            try { using var helper = Process.GetProcessById(task.HelperProcessId!.Value); if (helper.StartTime.ToUniversalTime() == task.HelperStartedAt) { helper.Kill(); helper.WaitForExit(5000); } } catch { }
        }
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
