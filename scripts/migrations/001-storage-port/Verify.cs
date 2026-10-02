using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NetPI;
using NetPI.Host.Storage.Sqlite;

namespace NetPI.Migration001;

/// <summary>
/// The verification of a finished dry run, and its report: row counts old vs new for every table and collection, the
/// SHA-256 of the messages, the workspace bindings, the ledger totals (overall and per day) and the fork-reset keys.
/// Any mismatch is a problem; problems fail the run (exit 1) and keep --apply refused.
/// </summary>
internal static class Verify
{
    public sealed record Check(string Name, string Old, string New, bool Ok, string Note = "");
    public sealed record Result(bool Passed, List<Check> Checks, List<string> Problems, List<string> Notes);

    public static Result Run(string oldDbPath, string newDbPath, string home, List<string> notes)
    {
        var checks = new List<Check>();
        var problems = new List<string>();
        using var old = new Database(oldDbPath, null);
        using var fresh = new Database(newDbPath, null);
        var options = new StorageOpenOptions
        {
            Home = Path.GetDirectoryName(newDbPath)!,
            Logger = NullLogger.Instance,
            Settings = MigrationSettings.For(home),
        };
        using var storage = new SqliteStorageProvider().Open(options);

        // ------------------------------------------------------------ core tables
        checks.Add(TableCheck("projects", Count(old, "projects"), Count(fresh, "projects")));
        checks.Add(TableCheck("sessions", Count(old, "sessions"), Count(fresh, "sessions")));
        checks.Add(TableCheck("messages", Count(old, "messages"), Count(fresh, "messages")));
        var kvOld = Count(old, "kv");
        var hadResetKeys = old.Scalar<string>("SELECT value FROM kv WHERE key = 'fork.resetKeys'") is not null;
        var kvExpected = kvOld + (hadResetKeys ? 0 : 1);
        checks.Add(new Check("kv", Num(kvOld), Num(kvExpected), Count(fresh, "kv") == kvExpected,
            hadResetKeys ? "existing fork.resetKeys merged" : "fork.resetKeys seeded (+1)"));

        // ------------------------------------------------------------ plugin collections
        checks.Add(CollectionCheck(storage, WorkspacesMigration.PluginId, "workspaces", WorkspacesMigration.Spec(), Count(old, "workspaces")));
        checks.Add(CollectionCheck(storage, ContextMigration.PluginId, "context_prompts", ContextMigration.PromptsSpec(), Count(old, "context_prompts")));
        checks.Add(CollectionCheck(storage, ContextMigration.PluginId, "context_tools", ContextMigration.ToolsSpec(), Count(old, "context_tools")));
        checks.Add(CollectionCheck(storage, ContextMigration.PluginId, "context_sent", ContextMigration.SentSpec(), Count(old, "context_sent")));
        var callsOld = Count(old, "usage_calls");
        checks.Add(CollectionCheck(storage, AgentsMigration.PluginId, "usage_calls", AgentsMigration.CallsSpec(), callsOld));
        checks.Add(CollectionCheck(storage, AgentsMigration.PluginId, "lanes_usage", AgentsMigration.LanesSpec(), Count(old, "lanes_usage")));
        checks.Add(new Check(
            $"{AgentsMigration.PluginId} lane_usage",
            Num(LaneGroups(old)), Num(storage.Plugins.For(AgentsMigration.PluginId).Collection("lane_usage", AgentsMigration.LaneSpec()).Count()),
            LaneGroups(old) == storage.Plugins.For(AgentsMigration.PluginId).Collection("lane_usage", AgentsMigration.LaneSpec()).Count(),
            "the (day, lane) roll-up of the old usage_calls"));
        var maxCallId = old.Scalar<long?>("SELECT MAX(id) FROM usage_calls");
        var counters = storage.Plugins.For(AgentsMigration.PluginId).Collection("counters", AgentsMigration.CountersSpec());
        var counterDoc = counters.Get("usage_calls");
        var counterValue = counterDoc is null ? (long?)null : L(counterDoc["value"]);
        var countersOk = maxCallId is null ? counterValue is null : counterValue == maxCallId;
        var counterText = counterValue is null ? "none" : Num(counterValue.Value);
        var maxIdText = maxCallId is null ? "none" : Num(maxCallId.Value);
        checks.Add(new Check($"{AgentsMigration.PluginId} counters", maxCallId is null ? "no calls" : Num(maxCallId.Value), counterValue is null ? "—" : Num(counterValue.Value),
            countersOk, $"usage_calls counter = {counterText} (old max id {maxIdText})"));
        checks.Add(CollectionCheck(storage, RuntimeMigration.PluginId, RuntimeMigration.Collection, RuntimeMigration.Spec(), Count(old, "agent_records")));

        // The Ideas tables: counted for the report, not migrated until the mapping arrives (the stub keeps --apply refused).
        var ideasOld = OldSchema.IdeasTables
            .Select(t => Count(old, t))
            .Where(c => c > 0)
            .Select(c => Num(c))
            .Aggregate(string.Empty, (a, b) => a + (a.Length > 0 ? " " : "") + b);
        checks.Add(new Check($"{IdeasMigration.PluginId} (ideas_*)", ideasOld.Length > 0 ? ideasOld : "none", "—", true,
            "PENDING: the collection mapping is not in this build (IdeasMigration stub)"));

        // ------------------------------------------------------------ the messages, bit for bit
        var oldHash = MessageHash(old);
        var newHash = MessageHash(fresh);
        var hashOk = oldHash == newHash;
        checks.Add(new Check("messages sha256 (id, session, seq, role, parts, meta)", Short(oldHash), Short(newHash), hashOk, "in id order"));
        if (!hashOk) problems.Add($"messages hash differs: old {oldHash} new {newHash}");

        // ------------------------------------------------------------ the session bindings
        var paths = WorkspacePaths(old);
        var bound = BoundCount(old);
        var valid = new List<(string Id, string Workspace, string Path)>();
        foreach (var (id, workspace) in BoundSessions(old))
            if (paths.TryGetValue(workspace, out var path)) valid.Add((id, workspace, path));
        var newBound = new Dictionary<string, (string? Workspace, string? Cwd)>();
        foreach (var (id, metaText) in fresh.Query("SELECT id, meta FROM sessions", null, r => (r.GetString("id"), r.GetStringOrNull("meta"))))
        {
            var meta = Jsonx.Object(metaText);
            if (meta?[SessionWorkspace.MetaKey] is JsonValue w && w.TryGetValue<string>(out var ws))
                newBound[id] = (ws, meta?[SessionCwd.MetaKey] is JsonValue c && c.TryGetValue<string>(out var cwd) ? cwd : null);
        }
        var bindingProblems = 0;
        foreach (var (id, workspace, path) in valid)
            if (!newBound.TryGetValue(id, out var b) || b.Workspace != workspace || b.Cwd != path)
            {
                bindingProblems++;
                problems.Add($"session {id}: expected meta.workspaceId '{workspace}' with cwd '{path}', got '{b.Workspace ?? "none"}'/'{b.Cwd ?? "none"}'");
            }
        var extraNew = newBound.Count(x => !valid.Any(v => v.Id == x.Key));
        bindingProblems += extraNew;
        if (extraNew > 0) problems.Add($"{extraNew} new session(s) carry a meta.workspaceId that was not a valid old binding");
        checks.Add(new Check("sessions with a workspace", $"{valid.Count} ({bound - valid.Count} dropped)", Num(newBound.Count),
            bindingProblems == 0 && newBound.Count == valid.Count, "meta.workspaceId + meta.cwd"));
        if (bindingProblems > 0 && newBound.Count != valid.Count) problems.Add($"session bindings: old {valid.Count} valid vs new {newBound.Count}");

        // ------------------------------------------------------------ the ledger's money
        var callsCol = storage.Plugins.For(AgentsMigration.PluginId).Collection("usage_calls", AgentsMigration.CallsSpec());
        var oldByDay = CostByDay(old);
        var newByDay = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var doc in callsCol.Find(null).OrderBy(d => long.Parse(d.Key)))
        {
            var day = doc.Doc["day"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
            newByDay[day] = newByDay.GetValueOrDefault(day) + D(doc.Doc["costUsd"]);
        }
        var totalOld = oldByDay.Values.Sum();
        var totalNew = newByDay.Values.Sum();
        var costOk = Math.Abs(totalOld - totalNew) < 1e-9;
        checks.Add(new Check("usage_calls cost (total)", Usd(totalOld), Usd(totalNew), costOk));
        if (!costOk) problems.Add($"usage_calls cost: old {totalOld} new {totalNew}");
        foreach (var day in oldByDay.Keys.Union(newByDay.Keys, StringComparer.Ordinal).OrderBy(d => d))
        {
            var o = oldByDay.GetValueOrDefault(day);
            var n = newByDay.GetValueOrDefault(day);
            var ok = Math.Abs(o - n) < 1e-9;
            checks.Add(new Check($"usage_calls cost {day}", Usd(o), Usd(n), ok));
            if (!ok) problems.Add($"usage_calls cost {day}: old {o} new {n}");
        }

        // ------------------------------------------------------------ fork-reset keys
        var resetOld = hadResetKeys ? ResetKeys(old) : new List<string>();
        var resetNew = ResetKeys(fresh);
        var resetOk = CoreCopy.ForkResetKeys.All(k => resetNew.Contains(k, StringComparer.Ordinal));
        checks.Add(new Check("kv fork.resetKeys", $"{resetOld.Count} remembered", Num(resetNew.Count), resetOk, "seeded with today's list, merged"));
        if (!resetOk) problems.Add("kv fork.resetKeys is missing one of today's keys");

        foreach (var check in checks.Where(c => !c.Ok && !problems.Contains(c.Name)))
            problems.Add(check.Name);
        return new Result(problems.Count == 0, checks, problems, notes);
    }

    // ------------------------------------------------------------------ the pieces

    private static Check TableCheck(string name, long oldCount, long newCount) =>
        new(name, Num(oldCount), Num(newCount), oldCount == newCount);

    private static Check CollectionCheck(IStorage storage, string plugin, string name, CollectionSpec spec, long oldCount)
    {
        var newCount = storage.Plugins.For(plugin).Collection(name, spec).Count();
        return new Check($"{plugin} {name}", Num(oldCount), Num(newCount), oldCount == newCount);
    }

    /// <summary>The SHA-256 of (id, session_id, seq, role, parts, meta) of every message, in id order.</summary>
    private static string MessageHash(Database db)
    {
        using var sha = SHA256.Create();
        db.Query("SELECT id, session_id, seq, role, parts, meta FROM messages ORDER BY id", null, r =>
        {
            Write(sha, r.GetInt64("id").ToString(System.Globalization.CultureInfo.InvariantCulture));
            Write(sha, r.GetString("session_id"));
            Write(sha, r.GetInt64("seq").ToString(System.Globalization.CultureInfo.InvariantCulture));
            Write(sha, r.GetString("role"));
            Write(sha, r.GetString("parts"));
            Write(sha, r.GetStringOrNull("meta"));
            return true;
        });
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    private static void Write(SHA256 sha, string? value)
    {
        var prefix = value is null ? (byte)1 : (byte)0;
        sha.TransformBlock(new byte[] { prefix }, 0, 1, null, 0);
        if (value is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
            sha.TransformBlock(new byte[] { 2 }, 0, 1, null, 0);
        }
    }

    private static Dictionary<string, string> WorkspacePaths(Database old)
    {
        try
        {
            return old.Query("SELECT id, path FROM workspaces", null, r => (r.GetString("id"), r.GetString("path")))
                .ToDictionary(t => t.Item1, t => t.Item2, StringComparer.Ordinal);
        }
        catch (SqliteException) { return []; }
    }

    private static List<(string, string)> BoundSessions(Database old)
    {
        try
        {
            return old.Query("SELECT id, workspace_id FROM sessions WHERE workspace_id IS NOT NULL", null,
                r => (r.GetString("id"), r.GetString("workspace_id"))).ToList();
        }
        catch (SqliteException) { return []; }
    }

    private static long LaneGroups(Database old)
    {
        try
        {
            return old.Scalar<long?>("SELECT COUNT(DISTINCT day || '|' || lower(lane)) FROM usage_calls WHERE lane IS NOT NULL") ?? 0;
        }
        catch (SqliteException) { return 0; }
    }

    private static Dictionary<string, double> CostByDay(Database old)
    {
        try
        {
            return old.Query("SELECT day, SUM(cost_usd) AS total FROM usage_calls GROUP BY day", null,
                r => (r.GetString("day"), r.GetDouble("total"))).ToDictionary(t => t.Item1, t => t.Item2, StringComparer.Ordinal);
        }
        catch (SqliteException) { return []; }
    }

    private static List<string> ResetKeys(Database db)
    {
        var value = db.Scalar<string>("SELECT value FROM kv WHERE key = 'fork.resetKeys'");
        if (value is null) return [];
        return (JsonNode.Parse(value) as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList() ?? [];
    }

    private static long BoundCount(Database old)
    {
        try
        {
            return old.Scalar<long?>("SELECT COUNT(*) FROM sessions WHERE workspace_id IS NOT NULL") ?? 0;
        }
        catch (SqliteException) { return 0; }   // an old database from before workspaces: no workspace_id column
    }

    private static long Count(Database db, string table)
    {
        try { return db.Scalar<long>($"SELECT COUNT(*) FROM {table}"); }
        catch (SqliteException) { return 0; }
    }

    private static long L(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var x) ? x : 0;
    private static double D(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
    private static string Num(long n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static string Short(string hash) => hash.Length <= 20 ? hash : hash[..16] + "…";
    private static string Usd(double v) => v.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);
}
