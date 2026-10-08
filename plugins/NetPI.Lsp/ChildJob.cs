using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NetPI.Lsp;

/// <summary>
/// A Windows job object that kills every process in it when the last handle closes, which is when NetPI exits for any reason.
/// The language servers join it; without it a killed NetPI leaves them running (a copy of the Claude CLI plugin's: plugins do not share code). Other
/// platforms: no-op (their children get SIGHUP with the parent's terminal, and a service manager kills the group).
/// </summary>
internal static class ChildJob
{
    private static readonly nint Job = Create();

    private static nint Create()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        try
        {
            var job = CreateJobObjectW(0, null);
            if (job == 0) return 0;
            var info = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 /* JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE */ } };
            var size = Marshal.SizeOf<ExtendedLimits>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(job, 9 /* JobObjectExtendedLimitInformation */, ptr, (uint)size)) return 0;
            }
            finally { Marshal.FreeHGlobal(ptr); }
            return job;
        }
        catch { return 0; }
    }

    public static void Assign(Process p)
    {
        if (Job == 0) return;
        try { AssignProcessToJobObject(Job, p.Handle); }
        catch { /* the process ended already, or it is in a job that forbids nesting: the cancel path still kills it */ }
    }

    /// <summary>True when the process is in the kill-on-close job (for the tests).</summary>
    public static bool Contains(Process p) =>
        Job != 0 && OperatingSystem.IsWindows() && IsProcessInJob(p.Handle, Job, out var inJob) && inJob;

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong A, B, C, D, E, F; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, nint info, uint size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);
}
