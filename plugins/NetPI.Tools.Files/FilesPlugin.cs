using Microsoft.Extensions.Logging;

namespace NetPI.Tools.Files;

/// <summary>
/// File tools: read, write, edit, grep, find, ls, git (read-only) (+ files.search / files.list / files.open / files.git RPC).
/// Settings: <c>files.newFileEol</c> ("lf" | "crlf" | "auto").
/// </summary>
[NetPiPlugin("netpi.tools.files", Name = "File tools", Description = "read, write, edit (CRLF/LF agnostic), grep, find, ls, git (read-only)", Order = 20)]
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
        new GitTool(settings),
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

        context.Rpc.Register(new RpcMethod("files.search", "Fuzzy file-name search for @ mentions: { sessionId?, cwd?, query, limit? } → { path, rel, isDir }[]", ReadOnly: true, Params: [RpcParam.Opt("q"), RpcParam.Opt("query"), RpcParam.Opt("limit", RpcParamType.Integer), RpcParam.Opt("sessionId"), RpcParam.Opt("cwd")]),
            async (req, token) =>
        {
            var root = ResolveRoot(context, req);
            return await _index.SearchAsync(root, req.Str("query") ?? req.Str("q"), req.Int("limit") ?? 50, token).ConfigureAwait(false);
        });

        context.Rpc.Register(new RpcMethod("files.list", "List one directory for the file tree: { sessionId?, cwd?, dir? } → { root, dir, entries: { name, rel, isDir, size?, mtime?, ignored? }[], truncated, total } (one directory lists at most FileIndex.MaxListedEntries = 10000 entries; a bigger one is capped and the rest is counted)", ReadOnly: true, Params: [RpcParam.Opt("dir"), RpcParam.Opt("sessionId"), RpcParam.Opt("cwd")]),
            (req, token) =>
        {
            var root = ResolveRoot(context, req);
            return Task.FromResult<object?>(_index.List(root, req.Str("dir")));
        });

        context.Rpc.Register(new RpcMethod("files.open", "Open a path with the operating system (files in their default app, folders in the file manager): { path, sessionId?, cwd?, confirm?, user? } → { path, action }, where action 'confirm' is a path outside the session's workspace: ask the user, then call again with confirm: true. Without user? the rules stay conservative (text and code in an editor, executables and other binaries only revealed); with user: true — the user picked this file deliberately — every file opens with its default program, executables and scripts included, like a double click in the file manager", ReadOnly: false, Params: [RpcParam.Req("path"), RpcParam.Opt("sessionId"), RpcParam.Opt("cwd"), RpcParam.Opt("confirm", RpcParamType.Boolean), RpcParam.Opt("user", RpcParamType.Boolean)]),
            (req, _) =>
        {
            var (path, action) = FileOpener.Decide(context, ResolveRoot(context, req), req.Required("path"), req.Bool("confirm") ?? false, req.Bool("user") ?? false);
            if (action != "confirm") FileOpener.Run(path, action);
            return Task.FromResult<object?>(new { path, action });
        });

        context.Rpc.Register(new RpcMethod("files.applyEdits", "Apply text edits by position (a language server's rename): { sessionId?, cwd?, edits: [{ path, edits: [{ startLine, startCol, endLine, endCol, newText }] }] } → { files: { path, edits }[], edits } (0-based line and UTF-16 column, as LSP counts them; relative paths resolve against the session's workspace; each file keeps its encoding, BOM and line endings; all files or none: every file is read, checked against the workspace rule and edited in memory before any is written; overlapping edits are refused)", ReadOnly: false, Params: [RpcParam.Req("edits", RpcParamType.Array), RpcParam.Opt("sessionId"), RpcParam.Opt("cwd")]),
            async (req, token) =>
        {
            var root = ResolveRoot(context, req);
            var parsed = EditApplier.Parse(req.Params.TryGetProperty("edits", out var e) ? e : default);
            var applied = await EditApplier.ApplyAsync(root, parsed, (full, ct) => MutationRefusalAsync(context, req.Str("sessionId"), full, ct), token).ConfigureAwait(false);
            return new { files = applied.Select(a => new { path = a.Path, edits = a.Edits }).ToArray(), edits = applied.Sum(a => a.Edits) };
        });

        context.Rpc.Register(new RpcMethod("files.git", "The workspace's changes since the last commit, for the Files tab: { sessionId?, cwd? } → { repo, root, workspaceId?, branch, ahead, behind, files: { path, rel, status, added?, deleted? }[], added, deleted } | null (not a git repository)", ReadOnly: true, Params: [RpcParam.Opt("sessionId"), RpcParam.Opt("cwd")]),
            async (req, token) =>
            await GitStatus.ReadAsync(ResolveRoot(context, req), token).ConfigureAwait(false));

        // What the Files tab keys its refreshes on: the workspace identity and version, so a late answer computed for
        // the previous root is recognizably stale instead of being merged into the new one.
        context.Rpc.Register(new RpcMethod("files.scope", "Identity and version of what the Files tab is showing: { sessionId?, cwd? } → { sessionId, root, workspaceId?, branch?, isolated, identity?, version }", ReadOnly: true, Params: [RpcParam.Opt("sessionId"), RpcParam.Opt("cwd")]),
            (req, _) =>
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
        });

        context.Rpc.Register(new RpcMethod("files.commits", "Repository history: { sessionId?, cwd?, gitDir?, commonDir?, since?, until?, limit? } → { repo, gitDir, commonDir, reachable, commits } | null (gitDir/commonDir, when given, are reused instead of re-resolved: only the git log runs)", ReadOnly: true, Params: [RpcParam.Opt("limit", RpcParamType.Integer), RpcParam.Opt("sessionId"), RpcParam.Opt("cwd"), RpcParam.Opt("commonDir"), RpcParam.Opt("gitDir"), RpcParam.Opt("hash"), RpcParam.Opt("since"), RpcParam.Opt("until")]),
            async (req, token) => await CommitsAsync(context, req, token).ConfigureAwait(false));

        // Left-panel file tree of the active session's workspace.
        context.Ui.AddTab(new UiTabInfo { Id = "files", Title = "Files", Panel = UiPanel.Left, Icon = "files", Order = 30, Module = "ui.js" });
        context.Logger.LogDebug("File tools registered");
        return Task.CompletedTask;
    }

    /// <summary>The workspace rule for a mutation by a session (<see cref="WorkspacePaths.MutationRefusalAsync"/>'s, for a caller that has no tool context).</summary>
    private static async Task<string?> MutationRefusalAsync(IPluginContext context, string? sessionId, string full, CancellationToken ct)
    {
        var session = sessionId is { Length: > 0 } ? context.Sessions?.GetSession(sessionId) : null;
        var binding = session is not null ? context.Services.Get<IWorkspaceResolver>()?.Resolve(session) : null;
        if (binding is null || !binding.Isolated) return null;
        var probe = context.Services.Get<IWorkspaceRepoProbe>();
        var verdict = await WorkspacePaths.CheckMutationAsync(binding, full, probe, ct).ConfigureAwait(false);
        return verdict is WorkspacePathVerdict.ForeignCheckout or WorkspacePathVerdict.Unverifiable
            ? await WorkspacePaths.RefusalAsync(binding, full, probe, verdict, ct).ConfigureAwait(false)
            : null;
    }

    /// <summary><c>files.commits</c>: the repository's history, or with <c>hash</c> one commit's patch.</summary>
    private static async Task<object?> CommitsAsync(IPluginContext context, RpcRequest req, CancellationToken token)
    {
        if (req.Str("hash") is { Length: > 0 } hash) return await GitStatus.PatchAsync(ResolveRoot(context, req), hash, token).ConfigureAwait(false);
        var limit = Math.Clamp(req.Int("limit") ?? 20, 1, 200);
        return await GitStatus.CommitsAsync(ResolveRoot(context, req), req.Str("since"), req.Str("until"), limit,
            req.Str("gitDir"), req.Str("commonDir"), token).ConfigureAwait(false);
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
                if (SessionWorkspace.Of(session) is not null)
                {
                    var resolver = context.Services.Get<IWorkspaceResolver>();
                    if (resolver is not null)
                    {
                        // A workspace that cannot be used is an answer, not a fall back to the project's folder: the tab
                        // would otherwise show a tree the session does not edit.
                        try { return resolver.CwdOf(session); }
                        catch (WorkspaceUnavailableException ex) { throw new RpcException("workspace_unavailable", ex.Message); }
                    }
                }
                var sc = context.Sessions!.GetCwd(session);
                if (!string.IsNullOrWhiteSpace(sc)) return sc;
            }
        }
        return context.Paths.DefaultWorkspace;
    }
}
