using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NetPI.Tools.Shell;

/// <summary>
/// One launched command, plus the Windows job object that keeps it and everything it spawns
/// reachable as a unit.
/// <para>
/// Why this exists: <see cref="Process.Kill(bool)"/> with <c>entireProcessTree: true</c> walks the
/// Windows parent-pid chain. The MSYS runtime does not keep that chain intact — a background child of
/// <c>bash</c> (a <c>sleep 60 &amp;</c> left running) is re-parented, so it never appears as a descendant
/// and survives the "tree" kill. A survivor keeps the inherited stdout pipe open, so the caller waits for
/// the whole of its output no matter how long it lives. A job object kills by membership, not by
/// parentage, so it reaches those processes.
/// </para>
/// <para>
/// The job is deliberately <b>not</b> kill-on-close: a command may exit and leave a daemon behind on
/// purpose, and that must keep working. <see cref="Kill"/> is the only thing that terminates the group.
/// </para>
/// </summary>
internal sealed class ProcessTree : IDisposable
{
    private readonly Process _process;
    private IntPtr _job; // Windows job handle; zero on other platforms, or when assignment was refused

    private ProcessTree(Process process, IntPtr job) { _process = process; _job = job; }

    /// <summary>Put the process (and anything it spawns) in a job, so a kill can reach all of it.</summary>
    public static ProcessTree Attach(Process process)
    {
        var job = IntPtr.Zero;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                job = JobHandle.Create();
                // The process has to be in the job before it spawns anything. bash needs tens of
                // milliseconds to start up, so this lands first; a child that somehow escaped would
                // still be caught by the tree kill in Kill().
                if (job == IntPtr.Zero || !JobHandle.Assign(job, process.Handle)) job = Close(job);
            }
            catch { job = IntPtr.Zero; }
        }
        return new ProcessTree(process, job);
    }

    /// <summary>Terminate the process and every descendant. Safe to call more than once.</summary>
    public void Kill()
    {
        if (_job != IntPtr.Zero)
        {
            try { JobHandle.Terminate(_job); return; } catch { /* fall through to the tree kill */ }
        }
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (NotSupportedException) { }
    }

    /// <summary>Release the job handle. Never terminates anything: a detached child keeps running.</summary>
    public void Dispose()
    {
        var job = _job;
        _job = IntPtr.Zero;
        if (job != IntPtr.Zero) Close(job);
    }

    private static IntPtr Close(IntPtr job)
    {
        if (job != IntPtr.Zero) { try { JobHandle.Close(job); } catch { } }
        return IntPtr.Zero;
    }

    private static class JobHandle
    {
        const int ExtendedLimitInformation = 9;

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimitInformation
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimitInformationInfo
        {
            public BasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        public static IntPtr Create()
        {
            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            // No JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: a command may leave a daemon behind on purpose.
            var info = new ExtendedLimitInformationInfo
            {
                BasicLimitInformation = new BasicLimitInformation { LimitFlags = 0 }
            };
            var size = Marshal.SizeOf<ExtendedLimitInformationInfo>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(job, ExtendedLimitInformation, ptr, (uint)size))
                {
                    CloseHandle(job);
                    return IntPtr.Zero;
                }
            }
            finally { Marshal.FreeHGlobal(ptr); }
            return job;
        }

        public static bool Assign(IntPtr job, IntPtr process) => AssignProcessToJobObject(job, process);
        public static void Terminate(IntPtr job) => TerminateJobObject(job, 1);
        public static void Close(IntPtr job) => CloseHandle(job);
    }
}
