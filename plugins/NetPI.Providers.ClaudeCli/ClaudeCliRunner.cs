using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace NetPI.Providers.ClaudeCli;

/// <summary>One CLI invocation: what to run, what to feed it on stdin.</summary>
internal sealed record CliCall(string Executable, IReadOnlyList<string> Args, string Stdin, string WorkDir, TimeSpan Timeout);

/// <summary>Produces the CLI's stdout lines. Replaced in tests; <see cref="ProcessRunner"/> is the real one.</summary>
internal delegate IAsyncEnumerable<string> CliRunner(CliCall call, CancellationToken ct);

internal static class ProcessRunner
{
    /// <summary>Variables that would make the CLI bill an API key instead of using the subscription login.</summary>
    private static readonly string[] BillingVariables = ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN"];

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
        foreach (var v in BillingVariables) psi.Environment.Remove(v);
        psi.Environment["CLAUDE_CODE_DISABLE_AUTO_MEMORY"] = "1"; // the owner's own Claude Code notes are not this agent's memory

        using var proc = new Process { StartInfo = psi };
        try { proc.Start(); }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            throw new ModelException(
                $"Claude CLI '{call.Executable}' could not be started ({ex.Message}). Install Claude Code, run `claude auth login` once, or set providers.claudeCli.executable.",
                false, null, "cli_not_found", ex);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(call.Timeout);
        using var kill = cts.Token.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch { /* already gone */ } });

        var stderr = new StringBuilder();
        var errTask = Task.Run(async () =>
        {
            try { while (await proc.StandardError.ReadLineAsync().ConfigureAwait(false) is { } l) { lock (stderr) stderr.AppendLine(l); } }
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
        if (cts.IsCancellationRequested)
            throw new ModelException($"Claude CLI ran longer than {call.Timeout.TotalSeconds:0}s and was stopped (providers.claudeCli.timeoutSeconds).", false, null, "timeout");
        if (!any && proc.ExitCode != 0)
        {
            string err; lock (stderr) err = stderr.ToString().Trim();
            throw new ModelException($"Claude CLI exited with code {proc.ExitCode} and no output. {J.Truncate(err, 800)}".TrimEnd(), false, null, "cli_failed");
        }
    }
}
