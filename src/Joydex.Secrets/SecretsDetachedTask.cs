using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Joydex.Secrets;

/// <summary>Value-free receipt for one detached execution. A running receipt is not a health check.</summary>
public sealed record SecretsTaskReceipt(string TaskId, string State, DateTimeOffset? CreatedAt,
    int? HelperProcessId, DateTime? HelperStartedAt, string HelperPath,
    int? ChildProcessId, DateTime? ChildStartedAt, DateTimeOffset? Deadline,
    int? ExitCode, string StandardOutputPath, string StandardErrorPath, DateTimeOffset? FinishedAt = null);

internal sealed record DetachedLaunch(ResolvedExecOperation Operation, Dictionary<string, string> Environment,
    SecretsAuditRecord Audit, string AuditPath);

/// <summary>Explorer handoff and broker-independent control for a single-task helper.</summary>
public static class SecretsDetachedTask
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
    internal static Action<string, string>? TestLaunchHost;
    internal static bool TestLoseLaunchReply;

    internal static async Task<SecretsTaskReceipt> LaunchAsync(string auditPath, ResolvedExecOperation operation,
        Dictionary<string, string> environment, SecretsAuditRecord audit, Action commit, CancellationToken token)
    {
        var taskId = operation.TaskId!;
        var taskDirectory = TaskDirectory(Path.GetDirectoryName(auditPath)!, taskId);
        var claims = Path.Combine(Path.GetDirectoryName(auditPath)!, "task-claims");
        EnsurePrivateDirectory(claims);
        var requestIdentity = JsonSerializer.Serialize(new { audit.ClientRegistrationId, audit.ClientGeneration,
            audit.ClientReference, audit.ProjectReference, audit.RequestId });
        var requestClaim = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(requestIdentity)));
        // Request and task claims are permanent, independent of the bounded audit retention.
        using (var claim = new FileStream(Path.Combine(claims, requestClaim), FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough))
        { var id = System.Text.Encoding.UTF8.GetBytes(taskId); claim.Write(id); claim.Flush(true); }
        EnsurePrivateDirectory(taskDirectory);
        // This permanent claim remains after audit pruning and prevents replaying a task ID.
        using (var claim = new FileStream(Path.Combine(taskDirectory, "claim"), FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 4096, FileOptions.WriteThrough)) { claim.Flush(true); }
        var host = Path.Combine(AppContext.BaseDirectory, "Joydex.SecretsHost.exe");
        var receipt = new SecretsTaskReceipt(taskId, "starting", DateTimeOffset.UtcNow, null, null, host,
            null, null, null, null, Path.Combine(taskDirectory, "stdout.log"), Path.Combine(taskDirectory, "stderr.log"));
        Save(taskDirectory, receipt);
        var pipeName = "Joydex.TaskLaunch." + Guid.NewGuid().ToString("N");
        using var pipe = Server(pipeName);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(HandshakeTimeout);
        using var owner = Process.GetCurrentProcess();
        var arguments = string.Join(" ", new[] { "--task-host", taskDirectory, pipeName,
            owner.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            owner.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            owner.MainModule!.FileName }.Select(SecretsOwnedProcess.Quote));
        var committed = false;
        try
        {
            if (!File.Exists(host)) throw new FileNotFoundException("The packaged task host is missing.", host);
            await LaunchFromExplorerAsync(host, arguments).ConfigureAwait(false);
            await pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
            using var helper = Peer(pipe, server: false);
            Verify(helper, host, null, owner.SessionId);
            await WriteAsync(pipe, new DetachedLaunch(operation, environment, audit, auditPath), deadline.Token).ConfigureAwait(false);
            if (await ReadAsync<string>(pipe, deadline.Token).ConfigureAwait(false) != "ready")
                throw new InvalidDataException("Task helper did not prepare the approved execution.");
            deadline.Token.ThrowIfCancellationRequested();
            commit();
            // A failed write can still deliver commit. From here on only status may resolve uncertainty.
            committed = true;
            await WriteAsync(pipe, "commit", deadline.Token).ConfigureAwait(false);
            if (TestLoseLaunchReply) throw new IOException("Injected launch acknowledgement loss.");
            receipt = await ReadAsync<SecretsTaskReceipt>(pipe, deadline.Token).ConfigureAwait(false);
            if (receipt.TaskId != taskId || receipt.HelperProcessId != helper.Id
                || receipt.HelperStartedAt != helper.StartTime.ToUniversalTime() || receipt.State != "running")
                throw new InvalidDataException("Task helper returned an invalid launch receipt.");
            return receipt;
        }
        catch when (committed)
        {
            return receipt with { State = "unknown" };
        }
        catch
        {
            Save(taskDirectory, receipt with { State = "failedBeforeLaunch", FinishedAt = DateTimeOffset.UtcNow });
            throw;
        }
    }

    /// <summary>Runs a single task; called only by the packaged host's internal entry point.</summary>
    public static async Task<int> RunHostAsync(string taskDirectory, string pipeName, int brokerPid, long brokerTicks, string brokerPath)
    {
        using var timeout = new CancellationTokenSource(HandshakeTimeout);
        using var handoff = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await handoff.ConnectAsync(timeout.Token).ConfigureAwait(false);
        using var broker = Peer(handoff, server: true);
        using var self = Process.GetCurrentProcess();
        if (broker.Id != brokerPid) throw new UnauthorizedAccessException("Unexpected task broker.");
        Verify(broker, brokerPath, new DateTime(brokerTicks, DateTimeKind.Utc), self.SessionId);
        var launch = await ReadAsync<DetachedLaunch>(handoff, timeout.Token).ConfigureAwait(false);
        var expectedDirectory = TaskDirectory(Path.GetDirectoryName(launch.AuditPath)!, launch.Operation.TaskId!);
        if (!string.Equals(Path.GetFullPath(taskDirectory), expectedDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Task directory does not match the launch.");
        using var files = ExecOperationCanonicalizer.AcquireCurrentFiles(launch.Operation);
        using var job = SecretsOwnedProcess.PrepareJob();
        using var control = Server(ControlName(taskDirectory));
        await WriteAsync(handoff, "ready", timeout.Token).ConfigureAwait(false);
        if (await ReadAsync<string>(handoff, timeout.Token).ConfigureAwait(false) != "commit") return 2;
        // No dependency on broker/caller cancellation after this point.
        var journal = new SecretsAuditJournal(launch.AuditPath);
        var receipt = Load(taskDirectory);
        var started = false;
        SecretsOwnedProcess? child = null;
        try
        {
            var info = new ProcessStartInfo(launch.Operation.Executable)
            { WorkingDirectory = launch.Operation.WorkingDirectory, RedirectStandardInput = false };
            foreach (var argument in launch.Operation.Arguments) info.ArgumentList.Add(argument);
            info.Environment.Clear();
            foreach (var pair in launch.Environment) info.Environment[pair.Key] = pair.Value;
            child = SecretsOwnedProcess.Start(info, job);
            files.Dispose();
            started = true;
            launch.Environment.Clear(); info.Environment.Clear();
            receipt = receipt with { State = "running", HelperProcessId = self.Id, HelperStartedAt = self.StartTime.ToUniversalTime(),
                ChildProcessId = child.Id, ChildStartedAt = child.StartTime.ToUniversalTime(),
                Deadline = launch.Operation.DetachedTimeoutSeconds is { } seconds ? DateTimeOffset.UtcNow.AddSeconds(seconds) : null };
            Save(taskDirectory, receipt);
            journal.Append(launch.Audit with { Kind = SecretsAuditEventKind.LaunchStarted, Timestamp = DateTimeOffset.UtcNow,
                ProcessId = child.Id, ProcessStartedAt = child.StartTime.ToUniversalTime() });
            // Receipt loss does not terminate an execution already committed to this helper.
            try { await WriteAsync(handoff, receipt, timeout.Token).ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or OperationCanceledException) { }
            handoff.Dispose();
            using var lifetime = new CancellationTokenSource();
            if (launch.Operation.DetachedTimeoutSeconds is { } duration) lifetime.CancelAfter(TimeSpan.FromSeconds(duration));
            using var serving = new CancellationTokenSource();
            var completed = new TaskCompletionSource<SecretsTaskReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
            var controls = ServeControlAsync(control, taskDirectory, receipt, lifetime, completed.Task, serving.Token);
            using var stdout = new RotatingTaskLog(receipt.StandardOutputPath);
            using var stderr = new RotatingTaskLog(receipt.StandardErrorPath);
            async Task Pump(Stream source, Stream destination)
            {
                try { await source.CopyToAsync(destination, lifetime.Token).ConfigureAwait(false); }
                catch { lifetime.Cancel(); throw; }
            }
            try
            {
                await Task.WhenAll(Pump(child.StandardOutput.BaseStream, stdout), Pump(child.StandardError.BaseStream, stderr),
                    child.WaitForExitAsync(lifetime.Token)).ConfigureAwait(false);
                receipt = receipt with { State = "completed", ExitCode = child.ExitCode };
            }
            catch
            {
                receipt = receipt with { State = child.TerminateAndConfirm() ? "stopped" : "unknown" };
            }
            stdout.Dispose(); stderr.Dispose();
            receipt = receipt with { FinishedAt = DateTimeOffset.UtcNow };
            Save(taskDirectory, receipt);
            journal.Append(launch.Audit with { Kind = receipt.State == "completed" ? SecretsAuditEventKind.LaunchCompleted :
                receipt.State == "stopped" ? SecretsAuditEventKind.LaunchTerminated : SecretsAuditEventKind.LaunchUnconfirmed,
                Timestamp = DateTimeOffset.UtcNow, ProcessId = child.Id, ProcessStartedAt = child.StartTime.ToUniversalTime() });
            completed.TrySetResult(receipt);
            // Let a pending stop receive the confirmed terminal receipt before closing the control pipe.
            if (control.IsConnected) await Task.WhenAny(controls, Task.Delay(1000)).ConfigureAwait(false);
            serving.Cancel();
            try { await controls.ConfigureAwait(false); } catch (OperationCanceledException) { }
            return receipt.State == "unknown" ? 2 : 0;
        }
        catch
        {
            var state = !started ? "failedBeforeLaunch" : child!.TerminateAndConfirm() ? "stopped" : "unknown";
            Save(taskDirectory, receipt with { State = state, FinishedAt = DateTimeOffset.UtcNow });
            throw;
        }
        finally { child?.Dispose(); launch.Environment.Clear(); }
    }

    /// <summary>Queries or stops the exact recorded helper without needing the broker.</summary>
    public static async Task<SecretsTaskReceipt> ControlAsync(string dataRoot, string taskId, bool stop, CancellationToken token = default)
    {
        var directory = TaskDirectory(SecretsPaths.GetSecretsRoot(dataRoot), taskId);
        SecretsTaskReceipt receipt;
        try { receipt = Load(directory); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(taskId, "unknown", null, null, null, Path.Combine(AppContext.BaseDirectory, "Joydex.SecretsHost.exe"),
                null, null, null, null, Path.Combine(directory, "stdout.log"), Path.Combine(directory, "stderr.log"));
        }
        if (Terminal(receipt.State)) return receipt;
        // The helper publishes its identity before serving control. An early status call
        // must not mistake a not-yet-published identity for a forged peer.
        if (receipt.HelperProcessId is null || receipt.HelperStartedAt is null)
            return receipt with { State = "unknown" };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(stop ? 12 : 3));
        try
        {
            using var pipe = new NamedPipeClientStream(".", ControlName(directory), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            using var helper = Peer(pipe, server: true);
            if (helper.Id != receipt.HelperProcessId || receipt.HelperStartedAt is null)
                throw new UnauthorizedAccessException("The task helper identity does not match its receipt.");
            Verify(helper, receipt.HelperPath, receipt.HelperStartedAt, Process.GetCurrentProcess().SessionId);
            await WriteAsync(pipe, stop ? "stop" : "status", timeout.Token).ConfigureAwait(false);
            var result = await ReadAsync<SecretsTaskReceipt>(pipe, timeout.Token).ConfigureAwait(false);
            if (result.TaskId != taskId) throw new InvalidDataException("Unexpected task receipt.");
            return result;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            receipt = Load(directory);
            return Terminal(receipt.State) ? receipt : receipt with { State = "unknown" };
        }
    }

    private static async Task ServeControlAsync(NamedPipeServerStream pipe, string directory, SecretsTaskReceipt running,
        CancellationTokenSource lifetime, Task<SecretsTaskReceipt> completed, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
                request.CancelAfter(TimeSpan.FromSeconds(10));
                using var caller = Peer(pipe, server: false);
                if (caller.SessionId != Process.GetCurrentProcess().SessionId) throw new UnauthorizedAccessException("Wrong session.");
                var command = await ReadAsync<string>(pipe, request.Token).ConfigureAwait(false);
                SecretsTaskReceipt reply;
                if (command == "stop") { lifetime.Cancel(); reply = await completed.WaitAsync(request.Token).ConfigureAwait(false); }
                else if (command == "status") reply = Load(directory);
                else throw new InvalidDataException("Unknown task control command.");
                await WriteAsync(pipe, reply, request.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException or ArgumentException
                or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { if (pipe.IsConnected) pipe.Disconnect(); }
        }
    }

    internal static Task LaunchFromExplorerAsync(string executable, string arguments)
    {
        if (TestLaunchHost is { } launch) { launch(executable, arguments); return Task.CompletedTask; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!)!;
                dynamic windows = shell.Windows();
                object location = 0, root = 0; int handle = 0;
                dynamic desktop = windows.FindWindowSW(ref location, ref root, 8, out handle, 1);
                if (desktop is null) throw new InvalidOperationException("Explorer desktop is unavailable.");
                desktop.Document.Application.ShellExecute(executable, arguments, Path.GetDirectoryName(executable), "open", 0);
                completion.SetResult();
            }
            catch (Exception e) { completion.SetException(e); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(HandshakeTimeout);
    }

    private static string TaskDirectory(string secretsRoot, string taskId)
    {
        if (!Guid.TryParseExact(taskId, "N", out _)) throw new ArgumentException("Invalid task ID.");
        return Path.GetFullPath(Path.Combine(secretsRoot, "tasks", taskId));
    }
    private static string ControlName(string directory) => "Joydex.Task." + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(directory.ToUpperInvariant())))[..32];
    private static bool Terminal(string state) => state is "completed" or "stopped" or "failedBeforeLaunch";
    private static void Save(string directory, SecretsTaskReceipt receipt) => SecretsAtomicFile.WriteJson(Path.Combine(directory, "receipt.json"), receipt, Json);
    private static SecretsTaskReceipt Load(string directory)
    {
        // Readers must allow the helper to atomically replace its receipt on Windows.
        using var stream = new FileStream(Path.Combine(directory, "receipt.json"), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<SecretsTaskReceipt>(stream, Json) ?? throw new InvalidDataException("Task receipt is missing.");
    }
    private static NamedPipeServerStream Server(string name) => new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
    private static void EnsurePrivateDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        ExecOperationCanonicalizer.RejectReparsePoints(directory, "task directory", true);
        var user = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("No user SID.");
        var acl = new DirectorySecurity(); acl.SetOwner(user); acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(acl);
    }
    private static Process Peer(PipeStream pipe, bool server)
    {
        uint pid;
        if (!(server ? GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid) : GetNamedPipeClientProcessId(pipe.SafePipeHandle, out pid)))
            throw new UnauthorizedAccessException("Cannot identify pipe peer.");
        return Process.GetProcessById((int)pid);
    }
    private static void Verify(Process peer, string path, DateTime? start, int session)
    {
        if (peer.SessionId != session || !string.Equals(peer.MainModule?.FileName, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)
            || (start is not null && peer.StartTime.ToUniversalTime() != start)) throw new UnauthorizedAccessException("Task peer identity mismatch.");
    }
    private static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        try
        {
            if (bytes.Length > 1024 * 1024) throw new InvalidDataException("Task frame exceeds its bound.");
            var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
            await stream.WriteAsync(header, token).ConfigureAwait(false); await stream.WriteAsync(bytes, token).ConfigureAwait(false); await stream.FlushAsync(token).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > 1024 * 1024) throw new InvalidDataException("Invalid task frame.");
        var bytes = new byte[length];
        try { await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false); return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Empty task frame."); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
}

internal sealed class RotatingTaskLog(string path) : Stream
{
    private const long Limit = 10 * 1024 * 1024;
    private FileStream _file = Open(path);
    private static FileStream Open(string name) => new(name, FileMode.Append, FileAccess.Write, FileShare.Read);
    public override void Write(byte[] buffer, int offset, int count)
    {
        while (count > 0)
        {
            if (_file.Length >= Limit)
            {
                _file.Dispose();
                File.Delete(path + ".2");
                if (File.Exists(path + ".1")) File.Move(path + ".1", path + ".2");
                File.Move(path, path + ".1"); _file = Open(path);
            }
            var take = (int)Math.Min(count, Limit - _file.Length);
            _file.Write(buffer, offset, take); _file.Flush(); offset += take; count -= take;
        }
    }
    public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
    public override long Length => _file.Length; public override long Position { get => _file.Position; set => throw new NotSupportedException(); }
    public override void Flush() => _file.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _file.Dispose(); base.Dispose(disposing); }
}
