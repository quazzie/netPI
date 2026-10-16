extern alias claudecli;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using NetPI;
using CL = claudecli::NetPI.Providers.ClaudeCli;

namespace NetPI.Providers.Tests;

/// <summary>What a Claude CLI provider needs beyond the happy path: usage from the CLI's own output, side calls, limits, the child's environment and lifetime.</summary>
internal static class ClaudeCliProductionTests
{
    private const string Init = """{"type":"system","subtype":"init","apiKeySource":"none"}""";
    private const string Result = """{"type":"result","subtype":"success","is_error":false,"result":"ok","usage":{"input_tokens":1,"output_tokens":1}}""";
    private const string RateLimit = """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1791307800,"rateLimitType":"five_hour","unifiedWindows":{"five_hour":{"utilization":0.24,"resetsAt":1791307800},"seven_day":{"utilization":0.09,"resetsAt":1791514800},"seven_day_opus":{"utilization":0.5,"resetsAt":1791514800},"overage":{"utilization":0.1}}}}""";

    private static async IAsyncEnumerable<string> Lines(IEnumerable<string> lines, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var l in lines) { ct.ThrowIfCancellationRequested(); yield return l; await Task.Yield(); }
    }

    private static ModelRequest Req(string purpose = "agent") => new()
    {
        Model = new ModelInfo { Provider = "claude-cli", Id = "claude-sonnet-5-5" },
        SystemPrompt = "You are a planner.",
        Messages = [ChatMessage.UserText("Plan a thing.")],
        SessionId = "ses_1",
        Purpose = purpose,
    };

    private static JsonObject Cfg(Action<JsonObject>? tweak = null)
    {
        var o = new JsonObject { ["enabled"] = true };
        tweak?.Invoke(o);
        return o;
    }

    private static async Task<List<ModelStreamEvent>> Collect(IModelProvider p, ModelRequest r)
    {
        var list = new List<ModelStreamEvent>();
        await foreach (var e in p.StreamAsync(r, CancellationToken.None)) list.Add(e);
        return list;
    }

    private static async Task<ModelException?> Fails(IModelProvider p, ModelRequest r)
    {
        try { await Collect(p, r); return null; }
        catch (ModelException ex) { return ex; }
    }

    public static async Task RunAsync(TestRunner t)
    {
        await t.Run("claude-cli usage: the CLI's own rate_limit_event becomes the reading (fractions as percent), kept and stored, no credentials read", async () =>
        {
            JsonObject? stored = null;
            var published = new List<JsonObject>();
            var tracker = new CL.ClaudeUsageTracker(snap => stored = snap) { OnChanged = published.Add };
            var p = new CL.ClaudeCliProvider(() => Cfg(), null, (_, ct) => Lines([Init, RateLimit, Result], ct), usage: tracker);
            await Collect(p, Req());

            var snap = tracker.Get();
            var limits = snap["limits"]!.AsArray().Select(l => l!.AsObject()).ToDictionary(l => l["key"]!.GetValue<string>());
            t.Eq(3, limits.Count, "session, week, week (opus): the overage block and unknown windows are not limits");
            t.Eq(24.0, limits["session"]["percent"]!.GetValue<double>(), "0.24 is 24 %");
            t.Eq(9.0, limits["weekly_all"]["percent"]!.GetValue<double>(), "weekly");
            t.Eq("Week (Opus)", limits["weekly_opus"]["label"]!.GetValue<string>(), "a model window is named");
            t.Eq(true, limits["session"]["active"]!.GetValue<bool>(), "the window the event is about is active");
            t.Eq("2026-10-06T", limits["session"]["resetsAt"]!.GetValue<string>()[..11], "resetsAt is an ISO time");
            t.Check(stored is not null && published.Count == 1, "the reading was stored and published");

            // a restart: the stored reading shows until a call brings a fresher one
            var again = new CL.ClaudeUsageTracker();
            again.Restore(stored);
            t.Eq(3, again.Get()["limits"]!.AsArray().Count, "restored");
            again.SetPlan("max");
            t.Eq("max", again.Get()["plan"]!.GetValue<string>(), "the plan from `claude auth status`");

            // the pill can be switched off
            var off = new CL.ClaudeUsageTracker();
            p = new CL.ClaudeCliProvider(() => Cfg(o => o["usage"] = false), null, (_, ct) => Lines([Init, RateLimit, Result], ct), usage: off);
            await Collect(p, Req());
            t.Eq(0, off.Get()["limits"]!.AsArray().Count, "usage off: nothing recorded");
        });

        await t.Run("claude-cli usage refresh: Anthropic's answer becomes the reading, one ask a minute, concurrent callers share it, a failure throws", async () =>
        {
            var answer = JsonNode.Parse("""
                {"five_hour":{"utilization":52.0},"limits":[
                 {"kind":"session","percent":52,"severity":"normal","resets_at":"2026-10-07T20:30:00.330650+00:00","scope":null,"is_active":true},
                 {"kind":"weekly_all","percent":41,"severity":"warning","resets_at":"2026-10-09T03:00:00+00:00","scope":null,"is_active":false},
                 {"kind":"weekly_scoped","percent":7.25,"severity":"normal","resets_at":"2026-10-09T03:00:00+00:00","scope":{"model":{"display_name":"Fable"}},"is_active":false},
                 {"kind":"weekly_scoped","percent":1,"scope":null},{"kind":"other","percent":3}]}
                """)!.AsObject();
            var snap = CL.ClaudeUsageTracker.NormalizeApi(answer)!;
            var limits = snap["limits"]!.AsArray().Select(l => l!.AsObject()).ToDictionary(l => l["key"]!.GetValue<string>());
            t.Eq(3, limits.Count, "session, week, week (a model); a scoped window without a model and an unknown kind are left out");
            t.Eq(41.0, limits["weekly_all"]["percent"]!.GetValue<double>(), "percent is already percent");
            t.Eq("Week (Fable)", limits["weekly_fable"]["label"]!.GetValue<string>(), "a model window is named");
            t.Eq("warning", limits["weekly_all"]["severity"]!.GetValue<string>(), "severity kept");
            t.Eq(true, limits["session"]["active"]!.GetValue<bool>(), "active kept");
            t.Check(CL.ClaudeUsageTracker.NormalizeApi(JsonNode.Parse("""{"limits":[]}""")!.AsObject()) is null, "no windows, no reading");

            var published = new List<JsonObject>();
            var tracker = new CL.ClaudeUsageTracker { OnChanged = published.Add };
            var calls = 0;
            var gate = new TaskCompletionSource();
            async Task<JsonObject> Fetch(CancellationToken _) { calls++; await gate.Task; return snap.DeepClone().AsObject(); }
            var first = tracker.RefreshAsync(Fetch, CancellationToken.None);
            var second = tracker.RefreshAsync(Fetch, CancellationToken.None);
            t.Check(!first.IsCompleted, "waits for the answer");
            gate.SetResult();
            await Task.WhenAll(first, second);
            t.Eq(1, calls, "two callers, one ask");
            t.Eq(1, published.Count, "published once");
            t.Eq(3, (await tracker.RefreshAsync(Fetch, CancellationToken.None))["limits"]!.AsArray().Count, "within a minute: the reading it has");
            t.Eq(1, calls, "no second ask inside the minute");

            var failing = new CL.ClaudeUsageTracker();
            var threw = false;
            try { await failing.RefreshAsync(_ => throw new RpcException("unavailable", "expired"), CancellationToken.None); }
            catch (RpcException) { threw = true; }
            t.Check(threw, "a failed ask throws");
            // within the minute after it the failure is the answer again: an old reading is not passed off as the live one
            var asked = 0;
            string? again = null;
            try { await failing.RefreshAsync(_ => { asked++; return Task.FromResult(new JsonObject()); }, CancellationToken.None); }
            catch (RpcException ex) { again = ex.Message; }
            t.Eq("expired", again, "the same failure, within the minute");
            t.Eq(0, asked, "and Anthropic is not asked again inside the minute");
        });

        await t.Run("claude-cli: the models carry the CLI's real window, learned from its result line, and say they keep their own context", async () =>
        {
            var learned = 0;
            var withLimits = """{"type":"result","subtype":"success","is_error":false,"result":"ok","modelUsage":{"claude-haiku-4-5":{"contextWindow":400000,"maxOutputTokens":50000}}}""";
            var p = new CL.ClaudeCliProvider(() => Cfg(o => o["models"] = new JsonArray("claude-sonnet-5-5", "claude-haiku-4-5")), null, (_, ct) => Lines([Init, withLimits], ct)) { OnModelsLearned = () => learned++ };
            var before = (await p.ListModelsAsync(true, CancellationToken.None)).ToDictionary(m => m.Id);
            t.Eq(1_000_000, before["claude-sonnet-5-5"].ContextWindow, "a Claude 5 model: 1M");
            t.Eq(200_000, before["claude-haiku-4-5"].ContextWindow, "an earlier one: 200k");
            t.Check(before.Values.All(m => m.ManagesOwnContext), "NetPI's compaction leaves these chats alone");

            var req = new ModelRequest
            {
                Model = new ModelInfo { Provider = "claude-cli", Id = "claude-haiku-4-5" }, SystemPrompt = "x",
                Messages = [ChatMessage.UserText("hi")], SessionId = "ses_1", Purpose = "agent",
            };
            await Collect(p, req);
            var after = (await p.ListModelsAsync(true, CancellationToken.None)).ToDictionary(m => m.Id);
            t.Eq(400_000, after["claude-haiku-4-5"].ContextWindow, "what the CLI said replaces the table");
            t.Eq(50_000, after["claude-haiku-4-5"].MaxOutputTokens, "and the output limit");
            t.Eq(1, learned, "the catalog was told once");
            await Collect(p, req);
            t.Eq(1, learned, "not again for the same limits");

            // The CLI lists every model it used: a Haiku for an Explore subagent comes first here, and is not the model asked for
            var twoModels = """{"type":"result","subtype":"success","is_error":false,"result":"ok","modelUsage":{"claude-haiku-4-5":{"contextWindow":200000,"maxOutputTokens":64000},"claude-sonnet-5-5-20260101":{"contextWindow":900000,"maxOutputTokens":100000}}}""";
            var q = new CL.ClaudeCliProvider(() => Cfg(), null, (_, ct) => Lines([Init, twoModels], ct));
            await Collect(q, new ModelRequest { Model = new ModelInfo { Provider = "claude-cli", Id = "claude-sonnet-5-5" }, SystemPrompt = "x", Messages = [ChatMessage.UserText("hi")], SessionId = "ses_2", Purpose = "agent" });
            var sonnet = (await q.ListModelsAsync(true, CancellationToken.None)).Single(m => m.Id == "claude-sonnet-5-5");
            t.Eq(900_000, sonnet.ContextWindow, "the asked model's own (dated) entry, not the first one");
            t.Eq(100_000, sonnet.MaxOutputTokens, "and its output limit");
            var none = """{"type":"result","subtype":"success","is_error":false,"result":"ok","modelUsage":{"claude-haiku-4-5":{"contextWindow":200000},"claude-fable-5-1":{"contextWindow":300000}}}""";
            q = new CL.ClaudeCliProvider(() => Cfg(), null, (_, ct) => Lines([Init, none], ct));
            await Collect(q, new ModelRequest { Model = new ModelInfo { Provider = "claude-cli", Id = "claude-sonnet-5-5" }, SystemPrompt = "x", Messages = [ChatMessage.UserText("hi")], SessionId = "ses_2", Purpose = "agent" });
            t.Eq(1_000_000, (await q.ListModelsAsync(true, CancellationToken.None)).Single(m => m.Id == "claude-sonnet-5-5").ContextWindow,
                "several entries and none of them the asked model: nothing is learned");
            t.Check(CL.ClaudeCliStream.PickModelUsage(JsonNode.Parse("""{"x":{"contextWindow":1}}""")!.AsObject(), "claude-opus-5-5") is not null,
                "a single entry is the call's model, whatever its spelling");
        });

        await t.Run("claude-cli: the prompt files and a side call's folder are the work folder the plugin gives (its home), not a shared temp folder", async () =>
        {
            var home = Path.Combine(Path.GetTempPath(), "netpi-claude-home-" + Guid.NewGuid().ToString("N"));
            try
            {
                var seen = new List<(string File, string WorkDir)>();
                var p = new CL.ClaudeCliProvider(() => Cfg(), null, (call, ct) =>
                {
                    var args = call.Args.ToList();
                    seen.Add((args[args.IndexOf("--system-prompt-file") + 1], call.WorkDir));
                    return Lines([Init, Result], ct);
                }, workDir: Path.Combine(home, "claude-cli"));
                await Collect(p, Req("title"));
                t.Check(seen[0].File.StartsWith(Path.Combine(home, "claude-cli"), StringComparison.OrdinalIgnoreCase), $"the prompt file is in the work folder: {seen[0].File}");
                t.Eq(Path.Combine(home, "claude-cli"), seen[0].WorkDir, "and a side call runs there");
                t.Check(!File.Exists(seen[0].File), "the prompt file is gone after the call");
            }
            finally { try { Directory.Delete(home, true); } catch { /* temp */ } }
        });

        await t.Run("claude-cli: a title or compaction call through the model is text only, in no project", async () =>
        {
            var seen = new List<(List<string> Args, string WorkDir, string Prompt)>();
            var p = new CL.ClaudeCliProvider(() => Cfg(), null, (call, ct) =>
            {
                var args = call.Args.ToList();
                seen.Add((args, call.WorkDir, File.ReadAllText(args[args.IndexOf("--system-prompt-file") + 1])));
                return Lines([Init, Result], ct);
            }, cwdOf: _ => Path.GetTempPath());
            await Collect(p, Req("agent"));
            await Collect(p, Req("title"));
            await Collect(p, Req("compaction"));

            var agent = seen[0];
            t.Eq("default", agent.Args[agent.Args.IndexOf("--tools") + 1], "an agent turn has the CLI's tools");
            t.Eq(Path.GetTempPath(), agent.WorkDir, "in the session's folder");
            foreach (var side in seen.Skip(1))
            {
                t.Eq("", side.Args[side.Args.IndexOf("--tools") + 1], "a side call has no tools");
                t.Check(!side.Args.Contains("--permission-mode"), "and no permission mode to bypass");
                t.Check(!string.Equals(side.WorkDir, Path.GetTempPath(), StringComparison.OrdinalIgnoreCase), "and does not run in the session's folder");
                t.Check(side.Prompt.EndsWith(CL.ClaudeCliProvider.NoToolsNote), "and is told it has none");
            }
        });

        await t.Run("claude-cli: only maxConcurrent CLI processes run at once; the next call waits for a free slot", async () =>
        {
            var release = new TaskCompletionSource();
            var running = 0;
            var peak = 0;
            async IAsyncEnumerable<string> Slow([EnumeratorCancellation] CancellationToken ct)
            {
                var now = Interlocked.Increment(ref running);
                int p0; while ((p0 = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, p0) != p0) { }
                await release.Task.WaitAsync(ct);
                Interlocked.Decrement(ref running);
                yield return Init;
                yield return Result;
            }
            var p = new CL.ClaudeCliProvider(() => Cfg(o => o["maxConcurrent"] = 1), null, (_, ct) => Slow(ct));
            var first = Collect(p, Req());
            await Task.Delay(100);
            var second = Collect(p, Req());
            await Task.Delay(200);
            t.Eq(1, Volatile.Read(ref running), "the second call has not started: one slot");
            release.SetResult();
            var events = await second;
            await first;
            t.Eq(1, peak, "never more than one at a time");
            t.Check(events.OfType<StreamNotice>().Any(n => n.Text.Contains("wait")), "the call that waited says so");

            // a call that is cancelled while it waits leaves its place
            var gate = new CL.CallGate();
            var (lease, _) = await gate.EnterAsync(() => 1, CancellationToken.None);
            using var cts = new CancellationTokenSource(100);
            var cancelled = false;
            try { await gate.EnterAsync(() => 1, cts.Token); } catch (OperationCanceledException) { cancelled = true; }
            t.Check(cancelled, "cancelled while waiting");
            lease.Dispose();
            var (again, waited) = await gate.EnterAsync(() => 1, CancellationToken.None);
            t.Check(!waited && gate.Running == 1, "the slot is free again");
            again.Dispose();
            t.Eq(0, gate.Running, "released");
        });

        await t.Run("claude-cli: executable: a path as it is, a bare name from PATH", () =>
        {
            t.Eq(@"C:\tools\claude.exe", CL.ClaudeCliProvider.ResolveExecutable(@"C:\tools\claude.exe"), "a path is used as given");
            var dir = Path.Combine(Path.GetTempPath(), "netpi-claude-path-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var oldPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                var file = Path.Combine(dir, OperatingSystem.IsWindows() ? "fakeclaude.exe" : "fakeclaude");
                File.WriteAllText(file, "");
                Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + oldPath);
                t.Eq(file, CL.ClaudeCliProvider.ResolveExecutable("fakeclaude"), "found on PATH");
                t.Eq("no-such-cli-xyz", CL.ClaudeCliProvider.ResolveExecutable("no-such-cli-xyz"), "not found: the name, and the runner explains");
            }
            finally { Environment.SetEnvironmentVariable("PATH", oldPath); Directory.Delete(dir, true); }
            return Task.CompletedTask;
        });

        await t.Run("claude-cli: configDir (with ~ expanded) is the CLI's config folder, one per account", async () =>
        {
            CL.CliCall? seen = null;
            var p = new CL.ClaudeCliProvider(() => Cfg(o => o["configDir"] = "~/.claude-work"), null,
                (call, ct) => { seen = call; return Lines([Init, Result], ct); });
            await Collect(p, new ModelRequest { Model = new ModelInfo { Provider = "claude-cli", Id = "claude-sonnet-5-5" }, SystemPrompt = "x", Messages = [ChatMessage.UserText("hi")], SessionId = "ses_c", Purpose = "agent" });
            t.Eq(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude-work"), seen?.ConfigDir, "the folder the child gets as CLAUDE_CONFIG_DIR");
            var plain = new CL.ClaudeCliProvider(() => Cfg(), null, (call, ct) => { seen = call; return Lines([Init, Result], ct); });
            await Collect(plain, new ModelRequest { Model = new ModelInfo { Provider = "claude-cli", Id = "claude-sonnet-5-5" }, SystemPrompt = "x", Messages = [ChatMessage.UserText("hi")], SessionId = "ses_d", Purpose = "agent" });
            t.Check(string.IsNullOrEmpty(seen?.ConfigDir), "none set: the CLI keeps its default login");
        });

        await t.Run("claude-cli: the child does not inherit NetPI's secrets or an API key, and runs inside the kill-on-close job", async () =>
        {
            // the variables a secret setting reads, and shell.hideEnv
            var settings = new FakeSettings(null, new JsonObject
            {
                ["providers"] = new JsonObject { ["openrouter"] = new JsonObject { ["apiKey"] = "env:MY_ROUTER_KEY" }, ["other"] = new JsonObject { ["token"] = "$MY_OTHER" } },
                ["shell"] = new JsonObject { ["hideEnv"] = new JsonArray("MY_HIDDEN") },
            });
            var hidden = claudecli::NetPI.Providers.ClaudeCli.ClaudeCliPlugin.HiddenEnvironment(settings);
            t.Check(hidden.Contains("MY_ROUTER_KEY", StringComparer.OrdinalIgnoreCase) && hidden.Contains("MY_OTHER", StringComparer.OrdinalIgnoreCase)
                && hidden.Contains("MY_HIDDEN", StringComparer.OrdinalIgnoreCase), "settings secrets and shell.hideEnv are hidden");

            if (!OperatingSystem.IsWindows()) t.Skip("the fake CLI is a Windows batch file");
            var dir = Path.Combine(Path.GetTempPath(), "netpi-claude-env-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var names = new[] { "NETPI_TOKEN", "ANTHROPIC_API_KEY", "MY_HIDDEN", "MY_VISIBLE" };
            try
            {
                foreach (var n in names) Environment.SetEnvironmentVariable(n, "secret-" + n);
                var cmd = Path.Combine(dir, "claude.cmd");
                File.WriteAllText(cmd, "@echo off\r\necho {\"type\":\"x\",\"v\":\"%NETPI_TOKEN%|%ANTHROPIC_API_KEY%|%MY_HIDDEN%|%MY_VISIBLE%\"}\r\n");
                var lines = new List<string>();
                await foreach (var l in claudecli::NetPI.Providers.ClaudeCli.ProcessRunner.Run(
                    new claudecli::NetPI.Providers.ClaudeCli.CliCall(cmd, [], "", dir, TimeSpan.FromSeconds(30), ["MY_HIDDEN"]), CancellationToken.None))
                    lines.Add(l);
                var v = JsonNode.Parse(lines[0])!["v"]!.GetValue<string>();
                t.Eq("|||secret-MY_VISIBLE", v, "the secrets are gone from the child's environment, the user's own variables stay");

                using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
                try
                {
                    claudecli::NetPI.ProcessKit.ChildJob.Assign(child);
                    t.Check(claudecli::NetPI.ProcessKit.ChildJob.Contains(child), "a child is in the job that dies with NetPI");
                }
                finally { try { child.Kill(true); } catch { /* done */ } }
            }
            finally
            {
                foreach (var n in names) Environment.SetEnvironmentVariable(n, null);
                try { Directory.Delete(dir, true); } catch { /* temp */ }
            }
        });

        await t.Run("claude-cli: a consumer that stops reading early ends the CLI process", async () =>
        {
            if (!OperatingSystem.IsWindows()) t.Skip("the fake CLI is a Windows batch file");
            var dir = Path.Combine(Path.GetTempPath(), "netpi-claude-early-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var marker = Path.Combine(dir, "finished.txt");
                var cmd = Path.Combine(dir, "claude.cmd");
                // one line at once, then a few seconds of work, then the marker: a CLI left running writes it
                File.WriteAllText(cmd, "@echo off\r\necho {\"type\":\"x\"}\r\nping -n 5 127.0.0.1 > nul\r\necho done> \"" + marker + "\"\r\n");
                await foreach (var _ in claudecli::NetPI.Providers.ClaudeCli.ProcessRunner.Run(
                    new claudecli::NetPI.Providers.ClaudeCli.CliCall(cmd, [], "", dir, TimeSpan.FromSeconds(30), []), CancellationToken.None))
                    break;
                await Task.Delay(6000);
                t.Check(!File.Exists(marker), "the CLI was killed when the consumer stopped, not left to finish");
            }
            finally { try { Directory.Delete(dir, true); } catch { /* temp */ } }
        });

        await t.Run("claude-cli: a refusal for the subscription's limit says when it opens and is not retried; stderr explains an early exit", async () =>
        {
            var limited = new[]
            {
                Init,
                """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1791307800,"rateLimitType":"five_hour","unifiedWindows":{"five_hour":{"utilization":1.0,"resetsAt":1791307800}}}}""",
                """{"type":"result","subtype":"success","is_error":true,"result":"5-hour limit reached","api_error_status":429}""",
            };
            var p = new CL.ClaudeCliProvider(() => Cfg(), null, (_, ct) => Lines(limited, ct));
            var e = await Fails(p, Req());
            t.Check(e is { Transient: false, ErrorType: "rate_limit" } && e.Message.Contains("opens again at 2026-10-06"), $"limit: {e?.Message}");

            var died = new[] { Init, """{"type":"netpi_stderr","text":"Error: out of memory"}""" };
            p = new CL.ClaudeCliProvider(() => Cfg(), null, (_, ct) => Lines(died, ct));
            e = await Fails(p, Req());
            t.Check(e is { ErrorType: "cli_incomplete" } && e.Message.Contains("out of memory"), $"stderr in the message: {e?.Message}");
        });
    }
}
