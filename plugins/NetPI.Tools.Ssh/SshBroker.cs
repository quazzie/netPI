using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace NetPI.Tools.Ssh;

/// <summary>
/// One long-lived <c>ssh</c> per host for clients that cannot multiplex (idea-pac35h): every Windows client, where OpenSSH's
/// ControlMaster needs a fork per session that the ports do not do. A call then pays one handshake (~300 ms to a LAN host,
/// measured 2026-10-03) only when no connection to the host is free, instead of on every call.
/// <para>
/// The connection runs a small POSIX <c>sh</c> loop (<see cref="Script"/>) that reads one frame per call from its stdin —
/// <c>R &lt;nonce&gt; &lt;command as base64&gt; &lt;stdin length&gt;</c>, a newline, then that many bytes — saves the stdin
/// to a temp file (so a command that does not read it cannot leave bytes behind for the next frame), runs the command the
/// way sshd would (<c>$SHELL -c</c>, in the home directory, a fresh child each time) and ends both streams with a line
/// carrying the call's random nonce, stdout's with the exit code. Each connection carries one call at a time; up to
/// <c>ssh.connectionsPerHost</c> run side by side, and a call that finds them all busy runs the old way, one ssh of its own.
/// </para>
/// <para>
/// A timeout or an abort kills the connection, exactly as killing a per-call ssh did (the remote side gets its hang-up), and
/// the next call opens a new one. A connection whose handshake does not come back — no POSIX sh, no <c>base64</c>, a
/// <c>head</c> that reads past its count, or ssh itself failing (auth, host key) — is not retried for
/// <see cref="Cooldown"/>: those calls run the old way, which also reports ssh's own error as it always did.
/// </para>
/// </summary>
internal sealed class SshBroker(ILogger log, SshBroker.Starter? start = null) : IDisposable
{
    /// <summary>Starts the connection: ssh with these options to this host, running <paramref name="remoteCommand"/>. Replaced in tests.</summary>
    internal delegate Process Starter(SshOptions o, SshHost host, string remoteCommand);

    /// <summary>How long a host whose broker would not start runs calls the old way before it is tried again.</summary>
    internal static TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(10);

    private const string Hello = "NETPI-BROKER 2 ok";

    /// <summary>
    /// The remote loop. The handshake line is printed only when the tools it relies on behave: <c>base64 -d</c> decodes, and
    /// <c>head -c</c> stops at its count on a pipe (a stdio-buffered head would swallow the next frame). Each call's output
    /// is framed on both streams: a start line (<c>&lt;nonce&gt; S</c>) before the command and the end line after it, so
    /// what a job the previous call left running wrote in between is told apart from this call's own (idea-ic4fr9).
    /// </summary>
    internal const string Script = """
        p=$(printf 'abcZZZ\n' | { head -c 3 >/dev/null; IFS= read -r r; printf %s "$r"; })
        d=$(printf aGk= | base64 -d 2>/dev/null)
        if [ "$p" != ZZZ ] || [ "$d" != hi ]; then echo "NETPI-BROKER 2 unsupported"; exit 3; fi
        echo "NETPI-BROKER 2 ok"
        while IFS= read -r line; do
          set -f; set -- $line; set +f
          case "$1" in
            Q) exit 0 ;;
            R)
              t=$(mktemp 2>/dev/null) || t="${TMPDIR:-/tmp}/netpi-broker.$$"
              head -c "$4" >"$t"
              c=$(printf %s "$3" | base64 -d)
              printf '%s S\n' "$2"
              printf '%s S\n' "$2" >&2
              (cd && exec "${SHELL:-/bin/sh}" -c "$c") <"$t"; rc=$?
              rm -f "$t"
              printf '\n%s X %s\n' "$2" "$rc"
              printf '\n%s X\n' "$2" >&2
              ;;
          esac
        done
        """;

    /// <summary>
    /// A lone <c>&amp;</c> (not <c>&amp;&amp;</c>, <c>&amp;&gt;</c>, <c>&gt;&amp;</c>, <c>2&gt;&amp;1</c>): the command leaves a job
    /// running, and whatever it prints later would reach the next call on the same connection, so its connection is not kept.
    /// Quoted text that merely contains one costs a reconnect, nothing more.
    /// </summary>
    internal static bool Backgrounds(string command) => BackgroundAmp.IsMatch(command);

    private static readonly System.Text.RegularExpressions.Regex BackgroundAmp = new(@"(?<![&<>|])&(?![&>])");

    private readonly Starter _start = start ?? StartSsh;
    private readonly object _gate = new();
    private readonly Dictionary<string, List<Connection>> _pools = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _cooling = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>Connections alive now, per host key (tests and diagnostics).</summary>
    internal int Open(string? host = null)
    {
        lock (_gate) return _pools.Where(p => host is null || p.Key.StartsWith(host + "|", StringComparison.OrdinalIgnoreCase)).Sum(p => p.Value.Count);
    }

    /// <summary>
    /// Run <paramref name="command"/> on a connection to <paramref name="host"/>; null when the call should run the old way
    /// (every connection busy, or the host is cooling down after a broker that would not start). <paramref name="work"/> is
    /// the text whose background jobs decide whether the connection is kept: the user's script when the command is a fixed
    /// wrapper that runs it (ssh_run's wrapper backgrounds the script itself, and waits for it), else the command.
    /// </summary>
    public async Task<SshExec?> TryRunAsync(SshOptions o, SshHost host, string command, byte[]? stdin, TimeSpan timeout,
        int perHost, TimeSpan idle, Action<string>? onStdout, Action<string>? onStderr, CancellationToken ct, string? work = null)
    {
        var key = $"{host.Alias}|{o.Ssh}|{o.Config}";
        var conn = await AcquireAsync(key, o, host, perHost, idle, ct).ConfigureAwait(false);
        if (conn is null) return null;
        var keep = false;
        try
        {
            var (result, reusable, stray) = await conn.RunAsync(command, stdin ?? [], timeout, onStdout, onStderr, ct).ConfigureAwait(false);
            // A call that left a job printing (its output came between the calls, or after this one's end) or that starts
            // one in the background does not hand its connection to the next call: that output would land in it (idea-ic4fr9).
            keep = reusable && !stray && !Backgrounds(work ?? command);
            if (reusable && !keep)
                log.LogDebug("SSH to {Host}: the connection is not kept after this call ({Why})", host.Alias,
                    stray ? "output arrived outside the call" : "the command runs a job in the background");
            return result;
        }
        finally
        {
            Release(key, conn, keep);
        }
    }

    private async Task<Connection?> AcquireAsync(string key, SshOptions o, SshHost host, int perHost, TimeSpan idle, CancellationToken ct)
    {
        Connection? fresh;
        List<Connection> dead = [];
        lock (_gate)
        {
            if (_disposed) return null;
            if (_cooling.TryGetValue(key, out var until))
            {
                if (DateTimeOffset.UtcNow < until) return null;
                _cooling.Remove(key);
            }
            if (!_pools.TryGetValue(key, out var pool)) _pools[key] = pool = [];
            foreach (var c in pool.Where(c => !c.Busy && (c.Exited || DateTimeOffset.UtcNow - c.LastUsed > idle)).ToList())
            {
                pool.Remove(c);
                dead.Add(c);
            }
            if (pool.FirstOrDefault(c => !c.Busy) is { } free)
            {
                free.Busy = true;
                fresh = null;
                foreach (var d in dead) d.Dispose();
                return free;
            }
            if (pool.Count >= Math.Max(1, perHost))
            {
                foreach (var d in dead) d.Dispose();
                return null;
            }
            fresh = new Connection { Busy = true };
            pool.Add(fresh);   // a placeholder holds the place while it connects, so a burst cannot open more than perHost
        }
        foreach (var d in dead) d.Dispose();

        string? why;
        try { why = await fresh.ConnectAsync(() => _start(o, host, "exec sh -c " + Sh.Quote(Script)), TimeSpan.FromSeconds(o.ConnectTimeout + 5), ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { why = ex.Message; }
        catch (OperationCanceledException)
        {
            Drop(key, fresh);
            throw;
        }
        if (why is null) return fresh;
        Drop(key, fresh);
        lock (_gate) _cooling[key] = DateTimeOffset.UtcNow + Cooldown;
        log.LogInformation("SSH to {Host}: no reusable connection ({Why}); calls open their own for {Minutes} min", host.Alias, why, Cooldown.TotalMinutes);
        return null;
    }

    private void Release(string key, Connection conn, bool keep)
    {
        lock (_gate)
        {
            if (keep && !_disposed && !conn.Exited)
            {
                conn.Busy = false;
                conn.LastUsed = DateTimeOffset.UtcNow;
                return;
            }
        }
        Drop(key, conn);
    }

    private void Drop(string key, Connection conn)
    {
        lock (_gate)
            if (_pools.TryGetValue(key, out var pool)) pool.Remove(conn);
        conn.Dispose();
    }

    public void Dispose()
    {
        List<Connection> all;
        lock (_gate)
        {
            _disposed = true;
            all = [.. _pools.Values.SelectMany(p => p)];
            _pools.Clear();
        }
        foreach (var c in all) c.Dispose();
    }

    private static Process StartSsh(SshOptions o, SshHost host, string remoteCommand)
    {
        var psi = new ProcessStartInfo(o.Ssh)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var a in o.CommonArgs()) psi.ArgumentList.Add(a);
        foreach (var a in new[] { "-T", "--", host.Alias, remoteCommand }) psi.ArgumentList.Add(a);
        return Process.Start(psi) ?? throw new InvalidOperationException($"{o.Ssh} did not start");
    }

    /// <summary>One ssh process running <see cref="Script"/>; one call at a time.</summary>
    private sealed class Connection : IDisposable
    {
        private Process? _process;
        private readonly char[] _outBuffer = new char[8192], _errBuffer = new char[8192];

        public bool Busy { get; set; }
        public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;
        public bool Exited
        {
            get { try { return _process is { HasExited: true }; } catch (InvalidOperationException) { return true; } }
        }

        /// <summary>Start it and wait for the handshake line; null when it is ready, else why not.</summary>
        public async Task<string?> ConnectAsync(Func<Process> start, TimeSpan within, CancellationToken ct)
        {
            _process = start();
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(within);
            try
            {
                var line = await _process.StandardOutput.ReadLineAsync(timer.Token).ConfigureAwait(false);
                if (line?.Trim() == Hello) return null;
                var err = "";
                try { err = await _process.StandardError.ReadToEndAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (Exception) { }
                var said = (line ?? "").Trim();
                return said.Length > 0 ? said : err.Trim() is { Length: > 0 } e ? e : "the connection closed before the handshake";
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return $"no handshake within {within.TotalSeconds:0} s";
            }
        }

        /// <summary>The call's result, whether the connection can carry the next one, and whether output arrived outside the call
        /// (before its start line or after its end line: a job an earlier call left running).</summary>
        public async Task<(SshExec Result, bool Reusable, bool Stray)> RunAsync(string command, byte[] stdin, TimeSpan timeout,
            Action<string>? onStdout, Action<string>? onStderr, CancellationToken ct)
        {
            var p = _process!;
            var nonce = "__netpi_end_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            var stdout = new ProcessLauncher.Sink();
            var stderr = new ProcessLauncher.Sink();
            using var timer = new CancellationTokenSource(timeout);
            using var both = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, ct);
            try
            {
                var frame = Encoding.ASCII.GetBytes($"R {nonce} {Convert.ToBase64String(Encoding.UTF8.GetBytes(command))} {stdin.Length}\n");
                var input = p.StandardInput.BaseStream;
                await input.WriteAsync(frame, both.Token).ConfigureAwait(false);
                if (stdin.Length > 0) await input.WriteAsync(stdin, both.Token).ConfigureAwait(false);
                await input.FlushAsync(both.Token).ConfigureAwait(false);

                var start = nonce + " S\n";
                var outTask = Until(p.StandardOutput, _outBuffer, start, "\n" + nonce + " X ", stdout, onStdout, both.Token);
                var errTask = Until(p.StandardError, _errBuffer, start, "\n" + nonce + " X", stderr, onStderr, both.Token);
                var (code, outStray) = await outTask.ConfigureAwait(false);
                var (errDone, errStray) = await errTask.ConfigureAwait(false);
                if (code is null || errDone is null)
                {
                    stderr.Append((stderr.ToString().Length > 0 ? "\n" : "") + "the ssh connection closed during the call");
                    return (new SshExec(255, stdout.ToString(), stderr.ToString(), false, false, stdout.Cut), false, outStray || errStray);
                }
                var exit = int.TryParse(code, out var c) ? c : 255;
                return (new SshExec(exit, stdout.ToString(), stderr.ToString(), false, false, stdout.Cut), true, outStray || errStray);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // a timeout or an abort ends the connection, which is what killing a per-call ssh did
                var aborted = ct.IsCancellationRequested;
                var timedOut = !aborted && timer.IsCancellationRequested;
                if (!aborted && !timedOut) stderr.Append("the ssh connection closed during the call");
                return (new SshExec(aborted || timedOut ? -1 : 255, stdout.ToString(), stderr.ToString(), timedOut, aborted, stdout.Cut), false, false);
            }
        }

        /// <summary>
        /// Pass a stream through to <paramref name="sink"/> and <paramref name="live"/> from the call's start line to its end
        /// marker; what follows the marker on its line (the exit code on stdout, nothing on stderr), or null when the stream
        /// ended first. What came before the start line is not this call's (a job an earlier call left running): it is dropped
        /// and reported as stray, and so is anything already after the end line. The newline the loop prints in front of the
        /// marker is not the command's, so it is not passed on; an end that could still be the start of the marker is held back
        /// (at most the text after the last newline), and flushed to the sink when the call is cancelled.
        /// </summary>
        private static async Task<(string? Tail, bool Stray)> Until(StreamReader reader, char[] buffer, string start, string marker,
            ProcessLauncher.Sink sink, Action<string>? live, CancellationToken ct)
        {
            var pending = new StringBuilder();
            var started = false;
            var stray = false;
            void Emit(string text)
            {
                if (text.Length == 0) return;
                sink.Append(text);
                try { live?.Invoke(text); } catch (Exception) { }
            }
            while (true)
            {
                int n;
                try { n = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    // a timeout or an abort: what was held back is the command's too (ssh_run finds its process group
                    // id in it, to end the remote work), so it goes to the sink before the call gives up
                    if (started) Emit(pending.ToString());
                    throw;
                }
                if (n == 0)
                {
                    if (started) Emit(pending.ToString());
                    return (null, stray);
                }
                pending.Append(buffer, 0, n);
                var text = pending.ToString();
                if (!started)
                {
                    var s = text.IndexOf(start, StringComparison.Ordinal);
                    if (s < 0)
                    {
                        // keep only an end that could still grow into the start line; the rest is somebody else's output
                        var keep = 0;
                        for (var k = Math.Min(text.Length, start.Length - 1); k > 0; k--)
                            if (text.EndsWith(start[..k], StringComparison.Ordinal)) { keep = k; break; }
                        if (text.Length > keep) stray = true;
                        pending.Clear().Append(text[^keep..]);
                        continue;
                    }
                    if (s > 0) stray = true;
                    started = true;
                    text = text[(s + start.Length)..];
                    pending.Clear().Append(text);
                }
                var at = text.IndexOf(marker, StringComparison.Ordinal);
                if (at >= 0)
                {
                    var eol = text.IndexOf('\n', at + marker.Length);
                    if (eol < 0 && marker.EndsWith(' ')) continue;   // the exit code has not all arrived
                    Emit(text[..at]);
                    if (eol >= 0 && eol + 1 < text.Length) stray = true;   // printed after the call ended: a job it left running
                    return (eol < 0 ? "" : text[(at + marker.Length)..eol].Trim(), stray);
                }
                // hold back only an end that could still grow into the marker (the marker starts with a newline, so
                // this is at most the text after the last newline), and pass the rest on now
                var hold = 0;
                for (var k = Math.Min(text.Length, marker.Length - 1); k > 0; k--)
                    if (text.EndsWith(marker[..k], StringComparison.Ordinal)) { hold = k; break; }
                Emit(text[..^hold]);
                pending.Clear().Append(text[^hold..]);
            }
        }

        public void Dispose()
        {
            if (_process is not { } p) return;
            try
            {
                if (!p.HasExited)
                {
                    try { p.StandardInput.Write("Q\n"); p.StandardInput.Flush(); } catch (Exception) { }
                    p.Kill(entireProcessTree: true);
                }
            }
            catch (Exception) { }
            p.Dispose();
        }
    }
}
