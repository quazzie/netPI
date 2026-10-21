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

        // the consumer may stop early (the provider breaks on the result line) or be cancelled: the CLI must not outlive that
        try
        {
            var stderr = new StringBuilder();
            var errTask = Task.Run(async () =>
            {
                try { while (await proc.StandardError.ReadLineAsync().ConfigureAwait(false) is { } l) { lock (stderr) { if (stderr.Length < 8000) stderr.AppendLine(l); } } }
                catch { /* the process was killed */ }
            });

            // written on its own task: a CLI that answers before it has read a large prompt must not deadlock us
            var inTask = Task.Run(async () =>
            {
                try
                {
                    await proc.StandardInput.WriteAsync(call.Stdin).ConfigureAwait(false);
                    proc.StandardInput.Close();
                }
                catch (Exception) { /* the CLI exited before reading its input: its output says why */ }
            });

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
            await inTask.ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            string err; lock (stderr) err = stderr.ToString().Trim();
            if (cts.IsCancellationRequested)
                throw new ModelException($"Claude CLI ran longer than {call.Timeout.TotalSeconds:0}s and was stopped (providers.claudeCli.timeoutSeconds).", false, null, "timeout");
            var exit = SafeExitCode(proc);
            if (!any && exit != 0)
                throw new ModelException($"Claude CLI exited with code {(exit?.ToString() ?? "unknown")} and no output. {J.Truncate(err, 800)}".TrimEnd(), false, null, "cli_failed");
            // what the CLI said on stderr goes to the stream as a last line: the provider adds it when the run ended without a result
            if (err.Length > 0) yield return new System.Text.Json.Nodes.JsonObject { ["type"] = StderrLine, ["text"] = J.Truncate(err, 1500) }.ToJsonString();
        }
        finally
        {
            // waited for, so the chat's next call (ChatLocks) never starts beside a CLI that is still going down
            try { if (!proc.HasExited) { proc.Kill(entireProcessTree: true); proc.WaitForExit(5000); } } catch { /* already gone */ }
        }
    }

    static int? SafeExitCode(Process p)
    {
        try { p.WaitForExit(2000); return p.HasExited ? p.ExitCode : null; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>The <c>type</c> of the synthetic last line that carries the CLI's stderr.</summary>
    internal const string StderrLine = "netpi_stderr";
}
