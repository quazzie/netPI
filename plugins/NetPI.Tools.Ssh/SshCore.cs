using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace NetPI.Tools.Ssh;

internal sealed record SshHost(string Alias, string? HostName, string? User, int? Port);

/// <summary>
/// The host aliases in the user's OpenSSH config, read on every call: concrete <c>Host</c> patterns (wildcards and
/// negations skipped, <c>Match</c> blocks ignored), <c>Include</c> followed, with HostName / User / Port for display.
/// </summary>
internal static class SshConfig
{
    public static List<SshHost> Read(string path)
    {
        var hosts = new List<SshHost>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var details = new Dictionary<string, (string? HostName, string? User, int? Port)>(StringComparer.OrdinalIgnoreCase);
        Parse(path, 0, hosts, seen, details);
        return hosts.Select(h => details.TryGetValue(h.Alias, out var d) ? h with { HostName = d.HostName, User = d.User, Port = d.Port } : h).ToList();
    }

    private static void Parse(string path, int depth, List<SshHost> hosts, HashSet<string> seen,
        Dictionary<string, (string? HostName, string? User, int? Port)> details)
    {
        if (depth > 8 || !File.Exists(path)) return;
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        var current = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var (key, args) = Split(line);
            switch (key.ToLowerInvariant())
            {
                case "host":
                    current = args.Where(a => a.Length > 0 && a[0] != '!' && a.IndexOfAny(['*', '?']) < 0).ToList();
                    foreach (var a in current)
                        if (seen.Add(a)) hosts.Add(new SshHost(a, null, null, null));
                    break;
                case "match":
                    current = [];
                    break;
                case "hostname" or "user" or "port":
                    foreach (var a in current)
                    {
                        var d = details.GetValueOrDefault(a);
                        var v = args.FirstOrDefault();
                        // the first value wins, as in OpenSSH
                        if (key.Equals("hostname", StringComparison.OrdinalIgnoreCase)) d.HostName ??= v;
                        else if (key.Equals("user", StringComparison.OrdinalIgnoreCase)) d.User ??= v;
                        else if (d.Port is null && int.TryParse(v, out var port)) d.Port = port;
                        details[a] = d;
                    }
                    break;
                case "include":
                    foreach (var pattern in args)
                        foreach (var file in Expand(pattern, path))
                            Parse(file, depth + 1, hosts, seen, details);
                    break;
            }
        }
    }

    /// <summary><c>Keyword args…</c> or <c>Keyword=args…</c>; arguments may be double-quoted.</summary>
    private static (string Key, List<string> Args) Split(string line)
    {
        var i = 0;
        while (i < line.Length && !char.IsWhiteSpace(line[i]) && line[i] != '=') i++;
        var key = line[..i];
        var rest = line[i..].TrimStart();
        if (rest.StartsWith('=')) rest = rest[1..].TrimStart();
        var args = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        foreach (var c in rest)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (sb.Length > 0) { args.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) args.Add(sb.ToString());
        return (key, args);
    }

    /// <summary>
    /// Include targets: <c>~</c> expanded, relative paths against the including file's folder, <c>*</c>/<c>?</c> in the
    /// file name, and a plain directory — whose regular files are included, as in ssh_config(5) — enumerated instead of
    /// the directory itself (which would fail the include's file check and drop every host in it).
    /// </summary>
    private static IEnumerable<string> Expand(string pattern, string includingFile)
    {
        if (pattern.StartsWith("~/") || pattern.StartsWith("~\\"))
            pattern = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), pattern[2..]);
        if (!Path.IsPathRooted(pattern)) pattern = Path.Combine(Path.GetDirectoryName(includingFile) ?? ".", pattern);
        var dir = Path.GetDirectoryName(pattern) ?? ".";
        var name = Path.GetFileName(pattern);
        if (name.IndexOfAny(['*', '?']) < 0)
        {
            if (Directory.Exists(pattern))
            {
                try { return Directory.EnumerateFiles(pattern).Order(StringComparer.Ordinal).ToList(); }
                catch (IOException) { return []; }
            }
            return [pattern];
        }
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, name).Order(StringComparer.Ordinal) : []; }
        catch (IOException) { return []; }
    }
}

/// <summary>
/// What the <c>ssh</c> client on this machine can do, asked once and remembered.
/// <para>
/// <b>An option the client does not know is not a warning, it is a hard error.</b> OpenSSH prints
/// <c>Bad configuration option: …</c> and exits before it connects, so one <c>-o</c> that this build never had breaks
/// every call. NetPI's own client on Windows is OpenSSH_for_Windows (LibreSSL), which has no <c>ControlIdleTimeout</c>;
/// the Git/MSYS build does. Which one is in use is a fact only the binary knows, so the client is asked.
/// </para>
/// <para>
/// <b>Connection multiplexing is not universal either.</b> The Windows port does not implement it: it accepts
/// <c>ControlMaster</c> and then fails every session over the control socket with <c>getsockname failed: Not a
/// socket</c>. So the client is also asked whether it can multiplex — <c>-O check</c> on a socket that is not there
/// yet. A client that understands it answers <c>Control socket connect(…): No such file or directory</c> (nothing
/// running yet, which is the normal state before the first call); one that cannot multiplex answers something else.
/// </para>
/// <para>
/// The probes are offline (<c>-G</c> prints the effective configuration, <c>-O check</c> only inspects a socket), cost a
/// few milliseconds once per client, and are bounded. Anything unexpected — no client, a timeout, a crash, an answer we
/// do not recognise — counts as "not supported", which drops the option and the multiplexing: the call then runs the
/// way it ran before connection reuse was added, which always worked. That is the direction to be wrong in.
/// </para>
/// </summary>
internal static class SshClient
{
    /// <summary>How long the client has to answer before an option or multiplexing counts as unsupported.</summary>
    public static TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(3);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Options = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Multiplexing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Asks the client (replaced in tests): the ssh executable and the arguments, and what it answered.</summary>
    internal static Func<string, string[], (int Exit, string Err)> Probe = Run;

    /// <summary>Whether this client accepts <paramref name="option"/>, asked once and remembered.</summary>
    public static bool SupportsOption(string exe, string option, string value) =>
        Answer(Options, exe, "option", option, () =>
        {
            var (exit, _) = Probe(exe, ["-o", $"{option}={value}", "-G", "localhost"]);
            return exit == 0;
        });

    /// <summary>
    /// Whether this client can reuse one connection per host. <paramref name="socketPath"/> is a path inside our own
    /// control directory that no master uses; the probe only looks, so nothing is created and nothing connects.
    /// <para>
    /// On Windows the answer is always no, and no client is asked. OpenSSH's multiplexing has the master fork a child
    /// for every session, which the Windows ports do not do: the socket appears and the master stays up, and the first
    /// session over it is reset (<c>mux_client_request_session: read from master failed</c>). That is true of both
    /// builds here — OpenSSH_for_Windows, and the Git/MSYS 10.3p1 client — so it is a fact about the platform rather
    /// than the version, and asking the client cannot find it out (it reports the socket as merely absent, which is
    /// also what a working client says before its first call).
    /// </para>
    /// </summary>
    public static bool SupportsMultiplexing(string exe, string socketPath)
    {
        if (OperatingSystem.IsWindows()) return false;
        return Answer(Multiplexing, exe, "multiplexing", socketPath, () =>
        {
            var (exit, err) = Probe(exe, ["-O", "check", "-S", socketPath, "localhost"]);
            // A master is already running, or the client understood the command and found no socket yet.
            if (exit == 0) return true;
            return err.Contains("Control socket connect", StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>Forget what was asked (a settings change points at a different client; tests reset the cache).</summary>
    public static void Forget(string? exe = null)
    {
        void Clear(System.Collections.Concurrent.ConcurrentDictionary<string, bool> known)
        {
            if (exe is null) known.Clear();
            else foreach (var key in known.Keys.Where(k => k.StartsWith(exe + "|", StringComparison.OrdinalIgnoreCase)).ToList()) known.TryRemove(key, out _);
        }
        Clear(Options);
        Clear(Multiplexing);
    }

    /// <summary>Ask once, remember, and never let the question itself be the reason a call fails.</summary>
    private static bool Answer(System.Collections.Concurrent.ConcurrentDictionary<string, bool> known, string exe, string what, string key, Func<bool> ask)
    {
        try { return known.GetOrAdd($"{exe}|{what}|{key}", _ => ask()); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return false;
        }
    }

    /// <summary>Run the client and read its answer. Nothing here connects to a host.</summary>
    private static (int Exit, string Err) Run(string exe, string[] args)
    {
        var r = ChildProcess.RunAsync(exe, args, ProbeTimeout).GetAwaiter().GetResult();
        return (r.Exit, r.Err);
    }
}

/// <summary>
/// <c>ssh.*</c> settings, read on every call.
/// <see cref="ControlDir"/> is null when the directory the control sockets live in cannot be created: the call
/// then runs without multiplexing (one handshake per call) instead of failing.
/// </summary>
internal sealed record SshOptions(string Ssh, string Scp, string Config, bool DefaultConfig, int ConnectTimeout, int Timeout, string? ControlDir)
{
    public static readonly string DefaultConfigPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");

    public static SshOptions Read(ISettings s, string home)
    {
        var ssh = Blank(s.Get<string>("ssh.path")) ?? FindSsh();
        var scp = Blank(s.Get<string>("ssh.scpPath")) ?? Sibling(ssh, OperatingSystem.IsWindows() ? "scp.exe" : "scp");
        var config = Blank(s.Get<string>("ssh.config"));
        if (config is not null && (config.StartsWith("~/") || config.StartsWith("~\\")))
            config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), config[2..]);
        // ~/.netpi is the app's own state (NETPI_HOME when overridden): the sockets are NetPI's, not the user's ~/.ssh's.
        var controlDir = Path.Combine(home, "ssh");
        return new SshOptions(ssh, scp, config ?? DefaultConfigPath, config is null,
            Math.Clamp(s.Get("ssh.connectTimeoutSeconds", 10), 2, 120),
            Math.Clamp(s.Get("ssh.timeoutSeconds", 120), 1, 1800), controlDir);
    }

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    internal static string FindSsh()
    {
        if (!OperatingSystem.IsWindows()) return "ssh";
        var windir = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        foreach (var candidate in new[]
                 {
                     Path.Combine(windir, "System32", "OpenSSH", "ssh.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "usr", "bin", "ssh.exe"),
                 })
            if (File.Exists(candidate)) return candidate;
        return "ssh";
    }

    internal static string Sibling(string exe, string name)
    {
        var dir = Path.GetDirectoryName(exe);
        return string.IsNullOrEmpty(dir) ? Path.GetFileNameWithoutExtension(name) : Path.Combine(dir, name);
    }

    /// <summary>How long a master connection stays after the last call — and dies once it has been idle that long.</summary>
    public const int ControlPersistSeconds = 300;
    public const int ControlIdleSeconds = 300;

    /// <summary>
    /// Options shared by ssh and scp: never prompt, reject unknown host keys, fail fast, no banners — plus one master
    /// connection per host (OpenSSH's ControlMaster): a run of calls to the same host pays the TCP + transport + auth
    /// handshake once and the rest ride the control socket instead of each doing their own.
    /// </summary>
    public List<string> CommonArgs()
    {
        var args = new List<string>
        {
            "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", $"ConnectTimeout={ConnectTimeout}",
            "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3", "-o", "LogLevel=ERROR",
        };
        if (ControlDir is not null)
        {
            // %C is OpenSSH's hash of (remote host, port, remote user, local host): unique per host and per user, and
            // a fixed 32 hex chars, so the path stays under ControlPath's limit whatever the host names are. The
            // directory is ours (created before the first call); ssh will not make parent folders itself.
            args.AddRange(["-o", "ControlMaster=auto", "-o", $"ControlPath={Path.Combine(ControlDir, "netpi-%C")}",
                "-o", $"ControlPersist={ControlPersistSeconds}"]);
            // Only a client that knows the option gets it: OpenSSH 10 dropped ControlIdleTimeout, and asking for it
            // there fails every call before it connects. The connection is reused and expires on ControlPersist either
            // way, and ServerAliveInterval above already reaps a master that stops answering.
            if (SshClient.SupportsOption(Ssh, "ControlIdleTimeout", ControlIdleSeconds.ToString(CultureInfo.InvariantCulture)))
                args.AddRange(["-o", $"ControlIdleTimeout={ControlIdleSeconds}"]);
        }
        if (!DefaultConfig) args.InsertRange(0, ["-F", Config]);
        return args;
    }
}

/// <param name="Cut">stdout was longer than the launcher keeps: its start was dropped.</param>
internal sealed record SshExec(int ExitCode, string Stdout, string Stderr, bool TimedOut, bool Aborted, bool Cut = false);

/// <summary>Runs ssh / scp. Replaced in tests.</summary>
internal interface ISshLauncher
{
    /// <param name="onStdout">Live stdout chunks (the UI's tool output).</param>
    Task<SshExec> RunAsync(string exe, IReadOnlyList<string> args, byte[]? stdin, string? workDir, Action<string>? onStdout,
        Action<string>? onStderr, TimeSpan timeout, CancellationToken ct);
}

internal sealed class ProcessLauncher : ISshLauncher
{
    /// <summary>More than ssh_read fetches (the file plus its stat line), so a file is never cut here.</summary>
    public const int MaxChars = SshToolBase.MaxReadBytes + 64 * 1024;

    /// <summary>
    /// The tail of a command's output, at most <see cref="MaxChars"/> chars. A queue of chunks, so appending and
    /// trimming are O(1) amortized per chunk — not a <see cref="StringBuilder"/> whose <c>Remove(0, …)</c> re-shifts
    /// the whole buffer on every chunk past the cap (quadratic in the output size).
    /// </summary>
    private sealed class Sink
    {
        private readonly Queue<string> _chunks = new();
        private int _length;
        private readonly object _lock = new();
        public bool Cut;

        public void Append(string chunk)
        {
            lock (_lock)
            {
                _chunks.Enqueue(chunk);
                _length += chunk.Length;
                if (_length > MaxChars)
                {
                    while (_length > MaxChars && _chunks.Count > 1)
                        _length -= _chunks.Dequeue().Length;
                    Cut = true;
                }
            }
        }

        public override string ToString()
        {
            lock (_lock)
            {
                if (_chunks.Count == 0) return "";
                var sb = new StringBuilder(_length);
                var overflow = _length - MaxChars; // positive only when a single chunk alone outran the cap
                foreach (var c in _chunks)
                {
                    if (overflow > 0)
                    {
                        if (c.Length <= overflow) { overflow -= c.Length; continue; }
                        sb.Append(c, overflow, c.Length - overflow);
                        overflow = 0;
                    }
                    else sb.Append(c);
                }
                return sb.ToString();
            }
        }
    }

    public async Task<SshExec> RunAsync(string exe, IReadOnlyList<string> args, byte[]? stdin, string? workDir, Action<string>? onStdout,
        Action<string>? onStderr, TimeSpan timeout, CancellationToken ct)
    {
        // One deadline for the whole call — the process start, the transmission of stdin and the wait for exit:
        // a child that never reads its stdin blocks the write in the pipe, so the timeout must bound the
        // transmission, not start after it.
        var timedOut = false;
        var aborted = false;
        using var timer = new CancellationTokenSource(timeout);
        using var both = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, ct);

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        if (workDir is not null) psi.WorkingDirectory = workDir;
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdout = new Sink();
        var stderr = new Sink();
        var readOut = Pump(process.StandardOutput, stdout, onStdout);
        var readErr = Pump(process.StandardError, stderr, onStderr);
        try
        {
            if (stdin is { Length: > 0 }) await process.StandardInput.BaseStream.WriteAsync(stdin, both.Token).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (IOException) { } // the process exited early (e.g. ssh failed to connect)
        catch (OperationCanceledException) { } // the timeout or the caller gave up: the wait below kills the process and says which

        try { await process.WaitForExitAsync(both.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            aborted = ct.IsCancellationRequested;
            timedOut = !aborted;
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (Exception) { }
        }
        try { await Task.WhenAll(readOut, readErr).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (Exception) { }
        return new SshExec(process.HasExited ? process.ExitCode : -1, stdout.ToString(), stderr.ToString(), timedOut, aborted, stdout.Cut);
    }

    private static async Task Pump(StreamReader reader, Sink sink, Action<string>? live)
    {
        var buffer = new char[8192];
        while (true)
        {
            int n;
            try { n = await reader.ReadAsync(buffer).ConfigureAwait(false); }
            catch (Exception) { return; }
            if (n == 0) return;
            var chunk = new string(buffer, 0, n);
            sink.Append(chunk);
            try { live?.Invoke(chunk); } catch (Exception) { }
        }
    }
}

internal static class Sh
{
    /// <summary>A POSIX shell single-quoted word: safe for any content.</summary>
    public static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>A remote path as a shell word: a leading <c>~/</c> stays expandable.</summary>
    public static string Path(string p) => p == "~" ? "\"$HOME\"" : p.StartsWith("~/") ? "\"$HOME\"/" + Quote(p[2..]) : Quote(p);

    /// <summary><c>cd</c> into <paramref name="cwd"/> first (exit 125 when it does not exist).</summary>
    public static string Cd(string? cwd) => string.IsNullOrWhiteSpace(cwd) ? "" : $"cd -- {Path(cwd.Trim())} || exit 125; ";

    public static string Lf(string s) => s.Replace("\r\n", "\n");
}
