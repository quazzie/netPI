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
        public Env(ISshLauncher? launcher = null, string? config = null, SshBroker? broker = null)
        {
            if (config is null)
            {
                config = Path.Combine(Dir, "config");
                File.WriteAllText(config, "Host nuc\n  HostName 192.168.1.3\n  User quazzie\nHost server\n  HostName 192.168.1.2\n");
            }
            Ctx.SettingsFake.Set("ssh.config", JsonValue.Create(config));
            if (launcher is null) Ctx.SettingsFake.Set("ssh.path", JsonValue.Create("fake-ssh"));
            Tools = SshToolSet.Create(Ctx, launcher ?? Fake, broker).ToDictionary(t => t.Definition.Name);
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
            // a plain directory (no glob): ssh_config(5) includes the files inside it, not the directory
            Directory.CreateDirectory(Path.Combine(dir, "plain"));
            File.WriteAllText(Path.Combine(dir, "plain", "b.conf"), "Host fromdir\n  HostName 10.0.0.8\n");
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
                Include plain
                host "quoted name"
                """);
            var hosts = SshConfig.Read(Path.Combine(dir, "config"));
            Check.Equal("server,nuc,extra,fromdir,quoted name", string.Join(",", hosts.Select(h => h.Alias)));
            Check.Equal(new SshHost("server", "192.168.1.2", "quazzie", null), hosts[0]);
            Check.Equal(new SshHost("nuc", "192.168.1.3", null, 2222), hosts[1]);
            Check.Equal("10.0.0.9", hosts[2].HostName);
            Check.Equal("10.0.0.8", hosts[3].HostName, "a host from a plain-directory Include is not dropped");
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

        r.Add("ssh: the dispatcher's schema covers every action's arguments", () =>
        {
            // The handlers read their arguments themselves; the dispatcher's schema is all the model sees, so every
            // argument a handler accepts must be a property of it.
            var env = new Env();
            var def = env.Tools["ssh"].Definition;
            var props = def.Parameters["properties"]!.AsObject();
            void Covers(string action, params string[] args)
            {
                foreach (var a in args)
                    Check.True(props.ContainsKey(a), $"{action} reads {a} but the dispatcher's schema has no {a}");
            }
            var actions = props["action"]!.AsObject()["enum"]!.AsArray().Select(n => n!.GetValue<string>()!).ToHashSet();
            Check.True(actions.SetEquals(new[] { "hosts", "run", "read", "write", "edit", "copy" }),
                "the action enum names the actions: " + string.Join(", ", actions.OrderBy(x => x)));
            foreach (var a in def.Parameters["required"]!.AsArray().Select(n => n!.GetValue<string>()!))
                Check.True(props.ContainsKey(a), $"required {a} is not a property");
            Covers("run", "host", "script", "cwd", "timeout");
            Covers("read", "host", "path", "cwd", "offset", "limit");
            Covers("write", "host", "path", "content", "append", "cwd");
            Covers("edit", "host", "path", "cwd", "edits", "replace_all");
            Covers("copy", "host", "direction", "from", "to", "recursive");
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
            var remote = Remote(call.Args);
            Check.Contains(remote, "bash -c 'set -m; bash -c \"echo __netpi_pgid=\\$\\$; exec timeout -k 5 30 bash \\\"\\$0\\\" </dev/null 2>&1\" \"$0\" & p=$!; wait $p;");
            Check.NotContains(remote, "setsid", "no util-linux setsid: BusyBox's has no --wait (Home Assistant's SSH add-on)");
            Check.Contains(remote, "if [ $SECONDS -ge 30 ] && { [ $c -eq 124 ] || [ $c -eq 137 ] || [ $c -eq 143 ]; }; then kill -TERM -- -$p;", "a timeout BusyBox reports as 143 ends the group and is reported as 124");
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

        r.Add("ssh_run: a single line that outruns the tail budget keeps its end, and a lone trailing CR keeps its line", async () =>
        {
            var env = new Env();
            // one 100 KB line: the old char-counting loop kept nothing of it ("(no output)"), the byte-counting tail keeps its end
            var big = new string('x', 100_000);
            env.Fake.Reply = (_, _) => new SshExec(0, "__netpi_pgid=1\n" + big + "\n", "", false, false);
            var res = await env.Run("ssh_run", new { host = "nuc", script = "head -c 100000 /dev/zero | tr 0 x" });
            Check.False(res.IsError, res.Content);
            Check.True(res.Content.StartsWith("[Output truncated: showing the last 1 lines of 1. Full output saved to "), res.Content[..140]);
            Check.True(res.Content.EndsWith(big[..1000]), "the tail of the giant line is there, not (no output)");
            var saved = D(res).GetProperty("fullOutputPath").GetString()!;
            try { Check.True(File.ReadAllText(saved).Contains(big), "the whole line is saved"); }
            finally { File.Delete(saved); }

            // a line that ends in a lone \r (what \r\r\n leaves) is not emptied by the progress-line collapse
            env.Fake.Reply = (_, _) => new SshExec(0, "__netpi_pgid=1\ndone\r\r\n", "", false, false);
            Check.Equal("done", (await env.Run("ssh_run", new { host = "nuc", script = "printf 'done\\r\\r'" })).Content);
            env.Fake.Reply = (_, _) => new SshExec(0, "__netpi_pgid=1\n", "progress 10%\rprogress 50%\r\n", false, false);
            Check.Equal("progress 50%", (await env.Run("ssh_run", new { host = "nuc", script = "ls" })).Content, "overwrite progress still collapses");
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
            Check.Contains(Remote(call.Args), "p='/tmp/a b/it'\\''s.txt'; if [ -e \"$p\" ]; then echo existed; fi; mkdir -p -- \"$(dirname -- \"$p\")\" || exit 4; ");
            Check.Contains(Remote(call.Args), "t=$(mktemp -- \"${p}.netpi.XXXXXX\") || exit 5");
            Check.Contains(Remote(call.Args), "cat >\"$t\"");
            Check.Contains(Remote(call.Args), "chmod -- \"$(stat -c '%a' -- \"$p\")\" \"$t\"");
            Check.Contains(Remote(call.Args), "mv -f -- \"$t\" \"$p\"");
            Check.NotContains(Remote(call.Args), "cat >\"$p\"", "the target is never the stream's destination");
            env.Fake.Reply = (_, _) => new SshExec(0, "existed\n", "", false, false);
            Check.True((await env.Run("ssh_write", new { host = "nuc", path = "x.txt", content = "a" })).Content.StartsWith("Wrote nuc:x.txt"));
            await env.Run("ssh_write", new { host = "nuc", path = "log", content = "more\n", append = true });
            Check.Contains(Remote(env.Fake.Calls[^1].Args), "cat >>\"$p\"");

            // a read does not hash the file (only ssh_edit needs the hash): the reply is stat + text, no hash line
            env.Fake.Reply = (_, _) => new SshExec(0, "__netpi_stat=18 1700000000\nalpha\nbeta\ngamma\n", "", false, false);
            var read = await env.Run("ssh_read", new { host = "nuc", path = "/etc/demo" });
            Check.Equal("alpha\nbeta\ngamma", read.Content);
            Check.Equal(3, D(read).GetProperty("totalLines").GetInt32());
            Check.Contains(Remote(env.Fake.Calls[^1].Args), "stat -c '__netpi_stat=%s %Y' -- \"$p\" && head -c ");
            Check.NotContains(Remote(env.Fake.Calls[^1].Args), "sha256sum", "a read does not hash the whole file");
            Check.NotContains(Remote(env.Fake.Calls[^1].Args), "__netpi_hash");
            var paged = await env.Run("ssh_read", new { host = "nuc", path = "/etc/demo", offset = 2, limit = 1 });
            Check.Equal("beta\n\n[Showing lines 2-2 of 3. Use offset=3 to continue.]", paged.Content);
            // a negative offset counts from the file's end, so the tool captures the tail, not the head
            var fromEnd = await env.Run("ssh_read", new { host = "nuc", path = "/etc/demo", offset = -1 });
            Check.Equal("gamma", fromEnd.Content);
            Check.Contains(Remote(env.Fake.Calls[^1].Args), "tail -c ");

            env.Fake.Reply = (_, _) => new SshExec(0, "__netpi_stat=4 1\nab\0c", "", false, false);
            Check.Contains((await env.Run("ssh_read", new { host = "nuc", path = "bin" })).Content, "appears to be a binary file");
            env.Fake.Reply = (_, _) => new SshExec(2, "", "No such file: /x\n", false, false);
            Check.Equal("nuc: No such file: /x", (await env.Run("ssh_read", new { host = "nuc", path = "/x" })).Content);
        });

        r.Add("ssh_read: big-file windowing (no hash, tail for negatives, ssh_run hint), timeout and abort are named", async () =>
        {
            var env = new Env();
            const long big = 40L * 1024 * 1024; // 40 MB
            string Window(string tag) => "__netpi_stat=" + big + " 1700000000\n" + string.Join("\n", Enumerable.Range(0, 100).Select(i => $"{tag} {i}")) + "\n";

            // the first 8 MB window: a head fetch reports the window and says further content is ssh_run's job
            env.Fake.Reply = (_, _) => new SshExec(0, Window("head"), "", false, false);
            var first = await env.Run("ssh_read", new { host = "nuc", path = "/var/log/huge.log" });
            Check.False(first.IsError, first.Content);
            Check.Contains(first.Content, "the first 8 MB of a 40 MB file");
            Check.Contains(first.Content, "ssh_run");
            Check.True(D(first).GetProperty("truncated").GetBoolean());

            // an offset past the window cannot be reached by paging: it says so, and names the tool that can
            var beyond = await env.Run("ssh_read", new { host = "nuc", path = "/var/log/huge.log", offset = 10_000_000 });
            Check.True(beyond.IsError);
            Check.Contains(beyond.Content, "beyond the first 8 MB");
            Check.Contains(beyond.Content, "ssh_run");

            // a negative offset reaches the window's end, which is the file's end
            var atEnd = await env.Run("ssh_read", new { host = "nuc", path = "/var/log/huge.log", offset = -5 });
            Check.False(atEnd.IsError, atEnd.Content);
            Check.Contains(atEnd.Content, "the last 8 MB of a 40 MB file");
            Check.Contains(atEnd.Content, "end of the file");
            Check.False(D(atEnd).GetProperty("truncated").GetBoolean(), "the tail window ends at the file's end");
            Check.Contains(Remote(env.Fake.Calls[^1].Args), "tail -c ");

            // a timed-out read is named as such, with the escape hatches; an aborted one is not a remote failure
            env.Fake.Reply = (_, _) => new SshExec(-1, "", "", true, false);
            var slow = await env.Run("ssh_read", new { host = "nuc", path = "/var/log/huge.log" });
            Check.True(slow.IsError);
            Check.Contains(slow.Content, "timed out");
            Check.Contains(slow.Content, "ssh_run");
            env.Fake.Reply = (_, _) => new SshExec(-1, "", "", false, true);
            Check.Contains((await env.Run("ssh_read", new { host = "nuc", path = "/var/log/huge.log" })).Content, "aborted");
        });

        r.Add("ssh_read: a single line that outruns the page budget is clamped and named, not handed over whole", async () =>
        {
            var env = new Env();
            var bigLine = new string('x', 60_000); // ~60 KB: more than the 50 KB page
            var body = bigLine + "\nafter\n";
            env.Fake.Reply = (_, _) => new SshExec(0, "__netpi_stat=" + body.Length + " 1700000000\n" + body, "", false, false);
            var res = await env.Run("ssh_read", new { host = "nuc", path = "/etc/one-line" });
            Check.False(res.IsError, res.Content);
            Check.True(res.Content.StartsWith(bigLine[..SshReadTool.MaxChars]), "the line is cut to the budget");
            Check.Equal(SshReadTool.MaxChars, res.Content.IndexOf('\n'), "nothing after the clamped line except the notes");
            Check.Contains(res.Content, $"[Line 1 is {SshToolBase.Size(60_000)} long; showing its first {SshToolBase.Size(SshReadTool.MaxChars)}");
            Check.Contains(res.Content, "cut -c");
            Check.NotContains(res.Content, "after", "the rest of the file is not in this page");
            Check.True(D(res).GetProperty("truncated").GetBoolean(), "a clamped page is truncated");
            // the page after the huge line is reachable as usual
            var next = await env.Run("ssh_read", new { host = "nuc", path = "/etc/one-line", offset = 2 });
            Check.Equal("after", next.Content);
            Check.False(D(next).GetProperty("truncated").GetBoolean());
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
            // the read-with-hash script the tool sends (ssh_edit's own fetch carries the hash a read no longer does), run against the real file
            env.Fake.Reply = (args, _) => Remote(args).Contains("head -c")
                ? new SshExec(0, $"__netpi_stat=10 1700000000\n__netpi_hash=sha256 {new string('0', 64)}\n{File.ReadAllText(target)}", "", false, false)
                : new SshExec(0, "", "", false, false);
            await env.Run("ssh_edit", new { host = "nuc", path = posix, edits = new[] { new { oldText = "not present here", newText = "x" } } });
            var readScript = Remote(env.Fake.Calls[^1].Args);
            Check.Contains(readScript, "__netpi_hash=", "the edit's read script still carries the hash");
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

        r.Add("ssh: the offline client probe is one deadline over start, read and wait", () =>
        {
            var dir = T.TempDir("cp");
            string Exe(string name, string winBody, string posixBody)
            {
                if (OperatingSystem.IsWindows())
                {
                    var win = Path.Combine(dir, name + ".cmd");
                    File.WriteAllText(win, winBody);
                    return win;
                }
                var p = Path.Combine(dir, name);
                File.WriteAllText(p, "#!/bin/sh\n" + posixBody);
                File.SetUnixFileMode(p, UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                return p;
            }
            var ok = ChildProcess.RunAsync(Exe("ok", "@echo the answer 1>&2\nexit 7\n", "echo the answer >&2\nexit 7\n"), ["-G"], TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Check.False(ok.TimedOut);
            Check.Equal(7, ok.Exit, "the client's own exit code");
            Check.Contains(ok.Err, "the answer", "the answer is read from stderr");

            var slow = ChildProcess.RunAsync(Exe("slow", "@ping -n 4 127.0.0.1 >nul\nexit 0\n", "sleep 4\n"), [], TimeSpan.FromMilliseconds(300)).GetAwaiter().GetResult();
            Check.True(slow.TimedOut, "the deadline ends the run, whatever the client is doing");
            Check.Contains(slow.Err, "did not answer");
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
            // Edits apply to the evolving text, like the local edit: "bc" no longer exists once "ab" became "1"
            try { TextEdits.Apply("abc", [new TextEdit("ab", "1", false), new TextEdit("bc", "2", false)], "f"); throw new AssertException("expected an error"); }
            catch (EditException ex) { Check.Contains(ex.Message, "oldText not found"); }
            // ...and a second edit may match what a first one wrote (rename the declaration, then its first use)
            var seq = TextEdits.Apply("alpha beta\n", [new TextEdit("alpha beta", "alpha gamma", false), new TextEdit("alpha gamma", "omega gamma", false)], "f");
            Check.Equal("omega gamma\n", seq.Text);
            Check.Equal("--- a/f\n+++ b/f\n@@ -1,1 +1,1 @@\n-alpha beta\n+omega gamma\n", seq.Diff);
        });

        r.Add("ssh: ssh_edit and edit agree on one edit list (each edit sees what the earlier ones wrote)", async () =>
        {
            var dir = T.TempDir("parity");
            var file = Path.Combine(dir, "f.txt");
            var original = "one\ntwo\nthree\n";
            File.WriteAllText(file, original);
            var list = new[] { new { oldText = "one\ntwo", newText = "ONE two" }, new { oldText = "ONE two", newText = "TWO" } };
            var remote = TextEdits.Apply(original, [new TextEdit("one\ntwo", "ONE two", false), new TextEdit("ONE two", "TWO", false)], file);
            Check.Equal("TWO\nthree\n", remote.Text, "the second edit's oldText only exists after the first edit");
            var local = await new NetPI.Tools.Files.EditTool().ExecuteAsync(
                new ToolContext { SessionId = "s", AgentId = "a", CallId = "c", Cwd = dir, Services = new FakeServices(), Events = new FakeBus() },
                T.Args(new { path = file, edits = list }), CancellationToken.None);
            Check.False(local.IsError, local.Content);
            Check.Equal(remote.Text, File.ReadAllText(file), "the local edit tool lands on the same text");

            // and what neither tool accepts, both refuse without writing: a failed edit applies nothing
            var failed = await new NetPI.Tools.Files.EditTool().ExecuteAsync(
                new ToolContext { SessionId = "s", AgentId = "a", CallId = "c", Cwd = dir, Services = new FakeServices(), Events = new FakeBus() },
                T.Args(new { path = file, edits = new[] { new { oldText = "missing", newText = "x" }, new { oldText = "TWO", newText = "3" } } }), CancellationToken.None);
            Check.True(failed.IsError);
            Check.Contains(failed.Content, "Edit 1 of 2 failed");
            var ex = Check.Throws<EditException>(() => TextEdits.Apply("TWO\nthree\n", [new TextEdit("missing", "x", false), new TextEdit("TWO", "3", false)], file));
            Check.Contains(ex.Message, "oldText not found");
            Check.Equal("TWO\nthree\n", File.ReadAllText(file), "the file is untouched by the failed list");
        });

        r.Add("ssh_write: the remote write script, run in a local bash (a new file gets 644, an existing one keeps its mode, an interrupted stream leaves the original intact)", async () =>
        {
            var bash = FindBash();
            if (bash is null)
            {
                Console.WriteLine("    (no local bash found; the remote script is not run)");
                return;
            }
            var env = new Env();
            env.Fake.Reply = (_, _) => new SshExec(0, "", "", false, false);
            var dir = T.TempDir("sshwrite");
            var posix = PosixPath(dir);
            var chmodWorks = ChmodWorks(bash);
            async Task<(int Exit, string Out, string Err)> WriteScript(object contentArg, string? prelude = null)
            {
                await env.Run("ssh_write", new { host = "nuc", path = $"{posix}/doc.txt", content = contentArg });
                return RunRemote(bash, Remote(env.Fake.Calls[^1].Args), env.Fake.Calls[^1].Stdin!, prelude);
            }

            // a new file: the temp is renamed into place, mode 644, nothing left behind
            var (nex, nout, nerr) = await WriteScript("aaaa\nbbbb\n");
            Check.Equal(0, nex, "a write to a new file succeeds: " + nerr);
            Check.False(nout.Contains("existed"), "the target did not exist yet");
            Check.Equal("aaaa\nbbbb\n", File.ReadAllText(Path.Combine(dir, "doc.txt")));
            if (chmodWorks) Check.Equal("644", RunRemote(bash, $"stat -c '%a' '{posix}/doc.txt'", null).Out.Trim(), "a new file is world-readable");
            Check.Equal(0, Directory.GetFiles(dir).Count(f => Path.GetFileName(f) != "doc.txt"), "no temp file left behind");

            // an existing file keeps its mode and content is replaced atomically
            if (chmodWorks) RunRemote(bash, $"chmod 0640 '{posix}/doc.txt'", null);
            var (rex, rout, rerr) = await WriteScript("cccc\ndddd\n");
            Check.Equal(0, rex, "a write over an existing file succeeds: " + rerr);
            Check.Contains(rout, "existed", "the existing file was detected before the stream");
            Check.Equal("cccc\ndddd\n", File.ReadAllText(Path.Combine(dir, "doc.txt")));
            if (chmodWorks) Check.Equal("640", RunRemote(bash, $"stat -c '%a' '{posix}/doc.txt'", null).Out.Trim(), "the existing mode is preserved");

            // an interrupted stream: the temp dies with the failure, the original stays exactly as it was
            File.WriteAllText(Path.Combine(dir, "doc.txt"), "original\n");
            const string prelude = "fb=$(mktemp -d); printf '#!/bin/sh\\necho \"fake cat: simulated write failure\" >&2\\nexit 1\\n' > \"$fb/cat\"; chmod +x \"$fb/cat\"; export PATH=\"$fb:$PATH\"";
            var (iex, _, ierr) = await WriteScript("new\n", prelude);
            Check.True(iex != 0, "a failed write exits non-zero (exit " + iex + ")");
            Check.Contains(ierr, "simulated write failure");
            Check.Contains(ierr, "writing the temporary copy failed");
            Check.Equal("original\n", File.ReadAllText(Path.Combine(dir, "doc.txt")), "the original is intact, not truncated");
            Check.Equal(0, Directory.GetFiles(dir).Where(f => Path.GetFileName(f) != "doc.txt").Count(), "the temp was removed");

            // over the cap: refused without a single ssh call, with the alternatives
            var before = env.Fake.Calls.Count;
            var big = await env.Run("ssh_write", new { host = "nuc", path = "x", content = new string('a', SshToolBase.MaxWriteBytes + 1) });
            Check.True(big.IsError);
            Check.Contains(big.Content, $"ssh_write streams at most {SshToolBase.Size(SshToolBase.MaxWriteBytes)}");
            Check.Contains(big.Content, "ssh_run");
            Check.Contains(big.Content, "ssh_copy");
            Check.Equal(before, env.Fake.Calls.Count, "nothing was sent over the wire");
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
            foreach (var host in hosts)
            {
                await Live(host);
                // the same suite again over kept connections (idea-pac35h): every result has to be the same
                using var broker = new SshBroker(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
                var watch = Stopwatch.StartNew();
                await Live(host, broker);
                Console.WriteLine($"    {host} over kept connections: {watch.ElapsedMilliseconds} ms, {broker.Open()} open at the end");
            }
        });

        r.Add("ssh broker: calls share one connection; stdout, stderr, exit codes, stdin and unread stdin keep their bytes (idea-pac35h)", async () =>
        {
            if (FindBash() is not { } bash) { Console.WriteLine("    (no local bash found; the broker is not run)"); return; }
            var starts = 0;
            using var broker = new SshBroker(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, LocalBash(bash, () => Interlocked.Increment(ref starts)));
            var live = new StringBuilder();
            Task<SshExec?> Run(string command, string? stdin = null, int seconds = 20, int perHost = 3) =>
                broker.TryRunAsync(BrokerOptions, BrokerHost, command, stdin is null ? null : Encoding.UTF8.GetBytes(stdin), TimeSpan.FromSeconds(seconds),
                    perHost, TimeSpan.FromMinutes(5), s => { lock (live) live.Append(s); }, null, CancellationToken.None);

            var r1 = (await Run("echo hello; echo oops >&2; exit 3"))!;
            Check.Equal("hello\n", r1.Stdout);
            Check.Equal("oops\n", r1.Stderr, "stderr is its own stream, ended by its own marker");
            Check.Equal(3, r1.ExitCode);
            Check.Equal("hello\n", live.ToString(), "the live output is the command's, without the end marker");

            Check.Equal("line1\nno newline", (await Run("cat", "line1\nno newline"))!.Stdout, "stdin arrives byte for byte, and output without a final newline keeps it that way");
            Check.Equal("åäö € ✓", (await Run("printf 'åäö € ✓'"))!.Stdout);
            Check.Equal("x __netpi_end_0123 X 0\ny", (await Run("printf 'x __netpi_end_0123 X 0\\ny'"))!.Stdout, "a marker-like line with another nonce is output");

            // a command that does not read its stdin must not leave the bytes for the next frame
            var big = new string('z', 300_000);
            Check.Equal(0, (await Run("true", big))!.ExitCode);
            Check.Equal("after\n", (await Run("echo after"))!.Stdout);
            Check.Equal(300_000.ToString(), (await Run("wc -c | tr -d ' '", big))!.Stdout.Trim(), "a large stdin is delivered whole");

            Check.Equal(1, starts, "every call rode the one connection");
            Check.Equal(1, broker.Open());
        });

        r.Add("ssh broker: connections open up to the limit, a call past it runs the old way, a timeout drops its connection", async () =>
        {
            if (FindBash() is not { } bash) { Console.WriteLine("    (no local bash found; the broker is not run)"); return; }
            var starts = 0;
            using var broker = new SshBroker(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, LocalBash(bash, () => Interlocked.Increment(ref starts)));
            Task<SshExec?> Run(string command, int perHost, int seconds = 20) =>
                broker.TryRunAsync(BrokerOptions, BrokerHost, command, null, TimeSpan.FromSeconds(seconds), perHost, TimeSpan.FromMinutes(5), null, null, CancellationToken.None);

            var both = await Task.WhenAll(Run("sleep 1; echo a", 2), Run("sleep 1; echo b", 2));
            Check.Equal("a\nb\n", string.Concat(both.Select(r => r!.Stdout)));
            Check.Equal(2, starts, "two calls at once: two connections");

            var slow = Run("sleep 2; echo slow", 2);
            var slower = Run("sleep 2; echo slower", 2);
            await Task.Delay(300);
            Check.Equal(null, await Run("echo third", 2), "both connections busy: the call runs the old way (null)");
            Check.Equal("slow\n", (await slow)!.Stdout);
            Check.Equal("slower\n", (await slower)!.Stdout);

            // what a call printed before it was stopped is in its result: ssh_run reads its process group id from there to
            // end the remote work, and a short last line was once held back as a possible end marker and lost
            var stopped = (await Run("printf '__netpi_pgid=4242\\n'; " + Ticking.Replace("echo tick", "echo tick >&2", StringComparison.Ordinal), 2, seconds: 1))!;
            Check.True(stopped.TimedOut);
            Check.Equal("__netpi_pgid=4242\n", stopped.Stdout);

            var timedOut = (await Run(Ticking, 2, seconds: 1))!;
            Check.True(timedOut.TimedOut, "the call's own timeout");
            Check.Equal(0, broker.Open(), "both connections timed out (the stopped call and this one) and are gone");
            Check.Equal("fine\n", (await Run("echo fine", 2))!.Stdout, "the next call works");

            using var abort = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var aborted = (await broker.TryRunAsync(BrokerOptions, BrokerHost, Ticking, null, TimeSpan.FromSeconds(20), 2, TimeSpan.FromMinutes(5), null, null, abort.Token))!;
            Check.True(aborted.Aborted && !aborted.TimedOut, "an abort is an abort");
        });

        r.Add("ssh broker: a connection whose handshake does not come back sends the host's calls the old way for a while", async () =>
        {
            if (FindBash() is not { } bash) { Console.WriteLine("    (no local bash found; the broker is not run)"); return; }
            var starts = 0;
            var cooldown = SshBroker.Cooldown;
            try
            {
                using var broker = new SshBroker(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                    LocalBash(bash, () => Interlocked.Increment(ref starts), replace: "echo 'Permission denied (publickey).' >&2; exit 255"));
                Task<SshExec?> Run() => broker.TryRunAsync(BrokerOptions, BrokerHost, "echo hi", null, TimeSpan.FromSeconds(10), 3, TimeSpan.FromMinutes(5), null, null, CancellationToken.None);
                Check.Equal(null, await Run(), "no handshake: the call runs the old way, which reports ssh's own error");
                Check.Equal(null, await Run());
                Check.Equal(1, starts, "the host is not tried again while it cools down");
                Check.Equal(0, broker.Open());

                // with no cooldown the next call tries again (the cooldown is fixed when the handshake fails)
                SshBroker.Cooldown = TimeSpan.Zero;
                using var again = new SshBroker(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                    LocalBash(bash, () => Interlocked.Increment(ref starts), replace: "exit 255"));
                for (var i = 0; i < 2; i++)
                    Check.Equal(null, await again.TryRunAsync(BrokerOptions, BrokerHost, "echo hi", null, TimeSpan.FromSeconds(10), 3, TimeSpan.FromMinutes(5), null, null, CancellationToken.None));
                Check.Equal(3, starts, "after the cooldown it is tried again");
            }
            finally { SshBroker.Cooldown = cooldown; }
        });

        r.Add("ssh tool: where the client cannot multiplex, calls go through the broker; ssh.reuseConnections off is one ssh per call", async () =>
        {
            if (!OperatingSystem.IsWindows() || FindBash() is not { } bash) { Console.WriteLine("    (Windows with a local bash only: elsewhere the client multiplexes itself)"); return; }
            var starts = 0;
            using var broker = new SshBroker(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, LocalBash(bash, () => Interlocked.Increment(ref starts)));
            var env = new Env(broker: broker);
            var first = await env.Run("ssh_run", new { host = "nuc", script = "echo kept" });
            Check.Contains(first.Content, "kept");
            await env.Run("ssh_run", new { host = "nuc", script = "echo again" });
            Check.Equal(0, env.Fake.Calls.Count, "no ssh of its own");
            Check.Equal(1, starts, "two calls, one connection");

            env.Ctx.SettingsFake.Set("ssh.reuseConnections", JsonValue.Create(false));
            await env.Run("ssh_run", new { host = "nuc", script = "echo own" });
            Check.Equal(1, env.Fake.Calls.Count, "reuse off: the call runs its own ssh");
        });
    }

    private static readonly SshOptions BrokerOptions = new("ssh", "scp", "config", true, 5, 30, null);
    private static readonly SshHost BrokerHost = new("h1", null, null, null);

    /// <summary>
    /// A command that runs until it is stopped and then ends by itself: killing the local bash that stands in for the
    /// remote side does not reach an MSYS child, but a writer whose pipe has closed dies on its next write. A plain
    /// sleep would outlive the test and hold the runner's output open for its full length.
    /// </summary>
    private const string Ticking = "while :; do echo tick || exit; sleep 0.2; done";

    /// <summary>
    /// The broker's connection as a local bash running the remote command, as sshd would: PATH with the bash's own tools
    /// (Git's usr\bin on Windows) and SHELL set. <paramref name="replace"/> runs instead of the broker script (a failing ssh).
    /// </summary>
    private static SshBroker.Starter LocalBash(string bash, Action onStart, string? replace = null) => (_, _, remote) =>
    {
        onStart();
        var psi = new ProcessStartInfo(bash)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(replace ?? remote);
        if (OperatingSystem.IsWindows() && Path.GetDirectoryName(bash) is { Length: > 0 } binDir)
            psi.Environment["PATH"] = binDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        psi.Environment["SHELL"] = OperatingSystem.IsWindows() ? "/usr/bin/bash" : "/bin/bash";
        return Process.Start(psi)!;
    };

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
    private static (int Exit, string Out, string Err) RunRemote(string bash, string script, byte[]? stdin, string? prelude = null)
    {
        var body = (prelude is { Length: > 0 } pre ? pre + "; " : "") + script;
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

    private static async Task Live(string host, SshBroker? broker = null)
    {
        var env = new Env(new ProcessLauncher(), SshOptions.DefaultConfigPath, broker);
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
