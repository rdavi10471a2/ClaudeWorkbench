using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AIMonitor.MSBuild;

// A backstop that guarantees Roslyn's BuildHost cannot outlive us.
//
// WHAT PROMPTED IT
// ----------------
// MSBuildWorkspace does not evaluate projects in-process. It spawns a child
// `dotnet BuildHost-netcore/Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll` and talks to it
// over a pipe. On 2026-07-20 a BuildHost from the previous evening's session was found still running
// out of C:\ClaudeWorkBenchLive\host with no parent alive. It held file handles on the assemblies it
// had loaded, which made `publish-live.ps1 -Clean` fail: "Access to the path
// 'Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll' is denied."
//
// WHAT IS ACTUALLY VERIFIED — read this before assuming the bug is understood
// --------------------------------------------------------------------------
// The obvious theory was "kill the parent and the BuildHost is orphaned". That theory was tested
// and it is WRONG. With this job disabled, killing a live parent process (`Stop-Process -Force`
// while it was confirmed running) still resulted in the BuildHost exiting within four seconds:
// modern BuildHost notices its pipe break and shuts itself down. MSBuildWorkspaceLoader also
// disposes the workspace at every call site, which handles the graceful path.
//
// So the orphan above is real and was observed, but the code path that produces it is NOT
// identified. A plain parent kill does not reproduce it. Plausible remaining candidates — none
// confirmed — are a BuildHost wedged mid-evaluation so it never services the broken pipe, or its
// pipe handle being inherited by another process and thus never signalling EOF.
//
// WHY KEEP THIS ANYWAY
// --------------------
// Because it converts "the child usually notices and exits" into "the OS terminates the child,
// always". A Job Object with KILL_ON_JOB_CLOSE does not depend on the BuildHost being responsive,
// which is precisely the state it would have to be in to leak. Child processes inherit the job, so
// the BuildHost joins it the moment Roslyn spawns it, and the only handle to the job is the one held
// here — when this process ends, gracefully or not, Windows terminates every remaining member.
//
// Do not describe this as "the fix for the orphaned BuildHost". It is a guarantee that makes that
// class of orphan impossible; the specific defect that caused the observed one remains unexplained.
//
// This lives at the layer that creates the workspace rather than in the Launcher because the
// Launcher's job only covers instances the Launcher started — a host run from the IDE, and every
// testhost, had no job at all. Nested jobs are fine (Windows 8+): when the Launcher already placed
// the host in its own job, this one nests inside it and either job's termination still applies.
//
// Best-effort by contract. Every failure is swallowed: an orphaned BuildHost is a cleanup problem,
// but refusing to index because a job could not be created would be a functional outage.
internal static class WorkspaceChildProcessJob
{
    private static readonly object Gate = new();
    private static bool attempted;

    // Held for the process lifetime on purpose. Never dispose it — closing the handle is precisely
    // what kills the members, so it must stay open until the process itself goes away.
    private static IntPtr jobHandle;

    public static void EnsureCurrentProcessIsJobbed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (Gate)
        {
            if (attempted)
            {
                return;
            }

            attempted = true;

            try
            {
                Attach();
            }
            catch
            {
                // See the contract note above: never let cleanup plumbing break indexing.
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Attach()
    {
        IntPtr handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            return;
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
                CloseHandle(handle);
                return;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

        if (!AssignProcessToJobObject(handle, GetCurrentProcess()))
        {
            // Most likely an older Windows that cannot nest jobs, and we are already inside one.
            // In that case the outer job already gives us the guarantee we wanted.
            CloseHandle(handle);
            return;
        }

        jobHandle = handle;
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
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
