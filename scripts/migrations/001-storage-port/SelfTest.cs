using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NetPI;
using NetPI.Host.Storage.Sqlite;

namespace NetPI.Migration001;

/// <summary>
/// The only thing this tool may run on: an old-format database it builds itself, in a temp directory, with a handful of
/// rows in every old table — a session bound to a workspace, a broken binding, a compacted summary, a ledger of a few
/// days of calls. It runs the dry run on that home and checks the verification passes and the spot checks hold. It never
/// points at a real home.
/// </summary>
internal static class SelfTest
{
    public static int Run()
    {
        var home = Path.Combine(Path.GetTempPath(), "netpi-migration001-selftest-" + Guid.NewGuid().ToString("N"));
        Console.WriteLine($"selftest home: {home}");
        try
        {
            Directory.CreateDirectory(home);
            BuildOldHome(home);
            var oldBytes = File.ReadAllBytes(Path.Combine(home, "netpi.db"));
            var code = Program.Run(home, apply: false);
            if (code != 0)
            {
                Console.Error.WriteLine($"selftest FAILED: the dry run exited {code}");
                return 1;
            }
            Expect(File.ReadAllBytes(Path.Combine(home, "netpi.db")).SequenceEqual(oldBytes), true, "the old netpi.db bit-identical after the run");
            SpotChecks(home);
            Console.WriteLine("selftest passed: the old-format home migrated and verified.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("selftest FAILED: " + ex.Message);
            return 1;
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { /* a temp dir left behind is the OS's to reap */ }
        }
    }

    /// <summary>The old-format database of a home: every old table with rows in it.</summary>
    private static void BuildOldHome(string home)
    {
        var db = new Database(Path.Combine(home, "netpi.db"), null);
        try
        {
            foreach (var statement in OldSchema.Statements())
                db.Execute(statement);
            foreach (var (scope, version) in OldSchema.Migrations)
                db.Execute("INSERT INTO _migrations(scope, version) VALUES(@scope, @version)", new { scope, version });

            // ---- projects, workspaces, sessions
            Ins(db, "INSERT INTO projects(id, name, path, created_at, updated_at, last_used_at, meta) VALUES(@id, @name, @path, @created, @updated, @lastUsed, @meta)",
                new { id = "prj_a", name = "A", path = @"C:\w\a", created = T, updated = T, lastUsed = T + 1000, meta = "{\"profile\":\"x\"}" });
            Ins(db, "INSERT INTO projects(id, name, path, created_at, updated_at, last_used_at, meta) VALUES(@id, @name, @path, @created, @updated, @lastUsed, @meta)",
                new { id = "prj_b", name = "B", path = @"C:\w\b", created = T, updated = T, lastUsed = (object?)null, meta = (string?)null });

            Ins(db, "INSERT INTO workspaces(id, name, path, project_id, kind, branch, base_commit, repo_common_dir, owner_session_id, owner_agent_id, managed, created_at, updated_at, meta) VALUES(@id, @name, @path, @projectId, @kind, @branch, @baseCommit, @repo, @ownerSession, @ownerAgent, @managed, @created, @updated, @meta)",
                new { id = "wsp_a", name = "worker A", path = @"C:\w\wt-a", projectId = "prj_a", kind = "worktree", branch = "feat", baseCommit = "abc123", repo = @"C:\w\a\.git", ownerSession = "ses_1", ownerAgent = "ag_1", managed = 1, created = T, updated = T + 5000, meta = (string?)null });
            Ins(db, "INSERT INTO workspaces(id, name, path, project_id, kind, branch, base_commit, repo_common_dir, owner_session_id, owner_agent_id, managed, created_at, updated_at, meta) VALUES(@id, @name, @path, @projectId, @kind, @branch, @baseCommit, @repo, @ownerSession, @ownerAgent, @managed, @created, @updated, @meta)",
                new { id = "wsp_b", name = "folder B", path = @"C:\w\wt-b", projectId = (object?)null, kind = "folder", branch = (object?)null, baseCommit = (object?)null, repo = (object?)null, ownerSession = (object?)null, ownerAgent = (object?)null, managed = 0, created = T, updated = T, meta = (string?)null });

            Ins(db, SessionSql, new { id = "ses_1", title = "bound chat", projectId = "prj_a", parent = (object?)null, kind = "chat", model = (object?)null, reasoning = (object?)null, created = T, updated = T + 90000, archived = 0, pinned = 1, count = 3, tokens = 100, meta = "{\"profile\":\"p1\",\"toolsOff\":[\"x\"]}", workspace = "wsp_a" });
            Ins(db, SessionSql, new { id = "ses_2", title = "bound null meta", projectId = "prj_a", parent = (object?)null, kind = "chat", model = (object?)null, reasoning = (object?)null, created = T, updated = T + 80000, archived = 0, pinned = 0, count = 2, tokens = 50, meta = (object?)null, workspace = "wsp_b" });
            Ins(db, SessionSql, new { id = "ses_3", title = "broken binding", projectId = "prj_b", parent = (object?)null, kind = "chat", model = (object?)null, reasoning = (object?)null, created = T, updated = T + 70000, archived = 0, pinned = 0, count = 1, tokens = 10, meta = "{\"goal\":\"g\"}", workspace = "wsp_gone" });
            Ins(db, SessionSql, new { id = "ses_4", title = "subagent", projectId = "prj_a", parent = "ses_1", kind = "subagent", model = "anthropic/m", reasoning = (object?)null, created = T, updated = T + 60000, archived = 1, pinned = 0, count = 1, tokens = 5, meta = (object?)null, workspace = (object?)null });

            // ---- messages (a compacted summary among them)
            Ins(db, MessageSql, new { id = 1L, session = "ses_1", seq = 1L, role = "user", parts = Parts("hi"), created = T + 1000, provider = (object?)null, model = (object?)null, stop = (object?)null, usage = (object?)null, duration = (object?)null, compacted = 0, meta = (object?)null });
            Ins(db, MessageSql, new { id = 2L, session = "ses_1", seq = 2L, role = "assistant", parts = Parts("hello"), created = T + 2000, provider = "openrouter", model = "m1", stop = "stop", usage = "{\"inputTokens\":10,\"outputTokens\":5}", duration = 400L, compacted = 0, meta = (object?)null });
            Ins(db, MessageSql, new { id = 3L, session = "ses_1", seq = 3L, role = "tool", parts = Parts("tool result"), created = T + 3000, provider = (object?)null, model = (object?)null, stop = (object?)null, usage = (object?)null, duration = (object?)null, compacted = 0, meta = "{\"for\":1}" });
            Ins(db, MessageSql, new { id = 4L, session = "ses_2", seq = 1L, role = "user", parts = Parts("summarize"), created = T + 4000, provider = (object?)null, model = (object?)null, stop = (object?)null, usage = (object?)null, duration = (object?)null, compacted = 1, meta = (object?)null });
            Ins(db, MessageSql, new { id = 5L, session = "ses_2", seq = 2L, role = "summary", parts = Parts("the summary"), created = T + 5000, provider = (object?)null, model = (object?)null, stop = (object?)null, usage = (object?)null, duration = (object?)null, compacted = 0, meta = "{\"coversUpToSeq\":1}" });
            Ins(db, MessageSql, new { id = 6L, session = "ses_3", seq = 1L, role = "user", parts = Parts("orphan workspace"), created = T + 6000, provider = (object?)null, model = (object?)null, stop = (object?)null, usage = (object?)null, duration = (object?)null, compacted = 0, meta = (object?)null });
            Ins(db, MessageSql, new { id = 7L, session = "ses_4", seq = 1L, role = "user", parts = Parts("task"), created = T + 7000, provider = (object?)null, model = (object?)null, stop = (object?)null, usage = (object?)null, duration = (object?)null, compacted = 0, meta = (object?)null });

            // ---- kv (with a pre-existing fork.resetKeys to merge)
            Ins(db, "INSERT INTO kv(key, value) VALUES(@key, @value)", new { key = "ui.state", value = "{}" });
            Ins(db, "INSERT INTO kv(key, value) VALUES(@key, @value)", new { key = "fork.resetKeys", value = "[\"goal\"]" });

            // ---- context
            Ins(db, "INSERT INTO context_prompts(session_id, prompt, created_at, prompt_revision) VALUES(@sid, @prompt, @created, @revision)",
                new { sid = "ses_1", prompt = "system prompt one", created = "2026-10-01T08:00:00.0000000Z", revision = 0L });
            Ins(db, "INSERT INTO context_prompts(session_id, prompt, created_at, prompt_revision) VALUES(@sid, @prompt, @created, @revision)",
                new { sid = "ses_2", prompt = "system prompt two", created = "2026-10-02T08:00:00.0000000Z", revision = 3L });
            Ins(db, "INSERT INTO context_tools(session_id, tools, since_seq) VALUES(@sid, @tools, @since)",
                new { sid = "ses_1", tools = "[\"read\",\"write\"]", since = 0L });
            Ins(db, "INSERT INTO context_tools(session_id, tools, since_seq) VALUES(@sid, @tools, @since)",
                new { sid = "ses_2", tools = "[\"read\"]", since = 2L });
            Ins(db, "INSERT INTO context_sent(session_id, version, after_seq, prompt, tools, created_at) VALUES(@sid, @version, @after, @prompt, @tools, @created)",
                new { sid = "ses_1", version = 1L, after = 0L, prompt = "system prompt one", tools = "[{\"name\":\"read\"},{\"name\":\"write\"}]", created = "2026-10-01T08:00:00.0000000Z" });
            Ins(db, "INSERT INTO context_sent(session_id, version, after_seq, prompt, tools, created_at) VALUES(@sid, @version, @after, @prompt, @tools, @created)",
                new { sid = "ses_1", version = 2L, after = 2L, prompt = "system prompt one v2", tools = "[{\"name\":\"read\"}]", created = "2026-10-01T09:00:00.0000000Z" });
            Ins(db, "INSERT INTO context_sent(session_id, version, after_seq, prompt, tools, created_at) VALUES(@sid, @version, @after, @prompt, @tools, @created)",
                new { sid = "ses_2", version = 1L, after = 0L, prompt = "system prompt two", tools = "[{\"name\":\"read\"}]", created = "2026-10-02T08:00:00.0000000Z" });

            // ---- the ledger: a few days of calls (two lanes that differ only in case, a laneless call, a sessionless one)
            Ins(db, "INSERT INTO lanes_usage(day, provider, model, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, calls) VALUES(@day, @provider, @model, @input, @output, @cr, @cw, @calls)",
                new { day = "2026-10-01", provider = "openrouter", model = "m1", input = 10L, output = 5L, cr = 1L, cw = 0L, calls = 1L });
            Ins(db, "INSERT INTO lanes_usage(day, provider, model, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, calls) VALUES(@day, @provider, @model, @input, @output, @cr, @cw, @calls)",
                new { day = "2026-10-02", provider = "openrouter", model = "m1", input = 7L, output = 3L, cr = 0L, cw = 2L, calls = 1L });
            Ins(db, "INSERT INTO lanes_usage(day, provider, model, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, calls) VALUES(@day, @provider, @model, @input, @output, @cr, @cw, @calls)",
                new { day = "2026-10-02", provider = "anthropic", model = "m2", input = 2L, output = 1L, cr = 0L, cw = 0L, calls = 1L });

            Call(db, 1001L, T + 11_000, "2026-10-01", "ses_1", "ses_1", "ag_1", "Alpha", "openrouter", "m1", "agent", 10L, 5L, 1L, 0L, 0.011, "reported");
            Call(db, 1002L, T + 12_000, "2026-10-01", "ses_1", "ses_1", "ag_1", "ALPHA", "openrouter", "m1", "agent", 7L, 3L, 0L, 2L, 0.022, "estimated");
            Call(db, 1003L, T + 13_000, "2026-10-02", "ses_2", "ses_2", null, null, "anthropic", "m2", "compaction", 2L, 1L, 0L, 0L, 0.003, "free");
            Call(db, 1004L, T + 14_000, "2026-10-02", "ses_4", "ses_1", "ag_1", "Alpha", "openrouter", "m1", "agent", 4L, 2L, 1L, 1L, 0.013, "interrupted-estimate");
            Call(db, 1005L, T + 15_000, "2026-10-01", null, null, null, null, "openrouter", "m1", "decide", 3L, 1L, 0L, 0L, 0.0, "unknown");
            Call(db, 1006L, T + 16_000, "2026-10-01", "ses_1", "ses_1", null, null, "openrouter", "m1", "agent", 1L, 1L, 0L, 0L, 0.0005, "rejected");

            // ---- the runtime's agent records
            Ins(db, "INSERT INTO agent_records(id, session_id, parent_agent_id, name, status, task, result, error, model, created_at, finished_at, stats) VALUES(@id, @session, @parent, @name, @status, @task, @result, @error, @model, @created, @finished, @stats)",
                new { id = "ag_1", session = "ses_4", parent = (object?)null, name = "Worker", status = "failed", task = "do the thing", result = (object?)null, error = "interrupted", model = "anthropic/m", created = "2026-10-02T08:30:00.0000000Z", finished = "2026-10-02T08:31:00.0000000Z", stats = "{\"info\":{\"id\":\"ag_1\"},\"instructions\":\"go\",\"notifyParent\":true}" });
            Ins(db, "INSERT INTO agent_records(id, session_id, parent_agent_id, name, status, task, result, error, model, created_at, finished_at, stats) VALUES(@id, @session, @parent, @name, @status, @task, @result, @error, @model, @created, @finished, @stats)",
                new { id = "ag_2", session = "ses_4", parent = "ag_1", name = "Helper", status = "running", task = "help", result = (object?)null, error = (object?)null, model = (object?)null, created = "2026-10-02T09:00:00.0000000Z", finished = (object?)null, stats = (object?)null });

            // ---- the Ideas tables (the stub does not migrate them; they only count in the report)
            Ins(db, "INSERT INTO ideas_items(id, ord, revision, title, status, priority, project_id, project_name, created_at, updated_at, doc) VALUES(@id, @ord, @rev, @title, @status, @prio, @projectId, @projectName, @created, @updated, @doc)",
                new { id = "idea_1", ord = 1L, rev = 1L, title = "first idea", status = "open", prio = "medium", projectId = "prj_a", projectName = "A", created = "2026-10-01T00:00:00Z", updated = "2026-10-01T00:00:00Z", doc = "{\"id\":\"idea_1\",\"title\":\"first idea\"}" });
            Ins(db, "INSERT INTO ideas_suggestions(id, ord, kind, session_id, idea_id, project_id, source_rev, title, at, doc) VALUES(@id, @ord, @kind, @session, @idea, @projectId, @sourceRev, @title, @at, @doc)",
                new { id = "sg_1", ord = 1L, kind = "save", session = "ses_1", idea = "idea_1", projectId = "prj_a", sourceRev = 1L, title = "save this", at = "2026-10-01T00:00:00Z", doc = "{}" });
            Ins(db, "INSERT INTO ideas_resolutions(suggestion_id, action, idea_id, at) VALUES(@sg, @action, @idea, @at)",
                new { sg = "sg_1", action = "saved", idea = "idea_1", at = "2026-10-01T00:00:01Z" });
            Ins(db, "INSERT INTO ideas_checks(session_id, rev, n, state, tries, at, claim, claim_until, error) VALUES(@session, @rev, @n, @state, @tries, @at, @claim, @claimUntil, @error)",
                new { session = "ses_1", rev = "r1", n = 2L, state = "done", tries = 0L, at = "2026-10-01T00:00:02Z", claim = (object?)null, claimUntil = (object?)null, error = (object?)null });
            Ins(db, "INSERT INTO ideas_repos(repo, project_id, project_name, hash, at, tries, error) VALUES(@repo, @projectId, @projectName, @hash, @at, @tries, @error)",
                new { repo = @"C:\w\a", projectId = "prj_a", projectName = "A", hash = "deadbeef", at = "2026-10-01T00:00:03Z", tries = 0L, error = (object?)null });
            Ins(db, "INSERT INTO ideas_imports(id, kind, source, checksum, schema_version, counts, at) VALUES(@id, @kind, @source, @checksum, @version, @counts, @at)",
                new { id = "imp_1", kind = "legacy", source = "file", checksum = "x", version = 1L, counts = "{\"ideas\":1}", at = "2026-10-01T00:00:04Z" });
            Ins(db, "INSERT INTO ideas_metadata(key, value) VALUES(@key, @value)", new { key = "cursor", value = "abc" });
            Ins(db, "INSERT INTO ideas_unread(repo, hash, subject, tries, error, at) VALUES(@repo, @hash, @subject, @tries, @error, @at)",
                new { repo = @"C:\w\a", hash = "f00d", subject = "a commit", tries = 0L, error = (object?)null, at = "2026-10-01T00:00:05Z" });
        }
        finally { db.Dispose(); }
    }

    // ------------------------------------------------------------------ the spot checks

    private static void SpotChecks(string home)
    {
        var newDb = Path.Combine(home, ".migrating", "netpi.db");
        using var db = new Database(newDb, null);
        using var storage = new SqliteStorageProvider().Open(new StorageOpenOptions
        {
            Home = Path.Combine(home, ".migrating"),
            Logger = NullLogger.Instance,
            Settings = MigrationSettings.For(home),
        });
        Expect(Workspace(db, "ses_1"), "wsp_a");
        Expect(Cwd(db, "ses_1"), @"C:\w\wt-a");
        Expect(MetaString(db, "ses_1", "profile"), "p1");   // the rest of the meta survives
        Expect(Workspace(db, "ses_2"), "wsp_b");            // a null meta became an object
        Expect(Cwd(db, "ses_2"), @"C:\w\wt-b");
        Expect(Workspace(db, "ses_3"), null);               // the broken binding was dropped
        Expect(MetaString(db, "ses_3", "goal"), "g");
        Expect(Workspace(db, "ses_4"), null);

        var resetText = db.Scalar<string>("SELECT value FROM kv WHERE key = 'fork.resetKeys'");
        JsonNode? resetNode = null;
        if (resetText is not null)
        {
            try { resetNode = JsonNode.Parse(resetText); } catch (System.Text.Json.JsonException) { }
        }
        var reset = resetNode as JsonArray;
        foreach (var key in CoreCopy.ForkResetKeys)
            Expect(reset is not null && reset.Any(n => n is JsonValue v && v.TryGetValue<string>(out var s) && s == key), true, $"kv fork.resetKeys misses '{key}'");

        var agents = storage.Plugins.For(AgentsMigration.PluginId);
        var calls = agents.Collection("usage_calls", AgentsMigration.CallsSpec());
        Expect(calls.Count(null), 6L, "usage_calls documents");
        Expect(agents.Collection("counters", AgentsMigration.CountersSpec()).Get("usage_calls")?["value"] is JsonValue counter && counter.TryGetValue<long>(out var max) && max == 1006,
            true, "the usage_calls counter");
        var lanes = agents.Collection("lane_usage", AgentsMigration.LaneSpec());
        Expect(lanes.Count(null), 2L, "lane_usage documents");
        Expect(lanes.Get("2026-10-01|alpha")?["costUsd"] is JsonValue c1 && c1.TryGetValue<double>(out var d1) && Math.Abs(d1 - 0.033) < 1e-9,
            true, "lane 2026-10-01 (the case split merged; the lane-less calls stay out of the document)");
        Expect(lanes.Get("2026-10-02|alpha")?["costUsd"] is JsonValue c2 && c2.TryGetValue<double>(out var d2) && Math.Abs(d2 - 0.013) < 1e-9,
            true, "lane 2026-10-02");

        var workspaces = storage.Plugins.For(WorkspacesMigration.PluginId).Collection(WorkspacesMigration.Collection, WorkspacesMigration.Spec());
        Expect(workspaces.Count(null), 2L, "workspaces documents");
        Expect(workspaces.Get("wsp_a")?["projectId"] is JsonValue pv && pv.TryGetValue<string>(out var proj) ? proj : null, "prj_a");
        Expect(workspaces.Get("wsp_a")?["updatedMs"] is JsonValue u && u.TryGetValue<long>(out var ms) && ms == T + 5000, true, "updatedMs");

        Expect(storage.Plugins.For(ContextMigration.PluginId).Collection("context_sent", ContextMigration.SentSpec()).Count(null), 3L, "context_sent documents");
        Expect(storage.Plugins.For(RuntimeMigration.PluginId).Collection(RuntimeMigration.Collection, RuntimeMigration.Spec()).Count(null), 2L, "agent_records documents");
    }

    private static string? Workspace(Database db, string sessionId)
    {
        var meta = SessionMeta(db, sessionId);
        return meta?["workspaceId"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    private static string? Cwd(Database db, string sessionId)
    {
        var meta = SessionMeta(db, sessionId);
        return meta?["cwd"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    private static string? MetaString(Database db, string sessionId, string key)
    {
        var meta = SessionMeta(db, sessionId);
        return meta?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    private static JsonObject? SessionMeta(Database db, string sessionId)
    {
        var meta = db.Scalar<string>("SELECT meta FROM sessions WHERE id = @id", new { id = sessionId });
        return Jsonx.Object(meta);
    }

    private static void Expect<T>(T actual, T expected, string what = "")
    {
        var ok = actual is null ? expected is null
            : (actual, expected) switch
            {
                (string a, string e) => a == e,
                (bool a, bool e) => a == e,
                (long a, long e) => a == e,
                _ => Equals(actual, expected),
            };
        if (!ok)
        {
            var where = what.Length > 0 ? $" ({what})" : "";
            throw new Exception($"spot check failed{where}: expected {Render(expected)}, got {Render(actual)}");
        }
    }

    private static string Render(object? o) => o switch { null => "null", string s => $"\"{s}\"", _ => o.ToString() ?? "null" };

    // ------------------------------------------------------------------ the seed

    private const long T = 1_760_000_000_000;   // a fixed "now" for the seed data

    private const string SessionSql =
        "INSERT INTO sessions(id, title, project_id, parent_session_id, kind, model, reasoning, created_at, updated_at, archived, message_count, context_tokens, meta, workspace_id, pinned) " +
        "VALUES(@id, @title, @projectId, @parent, @kind, @model, @reasoning, @created, @updated, @archived, @count, @tokens, @meta, @workspace, @pinned)";
    private const string MessageSql =
        "INSERT INTO messages(id, session_id, seq, role, parts, created_at, provider, model, stop_reason, usage, duration_ms, compacted, meta) " +
        "VALUES(@id, @session, @seq, @role, @parts, @created, @provider, @model, @stop, @usage, @duration, @compacted, @meta)";

    private static void Ins(Database db, string sql, object args) => db.Execute(sql, args);

    private static void Call(Database db, long id, long ts, string day, string? session, string? root, string? agent, string? lane,
        string provider, string model, string purpose, long input, long output, long cr, long cw, double cost, string source) =>
        db.Execute("""
            INSERT INTO usage_calls(id, ts, day, session_id, root_session_id, agent_id, lane, provider, model, purpose, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, cost_usd, cost_source)
            VALUES(@id, @ts, @day, @session, @root, @agent, @lane, @provider, @model, @purpose, @input, @output, @cr, @cw, @cost, @source)
            """, new { id, ts, day, session, root, agent, lane, provider, model, purpose, input, output, cr, cw, cost, source });

    private static string Parts(string text) =>
        "[{\"type\":\"text\",\"text\":" + System.Text.Json.JsonSerializer.Serialize(text) + "}]";
}
