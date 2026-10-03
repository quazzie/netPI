using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace NetPI.Tools.Ssh;

/// <summary>
/// One child process under one deadline: the ssh client's probes (<c>-G</c> prints the effective configuration,
/// <c>-O check</c> inspects a socket) start a client, read its answer and wait for it, and the timeout covers the
/// start, the reads and the wait — a client that never exits must not outlive the question. The whole process tree
/// is ended at the deadline, and the answer is "no answer", not an exception: a probe that cannot get one counts as
/// "not supported", which is the direction to be wrong in.
/// </summary>
internal static class ChildProcess
{
    /// <summary>What the child produced: 255 with <see cref="StartError"/> when it did not start, and
    /// <see cref="Err"/> the client's words on stderr (the probes read their answer from it).</summary>
    internal sealed record Result(int Exit, string Err, bool TimedOut, string? StartError);

    internal static async Task<Result> RunAsync(string exe, string[] args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process process;
        try { process = Process.Start(psi) ?? throw new Win32Exception("the client did not start"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return new Result(255, ex.Message, false, ex.Message); // no client to ask, or one that cannot be started
        }
        using (process)
        {
            using var timer = new CancellationTokenSource(timeout);
            var outp = process.StandardOutput.ReadToEndAsync();
            var err = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(timer.Token).ConfigureAwait(false);
                try { await outp.ConfigureAwait(false); } catch { } // stdout of the probes is not read
                return new Result(process.ExitCode, await err.ConfigureAwait(false), false, null);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { await Task.WhenAll(outp, err).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
                return new Result(255, "the client did not answer", true, null);
            }
        }
    }
}
