using System.ComponentModel;
using System.Diagnostics;
using System.Text;

// NOTE: mirrored in plugins/NetPI.Tools.Files and plugins/NetPI.Workspaces (plugins cannot share code). Keep the copies in sync.
namespace NetPI.Workspaces;

/// <summary>
/// Starting git: the one shape Files (files.git, files.commits) and Workspaces (the probes, provisioning) use for it —
/// <c>-c core.quotepath=off</c> so paths come back as they are, not octal-escaped; GIT_OPTIONAL_LOCKS=0 and
/// GIT_TERMINAL_PROMPT=0; stdin closed (no git command may wait on a terminal); UTF-8 streams; one deadline over
/// start, read and wait; and when the deadline ends the whole process tree, so a helper git started cannot outlive
/// the call and hold a pipe open.
/// </summary>
internal static class GitRunner
{
    /// <summary>The git executable (tests point it at a fake).</summary>
    public static string Executable { get; set; } = "git";

    /// <summary>How long a git call may take before its tree is ended (tests shorten it).</summary>
    public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// What one git call produced. When <see cref="StartError"/> is set git did not start (no git on PATH, an
    /// unreadable directory); <see cref="TimedOut"/> and <see cref="Aborted"/> are the two endings that are not
    /// git's own exit code, and <see cref="Cut"/> says the stdout stopped at its bound.
    /// </summary>
    public sealed record Result(int ExitCode, string Stdout, string Stderr, bool TimedOut, bool Aborted, bool Cut, string? StartError);

    /// <param name="maxStdoutChars">How much of stdout to keep: 0 is all of it.</param>
    public static async Task<Result> RunAsync(string cwd, CancellationToken ct, int maxStdoutChars, params string[] args)
    {
        var psi = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.quotepath=off");
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        Process process;
        try { process = Process.Start(psi)!; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
        {
            return new Result(0, "", "", false, false, false, ex.Message); // no git on PATH, or the directory is not runnable
        }
        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            var stdout = ReadAsync(process.StandardOutput, maxStdoutChars, timeout.Token);
            var stderr = ReadAsync(process.StandardError, 0, timeout.Token);
            try
            {
                try { process.StandardInput.Close(); } catch { } // git is never given a terminal to read from
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                var (outp, cut) = await stdout.ConfigureAwait(false);
                return new Result(process.ExitCode, outp, (await stderr.ConfigureAwait(false)).Text, false, false, cut, null);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
                return ct.IsCancellationRequested
                    ? new Result(0, "", "", false, true, false, null)
                    : new Result(0, "", "", true, false, false, null);
            }
        }
    }

    /// <summary>The stream, at most <paramref name="limit"/> characters kept (0: all), reading to the end either way (a pipe that is only read slowly blocks the process).</summary>
    private static async Task<(string Text, bool Cut)> ReadAsync(StreamReader reader, int limit, CancellationToken ct)
    {
        if (limit <= 0) return (await reader.ReadToEndAsync(ct).ConfigureAwait(false), false);
        var text = new StringBuilder();
        var buffer = new char[4096];
        var cut = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            if (text.Length < limit)
            {
                var room = limit - text.Length;
                text.Append(buffer, 0, Math.Min(count, room));
                cut |= count > room;
            }
            else cut = true;
        }
        return (text.ToString(), cut);
    }
}
