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

        context.Rpc.Register("files.search", async (req, token) =>
        {
            var root = ResolveRoot(context, req);
            return await _index.SearchAsync(root, req.Str("query") ?? req.Str("q"), req.Int("limit") ?? 50, token).ConfigureAwait(false);
        }, "Fuzzy file-name search for @ mentions: { sessionId?, cwd?, query, limit? } → { path, rel, isDir }[]");

        context.Rpc.Register("files.list", (req, token) =>
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

        context.Rpc.Register("files.git", async (req, token) =>
            await GitStatus.ReadAsync(ResolveRoot(context, req), token).ConfigureAwait(false),
            "The workspace's changes since the last commit, for the Files tab: { sessionId?, cwd? } → { repo, branch, ahead, behind, files: { path, rel, status, added?, deleted? }[], added, deleted } | null (not a git repository)");

        context.Rpc.Register("files.commits", async (req, token) =>
        {
            var since = req.Str("since");
            var limit = Math.Clamp(req.Int("limit") ?? 20, 1, 200);
            return await GitStatus.CommitsAsync(ResolveRoot(context, req), since, limit, token).ConfigureAwait(false);
        }, "The repository's commits, newest first: { sessionId?, cwd?, since? (a hash: only what came after it), limit? (20, max 200) } → { repo, commits: { hash, short, subject, author, at }[] } | null (not a git repository)", readOnly: true);

        // Left-panel file tree of the active session's workspace (UI in ui/main.js → wwwroot/ui.js).
        context.Ui.AddTab(new UiTabInfo { Id = "files", Title = "Files", Panel = UiPanel.Left, Icon = "files", Order = 30, Module = "ui.js" });

        context.Logger.LogDebug("File tools registered");
        return Task.CompletedTask;
    }

    /// <summary>Root directory for an RPC call: explicit cwd, else the session's cwd, else the default workspace.</summary>
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
                var sc = context.Sessions!.GetCwd(session);
                if (!string.IsNullOrWhiteSpace(sc)) return sc;
            }
        }
        return context.Paths.DefaultWorkspace;
    }
}
