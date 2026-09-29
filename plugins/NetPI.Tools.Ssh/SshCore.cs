using System.Diagnostics;
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

    /// <summary>Include targets: <c>~</c> expanded, relative paths against the including file's folder, <c>*</c>/<c>?</c> in the file name.</summary>
    private static IEnumerable<string> Expand(string pattern, string includingFile)
    {
        if (pattern.StartsWith("~/") || pattern.StartsWith("~\\"))
            pattern = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), pattern[2..]);
        if (!Path.IsPathRooted(pattern)) pattern = Path.Combine(Path.GetDirectoryName(includingFile) ?? ".", pattern);
        var dir = Path.GetDirectoryName(pattern) ?? ".";
        var name = Path.GetFileName(pattern);
        if (name.IndexOfAny(['*', '?']) < 0) return [pattern];
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, name).Order(StringComparer.Ordinal) : []; }
        catch (IOException) { return []; }
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
                "-o", $"ControlPersist={ControlPersistSeconds}", "-o", $"ControlIdleTimeout={ControlIdleSeconds}"]);
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

    private sealed class Sink
    {
        public readonly StringBuilder Text = new();
        public bool Cut;
        public override string ToString() { lock (Text) return Text.ToString(); }
    }

    public async Task<SshExec> RunAsync(string exe, IReadOnlyList<string> args, byte[]? stdin, string? workDir, Action<string>? onStdout,
        Action<string>? onStderr, TimeSpan timeout, CancellationToken ct)
    {
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
            if (stdin is { Length: > 0 }) await process.StandardInput.BaseStream.WriteAsync(stdin, ct).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (IOException) { } // the process exited early (e.g. ssh failed to connect)
        catch (OperationCanceledException) { }

        var timedOut = false;
        var aborted = false;
        using var timer = new CancellationTokenSource(timeout);
        using var both = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, ct);
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
            lock (sink.Text)
            {
                sink.Text.Append(chunk);
                if (sink.Text.Length > MaxChars) // keep the tail
                {
                    sink.Text.Remove(0, sink.Text.Length - MaxChars);
                    sink.Cut = true;
                }
            }
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
