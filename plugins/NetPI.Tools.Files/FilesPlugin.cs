using Microsoft.Extensions.Logging;

namespace NetPI.Tools.Files;

/// <summary>
/// File tools: read, write, edit, grep, find, ls (+ files.search / files.list / files.open / files.git RPC).
/// Settings: <c>files.newFileEol</c> ("lf" | "crlf" | "auto").
/// </summary>
[NetPiPlugin("netpi.tools.files", Name = "File tools", Description = "read, write, edit (CRLF/LF agnostic), grep, find, ls", Order = 20)]
public sealed class FilesPlugin : INetPiPlugin
{
    private readonly FileIndex _index = new();

    public static IReadOnlyList<IAgentTool> CreateTools(ISettings? settings) =>
    [
        new ReadTool(settings),
        new WriteTool(settings),
        new EditTool(settings),
        new GrepTool(settings),
        new FindTool(settings),
        new LsTool(settings),
    ];

    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "files", Title = "Files", Group = "Tools", Order = 10,
            Settings =
            [
                SettingInfo.Choice("files.newFileEol", "Line endings of new files", "lf", ["lf", "crlf", "auto"], "Existing files keep theirs; auto = CRLF on Windows."),
            ],
        });
        foreach (var tool in CreateTools(context.Settings))
            context.Tools.Register(tool);

        context.Rpc.RegisterReadOnly("files.search", async (req, token) =>
        {
            var root = ResolveRoot(context, req);
            return await _index.SearchAsync(root, req.Str("query") ?? req.Str("q"), req.Int("limit") ?? 50, token).ConfigureAwait(false);
        }, "Fuzzy file-name search for @ mentions: { sessionId?, cwd?, query, limit? } → { path, rel, isDir }[]");

        context.Rpc.RegisterReadOnly("files.list", (req, token) =>
        {
            var root = ResolveRoot(context, req);
            return Task.FromResult<object?>(_index.List(root, req.Str("dir")));
        }, "List one directory for the file tree: { sessionId?, cwd?, dir? } → { root, dir, entries: { name, rel, isDir, size?, mtime?, ignored? }[] }");

        context.Rpc.Register("files.open", (req, _) =>
        {
            var (path, action) = FileOpener.Decide(context, ResolveRoot(context, req), req.Required("path"));
            FileOpener.Run(path, action);
            return Task.FromResult<object?>(new { path, action });
        }, "Open a path with the operating system (files in their default app, folders in the file manager, scripts for editing, executables only revealed): { path, sessionId?, cwd? } → { path, action }");

        context.Rpc.RegisterReadOnly("files.git", async (req, token) =>
            await GitStatus.ReadAsync(ResolveRoot(context, req), token).ConfigureAwait(false),
            "The workspace's changes since the last commit, for the Files tab: { sessionId?, cwd? } → { repo, root, workspaceId?, branch, ahead, behind, files: { path, rel, status, added?, deleted? }[], added, deleted } | null (not a git repository)");

        // What the Files tab keys its refreshes on: the workspace identity and version, so a late answer computed for
        // the previous root is recognizably stale instead of being merged into the new one.
        context.Rpc.RegisterReadOnly("files.scope", (req, _) =>
        {
            var sessionId = req.Str("sessionId");
            var session = sessionId is { Length: > 0 } ? context.Sessions?.GetSession(sessionId) : null;
            var resolver = context.Services.Get<IWorkspaceResolver>();
            var binding = resolver is not null && session is not null ? resolver.ResolveLenient(session) : null;
            return Task.FromResult<object?>(new
            {
                sessionId,
                root = ResolveRoot(context, req),
                workspaceId = binding?.WorkspaceId,
                branch = binding?.Branch,
                isolated = binding?.Isolated ?? false,
                identity = session is not null && resolver is not null ? resolver.IdentityOf(session) : null,
                version = binding?.Version ?? 0,
            });
        }, "Identity and version of what the Files tab is showing: { sessionId?, cwd? } → { sessionId, root, workspaceId?, branch?, isolated, identity?, version }");

        context.Rpc.RegisterReadOnly("files.commits", async (req, token) =>
        {
            var since = req.Str("since");
            var until = req.Str("until");
            var limit = Math.Clamp(req.Int("limit") ?? 20, 1, 200);
            return await GitStatus.CommitsAsync(ResolveRoot(context, req), since, until, limit, token).ConfigureAwait(false);
        }, "The repository's commits, newest first: { sessionId?, cwd?, since? (a hash: only what came after it), until? (a hash: only what came before it), " +
           "limit? (20, max 200) } → { repo, gitDir, commonDir, reachable, commits: { hash, short, subject, author, at }[] } | null (not a git repository)");

        // Left-panel file tree of the active session's workspace (UI in ui/main.js → wwwroot/ui.js).
        context.Ui.AddTab(new UiTabInfo { Id = "files", Title = "Files", Panel = UiPanel.Left, Icon = "files", Order = 30, Module = "ui.js" });

        context.Logger.LogDebug("File tools registered");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Root directory for an RPC call: an explicit cwd, else the session's workspace root (through the workspace
    /// resolver, so the Files tab and the git line show the tree the session actually works in), else its project path,
    /// else the default workspace. A session bound to a workspace that cannot be used fails here instead of quietly
    /// showing the project checkout, which would be a different tree from the one the agent edits.
    /// </summary>
    internal static string ResolveRoot(IPluginContext context, RpcRequest req)
    {
        var cwd = req.Str("cwd");
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            var full = Path.GetFullPath(cwd);
            if (Directory.Exists(full)) return full;
            throw new RpcException("not_found", $"Directory not found: {full}");
        }
        var sessionId = req.Str("sessionId");
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var session = context.Sessions?.GetSession(sessionId);
            if (session is not null)
            {
                if (session.WorkspaceId is { Length: > 0 })
                {
                    var resolver = context.Services.Get<IWorkspaceResolver>();
                    if (resolver is not null) return resolver.CwdOf(session);   // throws when the workspace is broken
                }
                var sc = context.Sessions!.GetCwd(session);
                if (!string.IsNullOrWhiteSpace(sc)) return sc;
            }
        }
        return context.Paths.DefaultWorkspace;
    }
}
