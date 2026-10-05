using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace NetPI.Workspaces;

/// <summary>
/// Runs the verify command of a gated integrate (<c>workspaces.integrate { verify }</c>) in a worktree: a build and
/// tests before a branch may reach the project's branch. One command, stdin closed, stdout and stderr together, one
/// deadline after which the whole process tree is ended, and the tail of the output kept for the answer.
/// </summary>
public static class VerifyRunner
{
    public const int MaxTimeoutSeconds = 3600;
    public const int DefaultTimeoutSeconds = 900;
    /// <summary>How much of the output's end the answer carries.</summary>
    public const int TailChars = 6000;

    public sealed record Outcome(string Shell, int? ExitCode, bool TimedOut, long DurationMs, string Tail, string? StartError)
    {
        public bool Passed => StartError is null && !TimedOut && ExitCode == 0;
    }

    /// <param name="shell"><c>pwsh</c> (default on Windows, Windows PowerShell when pwsh is missing), <c>cmd</c>, or
    /// <c>sh</c> (default elsewhere).</param>
    public static async Task<Outcome> RunAsync(string command, string cwd, string? shell, TimeSpan timeout, CancellationToken ct)
    {
        shell = (shell ?? (OperatingSystem.IsWindows() ? "pwsh" : "sh")).Trim().ToLowerInvariant();
        string[][] launches = shell switch
        {
            "pwsh" or "powershell" => [["pwsh", "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command],
                                       ["powershell", "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command]],
            "cmd" when OperatingSystem.IsWindows() => [["cmd.exe", "/d", "/s", "/c", command]],
            "sh" or "bash" when !OperatingSystem.IsWindows() => [["/bin/sh", "-c", command]],
            _ => [],
        };
        if (launches.Length == 0)
            return new Outcome(shell, null, false, 0, "", $"verifyShell {shell} is not offered here: use {(OperatingSystem.IsWindows() ? "pwsh or cmd" : "sh")}.");

        string? startError = null;
        foreach (var argv in launches)
        {
            var psi = new ProcessStartInfo(argv[0])
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
            foreach (var a in argv.Skip(1)) psi.ArgumentList.Add(a);
            psi.Environment["NO_COLOR"] = "1";
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            Process process;
            try { process = Process.Start(psi)!; }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                startError = $"{argv[0]} could not be started: {ex.Message}";
                continue; // pwsh missing: Windows PowerShell
            }
            return await WaitAsync(process, shell, timeout, ct).ConfigureAwait(false);
        }
        return new Outcome(shell, null, false, 0, "", startError);
    }

    private static async Task<Outcome> WaitAsync(Process process, string shell, TimeSpan timeout, CancellationToken ct)
    {
        using (process)
        {
            var sw = Stopwatch.StartNew();
            var tail = new StringBuilder();
            var gate = new object();
            void Keep(string? line)
            {
                if (line is null) return;
                lock (gate)
                {
                    tail.Append(line).Append('\n');
                    if (tail.Length > TailChars * 2) tail.Remove(0, tail.Length - TailChars);
                }
            }
            process.OutputDataReceived += (_, e) => Keep(e.Data);
            process.ErrorDataReceived += (_, e) => Keep(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try { process.StandardInput.Close(); } catch { }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            var timedOut = false;
            try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                timedOut = !ct.IsCancellationRequested;
                try { process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch { }
                ct.ThrowIfCancellationRequested();
            }
            string text;
            lock (gate) text = tail.Length > TailChars ? tail.ToString(tail.Length - TailChars, TailChars) : tail.ToString();
            return new Outcome(shell, timedOut ? null : process.ExitCode, timedOut, sw.ElapsedMilliseconds, text.TrimEnd('\n'), null);
        }
    }
}
