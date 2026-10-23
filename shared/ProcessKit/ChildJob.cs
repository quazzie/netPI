using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NetPI.ProcessKit;

/// <summary>
/// A Windows job object that kills every process in it when the last handle closes, which is when NetPI exits for any reason.
/// Children that must not outlive NetPI join it (the Claude CLI, language servers, the agents' browser, whose launcher hands
/// the browser to a process of its own). Shared SOURCE compiled into each plugin (plugins do not share assemblies), so each
/// plugin load has a job of its own, and its handle is never closed on purpose: closing it on unload would kill the children
/// the plugin's next generation or a still-running call needs. A replaced generation's job (one kernel handle) lives until
/// the process exits, which ends whatever is left in it. Other platforms: no-op (their children get SIGHUP with the parent's terminal, and a service manager kills the group).
/// </summary>
internal static class ChildJob
{
    private static readonly nint Job = JobNative.Create();

    public static void Assign(Process p)
    {
        if (Job == 0) return;
        try { JobNative.Assign(Job, p); }
        catch { /* the process ended already, or it is in a job that forbids nesting: the owner's own cancel path still ends it */ }
    }

    /// <summary>True when the process is in the kill-on-close job (for the tests).</summary>
    public static bool Contains(Process p) => JobNative.Contains(Job, p);
}

/// <summary>
/// A job of its own for one process tree: <see cref="Kill"/> ends the process and everything it started (grandchildren of
/// cmd/npx included), and it also dies with NetPI. Dispose closes the handle, which kills whatever is still in it.
/// </summary>
internal sealed class ProcessJob : IDisposable
{
    private nint _job = JobNative.Create();

    /// <summary>Adds the process (assign right after Start; children it starts later join too).</summary>
    public void Assign(Process p)
    {
        var j = _job;
        if (j == 0) return;
        try { JobNative.Assign(j, p); } catch { /* ended already or nesting is forbidden: Kill falls back to the process tree */ }
    }

    /// <summary>Kills the whole tree: the job when there is one, always the process tree too.</summary>
    public void Kill(Process? p = null)
    {
        var j = _job;
        if (j != 0) JobNative.Terminate(j);
        try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); } catch { }
    }

    public void Dispose()
    {
        var j = Interlocked.Exchange(ref _job, 0);
        if (j != 0) JobNative.Close(j);
    }
}

internal static class JobNative
{
    public static nint Create()
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

    public static void Assign(nint job, Process p) => AssignProcessToJobObject(job, p.Handle);

    public static void Terminate(nint job) { try { TerminateJobObject(job, 1); } catch { } }

    public static void Close(nint job) { try { CloseHandle(job); } catch { } }

    public static bool Contains(nint job, Process p) =>
        job != 0 && OperatingSystem.IsWindows() && IsProcessInJob(p.Handle, job, out var inJob) && inJob;

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(nint job, uint exitCode);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

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
