using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace NetPI.Providers.ClaudeCli;

/// <summary>One CLI invocation: what to run, what to feed it on stdin, and which variables of NetPI's environment it must not inherit.</summary>
internal sealed record CliCall(string Executable, IReadOnlyList<string> Args, string Stdin, string WorkDir, TimeSpan Timeout,
    IReadOnlyCollection<string>? HiddenEnv = null, string? ConfigDir = null);

/// <summary>Produces the CLI's stdout lines. Replaced in tests; <see cref="ProcessRunner"/> is the real one.</summary>
internal delegate IAsyncEnumerable<string> CliRunner(CliCall call, CancellationToken ct);

internal static class ProcessRunner
{
    /// <summary>
    /// Variables that would send the CLI somewhere else than the subscription login (an API key, another endpoint, a cloud
    /// provider), and NetPI's own secrets: the CLI runs shell commands of its own, and a child inherits what the server has.
    /// The caller adds the variables the settings read secrets from.
    /// </summary>
    internal static readonly string[] AlwaysHidden =
    [
        "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX",
        "CLAUDE_CODE_USE_FOUNDRY", "NETPI_TOKEN", "OPENROUTER_API_KEY", "BRAVE_API_KEY",
    ];

    public static async IAsyncEnumerable<string> Run(CliCall call, [EnumeratorCancellation] CancellationToken ct)
    {
        Directory.CreateDirectory(call.WorkDir);
        var psi = new ProcessStartInfo(call.Executable)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = call.WorkDir,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in call.Args) psi.ArgumentList.Add(a);
        foreach (var v in AlwaysHidden) psi.Environment.Remove(v);
        foreach (var v in call.HiddenEnv ?? []) psi.Environment.Remove(v);
        if (!string.IsNullOrEmpty(call.ConfigDir)) psi.Environment["CLAUDE_CONFIG_DIR"] = call.ConfigDir; // another login, another account
        psi.Environment["CLAUDE_CODE_DISABLE_AUTO_MEMORY"] = "1"; // the owner's own Claude Code notes are not this agent's memory

        using var proc = new Process { StartInfo = psi };
        try { proc.Start(); }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            throw new ModelException(
                $"Claude CLI '{call.Executable}' could not be started ({ex.Message}). Install Claude Code, run `claude auth login` once, or set providers.claudeCli.executable.",
                false, null, "cli_not_found", ex);
        }
        // a NetPI that is killed or crashes must not leave a CLI behind that keeps working (and spending the subscription)
        ChildJob.Assign(proc);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(call.Timeout);
        using var kill = cts.Token.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch { /* already gone */ } });

        var stderr = new StringBuilder();
        var errTask = Task.Run(async () =>
        {
            try { while (await proc.StandardError.ReadLineAsync().ConfigureAwait(false) is { } l) { lock (stderr) { if (stderr.Length < 8000) stderr.AppendLine(l); } } }
            catch { /* the process was killed */ }
        });

        try
        {
            await proc.StandardInput.WriteAsync(call.Stdin).ConfigureAwait(false);
            proc.StandardInput.Close();
        }
        catch (IOException) { /* the CLI exited before reading its input: its output says why */ }

        var any = false;
        while (true)
        {
            string? line;
            try { line = await proc.StandardOutput.ReadLineAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { line = null; }
            if (line is null) break;
            if (line.Length == 0) continue;
            any = true;
            yield return line;
        }

        try { await proc.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (TimeoutException) { try { proc.Kill(true); } catch { /* gone */ } }
        await errTask.ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        string err; lock (stderr) err = stderr.ToString().Trim();
        if (cts.IsCancellationRequested)
            throw new ModelException($"Claude CLI ran longer than {call.Timeout.TotalSeconds:0}s and was stopped (providers.claudeCli.timeoutSeconds).", false, null, "timeout");
        if (!any && proc.ExitCode != 0)
            throw new ModelException($"Claude CLI exited with code {proc.ExitCode} and no output. {J.Truncate(err, 800)}".TrimEnd(), false, null, "cli_failed");
        // what the CLI said on stderr goes to the stream as a last line: the provider adds it when the run ended without a result
        if (err.Length > 0) yield return new System.Text.Json.Nodes.JsonObject { ["type"] = StderrLine, ["text"] = J.Truncate(err, 1500) }.ToJsonString();
    }

    /// <summary>The <c>type</c> of the synthetic last line that carries the CLI's stderr.</summary>
    internal const string StderrLine = "netpi_stderr";
}

/// <summary>
/// A Windows job object that kills every process in it when the last handle closes, which is when NetPI exits for any reason.
/// The CLI and the shells and subagents it starts join it; without it a killed NetPI leaves a running CLI behind. Other
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
