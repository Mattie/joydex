using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Joydex.RuntimeHost.Plugins.Voice;

internal interface IVoiceWorkerJob : IDisposable
{
    int ActiveProcessCount { get; }

    void Assign(IVoiceWorkerNativeProcess process);

    void Terminate();
}

internal sealed class VoiceWorkerJob : IVoiceWorkerJob
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private readonly SafeJobHandle _handle;
    private int _disposed;

    private VoiceWorkerJob(SafeJobHandle handle) => _handle = handle;

    public static VoiceWorkerJob Create()
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            throw Win32Failure("The Voice worker job could not be created.");
        }
        try
        {
            var information = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };
            if (!SetInformationJobObject(
                    handle,
                    JobObjectInfoType.ExtendedLimitInformation,
                    ref information,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                throw Win32Failure("The Voice worker job lifetime policy could not be configured.");
            }
            return new VoiceWorkerJob(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Assign(IVoiceWorkerNativeProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!AssignProcessToJobObject(_handle, process.ProcessHandle))
        {
            throw Win32Failure("The Voice worker process could not be assigned to its lifetime job.");
        }
    }

    public void Terminate()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!TerminateJobObject(_handle, 1))
        {
            throw Win32Failure("The Voice worker job could not be terminated.");
        }
    }

    public int ActiveProcessCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!QueryInformationJobObject(
                    _handle,
                    JobObjectInfoType.BasicAccountingInformation,
                    out JobObjectBasicAccountingInformation information,
                    (uint)Marshal.SizeOf<JobObjectBasicAccountingInformation>(),
                    out _))
            {
                throw Win32Failure("The Voice worker job state could not be inspected.");
            }
            return checked((int)information.ActiveProcesses);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _handle.Dispose();
        }
    }

    private static Win32Exception Win32Failure(string message)
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{message} Win32 error {error}.");
    }

    private enum JobObjectInfoType
    {
        BasicAccountingInformation = 1,
        ExtendedLimitInformation = 9,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeJobHandle() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern SafeJobHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle job,
        JobObjectInfoType informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        SafeJobHandle job,
        JobObjectInfoType informationClass,
        out JobObjectBasicAccountingInformation information,
        uint informationLength,
        out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
        SafeJobHandle job,
        SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
