using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Joydex.Secrets;

/// <summary>A process created inside its owner's non-inheritable, kill-on-close Windows job.</summary>
internal sealed class SecretsOwnedProcess : IDisposable
{
    private static readonly ConcurrentDictionary<SecretsOwnedProcess, byte> Active = new();
    private readonly SafeFileHandle _job;
    private readonly AnonymousPipeServerStream _input;
    private readonly AnonymousPipeServerStream _output;
    private readonly AnonymousPipeServerStream _error;
    private int _disposed;
    public Process Process { get; }
    public StreamWriter StandardInput { get; }
    public StreamReader StandardOutput { get; }
    public StreamReader StandardError { get; }
    public int Id => Process.Id;
    public DateTime StartTime => Process.StartTime;
    public int ExitCode => Process.ExitCode;

    private SecretsOwnedProcess(SafeFileHandle job, Process process,
        AnonymousPipeServerStream input, AnonymousPipeServerStream output, AnonymousPipeServerStream error)
    {
        _job = job; Process = process; _input = input; _output = output; _error = error;
        StandardInput = new StreamWriter(input, new UTF8Encoding(false)) { AutoFlush = true };
        StandardOutput = new StreamReader(output);
        StandardError = new StreamReader(error);
        Active.TryAdd(this, 0);
    }

    /// <summary>Fails before user code runs if job setup, handle isolation, or creation fails.</summary>
    internal static SafeFileHandle PrepareJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw Failure("Cannot create execution job");
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        { job.Dispose(); throw Failure("Cannot configure execution job"); }
        return job;
    }

    public static SecretsOwnedProcess Start(ProcessStartInfo start, SafeFileHandle? preparedJob = null)
    {
        var job = preparedJob ?? PrepareJob();
        AnonymousPipeServerStream? input = null, output = null, error = null;
        Process? process = null;
        IntPtr attributes = IntPtr.Zero, jobValue = IntPtr.Zero, handles = IntPtr.Zero, environment = IntPtr.Zero;
        ProcessInformation created = default;
        var environmentBytes = 0;
        var attributesInitialized = false;
        try
        {
            input = new(PipeDirection.Out, HandleInheritability.Inheritable);
            output = new(PipeDirection.In, HandleInheritability.Inheritable);
            error = new(PipeDirection.In, HandleInheritability.Inheritable);
            nuint size = 0;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            if (!InitializeProcThreadAttributeList(attributes, 2, 0, ref size))
                throw Failure("Cannot initialize process attributes");
            attributesInitialized = true;
            jobValue = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(jobValue, job.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, (nuint)0x2000D, jobValue, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw Failure("Cannot attach execution job at creation");
            handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
            var inherited = new[] { input.ClientSafePipeHandle.DangerousGetHandle(), output.ClientSafePipeHandle.DangerousGetHandle(), error.ClientSafePipeHandle.DangerousGetHandle() };
            for (var i = 0; i < inherited.Length; i++) Marshal.WriteIntPtr(handles, i * IntPtr.Size, inherited[i]);
            if (!UpdateProcThreadAttribute(attributes, 0, (nuint)0x20002, handles, (nuint)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero))
                throw Failure("Cannot isolate inherited handles");
            var startup = new StartupInfoEx
            {
                Startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                    Input = inherited[0], Output = inherited[1], Error = inherited[2] },
                Attributes = attributes,
            };
            var block = string.Join('\0', start.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Where(pair => pair.Value is not null).Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
            environment = Marshal.StringToHGlobalUni(block);
            environmentBytes = checked((block.Length + 1) * 2);
            var command = new StringBuilder(string.Join(" ", new[] { start.FileName }.Concat(start.ArgumentList).Select(Quote)));
            // Suspended creation lets us retain the root process handle before even a very short command exits.
            if (!CreateProcess(start.FileName, command, IntPtr.Zero, IntPtr.Zero, true,
                    0x00080000 | 0x00000400 | 0x08000000 | 0x00000004,
                    environment, start.WorkingDirectory, ref startup, out created))
                throw Failure("Cannot create protected execution");
            process = Process.GetProcessById((int)created.ProcessId);
            _ = process.Handle;
            input.DisposeLocalCopyOfClientHandle(); output.DisposeLocalCopyOfClientHandle(); error.DisposeLocalCopyOfClientHandle();
            if (ResumeThread(created.Thread) == uint.MaxValue) throw Failure("Cannot resume protected execution");
            var result = new SecretsOwnedProcess(job, process, input, output, error);
            if (!start.RedirectStandardInput) result.StandardInput.Close();
            return result;
        }
        catch
        {
            job.Dispose(); process?.Dispose(); input?.Dispose(); output?.Dispose(); error?.Dispose();
            throw;
        }
        finally
        {
            if (created.Thread != IntPtr.Zero) CloseHandle(created.Thread);
            if (created.Process != IntPtr.Zero) CloseHandle(created.Process);
            if (environment != IntPtr.Zero) { Marshal.Copy(new byte[environmentBytes], 0, environment, environmentBytes); Marshal.FreeHGlobal(environment); }
            if (attributes != IntPtr.Zero) { if (attributesInitialized) DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
            if (jobValue != IntPtr.Zero) Marshal.FreeHGlobal(jobValue);
        }
    }

    public async Task WaitForExitAsync(CancellationToken token)
    {
        try { await Process.WaitForExitAsync(token).ConfigureAwait(false); }
        finally
        {
            // Close descendant writers before callers await output pumps, including cancellation.
            if (!TerminateAndConfirm())
            {
                _job.Dispose();
                throw new IOException("Execution descendants could not be confirmed stopped.");
            }
        }
    }

    public bool TerminateAndConfirm()
    {
        if (_job.IsClosed || _job.IsInvalid) return false;
        if (!TerminateJobObject(_job, 1)) return false;
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (!QueryInformationJobObject(_job, 1, out Accounting info, (uint)Marshal.SizeOf<Accounting>(), IntPtr.Zero)) return false;
            if (info.ActiveProcesses == 0) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    internal static void TerminateAll()
    {
        foreach (var process in Active.Keys) process.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Active.TryRemove(this, out _);
        _job.Dispose(); // Last owner handle closes even during exception unwinding.
        _input.Dispose(); _output.Dispose(); _error.Dispose(); Process.Dispose();
    }

    internal static string Quote(string value)
    {
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    private static Win32Exception Failure(string message) => new(Marshal.GetLastWin32Error(), message);

    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    { public long PerProcessTime, PerJobTime; public uint Flags; public nuint MinimumWorkingSet, MaximumWorkingSet; public uint ActiveLimit; public nuint Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { public BasicLimits Basic; public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential)] private struct Accounting
    { public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime; public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo
    { public int Size; public IntPtr Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags; public ushort Show, ReservedSize; public IntPtr ReservedBytes, Input, Output, Error; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, out Accounting info, uint length, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(SafeFileHandle job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string app, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
