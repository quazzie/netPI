namespace NetPI.Migration001;

/// <summary>
/// The old (pre-storage-port) database shape, as the last build before the swap wrote it: the <c>Migrations</c> arrays of
/// <c>SessionStore</c> (scope "core", 5 steps), <c>IdeasRepository</c> (scope "netpi.ideas", 2), <c>Ledger</c> (scope "lanes", 3),
/// <c>PromptStore</c> (scope "context", 5) and <c>AgentStore</c> (scope "agent", 3), taken from the code at
/// <c>pre-swapover-2026-10-02</c> and folded into their final form. The self-test builds a database in exactly this shape;
/// the copy steps read it by these same table names.
/// </summary>
internal static class OldSchema
{
    public const string Ddl = """
        CREATE TABLE projects (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            path TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL,
            last_used_at INTEGER,
            meta TEXT
        );
        CREATE TABLE sessions (
            id TEXT PRIMARY KEY,
            title TEXT NOT NULL DEFAULT '',
            project_id TEXT REFERENCES projects(id) ON DELETE SET NULL,
            parent_session_id TEXT,
            kind TEXT NOT NULL DEFAULT 'chat',
            model TEXT,
            reasoning TEXT,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL,
            archived INTEGER NOT NULL DEFAULT 0,
            message_count INTEGER NOT NULL DEFAULT 0,
            context_tokens INTEGER NOT NULL DEFAULT 0,
            meta TEXT,
            workspace_id TEXT,
            pinned INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX ix_sessions_updated ON sessions(updated_at DESC);
        CREATE INDEX ix_sessions_project ON sessions(project_id, updated_at DESC);
        CREATE INDEX ix_sessions_parent ON sessions(parent_session_id);
        CREATE TABLE messages (
            id INTEGER PRIMARY KEY,
            session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
            seq INTEGER NOT NULL,
            role TEXT NOT NULL,
            parts TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            provider TEXT,
            model TEXT,
            stop_reason TEXT,
            usage TEXT,
            duration_ms INTEGER,
            compacted INTEGER NOT NULL DEFAULT 0,
            meta TEXT
        );
        CREATE UNIQUE INDEX ix_messages_session_seq ON messages(session_id, seq);
        CREATE TABLE kv (
            key TEXT PRIMARY KEY,
            value TEXT
        );
        CREATE TABLE workspaces (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL DEFAULT '',
            path TEXT NOT NULL,
            project_id TEXT REFERENCES projects(id) ON DELETE SET NULL,
            kind TEXT NOT NULL DEFAULT 'folder',
            branch TEXT,
            base_commit TEXT,
            repo_common_dir TEXT,
            owner_session_id TEXT,
            owner_agent_id TEXT,
            managed INTEGER NOT NULL DEFAULT 0,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL,
            meta TEXT
        );
        CREATE INDEX ix_workspaces_project ON workspaces(project_id, updated_at DESC);
        CREATE INDEX ix_workspaces_owner ON workspaces(owner_session_id);
        CREATE TABLE ideas_items (
            id            TEXT PRIMARY KEY,
            ord           INTEGER NOT NULL,
            revision      INTEGER NOT NULL DEFAULT 1,
            title         TEXT NOT NULL DEFAULT '',
            status        TEXT NOT NULL DEFAULT 'open',
            priority      TEXT NOT NULL DEFAULT 'medium',
            project_id    TEXT,
            project_name  TEXT,
            created_at    TEXT,
            updated_at    TEXT,
            doc           TEXT NOT NULL
        );
        CREATE INDEX ideas_items_order ON ideas_items(ord, id);
        CREATE INDEX ideas_items_project_status_order ON ideas_items(project_id, status, ord);
        CREATE INDEX ideas_items_status_order ON ideas_items(status, ord);
        CREATE TABLE ideas_suggestions (
            id         TEXT PRIMARY KEY,
            ord        INTEGER NOT NULL,
            kind       TEXT NOT NULL DEFAULT 'save',
            session_id TEXT,
            idea_id    TEXT,
            project_id TEXT,
            source_rev INTEGER,
            title      TEXT NOT NULL DEFAULT '',
            at         TEXT NOT NULL DEFAULT '',
            doc        TEXT NOT NULL
        );
        CREATE INDEX ideas_suggestions_order ON ideas_suggestions(ord, id);
        CREATE INDEX ideas_suggestions_idea ON ideas_suggestions(idea_id);
        CREATE INDEX ideas_suggestions_session ON ideas_suggestions(session_id, kind, title);
        CREATE TABLE ideas_resolutions (
            suggestion_id TEXT PRIMARY KEY,
            action        TEXT NOT NULL,
            idea_id       TEXT,
            at            TEXT NOT NULL DEFAULT ''
        );
        CREATE TABLE ideas_checks (
            session_id   TEXT PRIMARY KEY,
            rev          TEXT NOT NULL DEFAULT '',
            n            INTEGER NOT NULL DEFAULT 0,
            state        TEXT NOT NULL DEFAULT 'done',
            tries        INTEGER NOT NULL DEFAULT 0,
            at           TEXT NOT NULL DEFAULT '',
            claim        TEXT,
            claim_until  INTEGER,
            error        TEXT
        );
        CREATE INDEX ideas_checks_state ON ideas_checks(state);
        CREATE TABLE ideas_repos (
            repo         TEXT PRIMARY KEY,
            project_id   TEXT,
            project_name TEXT,
            hash         TEXT,
            at           TEXT NOT NULL DEFAULT '',
            tries        INTEGER NOT NULL DEFAULT 0,
            error        TEXT
        );
        CREATE TABLE ideas_imports (
            id             TEXT PRIMARY KEY,
            kind           TEXT NOT NULL DEFAULT 'legacy',
            source         TEXT NOT NULL DEFAULT '',
            checksum       TEXT NOT NULL DEFAULT '',
            schema_version INTEGER NOT NULL DEFAULT 1,
            counts         TEXT NOT NULL DEFAULT '{}',
            at             TEXT NOT NULL DEFAULT ''
        );
        CREATE TABLE ideas_metadata (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        CREATE TABLE ideas_unread (
            repo     TEXT NOT NULL,
            hash     TEXT NOT NULL,
            subject  TEXT NOT NULL DEFAULT '',
            tries    INTEGER NOT NULL DEFAULT 0,
            error    TEXT,
            at       TEXT NOT NULL DEFAULT '',
            PRIMARY KEY (repo, hash)
        );
        CREATE INDEX ideas_unread_at ON ideas_unread(at);
        CREATE TABLE lanes_usage (
            day TEXT NOT NULL,
            provider TEXT NOT NULL,
            model TEXT NOT NULL,
            input_tokens INTEGER NOT NULL DEFAULT 0,
            output_tokens INTEGER NOT NULL DEFAULT 0,
            cache_read_tokens INTEGER NOT NULL DEFAULT 0,
            cache_write_tokens INTEGER NOT NULL DEFAULT 0,
            calls INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (day, provider, model)
        );
        CREATE TABLE usage_calls (
            id INTEGER PRIMARY KEY,
            ts INTEGER NOT NULL,
            day TEXT NOT NULL,
            session_id TEXT,
            root_session_id TEXT,
            agent_id TEXT,
            lane TEXT,
            provider TEXT NOT NULL,
            model TEXT NOT NULL,
            purpose TEXT NOT NULL,
            input_tokens INTEGER NOT NULL DEFAULT 0,
            output_tokens INTEGER NOT NULL DEFAULT 0,
            cache_read_tokens INTEGER NOT NULL DEFAULT 0,
            cache_write_tokens INTEGER NOT NULL DEFAULT 0,
            cost_usd REAL NOT NULL DEFAULT 0,
            cost_source TEXT NOT NULL
        );
        CREATE INDEX usage_calls_ts ON usage_calls(ts);
        CREATE INDEX usage_calls_root ON usage_calls(root_session_id);
        CREATE INDEX usage_calls_day_lane ON usage_calls(day, lane);
        CREATE TABLE context_prompts (
            session_id TEXT PRIMARY KEY,
            prompt TEXT NOT NULL,
            created_at TEXT NOT NULL,
            prompt_revision INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE context_tools (
            session_id TEXT PRIMARY KEY,
            tools TEXT NOT NULL,
            since_seq INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE context_sent (
            session_id TEXT NOT NULL,
            version INTEGER NOT NULL,
            after_seq INTEGER NOT NULL,
            prompt TEXT NOT NULL,
            tools TEXT NOT NULL,
            created_at TEXT NOT NULL,
            PRIMARY KEY (session_id, version)
        );
        CREATE TABLE agent_records (
            id TEXT PRIMARY KEY,
            session_id TEXT NOT NULL,
            parent_agent_id TEXT,
            name TEXT NOT NULL,
            status TEXT NOT NULL,
            task TEXT,
            result TEXT,
            error TEXT,
            model TEXT,
            created_at TEXT NOT NULL,
            finished_at TEXT,
            stats TEXT
        );
        CREATE INDEX ix_agent_records_session ON agent_records(session_id);
        CREATE INDEX ix_agent_records_created ON agent_records(created_at);
        CREATE TABLE _migrations (
            scope TEXT PRIMARY KEY,
            version INTEGER NOT NULL
        );
        """;

    /// <summary>
    /// What the old build recorded in _migrations once every migration had run.
    /// </summary>
    public static readonly (string Scope, int Version)[] Migrations =
    [
        ("core", 5), ("netpi.ideas", 2), ("lanes", 3), ("context", 5), ("agent", 3),
    ];

    /// <summary>
    /// The DDL as individual statements: the Host's Execute prepares a whole multi-statement script up front, so a later
    /// statement that names a table an earlier one creates (a CREATE INDEX on the just-created table) fails to prepare
    /// before it runs. One Execute per statement keeps the order and avoids that.
    /// </summary>
    public static string[] Statements()
    {
        var list = new List<string>();
        foreach (var part in Ddl.Split(';'))
            if (part.Trim().Length > 0) list.Add(part.Trim());
        return [.. list];
    }
}
