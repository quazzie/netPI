using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Lsp;

/// <summary>
/// The <c>lsp</c> tool (v1, idea-fe26oh): what a language server knows about the code, asked again at every call.
/// diagnostics: errors and warnings of files (default: the workspace's changed files, from git status); definition: where
/// the symbol at a position is defined; references: where it is used; rename: change its name everywhere (the edit is applied
/// through the Files plugin's files.applyEdits); symbols: a file's outline, or the workspace's symbols matching a query. C# runs on
/// csharp-ls, .svelte on svelte-language-server, JavaScript/TypeScript on typescript-language-server, each installed on its
/// first use and started per workspace root.
/// </summary>
internal sealed class LspTool(LspPool pool, IRpcRegistry? rpc = null) : IAgentTool, IReadOnlyCalls
{
    private const int MaxFiles = 20;
    private const int MaxLines = 200;
    /// <summary>How long one diagnostics call asks in all: a server that hangs costs one wait, not one per file.</summary>
    private static readonly TimeSpan DiagnosticsBudget = TimeSpan.FromMinutes(2);
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "dist", "build", "out", "artifacts", "coverage", "wwwroot", "packages", "target",
    };

    public ToolDefinition Definition { get; } = new()
    {
        Name = "lsp",
        Label = "Code intelligence",
        Category = "code",
        ReadOnly = false, // rename writes; the calls that only read say so (IReadOnlyCalls)
        SummaryArg = "action",
        Description = "Ask a language server about code (C#, Svelte, JavaScript/TypeScript): diagnostics of files (default: the changed ones), " +
                      "the definition or the references of the symbol at a position, rename it everywhere, or symbols (a file's outline, or a workspace search).",
        Help = "action: diagnostics { files?: string[] (default: the files git reports changed) }, definition { file, line, column } " +
               "(1-based), references { file, line, column } (every use, the declaration included: file:line:column and the line), " +
               "rename { file, line, column, newName } (the server's rename applied to all the files it touches, or to none; " +
               "files keep their line endings and encoding; refused when the server also wants to create, rename or delete files), symbols { file } (its outline) or { query, language?: csharp|svelte|typescript, file? } (a workspace search; " +
               "the JavaScript servers search the projects of the files they have open: give a file of the project to search in, " +
               "else one is picked). The first call for a language installs its server (csharp-ls, svelte-language-server, " +
               "typescript-language-server, pinned and checked) and loads the project: it can take a minute or two; later calls are " +
               "fast. Answers are about the files as they are on disk now.",
        Parameters = JsonNode.Parse("""
            {"type":"object","properties":{
              "action":{"type":"string","enum":["diagnostics","definition","references","rename","symbols"]},
              "files":{"type":"array","items":{"type":"string"}},
              "file":{"type":"string"},
              "line":{"type":"integer","minimum":1},
              "column":{"type":"integer","minimum":1},
              "query":{"type":"string"},
              "newName":{"type":"string"},
              "language":{"type":"string","enum":["csharp","svelte","typescript"]}},
             "required":["action"]}
            """)!.AsObject(),
    };

    /// <summary>For the tests: a server for files the built-in ones do not serve.</summary>
    internal Func<string, LspServer?>? ServerFor { get; init; }

    public bool IsReadOnly(JsonElement args) => !string.Equals(new ToolArgs(args).Str("action"), "rename", StringComparison.OrdinalIgnoreCase);

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        var a = new ToolArgs(args);
        var root = Path.GetFullPath(context.Cwd ?? Environment.CurrentDirectory);
        try
        {
            return (a.Str("action") ?? "").ToLowerInvariant() switch
            {
                "diagnostics" => await DiagnosticsAsync(root, a, ct).ConfigureAwait(false),
                "definition" => await DefinitionAsync(root, a, ct).ConfigureAwait(false),
                "references" => await ReferencesAsync(root, a, ct).ConfigureAwait(false),
                "rename" => await RenameAsync(context, root, a, ct).ConfigureAwait(false),
                "symbols" => await SymbolsAsync(root, a, ct).ConfigureAwait(false),
                _ => ToolResult.Error("action: diagnostics, definition, references, rename or symbols."),
            };
        }
        catch (LspException ex) { return ToolResult.Error(ex.Message); }
        catch (ObjectDisposedException) { return ToolResult.Error("The language servers are stopping (NetPI is closing or reloading the plugin)."); }
    }

    private async Task<ToolResult> DiagnosticsAsync(string root, ToolArgs a, CancellationToken ct)
    {
        List<string>? given = a.List("files") is { Count: > 0 } list
            ? [.. list.Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
            : a.Str("file") is { } one ? [one] : null;
        var files = (given ?? await ChangedFilesAsync(root, ct).ConfigureAwait(false))
            .Select(f => Path.GetFullPath(Path.Combine(root, f))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var served = files.Where(f => LspServer.For(f) is not null && File.Exists(f)).ToList();
        var notes = new List<string>();
        if (given is not null && files.Count > served.Count)
            notes.Add("Not checked (no server for them, or missing): " +
                      string.Join(", ", files.Except(served, StringComparer.OrdinalIgnoreCase).Select(f => Rel(root, f) + (File.Exists(f) ? "" : " (missing)"))) + ".");
        if (served.Count == 0)
            return given is null
                ? ToolResult.Ok("No changed C#, Svelte or JavaScript/TypeScript files.")
                : ToolResult.Error("None of these files is C#, Svelte or JavaScript/TypeScript, or none exists.");
        if (served.Count > MaxFiles) notes.Add($"Only the first {MaxFiles} of {served.Count} files were checked.");

        var lines = new StringBuilder();
        int checkedFiles = 0, errors = 0, warnings = 0, others = 0, shown = 0;
        var budget = Stopwatch.StartNew();
        foreach (var group in served.Take(MaxFiles).GroupBy(f => LspServer.For(f)!))
        {
            LspSession session;
            try { session = await pool.GetAsync(group.Key, root, ct).ConfigureAwait(false); }
            catch (LspException ex)
            {
                notes.Add($"{string.Join(", ", group.Select(f => Rel(root, f)))}: not checked. {ex.Message}");
                continue;
            }
            foreach (var file in group)
            {
                var left = DiagnosticsBudget - budget.Elapsed;
                if (left <= TimeSpan.Zero)
                {
                    notes.Add($"{Rel(root, file)}: not checked, the call ran out of time.");
                    continue;
                }
                try
                {
                    var (items, complete) = await session.DiagnosticsAsync(file, left < session.Server.RequestTimeout ? left : session.Server.RequestTimeout, ct).ConfigureAwait(false);
                    checkedFiles++;
                    if (!complete) notes.Add($"{Rel(root, file)}: the server had not finished with it in time; what it said last is shown.");
                    foreach (var d in items.OfType<JsonObject>().OrderBy(Severity).ThenBy(Line))
                    {
                        switch (Severity(d)) { case 1: errors++; break; case 2: warnings++; break; default: others++; break; }
                        if (++shown > MaxLines) continue;
                        lines.Append(Rel(root, file)).Append(':').Append(Line(d) + 1).Append(':').Append(Column(d) + 1).Append(' ')
                            .Append(SeverityName(d)).Append(Code(d) is { } code ? " " + code : "").Append(": ")
                            .AppendLine(string.Join(' ', (Str(d["message"]) ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
                    }
                }
                catch (LspException ex) { notes.Add($"{Rel(root, file)}: {ex.Message}"); }
            }
        }

        var text = new StringBuilder();
        if (checkedFiles > 0)
            text.AppendLine(errors + warnings + others == 0
                ? $"{checkedFiles} file(s) checked: no problems."
                : $"{checkedFiles} file(s) checked: {errors} error(s), {warnings} warning(s){(others > 0 ? $", {others} note(s)" : "")}.");
        text.Append(lines);
        if (shown > MaxLines) text.AppendLine($"… {shown - MaxLines} more not shown.");
        foreach (var note in notes) text.AppendLine(note);
        return checkedFiles == 0 ? ToolResult.Error(text.ToString().TrimEnd()) : ToolResult.Ok(text.ToString().TrimEnd());
    }

    private async Task<ToolResult> DefinitionAsync(string root, ToolArgs a, CancellationToken ct)
    {
        if (a.Str("file") is not { } f || a.Int("line") is not { } line || a.Int("column") is not { } column || line < 1 || column < 1)
            return ToolResult.Error("definition needs file, line and column (1-based).");
        var file = Path.GetFullPath(Path.Combine(root, f));
        var session = await pool.GetAsync(Served(file), root, ct).ConfigureAwait(false);
        var answer = await session.DefinitionAsync(file, line - 1, column - 1, ct).ConfigureAwait(false);
        var locations = answer switch { JsonArray arr => arr.OfType<JsonObject>(), JsonObject o => [o], _ => [] };
        var shown = locations.Select(l => Location(root, l)).OfType<string>().Distinct().ToList();
        return ToolResult.Ok(shown.Count == 0 ? "No definition found there." : string.Join("\n", shown));
    }

    private const int MaxReferences = 200;
    private const int MaxLineText = 160;

    private async Task<ToolResult> ReferencesAsync(string root, ToolArgs a, CancellationToken ct)
    {
        if (a.Str("file") is not { } f || a.Int("line") is not { } line || a.Int("column") is not { } column || line < 1 || column < 1)
            return ToolResult.Error("references needs file, line and column (1-based).");
        var file = Path.GetFullPath(Path.Combine(root, f));
        var session = await pool.GetAsync(Served(file), root, ct).ConfigureAwait(false);
        if (!session.Offers("referencesProvider", out _)) return ToolResult.Error($"The {session.Server.Id} language server does not find references.");
        var answer = await session.ReferencesAsync(file, line - 1, column - 1, ct).ConfigureAwait(false);
        var locations = (answer as JsonArray ?? []).OfType<JsonObject>().Select(l => (Text: Location(root, l), Path: UriPath(l), Line: StartOf(l).Line))
            .Where(l => l.Text is not null).DistinctBy(l => l.Text).ToList();
        if (locations.Count == 0) return ToolResult.Ok("No references found there.");
        var lines = new List<string>();
        var cache = new Dictionary<string, string[]?>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in locations.Take(MaxReferences))
        {
            if (!cache.TryGetValue(l.Path, out var content))
            {
                try { content = File.Exists(l.Path) ? File.ReadAllLines(l.Path) : null; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { content = null; }
                cache[l.Path] = content;
            }
            var text = content is not null && l.Line < content.Length ? content[l.Line].Trim() : "";
            if (text.Length > MaxLineText) text = text[..MaxLineText] + "…";
            lines.Add(text.Length == 0 ? l.Text! : $"{l.Text}: {text}");
        }
        var fileCount = locations.Select(l => l.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var head = $"{locations.Count} reference(s) in {fileCount} file(s):";
        var more = locations.Count > MaxReferences ? $"\n… {locations.Count - MaxReferences} more not shown." : "";
        return ToolResult.Ok(head + "\n" + string.Join("\n", lines) + more);
    }

    private async Task<ToolResult> RenameAsync(ToolContext context, string root, ToolArgs a, CancellationToken ct)
    {
        if (a.Str("file") is not { } f || a.Int("line") is not { } line || a.Int("column") is not { } column || line < 1 || column < 1
            || a.Str("newName") is not { Length: > 0 } newName)
            return ToolResult.Error("rename needs file, line, column (1-based) and newName.");
        var file = Path.GetFullPath(Path.Combine(root, f));
        var session = await pool.GetAsync(Served(file), root, ct).ConfigureAwait(false);
        if (!session.Offers("renameProvider", out var options)) return ToolResult.Error($"The {session.Server.Id} language server does not rename.");
        if (rpc is null || !rpc.Exists("files.applyEdits"))
            return ToolResult.Error("rename applies its edit through the Files plugin (files.applyEdits), which is not loaded.");
        if (options?["prepareProvider"] is JsonValue pv && pv.TryGetValue<bool>(out var prepare) && prepare
            && await session.PrepareRenameAsync(file, line - 1, column - 1, ct).ConfigureAwait(false) is null)
            return ToolResult.Error("There is nothing to rename at that position (put the column on the name).");
        var proposed = await session.RenameAsync(file, line - 1, column - 1, newName, ct).ConfigureAwait(false);
        var (edits, problem) = ParseWorkspaceEdit(proposed);
        if (problem is not null) return ToolResult.Error(problem + " Nothing was changed.");
        if (edits.Count == 0) return ToolResult.Error("The server proposed no changes. Nothing was changed.");
        JsonNode? applied;
        try
        {
            applied = NetPiJson.ToNode(await rpc.InvokeAsync("files.applyEdits", new JsonObject { ["sessionId"] = context.SessionId, ["cwd"] = root, ["edits"] = edits }, ct).ConfigureAwait(false));
        }
        catch (RpcException ex) { return ToolResult.Error($"The rename was not applied: {ex.Message}"); }
        var changed = (applied?["files"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(o => (Path: Str(o["path"]) ?? "", Count: o["edits"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0)).ToList();
        await session.RefreshOpenAsync(changed.Select(c => c.Path), ct).ConfigureAwait(false);
        var total = changed.Sum(c => c.Count);
        var text = new StringBuilder($"Renamed to {newName}: {total} edit(s) in {changed.Count} file(s).");
        foreach (var c in changed) text.Append('\n').Append(Rel(root, c.Path)).Append(": ").Append(c.Count);
        return ToolResult.Ok(text.ToString(), new { files = changed.Select(c => new { path = c.Path, edits = c.Count }).ToArray(), edits = total });
    }

    /// <summary>
    /// A server's WorkspaceEdit as the <c>edits</c> of files.applyEdits (<c>changes</c>, or the text edits of
    /// <c>documentChanges</c>); a problem instead when it holds file operations (create, rename, delete) or files that are not local.
    /// </summary>
    internal static (JsonArray Edits, string? Problem) ParseWorkspaceEdit(JsonNode? edit)
    {
        var byPath = new Dictionary<string, JsonArray>(StringComparer.OrdinalIgnoreCase);
        void Add(string uri, JsonNode? list)
        {
            var path = LspSession.LocalPath(uri);
            if (!byPath.TryGetValue(path, out var target)) byPath[path] = target = [];
            foreach (var e in (list as JsonArray ?? []).OfType<JsonObject>())
            {
                var r = e["range"];
                int N(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
                target.Add(new JsonObject
                {
                    ["startLine"] = N(r?["start"]?["line"]), ["startCol"] = N(r?["start"]?["character"]),
                    ["endLine"] = N(r?["end"]?["line"]), ["endCol"] = N(r?["end"]?["character"]),
                    ["newText"] = Str(e["newText"]) ?? "",
                });
            }
        }
        if (edit is JsonObject o)
        {
            if (o["documentChanges"] is JsonArray changes)
            {
                foreach (var c in changes.OfType<JsonObject>())
                {
                    if (Str(c["kind"]) is { } kind)
                        return ([], $"The server also wants to {kind} a file ({Str(c["uri"] ?? c["oldUri"]) ?? "?"}); the lsp tool applies text edits only.");
                    if (Str(c["textDocument"]?["uri"]) is { } uri) Add(uri, c["edits"]);
                }
            }
            else if (o["changes"] is JsonObject plain)
                foreach (var (uri, list) in plain) Add(uri, list);
        }
        foreach (var path in byPath.Keys)
            if (!Path.IsPathFullyQualified(path)) return ([], $"The server wants to change {path}, which is not a local file.");
        return ([.. byPath.Where(p => p.Value.Count > 0).Select(p => (JsonNode)new JsonObject { ["path"] = p.Key, ["edits"] = p.Value })], null);
    }

    private static string UriPath(JsonObject l) => LspSession.LocalPath(Str(l["uri"] ?? l["targetUri"]) ?? "");

    private static (int Line, int Column) StartOf(JsonObject l)
    {
        var start = (l["range"] ?? l["targetSelectionRange"])?["start"];
        return (start?["line"] is JsonValue lv && lv.TryGetValue<int>(out var ln) ? ln : 0, start?["character"] is JsonValue cv && cv.TryGetValue<int>(out var cn) ? cn : 0);
    }

    private async Task<ToolResult> SymbolsAsync(string root, ToolArgs a, CancellationToken ct)
    {
        var file = a.Str("file") is { Length: > 0 } f ? Path.GetFullPath(Path.Combine(root, f)) : null;
        if (a.Str("query") is not { Length: > 0 } query)
        {
            if (file is null) return ToolResult.Error("symbols needs file (an outline) or query (a workspace search).");
            var session = await pool.GetAsync(Served(file), root, ct).ConfigureAwait(false);
            var answer = await session.DocumentSymbolsAsync(file, ct).ConfigureAwait(false);
            var text = new StringBuilder();
            foreach (var s in (answer as JsonArray ?? []).OfType<JsonObject>()) Outline(text, s, 0);
            return ToolResult.Ok(text.Length == 0 ? "No symbols." : text.ToString().TrimEnd());
        }

        var server = a.Str("language") is { } language ? LspServer.All.FirstOrDefault(s => s.Id == language)
            : file is not null ? LspServer.For(file) : null;
        server ??= Guess(root);
        var workspace = await pool.GetAsync(server, root, ct).ConfigureAwait(false);
        // the JS servers search the projects of their open files: open the given one, else one of theirs in the workspace
        if (server != LspServer.CSharp)
        {
            if (file is not null && File.Exists(file) && LspServer.For(file) == server) await workspace.TouchAsync(file, ct).ConfigureAwait(false);
            else if (!workspace.HasDocuments && Anchor(root, server) is { } anchor) await workspace.TouchAsync(anchor, ct).ConfigureAwait(false);
        }
        var found = await workspace.WorkspaceSymbolsAsync(query, ct).ConfigureAwait(false);
        var rows = (found as JsonArray ?? []).OfType<JsonObject>().Take(MaxLines)
            .Select(s => $"{Kind(s["kind"])} {Str(s["name"])}{(Str(s["containerName"]) is { Length: > 0 } c ? $" ({c})" : "")} {Location(root, s["location"] as JsonObject)}".TrimEnd())
            .ToList();
        return ToolResult.Ok(rows.Count == 0 ? $"No {server.Id} symbol matches \"{query}\"." : string.Join("\n", rows));
    }

    /// <summary>The server for a file that exists, or why there is none.</summary>
    private LspServer Served(string file) =>
        !File.Exists(file) ? throw new LspException($"There is no file {file}.")
        : (ServerFor?.Invoke(file) ?? LspServer.For(file)) ?? throw new LspException($"No language server here serves {Path.GetExtension(file)} files (C#, Svelte and JavaScript/TypeScript ones are served).");

    // ------------------------------------------------------------------ formatting

    private static void Outline(StringBuilder text, JsonObject s, int depth)
    {
        var start = (s["range"] ?? (s["location"] as JsonObject)?["range"])?["start"];
        var line = start?["line"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
        text.Append(' ', depth * 2).Append(Kind(s["kind"])).Append(' ').Append(Str(s["name"])).Append(" :").Append(line + 1).AppendLine();
        foreach (var child in (s["children"] as JsonArray ?? []).OfType<JsonObject>()) Outline(text, child, depth + 1);
    }

    /// <summary>A Location or LocationLink as path:line:column, the path relative to the workspace when it is inside it.</summary>
    private static string? Location(string root, JsonObject? l)
    {
        if (l is null || Str(l["uri"] ?? l["targetUri"]) is not { } uri) return null;
        var start = (l["range"] ?? l["targetSelectionRange"])?["start"];
        var line = start?["line"] is JsonValue lv && lv.TryGetValue<int>(out var ln) ? ln : 0;
        var column = start?["character"] is JsonValue cv && cv.TryGetValue<int>(out var cn) ? cn : 0;
        return $"{Rel(root, LspSession.LocalPath(uri))}:{line + 1}:{column + 1}";
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>A diagnostic's code as its compiler writes it: TypeScript's numbers (the Svelte server passes them on bare) as TS1109.</summary>
    private static string? Code(JsonObject d) => d["code"] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0 ? s : null,
        JsonValue v when v.TryGetValue<long>(out var n) => Str(d["source"]) is "ts" or "js" or "typescript" or "javascript" ? $"TS{n}" : n.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    private static string Kind(JsonNode? kind) => (kind is JsonValue v && v.TryGetValue<int>(out var k) ? k : 0) switch
    {
        1 => "file", 2 => "module", 3 => "namespace", 4 => "package", 5 => "class", 6 => "method", 7 => "property", 8 => "field",
        9 => "constructor", 10 => "enum", 11 => "interface", 12 => "function", 13 => "variable", 14 => "constant", 15 => "string",
        16 => "number", 17 => "boolean", 18 => "array", 19 => "object", 20 => "key", 21 => "null", 22 => "enum member", 23 => "struct",
        24 => "event", 25 => "operator", 26 => "type parameter", _ => "symbol",
    };

    private static int Line(JsonObject d) => d["range"]?["start"]?["line"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
    private static int Column(JsonObject d) => d["range"]?["start"]?["character"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
    private static int Severity(JsonObject d) => d["severity"] is JsonValue v && v.TryGetValue<int>(out var s) ? s : 1;
    private static string SeverityName(JsonObject d) => Severity(d) switch { 1 => "error", 2 => "warning", 3 => "info", _ => "hint" };

    /// <summary>A path relative to the workspace when it is inside it; a full path, or a server's own URI (a decompiled
    /// metadata file, say), otherwise.</summary>
    private static string Rel(string root, string path)
    {
        if (!Path.IsPathFullyQualified(path)) return path;
        var rel = Path.GetRelativePath(root, path);
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathFullyQualified(rel) ? path : rel.Replace('\\', '/');
    }

    /// <summary>The workspace's server for a symbol search without a language: C# when it has a project or solution, else TypeScript.</summary>
    private static LspServer Guess(string root) =>
        Directory.EnumerateFiles(root, "*.*proj").Concat(Directory.EnumerateFiles(root, "*.sln*")).Any() ? LspServer.CSharp : LspServer.TypeScript;

    /// <summary>
    /// A file of the server's to open so it has a project to search: breadth-first from the root, a few levels deep, past
    /// dependency, build and hidden folders, preferring one under a <c>src</c> folder (a config file at a project's top is
    /// often outside the project).
    /// </summary>
    internal static string? Anchor(string root, LspServer server)
    {
        string? first = null;
        var queue = new Queue<(string Dir, int Depth)>([(root, 0)]);
        var looked = 0;
        while (queue.Count > 0 && looked < 4000)
        {
            var (dir, depth) = queue.Dequeue();
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(dir);
                dirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            looked += files.Length + dirs.Length;
            var mine = files.Where(f => server.Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (mine is not null)
            {
                if (Path.GetRelativePath(root, mine).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("src", StringComparer.OrdinalIgnoreCase))
                    return mine;
                first ??= mine;
            }
            if (depth >= 4) continue;
            foreach (var sub in dirs.Order(StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(sub);
                if (!name.StartsWith('.') && !SkippedFolders.Contains(name)) queue.Enqueue((sub, depth + 1));
            }
        }
        return first;
    }

    /// <summary>
    /// The files git reports changed under the root (staged, unstaged and new; not deleted) as full paths; none outside a
    /// repository. Porcelain paths are relative to the repository's top whatever folder git runs in, so the top is asked
    /// for too; <c>-z</c> leaves them unquoted.
    /// </summary>
    internal static async Task<List<string>> ChangedFilesAsync(string root, CancellationToken ct)
    {
        var top = (await GitAsync(root, ct, "rev-parse", "--show-toplevel").ConfigureAwait(false))?.Trim();
        if (string.IsNullOrEmpty(top)) return [];
        var status = await GitAsync(root, ct, "status", "--porcelain=v1", "-z", "-uall", "--", ".").ConfigureAwait(false);
        if (status is null) return [];
        var files = new List<string>();
        var fields = status.Split('\0');
        for (var i = 0; i < fields.Length; i++)
        {
            var entry = fields[i];
            if (entry.Length < 4) continue;
            if (entry[0] is 'R' or 'C') i++; // a rename's or copy's source follows as a field of its own
            if (entry[0] == 'D' || entry[1] == 'D') continue;
            files.Add(Path.GetFullPath(Path.Combine(top, entry[3..])));
        }
        return files;
    }

    private static async Task<string?> GitAsync(string root, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(root);
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        try
        {
            using var git = Process.Start(psi);
            if (git is null) return null;
            var output = git.StandardOutput.ReadToEndAsync(ct);
            var errors = git.StandardError.ReadToEndAsync(ct); // read as well, so a chatty git never blocks on a full pipe
            await git.WaitForExitAsync(ct).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            return git.ExitCode == 0 ? await output.ConfigureAwait(false) : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
}
