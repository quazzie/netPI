using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.Tools.Ssh;

namespace NetPI.Aux.Tests;

/// <summary>
/// SSH tools: argument construction and byte-exact payloads against a fake launcher, config parsing, edits and diffs.
/// With NETPI_SSH_TEST_HOSTS=nuc,server the live tests also run against those hosts (under /tmp, cleaned up).
/// </summary>
public static class SshTests
{
    private sealed class FakeLauncher : ISshLauncher
    {
        public readonly List<(string Exe, List<string> Args, byte[]? Stdin, string? WorkDir)> Calls = [];
        public Func<List<string>, byte[]?, SshExec> Reply = (_, _) => new SshExec(0, "", "", false, false);

        public Task<SshExec> RunAsync(string exe, IReadOnlyList<string> args, byte[]? stdin, string? workDir, Action<string>? onStdout,
            Action<string>? onStderr, TimeSpan timeout, CancellationToken ct)
        {
            lock (Calls) Calls.Add((exe, args.ToList(), stdin, workDir));
            var r = Reply(args.ToList(), stdin);
            if (r.Stdout.Length > 0) onStdout?.Invoke(r.Stdout);
            if (r.Stderr.Length > 0) onStderr?.Invoke(r.Stderr);
            return Task.FromResult(r);
        }
    }

    /// <summary>A client answer: every option is known except <c>reject</c>, which it has never heard of.</summary>
    private sealed class FileIOPermissionAwareProbe(List<string> asked, string reject)
    {
        public (int Exit, string Err) Ask(string[] args)
        {
            if (args.Contains("-O")) return (1, "Control socket connect(/tmp/x): No such file or directory");
            foreach (var a in args.Where(a => a.Contains('=')))
                asked.Add(a.Split('=')[0]);
            var name = args.FirstOrDefault(a => a.Contains('=') && !a.StartsWith("ControlPath=", StringComparison.Ordinal))?.Split('=')[0] ?? "";
            return name == reject ? (255, $"command-line: line 0: Bad configuration option: {reject.ToLowerInvariant()}") : (0, "");
        }
    }

    private sealed class Env
    {
        public FakePluginContext Ctx { get; } = new(T.TempDir("ssh-home"));
        public string Dir { get; } = T.TempDir("ssh");
        public FakeLauncher Fake { get; } = new();
        public Dictionary<string, IAgentTool> Tools { get; }
        public StringBuilder Live { get; } = new();

        /// <summary>Without a config a test config is written into the temp folder; a given config (live tests) is only read.</summary>
        public Env(ISshLauncher? launcher = null, string? config = null)
        {
            if (config is null)
            {
                config = Path.Combine(Dir, "config");
                File.WriteAllText(config, "Host nuc\n  HostName 192.168.1.3\n  User quazzie\nHost server\n  HostName 192.168.1.2\n");
            }
            Ctx.SettingsFake.Set("ssh.config", JsonValue.Create(config));
            if (launcher is null) Ctx.SettingsFake.Set("ssh.path", JsonValue.Create("fake-ssh"));
            Tools = SshToolSet.Create(Ctx, launcher ?? Fake).ToDictionary(t => t.Definition.Name);
        }

        /// <summary>"ssh_run" etc.: the ssh tool with that action (the tests name the jobs as the tools they once were).</summary>
        public Task<ToolResult> Run(string tool, object args, CancellationToken ct = default)
        {
            var a = (JsonObject)NetPiJson.ToNode(args)!;
            if (tool.StartsWith("ssh_", StringComparison.Ordinal)) { a["action"] = tool[4..]; tool = "ssh"; }
            return Tools[tool].ExecuteAsync(new ToolContext
            {
                SessionId = "ses_1", AgentId = "agt_1", CallId = "call_1", Cwd = Dir, Services = Ctx.Services, Events = Ctx.Events,
                Output = s => { lock (Live) Live.Append(s); },
            }, T.Args(a.ToJsonString()), ct);
        }
    }

    private static JsonElement D(ToolResult r) => NetPiJson.ToElement(r.Details);
    private static string Remote(List<string> args) => args[^1];
    private static string Utf8(byte[]? b) => b is null ? "" : Encoding.UTF8.GetString(b);

    public static void Register(TestRunner r)
    {
        r.Add("ssh: config aliases (wildcards, negations, Match, Include, quoting, HostName/User/Port)", () =>
        {
            var dir = T.TempDir("sshcfg");
            Directory.CreateDirectory(Path.Combine(dir, "conf.d"));
            File.WriteAllText(Path.Combine(dir, "conf.d", "a.conf"), "Host extra\n  HostName 10.0.0.9\n");
            File.WriteAllText(Path.Combine(dir, "config"), """
                # a comment
                Host server
                    HostName 192.168.1.2
                    User quazzie
                    User ignored
                Host nuc build-* !bad
                    HostName=192.168.1.3
                    Port 2222
                Host *
                    ServerAliveInterval 30
                Match host nuc
                    User other
                Include conf.d/*.conf
                host "quoted name"
                """);
            var hosts = SshConfig.Read(Path.Combine(dir, "config"));
            Check.Equal("server,nuc,extra,quoted name", string.Join(",", hosts.Select(h => h.Alias)));
            Check.Equal(new SshHost("server", "192.168.1.2", "quazzie", null), hosts[0]);
            Check.Equal(new SshHost("nuc", "192.168.1.3", null, 2222), hosts[1]);
            Check.Equal("10.0.0.9", hosts[2].HostName);
            Check.Equal(0, SshConfig.Read(Path.Combine(dir, "missing")).Count);
        });

        r.Add("ssh: tools, hosts from the config, unknown hosts refused", async () =>
        {
            var env = new Env();
            Check.Equal("ssh", string.Join(",", env.Tools.Keys.Order()));
            var ssh = env.Tools["ssh"];
            Check.Equal("ssh", ssh.Definition.Category);
            Check.False(ssh.Definition.ReadOnly);
            // hosts and read only read; run, write, edit and copy change things
            var calls = (IReadOnlyCalls)ssh;
            foreach (var (action, readOnly) in new[] { ("hosts", true), ("read", true), ("run", false), ("write", false), ("edit", false), ("copy", false) })
                Check.Equal(readOnly, calls.IsReadOnly(T.Args(new { action, host = "nuc" })), action);
            Check.False(calls.IsReadOnly(T.Args(new { host = "nuc", script = "ls" })), "a script without an action runs");
            foreach (var job in new[] { "hosts:", "run:", "read:", "write:", "edit:", "copy:" }) Check.Contains(ssh.Definition.Help!, "- " + job);
            Check.Contains((await ssh.ExecuteAsync(new ToolContext { SessionId = "s", AgentId = "a", CallId = "c", Cwd = env.Dir, Services = env.Ctx.Services, Events = env.Ctx.Events },
                T.Args(new { action = "fly" }), CancellationToken.None)).Content, "Unknown action \"fly\"");
            var hosts = await env.Run("ssh_hosts", new { });
            Check.Contains(hosts.Content, "nuc: quazzie@192.168.1.3\nserver: 192.168.1.2");
            var unknown = await env.Run("ssh_run", new { host = "elsewhere", script = "ls" });
            Check.True(unknown.IsError);
            Check.Contains(unknown.Content, "Unknown host \"elsewhere\"");
            Check.Contains(unknown.Content, "nuc, server");
            Check.Equal(0, env.Fake.Calls.Count);
        });

        r.Add("ssh_run: the script goes through stdin byte for byte; fixed remote command; output and exit codes", async () =>
        {
            var env = new Env();
            env.Fake.Reply = (_, _) => new SshExec(0, "__netpi_pgid=4242\nhello\n", "", false, false);
            const string script = "echo \"it's $HOME\" \\ ok\r\ncat <<'EOF'\nquote \" and ' and `x` — ü\nEOF";
            var res = await env.Run("ssh_run", new { host = "NUC", script, cwd = "/srv/it's here", timeout = 30 });
            Check.False(res.IsError, res.Content);
            Check.Equal("hello", res.Content);
            Check.Equal("hello\n", env.Live.ToString());
            var call = env.Fake.Calls.Single();
            Check.Equal("fake-ssh", call.Exe);
            var args = string.Join(" ", call.Args);
            foreach (var part in new[] { "BatchMode=yes", "StrictHostKeyChecking=yes", "ConnectTimeout=10", "LogLevel=ERROR", "-T -- nuc " })
                Check.Contains(args, part);
            Check.Contains(Remote(call.Args), "setsid --wait bash -c 'echo __netpi_pgid=$$; exec timeout -k 5 30 bash \"$0\" </dev/null 2>&1' \"$t\"");
            Check.Equal("cd -- '/srv/it'\\''s here' || exit 125; \necho \"it's $HOME\" \\ ok\ncat <<'EOF'\nquote \" and ' and `x` — ü\nEOF\n", Utf8(call.Stdin));
            Check.Equal("nuc", D(res).GetProperty("host").GetString());

            env.Fake.Reply = (_, _) => new SshExec(3, "__netpi_pgid=1\nboom\n", "", false, false);
            var failed = await env.Run("ssh_run", new { host = "nuc", script = "exit 3" });
            Check.False(failed.IsError);
            Check.Equal("boom\n[exit code 3]", failed.Content);

            env.Fake.Reply = (_, _) => new SshExec(124, "__netpi_pgid=1\npartial\n", "", false, false);
            var slow = await env.Run("ssh_run", new { host = "nuc", script = "sleep 9", timeout = 2 });
            Check.True(slow.IsError);
            Check.Contains(slow.Content, "partial\n[timed out after 2s; the remote process group was ended.");

            env.Fake.Reply = (_, _) => new SshExec(125, "__netpi_pgid=1\nbash: line 1: cd: /nope: No such file or directory\n", "", false, false);
            Check.Contains((await env.Run("ssh_run", new { host = "nuc", script = "ls", cwd = "/nope" })).Content, "[the working directory /nope does not exist on nuc]");

            env.Fake.Reply = (_, _) => new SshExec(255, "", "Host key verification failed.\r\n", false, false);
            var hostKey = await env.Run("ssh_run", new { host = "server", script = "ls" });
            Check.True(hostKey.IsError);
            Check.Contains(hostKey.Content, "ssh to server failed: Host key verification failed. The host key of server is not in known_hosts: the user has to connect once (ssh server) to accept it.");

            // ~ stays expandable in cwd
            env.Fake.Reply = (_, _) => new SshExec(0, "__netpi_pgid=1\n", "", false, false);
            await env.Run("ssh_run", new { host = "nuc", script = "pwd", cwd = "~/proj" });
            Check.True(Utf8(env.Fake.Calls[^1].Stdin).StartsWith("cd -- \"$HOME\"/'proj' || exit 125; \n"), Utf8(env.Fake.Calls[^1].Stdin));
        });

        r.Add("ssh_run: long output shows the tail and saves the whole output, like bash", async () =>
        {
            var env = new Env();
            var lines = Enumerable.Range(1, 3000).Select(i => $"out{i:D4}").ToList();
            env.Fake.Reply = (_, _) => new SshExec(0, "__netpi_pgid=7\n" + string.Join("\n", lines) + "\n", "", false, false);
            var res = await env.Run("ssh_run", new { host = "nuc", script = "seq 3000" });
            Check.False(res.IsError, res.Content);
            Check.True(res.Content.StartsWith("[Output truncated: showing the last 2000 lines of 3000. Full output saved to "), res.Content[..120]);
            Check.True(res.Content.EndsWith("\nout3000"), "ends with the last line");
            Check.NotContains(res.Content, "out1000\n");
            var saved = D(res).GetProperty("fullOutputPath").GetString()!;
            try
            {
                Check.Equal(string.Join("\n", lines) + "\n", File.ReadAllText(saved));
                Check.True(D(res).GetProperty("truncated").GetBoolean());
            }
            finally { File.Delete(saved); }

            // the launcher dropped the start: said so
            env.Fake.Reply = (_, _) => new SshExec(0, string.Join("\n", lines.Skip(2500)) + "\n", "", false, false, Cut: true);
            var cut = await env.Run("ssh_run", new { host = "nuc", script = "yes | head -n 9999999" });
            Check.Contains(cut.Content, "[Output truncated: showing the last 500 lines; only the last 8 MB of the output were kept. Those are saved to ");
            File.Delete(D(cut).GetProperty("fullOutputPath").GetString()!);
        });

        r.Add("ssh_run: an aborted run ends the remote process group", async () =>
        {
            var env = new Env();
            env.Fake.Reply = (args, _) => Remote(args).StartsWith("kill ")
                ? new SshExec(0, "", "", false, false)
                : new SshExec(-1, "__netpi_pgid=4242\nworking\n", "", false, true);
            var res = await env.Run("ssh_run", new { host = "nuc", script = "sleep 100" });
            Check.True(res.IsError);
            Check.Contains(res.Content, "working\n[aborted; the remote process group is being ended]");
            for (var i = 0; i < 50 && env.Fake.Calls.Count < 2; i++) await Task.Delay(20);
            Check.Equal("kill -TERM -- -4242 2>/dev/null; sleep 2; kill -KILL -- -4242 2>/dev/null; true", Remote(env.Fake.Calls[1].Args));
        });

        r.Add("ssh_write / ssh_read: content through stdin, quoted paths, created vs existing, paging, binary", async () =>
        {
            var env = new Env();
            env.Fake.Reply = (_, _) => new SshExec(0, "", "", false, false);
            const string content = "line with 'quotes', \"doubles\", $VAR, `ticks` and \\backslashes\\\nsecond — ü\n";
            var created = await env.Run("ssh_write", new { host = "nuc", path = "/tmp/a b/it's.txt", content });
            Check.False(created.IsError, created.Content);
            Check.Equal("Created nuc:/tmp/a b/it's.txt (" + Encoding.UTF8.GetByteCount(content) + " bytes, 2 lines).", created.Content);
            var call = env.Fake.Calls[^1];
            Check.Equal(content, Utf8(call.Stdin));
            Check.Contains(Remote(call.Args), "p='/tmp/a b/it'\\''s.txt'; if [ -e \"$p\" ]; then echo existed; fi; mkdir -p -- \"$(dirname -- \"$p\")\" && cat >\"$p\"");
            env.Fake.Reply = (_, _) => new SshExec(0, "existed\n", "", false, false);
            Check.True((await env.Run("ssh_write", new { host = "nuc", path = "x.txt", content = "a" })).Content.StartsWith("Wrote nuc:x.txt"));
            await env.Run("ssh_write", new { host = "nuc", path = "log", content = "more\n", append = true });
            Check.Contains(Remote(env.Fake.Calls[^1].Args), "cat >>\"$p\"");

            env.Fake.Reply = (_, _) => new SshExec(0, "__netpi_stat=18 1700000000\n__netpi_hash=sha256 f4f5\nalpha\nbeta\ngamma\n", "", false, false);
            var read = await env.Run("ssh_read", new { host = "nuc", path = "/etc/demo" });
            Check.Equal("alpha\nbeta\ngamma", read.Content);
            Check.Equal(3, D(read).GetProperty("totalLines").GetInt32());
            Check.Contains(Remote(env.Fake.Calls[^1].Args), "stat -c '__netpi_stat=%s %Y' -- \"$p\" && if command -v sha256sum");
            Check.Contains(Remote(env.Fake.Calls[^1].Args), "echo \"__netpi_hash=$h\" && head -c ");
            var paged = await env.Run("ssh_read", new { host = "nuc", path = "/etc/demo", offset = 2, limit = 1 });
            Check.Equal("beta\n\n[Showing lines 2-2 of 3. Use offset=3 to continue.]", paged.Content);
            Check.Equal("gamma", (await env.Run("ssh_read", new { host = "nuc", path = "/etc/demo", offset = -1 })).Content);

            env.Fake.Reply = (_, _) => new SshExec(0, "__netpi_stat=4 1\n__netpi_hash=sha256 0\nab\0c", "", false, false);
            Check.Contains((await env.Run("ssh_read", new { host = "nuc", path = "bin" })).Content, "appears to be a binary file");
            env.Fake.Reply = (_, _) => new SshExec(2, "", "No such file: /x\n", false, false);
            Check.Equal("nuc: No such file: /x", (await env.Run("ssh_read", new { host = "nuc", path = "/x" })).Content);
        });

        r.Add("ssh_edit: exact replacements keep CRLF, write only when the file is unchanged, diff for the UI", async () =>
        {
            var env = new Env();
            var file = "one\r\ntwo\r\nthree\r\n";
            const string hash = "sha256 4567ab4567ab4567ab4567ab4567ab4567ab4567ab4567ab4567ab4567ab45";
            env.Fake.Reply = (args, stdin) => Remote(args).Contains("head -c")
                ? new SshExec(0, "__netpi_stat=17 1700000000\n__netpi_hash=" + hash + "\n" + file, "", false, false)
                : new SshExec(0, "", "", false, false);
            var res = await env.Run("ssh_edit", new { host = "nuc", path = "/srv/a.txt", edits = new[] { new { oldText = "two", newText = "TWO\nextra" } } });
            Check.False(res.IsError, res.Content);
            Check.Equal("Applied 1 edit to nuc:/srv/a.txt (+2 −1)", res.Content);
            var write = env.Fake.Calls[^1];
            Check.Equal("one\r\nTWO\r\nextra\r\nthree\r\n", Utf8(write.Stdin));
            // the marker is the content hash the read recorded, not size + mtime
            Check.Contains(Remote(write.Args), $"[ \"$h\" = '{hash}' ] || {{ echo \"changed: $h\" >&2; exit 3; }}");
            Check.NotContains(Remote(write.Args), "%s %Y");
            // the new content goes to a temporary file in the same directory, in place by an atomic rename
            Check.Contains(Remote(write.Args), "t=$(mktemp -- \"${p}.netpi.XXXXXX\") || exit 4");
            Check.Contains(Remote(write.Args), "cat >\"$t\"");
            Check.Contains(Remote(write.Args), "chmod -- \"$(stat -c '%a' -- \"$p\")\" \"$t\"");
            Check.Contains(Remote(write.Args), "mv -f -- \"$t\" \"$p\"");
            Check.NotContains(Remote(write.Args), "cat >\"$p\"");
            Check.Contains(Remote(env.Fake.Calls[0].Args), "echo \"__netpi_hash=$h\"");
            Check.Contains(D(res).GetProperty("diff").GetString()!, "@@ -1,3 +1,4 @@\n one\n-two\n+TWO\n+extra\n three\n");
            Check.Equal("crlf", D(res).GetProperty("eol").GetString());

            var calls = env.Fake.Calls.Count;
            Check.Contains((await env.Run("ssh_edit", new { host = "nuc", path = "/srv/a.txt", edits = new[] { new { oldText = "four", newText = "4" } } })).Content, "oldText not found");
            Check.Equal(calls + 1, env.Fake.Calls.Count); // read only, nothing written

            env.Fake.Reply = (args, _) => Remote(args).Contains("head -c")
                ? new SshExec(0, "__netpi_stat=17 1700000000\n__netpi_hash=" + hash + "\n" + file, "", false, false)
                : new SshExec(3, "", "changed: sha256 deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef\n", false, false);
            Check.Contains((await env.Run("ssh_edit", new { host = "nuc", path = "/srv/a.txt", oldText = "one", newText = "1" })).Content, "changed on the host");
        });

        r.Add("ssh_edit: a same-size change inside one second is refused (different hash, same stat)", async () =>
        {
            var env = new Env();
            var original = "aaaa\nbbbb\n"; // 10 bytes
            const string h1 = "sha256 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            const string h2 = "sha256 fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210fedcba";
            // the read records size 10, whole-second mtime 1700000000 and the content's hash
            env.Fake.Reply = (args, stdin) => Remote(args).Contains("head -c")
                ? new SshExec(0, "__netpi_stat=10 1700000000\n__netpi_hash=" + h1 + "\n" + original, "", false, false)
                // a concurrent writer made a same-size edit in the same second: the old marker would still read
                // "10 1700000000", but the hash has moved, so the remote refuses
                : new SshExec(3, "", "changed: " + h2 + "\n", false, false);
            var res = await env.Run("ssh_edit", new { host = "nuc", path = "/srv/a.txt", edits = new[] { new { oldText = "bbbb", newText = "BBBB" } } });
            Check.True(res.IsError, res.Content);
            Check.Contains(res.Content, "changed on the host while it was being edited; nothing was written");
            // the write's marker is the hash: no size/mtime comparison anywhere in it
            var write = env.Fake.Calls[^1];
            Check.Contains(Remote(write.Args), $"[ \"$h\" = '{h1}' ]");
            Check.NotContains(Remote(write.Args), "%s %Y");
            Check.NotContains(Remote(write.Args), "\"$s\" =");
            // the read is what recorded the hash
            Check.Contains(Remote(env.Fake.Calls[0].Args), "echo \"__netpi_hash=$h\"");
        });

        r.Add("ssh_edit: the remote write script, run in a local bash (matching hash renames over the target, stale hash refused, an interrupted write leaves the original intact)", async () =>
        {
            var bash = FindBash();
            if (bash is null)
            {
                Console.WriteLine("    (no local bash found; the remote script is not run)");
                return;
            }
            var env = new Env();
            var dir = T.TempDir("sshedit");
            var target = Path.Combine(dir, "doc.txt");
            File.WriteAllText(target, "aaaa\nbbbb\n");
            var posix = PosixPath(target);
            var chmodWorks = ChmodWorks(bash);
            if (chmodWorks) RunRemote(bash, $"chmod 0640 '{posix}'", null);
            // the read script the tool would send, run against the real file
            env.Fake.Reply = (_, _) => new SshExec(0, "", "", false, false);
            await env.Run("ssh_read", new { host = "nuc", path = posix });
            var readScript = Remote(env.Fake.Calls[^1].Args);
            var (rex, rout, rerr) = RunRemote(bash, readScript, null);
            Check.Equal(0, rex, "the read script runs in a POSIX shell: " + rerr);
            Check.True(rout.StartsWith("__netpi_stat=10 "), "stat line first: " + Check.Show(rout));
            Check.Contains(rout, "__netpi_hash=");
            // feed that real reply back into the tool so the write script carries the real hash
            env.Fake.Reply = (_, _) => new SshExec(0, rout, "", false, false);
            var edit = await env.Run("ssh_edit", new { host = "nuc", path = posix, edits = new[] { new { oldText = "bbbb", newText = "BBBB" } } });
            Check.False(edit.IsError, edit.Content);
            var writeScript = Remote(env.Fake.Calls[^1].Args);
            var newContent = env.Fake.Calls[^1].Stdin!;
            string[] Leftovers() => Directory.GetFiles(dir).Where(f => Path.GetFileName(f) != "doc.txt").ToArray();
            Check.Equal(0, Leftovers().Length);

            // a matching hash: the temp file is renamed over the target, the mode is kept, nothing is left behind
            var (wex, _, werr) = RunRemote(bash, writeScript, newContent);
            Check.Equal(0, wex, "a matching hash writes: " + werr);
            Check.Equal("aaaa\nBBBB\n", File.ReadAllText(target), "the target now holds the new content");
            if (chmodWorks) Check.Equal("640", RunRemote(bash, $"stat -c '%a' '{posix}'", null).Out.Trim(), "the mode is preserved");
            Check.Equal(0, Leftovers().Length, "no temp file left behind");

            // a same-size rewrite by a concurrent writer: the hash has moved, the write is refused, the file untouched
            File.WriteAllText(target, "cccc\nbbbb\n");
            var (sex, _, serr) = RunRemote(bash, writeScript, newContent);
            Check.Equal(3, sex, "a stale hash is refused even at the same size within the same second: " + serr);
            Check.True(serr.TrimStart().StartsWith("changed: "), "the refusal says what changed: " + serr);
            Check.Equal("cccc\nbbbb\n", File.ReadAllText(target), "the file is untouched");
            Check.Equal(0, Leftovers().Length);

            // a write that fails before the rename: the original stays exactly as it was, the temp is removed
            var (rex2, rout2, rerr2) = RunRemote(bash, readScript, null);
            Check.Equal(0, rex2, rerr2);
            env.Fake.Reply = (_, _) => new SshExec(0, rout2, "", false, false);
            await env.Run("ssh_edit", new { host = "nuc", path = posix, edits = new[] { new { oldText = "bbbb", newText = "BBBB" } } });
            var freshScript = Remote(env.Fake.Calls[^1].Args);
            var freshContent = env.Fake.Calls[^1].Stdin!;
            const string prelude = "fb=$(mktemp -d); printf '#!/bin/sh\\necho \"fake cat: simulated write failure\" >&2\\nexit 1\\n' > \"$fb/cat\"; chmod +x \"$fb/cat\"; export PATH=\"$fb:$PATH\"";
            var (iex, _, ierr) = RunRemote(bash, freshScript, freshContent, prelude);
            Check.True(iex != 0, "a failed write exits non-zero (exit " + iex + ")");
            Check.Contains(ierr, "simulated write failure");
            Check.Contains(ierr, "writing the temporary copy failed");
            Check.Equal("cccc\nbbbb\n", File.ReadAllText(target), "the original is intact");
            Check.Equal(0, Leftovers().Length, "the temp was removed");
        });

        // What a client that can multiplex answers when the probe asks about a socket that is not there yet.
        const string MuxYes = "Control socket connect(/tmp/x): No such file or directory";
        /// <summary>OpenSSH_for_Windows, which accepts ControlMaster and then cannot use the socket.</summary>
        const string MuxNo = "getsockname failed: Not a socket\nRead from remote host localhost: Unknown error";

        r.Add("ssh: one master connection per host (ControlMaster, a hashed per-host-and-user ControlPath, persist)", async () =>
        {
            if (OperatingSystem.IsWindows())
            {
                // OpenSSH's multiplexing needs the master to fork a child per session, which the Windows ports do not do,
                // so the plugin never asks for it there: the "no multiplexing" test below covers this platform.
                Console.WriteLine("    (Windows: OpenSSH cannot multiplex, so the multiplexed path does not exist here)");
                return;
            }
            var env = new Env();
            env.Fake.Reply = (_, _) => new SshExec(0, "", "", false, false);
            var real = SshClient.Probe;
            SshClient.Probe = (_, args) => args.Contains("-O") ? (1, MuxYes) : (0, "");
            SshClient.Forget();
            try
            {
                await env.Run("ssh_run", new { host = "nuc", script = "ls" });
                var args = string.Join(" ", env.Fake.Calls.Single().Args);
                // %C is OpenSSH's hash of (remote host, port, remote user, local host): unique per host and per user, short
                Check.Contains(args, "ControlMaster=auto");
                Check.Contains(args, $"ControlPath={Path.Combine(env.Ctx.Paths.Home, "ssh")}{Path.DirectorySeparatorChar}netpi-%C");
                Check.Contains(args, "ControlPersist=300");
                Check.Contains(args, "ControlIdleTimeout=300");
                Check.True(Directory.Exists(Path.Combine(env.Ctx.Paths.Home, "ssh")), "the control socket's directory is created");

                // scp rides on the same options (the same socket)
                File.WriteAllText(Path.Combine(env.Dir, "data.bin"), "1");
                env.Ctx.SettingsFake.Set("ssh.scpPath", JsonValue.Create("fake-scp"));
                await env.Run("ssh_copy", new { host = "nuc", direction = "upload", from = "data.bin", to = "/tmp/" });
                var scp = string.Join(" ", env.Fake.Calls[^1].Args);
                Check.Equal("fake-scp", env.Fake.Calls[^1].Exe);
                Check.Contains(scp, "ControlMaster=auto");
                Check.Contains(scp, "netpi-%C");
            }
            finally { SshClient.Probe = real; SshClient.Forget(); }
        });

        r.Add("ssh: an option this client does not know is left out, and the call still works", async () =>
        {
            // OpenSSH_for_Windows has no ControlIdleTimeout: passing it is a hard error on every call ("Bad
            // configuration option"), so the client is asked once and the option goes when the answer is no.
            if (OperatingSystem.IsWindows())
            {
                // The idle timeout only travels with multiplexing, and Windows never multiplexes: there is nothing to
                // drop here, and the test below says what Windows does instead.
                Console.WriteLine("    (Windows: no multiplexing, so no per-option question)");
                return;
            }
            var asked = new List<string>();
            var real = SshClient.Probe;
            SshClient.Probe = (_, args) => args.Contains("-O") ? (1, MuxYes) : (0, "");
            SshClient.Forget();
            try
            {
                var env = new Env();
                env.Fake.Reply = (_, _) => new SshExec(0, "", "", false, false);
                // A client that can multiplex but does not know the idle timeout: asked about exactly that one option.
                var options = new FileIOPermissionAwareProbe(asked, reject: "ControlIdleTimeout");
                SshClient.Probe = (_, args) => options.Ask(args);
                await env.Run("ssh_run", new { host = "nuc", script = "ls" });
                var args = string.Join(" ", env.Fake.Calls.Single().Args);
                Check.NotContains(args, "ControlIdleTimeout", "the option the client rejected is not passed");
                Check.Contains(args, "BatchMode=yes", "the options it does know are untouched");
                Check.Contains(args, "ConnectTimeout=10");
                Check.Equal(1, env.Fake.Calls.Count, "the call ran once");
                Check.True(asked.Contains("ControlIdleTimeout"), "and the client was asked about it: " + string.Join(",", asked));
            }
            finally { SshClient.Probe = real; SshClient.Forget(); }
        });

        r.Add("ssh: a client that cannot multiplex runs without connection reuse instead of failing", async () =>
        {
            // OpenSSH_for_Windows (NetPI's own client on Windows) accepts ControlMaster and then fails every session
            // over the socket ("getsockname failed: Not a socket"), so it must not be asked to multiplex at all.
            var real = SshClient.Probe;
            SshClient.Probe = (_, args) => args.Contains("-O") ? (1, MuxNo) : (0, "");
            SshClient.Forget();
            try
            {
                var env = new Env();
                env.Fake.Reply = (_, _) => new SshExec(0, "ok", "", false, false);
                var r = await env.Run("ssh_run", new { host = "nuc", script = "ls" });
                Check.False(r.IsError, "the call runs: " + r.Content);
                var args = string.Join(" ", env.Fake.Calls.Single().Args);
                Check.NotContains(args, "ControlMaster", "no multiplexing is asked for");
                Check.NotContains(args, "ControlPath");
                Check.NotContains(args, "ControlPersist");
                Check.NotContains(args, "ControlIdleTimeout");
                Check.Contains(args, "BatchMode=yes", "the rest of the options are unchanged");
                Check.Contains(args, "ServerAliveInterval=15");
            }
            finally { SshClient.Probe = real; SshClient.Forget(); }
        });

        r.Add("ssh: on Windows no client is asked to multiplex, whatever it claims", async () =>
        {
            // The Git/MSYS build creates a real socket and keeps the master up, and still resets the first session over
            // it, so the question cannot be answered by asking the client: on Windows the answer is always no.
            var asked = 0;
            var real = SshClient.Probe;
            SshClient.Probe = (_, args) => { if (args.Contains("-O")) asked++; return args.Contains("-O") ? (1, MuxYes) : (0, ""); };
            SshClient.Forget();
            try
            {
                var env = new Env();
                env.Fake.Reply = (_, _) => new SshExec(0, "ok", "", false, false);
                var r = await env.Run("ssh_run", new { host = "nuc", script = "ls" });
                Check.False(r.IsError, "the call runs: " + r.Content);
                var args = string.Join(" ", env.Fake.Calls.Single().Args);
                Check.NotContains(args, "ControlMaster", OperatingSystem.IsWindows() ? "Windows cannot multiplex" : "a client that cannot multiplex");
                Check.Equal(OperatingSystem.IsWindows() ? 0 : 1, asked, "and the client is not asked a question the platform already answers");
            }
            finally { SshClient.Probe = real; SshClient.Forget(); }
        });

        r.Add("ssh: the client is asked once, not on every call", async () =>
        {
            var asked = 0;
            var real = SshClient.Probe;
            SshClient.Probe = (_, args) => { if (args.Contains("-O")) asked++; return args.Contains("-O") ? (1, MuxYes) : (0, ""); };
            SshClient.Forget();
            try
            {
                var env = new Env();
                env.Fake.Reply = (_, _) => new SshExec(0, "", "", false, false);
                await env.Run("ssh_run", new { host = "nuc", script = "ls" });
                await env.Run("ssh_run", new { host = "nuc", script = "ls" });
                await env.Run("ssh_run", new { host = "server", script = "ls" });
                Check.Equal(OperatingSystem.IsWindows() ? 0 : 1, asked,
                    "at most one question for three calls (the answer belongs to the client, not the call)");
            }
            finally { SshClient.Probe = real; SshClient.Forget(); }
        });

        r.Add("ssh: a client that cannot be asked loses the options, not the call", async () =>
        {
            var real = SshClient.Probe;
            SshClient.Probe = (_, _) => throw new InvalidOperationException("the client is not there");
            SshClient.Forget();
            try
            {
                var env = new Env();
                env.Fake.Reply = (_, _) => new SshExec(0, "ok", "", false, false);
                var r = await env.Run("ssh_run", new { host = "nuc", script = "ls" });
                Check.False(r.IsError, "the call still runs: " + r.Content);
                Check.NotContains(string.Join(" ", env.Fake.Calls.Single().Args), "ControlMaster");
            }
            finally { SshClient.Probe = real; SshClient.Forget(); }
        });

        r.Add("ssh: TextEdits (unique matches, replace_all, overlap, hunks and context)", () =>
        {
            var ten = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"row{i:D2}")) + "\n";
            var two = TextEdits.Apply(ten, [new TextEdit("row02\n", "ROW02\n", false), new TextEdit("row11", "ROW11", false)], "f");
            Check.Equal(2, two.Diff.Split("@@ -").Length - 1); // far apart: two hunks
            Check.Contains(two.Diff, "@@ -1,5 +1,5 @@\n row01\n-row02\n+ROW02\n row03\n row04\n row05\n");
            Check.Contains(two.Diff, "-row11\n+ROW11\n row12\n");
            Check.NotContains(two.Diff, "row12\n \n"); // no phantom line after the final newline
            var near = TextEdits.Apply(ten, [new TextEdit("row04", "ROW04", false), new TextEdit("row06", "ROW06", false)], "f");
            Check.Equal(1, near.Diff.Split("@@ -").Length - 1); // close: one hunk
            var removed = TextEdits.Apply("a\nb\nc\n", [new TextEdit("b\n", "", false)], "f");
            Check.Equal("a\nc\n", removed.Text);
            Check.Contains(removed.Diff, " a\n-b\n c\n");
            Check.Equal((0, 1), (removed.Added, removed.Removed));
            var all = TextEdits.Apply("x y x", [new TextEdit("x", "z", true)], "f");
            Check.Equal("z y z", all.Text);
            Check.Equal(2, all.Replacements);
            try { TextEdits.Apply("x y x", [new TextEdit("x", "z", false)], "f"); throw new AssertException("expected an error"); }
            catch (EditException ex) { Check.Contains(ex.Message, "matches 2 times"); }
            try { TextEdits.Apply("abc", [new TextEdit("ab", "1", false), new TextEdit("bc", "2", false)], "f"); throw new AssertException("expected an error"); }
            catch (EditException ex) { Check.Contains(ex.Message, "overlap"); }
        });

        r.Add("ssh_copy: scp runs in the local folder with a relative name (no drive-letter colon)", async () =>
        {
            var env = new Env();
            File.WriteAllText(Path.Combine(env.Dir, "data.bin"), "12345");
            env.Fake.Reply = (_, _) => new SshExec(0, "", "", false, false);
            env.Ctx.SettingsFake.Set("ssh.scpPath", JsonValue.Create("fake-scp"));
            var up = await env.Run("ssh_copy", new { host = "nuc", direction = "upload", from = "data.bin", to = "/tmp/in/" });
            Check.False(up.IsError, up.Content);
            var call = env.Fake.Calls[^1];
            Check.Equal("fake-scp", call.Exe);
            Check.Equal(env.Dir, call.WorkDir);
            Check.Equal("./data.bin nuc:/tmp/in/", string.Join(" ", call.Args.TakeLast(2)));
            Check.True(call.Args.Contains("-p") && call.Args.Contains("BatchMode=yes") && !call.Args.Contains("-r"));

            var down = await env.Run("ssh_copy", new { host = "nuc", direction = "download", from = "/etc/hostname", to = "got/hostname.txt" });
            Check.False(down.IsError, down.Content);
            Check.Equal(Path.Combine(env.Dir, "got"), env.Fake.Calls[^1].WorkDir);
            Check.Equal("nuc:/etc/hostname ./hostname.txt", string.Join(" ", env.Fake.Calls[^1].Args.TakeLast(2)));

            Directory.CreateDirectory(Path.Combine(env.Dir, "folder"));
            Check.Contains((await env.Run("ssh_copy", new { host = "nuc", direction = "upload", from = "folder", to = "/tmp/" })).Content, "set recursive");
        });

        r.Add("ssh launcher: the timeout covers the stdin transmission (a child that never reads stdin is stopped, not hung)", async () =>
        {
            // A child that never reads its stdin blocks the launcher's write of 8 MiB in the pipe, so the only thing
            // that can end the call is the timeout — which has to start before the transmission, not after it.
            var (exe, args) = OperatingSystem.IsWindows()
                ? ("ping", new[] { "-n", "61", "127.0.0.1" })
                : ("sleep", new[] { "60" });
            var sw = Stopwatch.StartNew();
            var call = new ProcessLauncher().RunAsync(exe, args, new byte[8 * 1024 * 1024], null, null, null, TimeSpan.FromMilliseconds(500), CancellationToken.None);
            var done = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(10)));
            Check.True(ReferenceEquals(done, call), $"the call gave up at the timeout, in {sw.Elapsed}");
            var r = await call;
            Check.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"returned in {sw.Elapsed}");
            Check.True(r.TimedOut, $"TimedOut={r.TimedOut} Aborted={r.Aborted} ExitCode={r.ExitCode}");
            Check.False(r.Aborted, $"Aborted={r.Aborted} TimedOut={r.TimedOut}");

            // And the normal case is untouched: a child that exits promptly still finishes normally.
            var (fast, fastArgs) = OperatingSystem.IsWindows()
                ? ("ping", new[] { "-n", "1", "127.0.0.1" })
                : ("true", Array.Empty<string>());
            var ok = await new ProcessLauncher().RunAsync(fast, fastArgs, new byte[] { 1, 2, 3 }, null, null, null, TimeSpan.FromSeconds(10), CancellationToken.None);
            Check.False(ok.TimedOut, $"TimedOut={ok.TimedOut} Aborted={ok.Aborted}");
            Check.False(ok.Aborted, $"TimedOut={ok.TimedOut} Aborted={ok.Aborted}");
            Check.Equal(0, ok.ExitCode, "exit code of a child that exits on its own");
        });

        r.Add("ssh live (NETPI_SSH_TEST_HOSTS): quoting, files, copy, cwd, timeout and abort end the remote processes", async () =>
        {
            var hosts = (Environment.GetEnvironmentVariable("NETPI_SSH_TEST_HOSTS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (hosts.Length == 0)
            {
                Console.WriteLine("    (set NETPI_SSH_TEST_HOSTS=nuc,server to run against real hosts)");
                return;
            }
            foreach (var host in hosts) await Live(host);
        });
    }

    /// <summary>A local bash to run the generated remote scripts in (Git Bash on Windows, /bin/bash elsewhere).</summary>
    private static string? FindBash()
    {
        if (!OperatingSystem.IsWindows()) return File.Exists("/bin/bash") ? "/bin/bash" : null;
        foreach (var c in new[]
        {
            @"C:\Program Files\Git\usr\bin\bash.exe",
            @"C:\Program Files\Git\bin\bash.exe",
        })
            if (File.Exists(c)) return c;
        return null;
    }

    /// <summary>C:\Users\x → /c/Users/x (how the Git Bash shell sees a Windows path).</summary>
    private static string PosixPath(string win)
    {
        var s = win.Replace('\\', '/');
        if (s.Length >= 3 && char.IsLetter(s[0]) && s[1] == ':') s = char.ToLowerInvariant(s[0]) + s[2..];
        return "/" + s;
    }

    /// <summary>Runs a generated remote script in a local bash the way the ssh login shell would: -c script, stdin in, exit out.</summary>
    private static (int Exit, string Out, string Err) RunRemote(string bash, string script, byte[]? stdin, string prelude = "")
    {
        var body = (prelude.Length > 0 ? prelude + "; " : "") + script;
        var psi = new ProcessStartInfo(bash)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(body);
        // Git's bash.exe takes the caller's PATH as it is: started from PowerShell (scripts/test.ps1) that has no Git usr\bin, and the
        // script's stat/chmod/mv are "command not found". From Git Bash the PATH already has it, which is why the test only ever
        // failed under PowerShell. Put the folder of the bash that runs the script first.
        if (OperatingSystem.IsWindows() && Path.GetDirectoryName(bash) is { Length: > 0 } binDir)
            psi.Environment["PATH"] = binDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        using var p = Process.Start(psi)!;
        var outT = p.StandardOutput.ReadToEndAsync();
        var errT = p.StandardError.ReadToEndAsync();
        if (stdin is not null)
        {
            try { p.StandardInput.BaseStream.Write(stdin, 0, stdin.Length); } catch (IOException) { }
        }
        try { p.StandardInput.Close(); } catch (IOException) { } // the child may have exited before it read it
        if (!p.WaitForExit(30_000)) { try { p.Kill(true); } catch { } throw new AssertException("the remote script did not finish within 30s"); }
        return (p.ExitCode, outT.GetAwaiter().GetResult(), errT.GetAwaiter().GetResult());
    }

    /// <summary>Whether chmod actually changes modes on this machine (it does not in every Git Bash).</summary>
    private static bool ChmodWorks(string bash)
    {
        var dir = T.TempDir("chmodprobe");
        var f = PosixPath(Path.Combine(dir, "probe"));
        File.WriteAllText(Path.Combine(dir, "probe"), "x");
        var r = RunRemote(bash, $"chmod 0604 '{f}' && stat -c '%a' '{f}'", null);
        return r.Exit == 0 && r.Out.Trim() == "604";
    }

    private static async Task Live(string host)
    {
        var env = new Env(new ProcessLauncher(), SshOptions.DefaultConfigPath);
        var dir = $"/tmp/netpi-ssh-test-{Guid.NewGuid():N}"[..34];
        async Task<ToolResult> Run(string script, int timeout = 60, string? cwd = null, CancellationToken ct = default)
        {
            var r = await env.Run("ssh_run", new { host, script, timeout, cwd }, ct);
            return r;
        }
        try
        {
            // a script full of quoting traps, run as it is
            var tricky = "x='it'\"'\"'s'; echo \"[$x]\" '$HOME' \"$((6*7))\" \\\\n\ncat <<'EOF'\nraw $HOME `id -u` \"q\" 'q' — ü\nEOF\nprintf '%s\\n' \"tab\there\"";
            var run = await Run(tricky);
            Check.False(run.IsError, run.Content);
            Check.Equal("[it's] $HOME 42 \\n\nraw $HOME `id -u` \"q\" 'q' — ü\ntab\there", run.Content);

            // write → read → edit → read
            var content = "a 'b' \"c\" $d `e` \\f\nline 2 — ü\n";
            var write = await env.Run("ssh_write", new { host, path = $"{dir}/sub dir/f.txt", content });
            Check.False(write.IsError, write.Content);
            Check.True(D(write).GetProperty("created").GetBoolean());
            var read = await env.Run("ssh_read", new { host, path = $"{dir}/sub dir/f.txt" });
            Check.Equal(content.TrimEnd('\n'), read.Content);
            var edit = await env.Run("ssh_edit", new { host, path = "sub dir/f.txt", cwd = dir, edits = new[] { new { oldText = "line 2", newText = "LINE TWO" } } });
            Check.False(edit.IsError, edit.Content);
            Check.Contains((await env.Run("ssh_read", new { host, path = $"{dir}/sub dir/f.txt" })).Content, "LINE TWO — ü");
            var pwd = await Run("pwd", cwd: dir);
            Check.Equal(dir, pwd.Content);

            // a 5 MB file reads and edits whole (the launcher keeps more than ssh_read fetches)
            var big = string.Concat(Enumerable.Range(0, 100_000).Select(i => $"line {i:D6} {new string('x', 40)}\n"));
            Check.False((await env.Run("ssh_write", new { host, path = $"{dir}/big.txt", content = big })).IsError);
            Check.Equal($"line 099999 {new string('x', 40)}", (await env.Run("ssh_read", new { host, path = $"{dir}/big.txt", offset = -1 })).Content);
            var bigEdit = await env.Run("ssh_edit", new { host, path = $"{dir}/big.txt", edits = new[] { new { oldText = "line 099998 ", newText = "LINE 099998 " } } });
            Check.False(bigEdit.IsError, bigEdit.Content);
            Check.Equal("1", (await Run($"grep -c '^LINE 099998 ' '{dir}/big.txt'")).Content);

            // copy up and down, byte for byte
            var local = Path.Combine(env.Dir, "blob.bin");
            var bytes = Enumerable.Range(0, 70_000).Select(i => (byte)(i * 7 % 256)).ToArray();
            File.WriteAllBytes(local, bytes);
            var up = await env.Run("ssh_copy", new { host, direction = "upload", from = local, to = $"{dir}/blob.bin" });
            Check.False(up.IsError, up.Content);
            var down = await env.Run("ssh_copy", new { host, direction = "download", from = $"{dir}/blob.bin", to = Path.Combine(env.Dir, "back.bin") });
            Check.False(down.IsError, down.Content);
            Check.True(File.ReadAllBytes(Path.Combine(env.Dir, "back.bin")).SequenceEqual(bytes), "downloaded bytes equal");

            // a timeout ends the whole remote process group, children included
            var slow = await Run("sleep 4321 & sleep 4322", timeout: 2);
            Check.True(slow.IsError);
            Check.Contains(slow.Content, "timed out after 2s");
            await Task.Delay(1500);
            Check.Equal("none", (await Run("pgrep -f 'sleep 432[12]' || echo none")).Content);

            // stopping the run ends it too
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var aborted = await Run("sleep 4323 & sleep 4324", timeout: 120, ct: cts.Token);
            Check.True(aborted.IsError);
            Check.Contains(aborted.Content, "[aborted");
            await Task.Delay(4500);
            Check.Equal("none", (await Run("pgrep -f 'sleep 432[34]' || echo none")).Content);
        }
        finally
        {
            await Run($"rm -rf -- '{dir}'");
        }
    }
}
