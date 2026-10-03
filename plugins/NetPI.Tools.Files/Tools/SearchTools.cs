using System.Text;
using System.Text.Json;

namespace NetPI.Tools.Files;

public sealed class GrepTool(ISettings? settings = null) : FileToolBase(settings)
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "grep",
        Label = "Grep",
        Category = "files",
        ReadOnly = true,
        SummaryArg = "pattern",
        Description = "Search file contents with a regular expression (.NET syntax); respects .gitignore.",
        Help =
            "Skips binary files and node_modules/bin/obj/.git/dist/build. Escape ( ) [ ] . * + ? to match them, or pass " +
            "literal=true for plain text. glob filters files (\"*.cs\", \"**/*.{ts,tsx}\", \"!**/*.test.ts\"; several separated by " +
            "spaces). outputMode: content (default: matching lines, with context lines around each), files (only paths), count " +
            "(matches per file); maxResults caps lines or files (default 200). multiline lets the pattern span lines (\\n in the " +
            "pattern; (?s) makes . match newlines). Output: `path:line: text` (context lines use `path-line- text`). " +
            "Line endings never matter: `$` matches at the end of each line in CRLF and LF files.",
        Parameters = ToolSchema.Object(
            ("pattern", ToolSchema.Str("Regex (or text with literal=true)"), true),
            ("path", ToolSchema.Str("Default: working directory"), false),
            ("glob", ToolSchema.Str("e.g. \"*.cs\", \"!**/*.test.ts\""), false),
            ("ignoreCase", ToolSchema.Bool(""), false),
            ("literal", ToolSchema.Bool(""), false),
            ("context", ToolSchema.Int("Lines around each match"), false),
            ("maxResults", ToolSchema.Int("Default 200"), false),
            ("outputMode", ToolSchema.Str("", "content", "files", "count"), false),
            ("multiline", ToolSchema.Bool(""), false)),
        PromptGuidelines = [UseFileTools],
    };

    protected override Task<ToolResult> RunAsync(ToolContext ctx, ToolArgs args, CancellationToken ct)
    {
        var pattern = args.Str("pattern", "regex", "query", "search", "q", "expression");
        if (string.IsNullOrEmpty(pattern)) return Task.FromResult(MissingArg("pattern", "{\"pattern\": \"TODO\", \"glob\": \"*.cs\"}"));

        var globs = new List<string>();
        if (args.List("glob", "globs", "include", "includes", "file_pattern", "filePattern", "files_glob") is { } gl)
            globs.AddRange(gl.Where(g => g.ValueKind == JsonValueKind.String).Select(g => g.GetString()!).Where(g => g.Length > 0));

        var pathArg = args.Str(PathNames.Concat(["dir", "directory", "cwd"]).ToArray());
        var target = ctx.Cwd;
        if (!string.IsNullOrWhiteSpace(pathArg))
        {
            target = ctx.ResolvePath(pathArg);
            if (!File.Exists(target) && !Directory.Exists(target) && Glob.HasGlobChars(pathArg))
            {
                // path given as a glob ("src/**/*.cs"): split into directory + glob filter.
                var (b, g) = Glob.SplitBase(pathArg);
                target = ctx.ResolvePath(b.Length == 0 ? "." : b);
                globs.Add(g);
            }
            if (!File.Exists(target) && !Directory.Exists(target)) return Task.FromResult(NotFound(ctx, target));
        }

        var mode = (args.Str("outputMode", "output_mode", "mode", "output") ?? "content").Trim().ToLowerInvariant() switch
        {
            "files" or "files_with_matches" or "fileswithmatches" or "l" or "paths" or "list" => GrepOutputMode.Files,
            "count" or "counts" or "c" => GrepOutputMode.Count,
            _ => GrepOutputMode.Content,
        };
        var contextLines = Math.Max(args.Int("context", "C", "context_lines", "contextLines") ?? 0,
            Math.Max(args.Int("A", "after", "after_context") ?? 0, args.Int("B", "before", "before_context") ?? 0));
        var options = new GrepOptions
        {
            Pattern = pattern,
            IgnoreCase = args.Bool("ignoreCase", "ignore_case", "i", "caseInsensitive", "case_insensitive", "insensitive") ?? false,
            Literal = args.Bool("literal", "fixed", "fixedStrings", "fixed_strings", "F", "plain") ?? false,
            Context = Math.Clamp(contextLines, 0, 50),
            MaxResults = Math.Clamp(args.Int("maxResults", "max_results", "limit", "max", "head_limit", "max_count", "maxCount") ?? 200, 1, 5000),
            Mode = mode,
            Multiline = args.Bool("multiline", "U", "multi_line") ?? false,
            Filter = globs.Count > 0 ? new GlobFilter(globs) : null,
            DisplayBase = ctx.Cwd,
        };

        var r = GrepEngine.Run(target, options, ct);
        var sb = new StringBuilder();
        foreach (var n in r.Notes.Where(n => n.Contains("literally"))) sb.Append(n).Append('\n');
        if (r.Output.Length == 0)
        {
            sb.Append($"No matches for {Quote(pattern)} in {Rel(ctx, target)} ({r.FilesSearched} file{(r.FilesSearched == 1 ? "" : "s")} searched");
            if (options.Filter is not null) sb.Append($", glob {string.Join(" ", globs)}");
            sb.Append(").");
        }
        else
        {
            sb.Append(r.Output);
        }
        foreach (var n in r.Notes.Where(n => !n.Contains("literally"))) sb.Append('\n').Append(n);
        if (r.Truncated)
        {
            var unit = mode == GrepOutputMode.Content ? "matches" : "files";
            sb.Append($"\n\n[Results truncated at {options.MaxResults} {unit}. Narrow the search with path/glob or a more specific pattern, or raise maxResults.]");
        }

        return Task.FromResult(ToolResult.Ok(sb.ToString(), new
        {
            pattern,
            path = target,
            outputMode = mode.ToString().ToLowerInvariant(),
            matches = r.Matches,
            files = r.FilesMatched,
            filesSearched = r.FilesSearched,
            truncated = r.Truncated,
        }));
    }

    private static string Quote(string s) => s.Length > 80 ? $"/{s[..80]}…/" : $"/{s}/";
}

public sealed class FindTool(ISettings? settings = null) : FileToolBase(settings)
{
    public const int DefaultMax = 1000;

    public override ToolDefinition Definition { get; } = new()
    {
        Name = "find",
        Label = "Find",
        Category = "files",
        ReadOnly = true,
        SummaryArg = "pattern",
        Description = "Find files and directories by glob pattern, e.g. \"**/*.cs\"; respects .gitignore.",
        Help =
            "Globs: `*`, `**`, `?`, `{a,b}`, `[abc]`, e.g. \"src/**/test_*.py\", \"*.{json,yaml}\". A pattern without '/' matches " +
            "file names at any depth; the pattern is relative to path. Directories are shown with a trailing '/'.",
        Parameters = ToolSchema.Object(
            ("pattern", ToolSchema.Str(""), true),
            ("path", ToolSchema.Str("Default: working directory"), false),
            ("maxResults", ToolSchema.Int($"Default {DefaultMax}"), false)),
        PromptGuidelines = [UseFileTools],
    };

    protected override Task<ToolResult> RunAsync(ToolContext ctx, ToolArgs args, CancellationToken ct)
    {
        var pattern = args.Str("pattern", "glob", "name", "query", "q") ?? "**/*";
        pattern = pattern.Trim().Replace('\\', '/');
        if (pattern.Length == 0) pattern = "**/*";
        var max = Math.Clamp(args.Int("maxResults", "max_results", "limit", "max") ?? DefaultMax, 1, 20_000);
        var pathArg = args.Str(PathNames.Concat(["dir", "directory", "cwd"]).ToArray());
        var root = string.IsNullOrWhiteSpace(pathArg) ? ctx.Cwd : ctx.ResolvePath(pathArg);

        // Absolute or prefixed patterns: walk from the literal base directory.
        if (Glob.HasGlobChars(pattern))
        {
            var (b, g) = Glob.SplitBase(pattern);
            if (b.Length > 0)
            {
                root = Path.IsPathRooted(b) || b.StartsWith('/') ? ctx.ResolvePath(b) : Path.GetFullPath(Path.Combine(root, b));
                pattern = g;
            }
        }
        else if (Path.IsPathRooted(pattern))
        {
            var full = ctx.ResolvePath(pattern);
            if (File.Exists(full) || Directory.Exists(full))
                return Task.FromResult(ToolResult.Ok(Rel(ctx, full) + (Directory.Exists(full) ? "/" : ""), new { pattern, path = full, count = 1, truncated = false }));
            return Task.FromResult(ToolResult.Ok($"No files matching {pattern}.", new { pattern, path = full, count = 0, truncated = false }));
        }
        if (File.Exists(root)) return Task.FromResult(ToolResult.Error($"{root} is a file; path must be a directory."));
        if (!Directory.Exists(root)) return Task.FromResult(NotFound(ctx, root));

        var glob = new Glob(pattern);
        var results = new List<string>();
        var truncated = false;
        foreach (var e in FileWalker.Walk(root, new WalkOptions(), ct: ct))
        {
            if (!glob.IsMatch(e.RelPath)) continue;
            if (results.Count >= max) { truncated = true; break; }
            results.Add(Rel(ctx, e.FullPath) + (e.IsDir ? "/" : ""));
        }

        string content;
        if (results.Count == 0)
            content = $"No files matching {pattern} in {Rel(ctx, root)}.";
        else
        {
            content = string.Join('\n', results);
            if (truncated) content += $"\n\n[Showing the first {max} results. Use a more specific pattern or path, or raise maxResults.]";
        }
        return Task.FromResult(ToolResult.Ok(content, new { pattern, path = root, count = results.Count, truncated }));
    }
}

public sealed class LsTool(ISettings? settings = null) : FileToolBase(settings)
{
    public const int MaxEntries = 1000;

    public override ToolDefinition Definition { get; } = new()
    {
        Name = "ls",
        Label = "List",
        Category = "files",
        ReadOnly = true,
        SummaryArg = "path",
        Description = "List a directory: subdirectories, then files with sizes.",
        Help = "Subdirectories first (with a trailing '/'), then files with their sizes. Entries ignored by .gitignore and build/dependency folders (.git, node_modules, bin…) are hidden unless all=true.",
        Parameters = ToolSchema.Object(
            ("path", ToolSchema.Str("Default: working directory"), false),
            ("all", ToolSchema.Bool("Also ignored entries"), false)),
        PromptGuidelines = [UseFileTools],
    };

    protected override Task<ToolResult> RunAsync(ToolContext ctx, ToolArgs args, CancellationToken ct)
    {
        var pathArg = args.Str(PathNames.Concat(["dir", "directory"]).ToArray());
        var dir = string.IsNullOrWhiteSpace(pathArg) ? ctx.Cwd : ctx.ResolvePath(pathArg);
        if (File.Exists(dir))
        {
            var fi = new FileInfo(dir);
            return Task.FromResult(ToolResult.Ok($"{Rel(ctx, dir)} is a file ({PathDisplay.FormatSize(fi.Length)}, modified {fi.LastWriteTime:yyyy-MM-dd HH:mm}). Use read to view it.",
                new { path = dir, entries = 1, dirs = 0, files = 1, hidden = 0, truncated = false }));
        }
        if (!Directory.Exists(dir)) return Task.FromResult(NotFound(ctx, dir));

        var (entries, visibleTotal, ignoredTotal) = FileWalker.ListDirectory(dir, respectIgnore: true, maxEntries: MaxEntries);
        var all = args.Bool("all", "a", "showAll", "show_all", "hidden", "includeIgnored") ?? false;

        // The listing is already capped at MaxEntries; the totals (streamed, not materialised) say what was not listed.
        var shownList = all ? entries : entries.Where(e => !e.Ignored).ToList();
        var dirs = shownList.Where(e => e.IsDir).ToList();
        var files = shownList.Where(e => !e.IsDir).ToList();
        var sb = new StringBuilder();
        var shown = 0;
        var width = Math.Min(48, files.Count == 0 ? 0 : files.Max(f => f.Name.Length));
        foreach (var d in dirs)
        {
            if (shown++ >= MaxEntries) break;
            sb.Append(d.Name).Append('/');
            if (d.IsLink) sb.Append(" (link)");
            sb.Append('\n');
        }
        foreach (var f in files)
        {
            if (shown++ >= MaxEntries) break;
            sb.Append(f.Name.PadRight(width)).Append("  ").Append(PathDisplay.FormatSize(f.Size)).Append('\n');
        }
        var hiddenTotal = all ? 0 : ignoredTotal;
        var total = all ? visibleTotal + ignoredTotal : visibleTotal;
        var truncated = total > shown;
        if (shownList.Count == 0) sb.Append("(empty directory)\n");
        if (truncated) sb.Append($"\n[Showing {shown} of {total} entries. Use find with a pattern to narrow down.]\n");
        if (hiddenTotal > 0) sb.Append($"\n[{hiddenTotal} ignored entr{(hiddenTotal == 1 ? "y" : "ies")} hidden (gitignored or build/dependency folders); use all=true to show them.]\n");
        return Task.FromResult(ToolResult.Ok(sb.ToString().TrimEnd('\n'),
            new { path = dir, entries = total, dirs = dirs.Count, files = files.Count, hidden = hiddenTotal, truncated }));
    }
}
