using System.Runtime.InteropServices;

namespace ClaudeWorkbench.Launcher;

// A Windows Job Object with KILL_ON_JOB_CLOSE. Every process assigned to it — the host,
// the sidecar the host spawns (child processes inherit the job), and the browser window —
// dies when the job is terminated OR when the launcher process exits and the handle
// closes. That gives us "kill one, kill all" for a workspace instance, and no orphaned
// backends if the launcher itself crashes.
public sealed class JobObject : IDisposable
{
    private IntPtr handle;

    public JobObject()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("CreateJobObject failed.");
        }

        JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = new()
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };

        int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr ptr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ptr, (uint)length))
            {
                throw new InvalidOperationException("SetInformationJobObject failed.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    // Assign a running process (by handle) to the job. Must be called before the process
    // spawns the children we want captured; the host boots before it launches the sidecar,
    // so assigning right after Start captures the sidecar too.
    public void Assign(nint processHandle)
    {
        if (handle != IntPtr.Zero && processHandle != IntPtr.Zero)
        {
            AssignProcessToJobObject(handle, processHandle);
        }
    }

    public void Terminate()
    {
        if (handle != IntPtr.Zero)
        {
            TerminateJobObject(handle, 0);
        }
    }

    public void Dispose()
    {
        if (handle != IntPtr.Zero)
        {
            CloseHandle(handle); // KILL_ON_JOB_CLOSE => any remaining members die here
            handle = IntPtr.Zero;
        }
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
