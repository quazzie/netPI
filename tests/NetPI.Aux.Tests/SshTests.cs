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

        public Task<ToolResult> Run(string tool, object args, CancellationToken ct = default) =>
            Tools[tool].ExecuteAsync(new ToolContext
            {
                SessionId = "ses_1", AgentId = "agt_1", CallId = "call_1", Cwd = Dir, Services = Ctx.Services, Events = Ctx.Events,
                Output = s => { lock (Live) Live.Append(s); },
            }, T.Args(args), ct);
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
            Check.Equal("ssh_copy,ssh_edit,ssh_hosts,ssh_read,ssh_run,ssh_write", string.Join(",", env.Tools.Keys.Order()));
            Check.True(env.Tools.Values.All(t => t.Definition.Category == "ssh"));
            Check.True(env.Tools["ssh_hosts"].Definition.ReadOnly && env.Tools["ssh_read"].Definition.ReadOnly);
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

            env.Fake.Reply = (_, _) => new SshExec(0, "__netpi_stat=18 1700000000\nalpha\nbeta\ngamma\n", "", false, false);
            var read = await env.Run("ssh_read", new { host = "nuc", path = "/etc/demo" });
            Check.Equal("alpha\nbeta\ngamma", read.Content);
            Check.Equal(3, D(read).GetProperty("totalLines").GetInt32());
            Check.Contains(Remote(env.Fake.Calls[^1].Args), "stat -c '__netpi_stat=%s %Y' -- \"$p\" && head -c ");
            var paged = await env.Run("ssh_read", new { host = "nuc", path = "/etc/demo", offset = 2, limit = 1 });
            Check.Equal("beta\n\n[Showing lines 2-2 of 3. Use offset=3 to continue.]", paged.Content);
            Check.Equal("gamma", (await env.Run("ssh_read", new { host = "nuc", path = "/etc/demo", offset = -1 })).Content);

            env.Fake.Reply = (_, _) => new SshExec(0, "__netpi_stat=4 1\nab\0c", "", false, false);
            Check.Contains((await env.Run("ssh_read", new { host = "nuc", path = "bin" })).Content, "appears to be a binary file");
            env.Fake.Reply = (_, _) => new SshExec(2, "", "No such file: /x\n", false, false);
            Check.Equal("nuc: No such file: /x", (await env.Run("ssh_read", new { host = "nuc", path = "/x" })).Content);
        });

        r.Add("ssh_edit: exact replacements keep CRLF, write only when the file is unchanged, diff for the UI", async () =>
        {
            var env = new Env();
            var file = "one\r\ntwo\r\nthree\r\n";
            env.Fake.Reply = (args, stdin) => Remote(args).Contains("head -c")
                ? new SshExec(0, "__netpi_stat=16 1700000000\n" + file, "", false, false)
                : new SshExec(0, "", "", false, false);
            var res = await env.Run("ssh_edit", new { host = "nuc", path = "/srv/a.txt", edits = new[] { new { oldText = "two", newText = "TWO\nextra" } } });
            Check.False(res.IsError, res.Content);
            Check.Equal("Applied 1 edit to nuc:/srv/a.txt (+2 −1)", res.Content);
            var write = env.Fake.Calls[^1];
            Check.Equal("one\r\nTWO\r\nextra\r\nthree\r\n", Utf8(write.Stdin));
            Check.Contains(Remote(write.Args), "[ \"$s\" = '16 1700000000' ] || { echo \"changed: $s\" >&2; exit 3; }; cat >\"$p\"");
            Check.Contains(D(res).GetProperty("diff").GetString()!, "@@ -1,3 +1,4 @@\n one\n-two\n+TWO\n+extra\n three\n");
            Check.Equal("crlf", D(res).GetProperty("eol").GetString());

            var calls = env.Fake.Calls.Count;
            Check.Contains((await env.Run("ssh_edit", new { host = "nuc", path = "/srv/a.txt", edits = new[] { new { oldText = "four", newText = "4" } } })).Content, "oldText not found");
            Check.Equal(calls + 1, env.Fake.Calls.Count); // read only, nothing written

            env.Fake.Reply = (args, _) => Remote(args).Contains("head -c")
                ? new SshExec(0, "__netpi_stat=16 1700000000\n" + file, "", false, false)
                : new SshExec(3, "", "changed: 20 1700000009\n", false, false);
            Check.Contains((await env.Run("ssh_edit", new { host = "nuc", path = "/srv/a.txt", oldText = "one", newText = "1" })).Content, "changed on the host");
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
