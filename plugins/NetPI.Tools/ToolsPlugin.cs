using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NetPI.Abstractions;

namespace NetPI.Tools;

/// <summary>Shared helpers for reading arguments out of a <see cref="JsonElement"/>.</summary>
internal static class Args
{
    public static string Str(JsonElement el, string name, string fallback = "")
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : fallback;

    public static string? OptStr(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    public static int Int(JsonElement el, string name, int fallback)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : fallback;

    public static bool Bool(JsonElement el, string name, bool fallback)
        => el.TryGetProperty(name, out var p) && p.ValueKind == System.Text.Json.JsonValueKind.True ? p.GetBoolean() : fallback;

    public static System.Text.Json.JsonElement Schema(params (string name, string type, string? desc)[] fields)
    {
        var props = new Dictionary<string, object>();
        foreach (var (n, t, d) in fields)
            props[n] = d is null ? new { type = t } : new { type = t, description = d };
        return JsonSerializer.SerializeToElement(new { type = "object", properties = props, additionalProperties = false });
    }

    /// <summary>Cap output to a max number of bytes, appending a truncation note.</summary>
    public static string Truncate(string s, int maxChars = 50_000)
        => s.Length <= maxChars ? s : s[..maxChars] + $"\n…[truncated {s.Length - maxChars} chars]";

    // ---- file-target tools: shared schema/validation helpers
    // (docs/plans/file-tool-reliability.md §2: required fields + integer/
    // minimum constraints declared in the schema AND validated at execution —
    // reject missing/null/wrong-type/blank path, empty oldText, absent
    // newText, fractional/zero/negative/out-of-range expectedCount. No
    // coercion; unrelated schemas are untouched.)

    private static Dictionary<string, object> BuildProps(params (string name, string type, string? desc)[] fields)
    {
        var props = new Dictionary<string, object>();
        foreach (var (n, t, d) in fields)
        {
            var o = new Dictionary<string, object> { ["type"] = t };
            if (d is not null) o["description"] = d;
            if (n == "path") o["minLength"] = 1;
            if (n == "oldText") o["minLength"] = 1;
            if (n == "expectedCount") { o["minimum"] = 1; o["maximum"] = int.MaxValue; }
            props[n] = o;
        }
        return props;
    }

    /// <summary>An object schema with a required list (file-target tools).</summary>
    public static JsonElement FileTargetSchema(params (string name, string type, string? desc)[] fields)
    {
        var required = fields.Where(f => f.name is "path" or "oldText" or "newText" or "expectedCount").Select(f => f.name).ToList();
        return JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = BuildProps(fields),
            required,
            additionalProperties = false,
        });
    }

    /// <summary>Required nonblank string path (rejects missing/null/wrong-type/blank; no coercion).</summary>
    public static bool TryPath(JsonElement el, out string path)
    {
        path = "";
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String) return false;
        var v = p.GetString();
        return v is { Length: > 0 } && !string.IsNullOrWhiteSpace(v) && (path = v, true).Item2;
    }

    /// <summary>A present nonempty string field.</summary>
    public static bool TryNonEmptyString(JsonElement el, string name, out string value)
    {
        value = "";
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String) return false;
        var v = p.GetString();
        return v is { Length: > 0 } && (value = v, true).Item2;
    }

    /// <summary>A present string field (explicit "" is valid). Rejects missing/null/wrong-type.</summary>
    public static bool TryStringPresent(JsonElement el, string name, out string value)
    {
        value = "";
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String) return false;
        value = p.GetString() ?? "";
        return true;
    }

    /// <summary>A present positive int32. Rejects missing/null/wrong-type, fractional, zero, negative, out-of-range — no coercion.</summary>
    public static bool TryPositiveInt(JsonElement el, string name, out int value)
    {
        value = 0;
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Number) return false;
        if (!p.TryGetInt32(out var v) || v <= 0) return false; // rejects fractional, zero, negative, out-of-range
        value = v;
        return true;
    }

    /// <summary>Read the validated path argument from a context (null when invalid).</summary>
    public static string? ValidPath(ToolContext ctx)
        => TryPath(ctx.Arguments, out var p) ? p : null;
}

/// <summary>A file-read tool.</summary>
public sealed class ReadTool : IAgentTool, IFileTargetTool
{
    public string Name => "read";
    public string Description => "Read a text file (line-numbered, bounded) or a PNG/JPEG/GIF/WebP image for visual inspection and display in chat. Relative paths resolve against the workspace. Use offset/limit to page large files; other binary files return an error.";
    public IReadOnlyList<string> Guidelines => [
        "Default is 200 lines, capped at 2000; 'Showing lines X-Y of Z' tells you when a file continues — pass offset=N to page.",
        "The file must exist; use grep or a shell command to locate it when the exact path is unknown.",
        "User messages may contain @path or @\"path with spaces\" references; treat these as file references and use read when their contents are needed.",
    ];
    public JsonElement Parameters => Args.Schema(
        ("path", "string", "File path to read."),
        ("offset", "number", "Line number to start reading from (1-indexed). Optional."),
        ("limit", "number", "Maximum number of lines to read. Optional."));

    /// <summary>Declare the target path without reading the file (validation first).</summary>
    public string? GetTargetPath(ToolContext context)
        => Args.ValidPath(context) is { } p ? context.ResolvePath(p) : null;

    public async ValueTask<ToolResult> ExecuteAsync(ToolContext ctx, CancellationToken ct)
    {
        // PLAN §19: relative -> workspace, absolute unchanged, text-only with
        // line numbers, bounded output + continuation marker, binary -> useful
        // error (no garbage).
        if (!Args.TryPath(ctx.Arguments, out var raw))
            return Error("path is required (a nonblank string).");
        var path = ctx.ResolvePath(raw);
        if (!File.Exists(path))
            return Error($"File not found: {path}");
        try
        {
            if (new FileInfo(path).Length > ImageContent.MaxBytes &&
                new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" }.Contains(Path.GetExtension(path).ToLowerInvariant()))
                return Error("Image exceeds the 10 MiB limit. Resize it before reading.");
            var bytes = await File.ReadAllBytesAsync(path, ct);
            if (ImageContent.DetectMimeType(bytes) is { } mime)
            {
                if (bytes.Length > ImageContent.MaxBytes) return Error("Image exceeds the 10 MiB limit.");
                return new ToolResult("tool", "read", [new TextPart($"Image: {path}"), new ImagePart(mime, bytes)]);
            }
            if (IsBinary(bytes))
                return Error($"Refusing to read binary file {path} ({bytes.Length} bytes). Convert it to text first or use the bash tool.");
            var offset = Math.Max(1, Args.Int(ctx.Arguments, "offset", 1));
            var limit = Math.Clamp(Args.Int(ctx.Arguments, "limit", 200), 1, 2000);
            var rawText = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n").Replace("\r", "\n");
            var lines = rawText.Split('\n');
            // A trailing newline must not count as an extra (empty) line.
            if (lines.Length > 0 && string.IsNullOrEmpty(lines[^1]))
                lines = lines.Take(lines.Length - 1).ToArray();
            var total = lines.Length;
            if (offset > total)
                return Ok($"No lines at offset {offset} (file has {total} lines).");
            var slice = lines.Skip(offset - 1).Take(limit).ToList();
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < slice.Count; i++)
                sb.AppendLine($"{offset + i,6}│{slice[i]}");
            var from = offset;
            var to = offset + slice.Count - 1;
            var header = $"Showing lines {from}-{to} of {total}.";
            if (to < total)
                header += "\nUse offset=" + (to + 1) + " to continue.";
            return Ok(Args.Truncate(sb.ToString()) + "\n" + header);
        }
        catch (Exception ex)
        {
            return Error($"Read failed: {ex.Message}");
        }
    }

    /// <summary>Binary sniff: NUL byte or high control-char density (PLAN §19).</summary>
    private static bool IsBinary(byte[] bytes, int sample = 4096)
    {
        var n = Math.Min(bytes.Length, sample);
        if (n == 0) return false;
        var suspicious = 0;
        for (var i = 0; i < n; i++)
        {
            var b = bytes[i];
            if (b == 0) return true;
            if (b < 32 && b is not (10 or 13 or 9 or 11)) suspicious++;
        }
        return suspicious > n / 16;
    }

    private static ToolResult Ok(string text) => new("tool", "read", [new TextPart(text)], false);
    private static ToolResult Error(string message) => new("tool", "read", [new TextPart(message)], IsError: true);
}

/// <summary>A file-write tool (creates or overwrites).</summary>
public sealed class WriteTool : IAgentTool, IFileTargetTool
{
    public string Name => "write";
    public string Description => "Write text content to a file, creating it (and parent dirs) if needed and overwriting if it exists.";
    public IReadOnlyList<string> Guidelines => [
        "Overwrites the whole file — read it first when the file already exists and you only want to change part of it; use edit for partial changes.",
    ];
    public JsonElement Parameters => Args.FileTargetSchema(
        ("path", "string", "File path to write (required)."),
        ("content", "string", "Full file content to write (required; must be present — an explicit empty string overwrites with an empty file)."));

    /// <summary>Declare the target path without reading the file (validation first).</summary>
    public string? GetTargetPath(ToolContext context)
        => Args.ValidPath(context) is { } p ? context.ResolvePath(p) : null;

    public async ValueTask<ToolResult> ExecuteAsync(ToolContext ctx, CancellationToken ct)
    {
        if (!Args.TryPath(ctx.Arguments, out var raw))
            return new ToolResult("tool", "write", [new TextPart("path is required (a nonblank string).")], IsError: true);
        if (!Args.TryStringPresent(ctx.Arguments, "content", out var content))
            return new ToolResult("tool", "write", [new TextPart("content is required (a present string; an explicit empty string overwrites with an empty file).")], IsError: true);
        var path = ctx.ResolvePath(raw);
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(path, content, ct);
            return new ToolResult("tool", "write", [new TextPart($"Wrote {content.Length} chars to {path}")], false);
        }
        catch (Exception ex)
        {
            return new ToolResult("tool", "write", [new TextPart($"Write failed: {ex.Message}")], IsError: true);
        }
    }
}

/// <summary>
/// A file-edit tool: replace the <c>old</c> text (which must occur exactly once)
/// with <c>new</c>. Matching is CRLF/LF-equivalent (comparison view only — the
/// file bytes are never normalized outside the replaced span); a lone CR stays
/// literal (docs/plans/file-tool-reliability.md §2).
/// </summary>
public sealed class EditTool : IAgentTool, IFileTargetTool
{
    public string Name => "edit";
    public string Description => "Replace a unique exact text span in a file. oldText must occur exactly once (0 or >1 matches both fail; no fuzzy matching). CRLF and LF compare equivalently; a lone CR is literal; the rest of the file is preserved byte-for-byte.";
    public IReadOnlyList<string> Guidelines => [
        "View the file first and copy the exact text (including whitespace) — fuzzy or partial matches fail; only line endings (CRLF vs LF) are equivalent.",
        "Widen oldText (more surrounding context) when a match is ambiguous.",
        "If oldText occurs multiple times, use replace with expectedCount instead.",
        "Same-file calls in one response run in your original order; a failed call in a file group skips the rest of that file's calls in this batch.",
    ];
    public JsonElement Parameters => Args.FileTargetSchema(
        ("path", "string", "File path to edit (required)."),
        ("oldText", "string", "Exact text to find (required, must occur exactly once; CRLF/LF-equivalent)."),
        ("newText", "string", "Replacement text (required; must be present — an explicit empty string deletes the span)."));

    /// <summary>Declare the target path without reading the file (validation first).</summary>
    public string? GetTargetPath(ToolContext context)
        => Args.ValidPath(context) is { } p ? context.ResolvePath(p) : null;

    public async ValueTask<ToolResult> ExecuteAsync(ToolContext ctx, CancellationToken ct)
    {
        // PLAN §21 (updated by docs/plans/file-tool-reliability.md): exactly one
        // CRLF/LF-equivalent literal match; encoding/BOM and untouched bytes
        // (including EOLs) survive the write; a failed validation never touches
        // the file.
        if (!Args.TryPath(ctx.Arguments, out var raw))
            return new ToolResult("tool", "edit", [new TextPart("path is required (a nonblank string).")], IsError: true);
        if (!Args.TryNonEmptyString(ctx.Arguments, "oldText", out var oldText))
            return new ToolResult("tool", "edit", [new TextPart("oldText is required (a nonempty string).")], IsError: true);
        if (!Args.TryStringPresent(ctx.Arguments, "newText", out var newText))
            return new ToolResult("tool", "edit", [new TextPart("newText is required (a present string; an explicit empty string deletes the span).")], IsError: true);

        var path = ctx.ResolvePath(raw);
        var (ok, message) = await TextFileEditor.EditAsync(path, oldText, newText, ct);
        return new ToolResult("tool", "edit", [new TextPart(message)], IsError: !ok);
    }
}


/// <summary>A grep/search tool: find files whose contents match a regex.</summary>
public sealed class GrepTool : IAgentTool
{
    public string Name => "grep";
    public string Description => "Search file contents with a regular expression (ripgrep when available, managed fallback otherwise). Results are file:line: text; relative paths resolve against the workspace.";
    public IReadOnlyList<string> Guidelines => [
        "Up to 250 matches are shown; narrow the pattern or add a file glob when the result is capped.",
        "Use the returned file:line to read only the relevant ranges.",
    ];
    public JsonElement Parameters => Args.Schema(
        ("pattern", "string", "Regular expression to search for."),
        ("path", "string", "File or directory to search (defaults to the workspace). Optional."),
        ("glob", "string", "File filter, e.g. \"*.cs\". Optional."));
    public async ValueTask<ToolResult> ExecuteAsync(ToolContext ctx, CancellationToken ct)
    {
        // PLAN §22: regex + result limits; ripgrep if it exists, else a managed
        // fallback; both normalize to 'file:line: text'.
        const int max = 250;
        var pattern = Args.Str(ctx.Arguments, "pattern");
        var target = ctx.ResolvePath(Args.Str(ctx.Arguments, "path", ""));
        var glob = Args.OptStr(ctx.Arguments, "glob");
        if (string.IsNullOrEmpty(pattern))
            return new ToolResult("tool", "grep", [new TextPart("pattern is required")], IsError: true);
        if (string.IsNullOrEmpty(target) || (!File.Exists(target) && !Directory.Exists(target)))
            return new ToolResult("tool", "grep", [new TextPart($"Path not found: {target}")], IsError: true);
        var isDir = Directory.Exists(target);
        try
        {
            var engine = FindRipgrep() is { } rg
                ? RunRipgrep(rg, pattern, target, glob, isDir, ctx.Workspace)
                : await RunManagedAsync(pattern, target, glob, isDir, ctx.Workspace, ct);
            if (engine.Count == 0)
                return new ToolResult("tool", "grep", [new TextPart("No matches.")], false);
            var more = engine.Count >= max ? "\n[showing first " + max + " matches - narrow the pattern or add a glob]" : "";
            return new ToolResult("tool", "grep", [new TextPart(string.Join("\n", engine) + more)], false);
        }
        catch (Exception ex)
        {
            return new ToolResult("tool", "grep", [new TextPart($"Grep failed: {ex.Message}")], IsError: true);
        }
    }

    /// <summary>PLAN §22: prefer ripgrep for speed on developer machines.</summary>
    private static string? FindRipgrep()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
            foreach (var cand in new[] { Path.Combine(dir, "rg.exe"), Path.Combine(dir, "rg") })
                if (File.Exists(cand)) return cand;
        return null;
    }

    private static List<string> RunRipgrep(string rg, string pattern, string target, string? glob, bool isDir, string workspace)
    {
        var psi = new ProcessStartInfo(rg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "--no-heading", "--line-number", "-m", "5000", "-e", pattern,
                   isDir && glob is not null ? "--glob" : null, isDir && glob is not null ? glob : null, "--", target })
            if (a is not null) psi.ArgumentList.Add(a);
        var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        // PLAN §22: normalize to the same 'file:line: text' shape as the managed
        // path, workspace-relative.
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l =>
            {
                var c2 = l.LastIndexOf(':');
                var c1 = c2 < 0 ? -1 : l.LastIndexOf(':', c2 - 1);
                if (c1 < 0) return l;
                var file = l[..c1];
                var line = l[(c1 + 1)..c2];
                var text = l[(c2 + 1)..];
                var normWs = workspace.Replace('\\', '/');
                file = file.Replace('\\', '/');
                if (file.StartsWith(normWs, StringComparison.OrdinalIgnoreCase))
                    file = file[normWs.Length..].TrimStart('/');
                return file + ":" + line + ": " + text;
            })
            .Where(l => l.Contains(':'))
            .ToList();
    }

    private static async Task<List<string>> RunManagedAsync(
        string pattern, string target, string? glob, bool isDir, string workspace, CancellationToken ct)
    {
        var rx = new System.Text.RegularExpressions.Regex(pattern);
        var files = isDir
            ? Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
                .Where(f => glob is null || MatchGlob(glob, Path.GetFileName(f)))
            : new[] { target };
        var matches = new List<string>();
        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var bytes = await File.ReadAllBytesAsync(file, ct);
                if (bytes.Length > 0 && bytes[0] < 32 && bytes[0] is not (9 or 10 or 13) && bytes[0] != 0) continue; // binary
                var lines = Encoding.UTF8.GetString(bytes).Split('\n');
                for (var i = 0; i < lines.Length; i++)
                    if (rx.IsMatch(lines[i]))
                    {
                        matches.Add(ToDisplay(file, workspace) + ":" + (i + 1) + ": " + lines[i].Trim());
                        if (matches.Count >= 250) return matches;
                    }
            }
            catch { /* skip unreadable */ }
        }
        return matches;
    }

    private static string ToDisplay(string file, string workspace)
        => file.StartsWith(workspace, StringComparison.OrdinalIgnoreCase)
            ? file.Substring(workspace.Length).TrimStart('\\', '/')
            : file;

    private static bool MatchGlob(string glob, string name)
    {
        var rx = "^" + System.Text.RegularExpressions.Regex.Escape(glob).Replace("\\*", ".*") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(name, rx);
    }

    private static bool TryRead(string f, out bool _)
    {
        _ = false;
        try { using var fs = File.OpenRead(f); fs.ReadByte(); return true; }
        catch { return false; }
    }
}

/// <summary>Base class for the shell-execution tools.</summary>
public abstract class ShellToolBase : IAgentTool
{
    protected abstract string ShellId { get; }
    /// <summary>
    /// Fallback prefix used when the plugin was constructed without a
    /// <see cref="ShellDetection"/> (tests, or detection unavailable).
    /// </summary>
    protected abstract IReadOnlyList<string> CommandPrefix { get; }

    private readonly ShellDetection? _detected;
    // astra-1 H: optional lightweight lifecycle tracker (registered by the
    // Tools plugin; null in standalone/test use — all calls are guarded).
    private readonly IForegroundProcessTracker? _tracker;

    protected ShellToolBase(ShellDetection? detected = null, IForegroundProcessTracker? tracker = null)
        => (_detected, _tracker) = (detected, tracker);

    public string Name => ShellId;
    public string Description => $"Run a {ShellId} command and return its output." +
        " Working directory is the session workspace; a non-zero exit is reported as [exit code: N]." +
        (_detected is { } d ? $" Backend: {d.Label}." : "");
    public IReadOnlyList<string> Guidelines => [
        "Runs non-interactively — no prompts, no TTY; pipe input with here-strings instead.",
        "Long commands may time out; split large work into smaller steps.",
    ];
    public JsonElement Parameters => Args.Schema(
        ("command", "string", "The command to run."),
        ("workdir", "string", "Working directory. Optional."),
        ("timeout_ms", "number", "Timeout in ms (default 120000). Optional."));

    /// <summary>The detected executable + argument prefix for this shell.</summary>
    protected (string FileName, IReadOnlyList<string> Args) Invocation
    {
        get
        {
            if (_detected is { } d)
                return (d.Executable, d.Arguments);

            return (CommandPrefix[0], CommandPrefix.Skip(1).ToList());
        }
    }

    public async ValueTask<ToolResult> ExecuteAsync(ToolContext ctx, CancellationToken ct)
    {
        var command = Args.Str(ctx.Arguments, "command");
        if (string.IsNullOrEmpty(command))
            return new ToolResult(Name, Name, [new TextPart("command is required")], IsError: true);
        try
        {
            var timeoutMs = Args.Int(ctx.Arguments, "timeout_ms", 120_000);
            var (fileName, args) = Invocation;
            var psi = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add(command);
            var workdir = Args.OptStr(ctx.Arguments, "workdir");
            // PLAN §25: default working directory is the session workspace; an
            // explicit workdir argument wins. PLAN §23: WSL backend owns the
            // Windows↔WSL working-dir conversion.
            var effectiveWorkdir = workdir ?? ctx.Workspace;
            if (effectiveWorkdir is not null && Directory.Exists(effectiveWorkdir))
                psi.WorkingDirectory = _detected is { IsWsl: true }
                    ? ShellDetector.ConvertToWslPath(effectiveWorkdir)
                    : effectiveWorkdir;


            using var proc = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            // PLAN §25: stream stdout/stderr live to the UI via ctx.Stream.
            // Every line the shell emits is pushed as a progressive tool.output
            // chunk so long commands (builds, tests, downloads) are watchable
            // before they finish. The same line is still accumulated into the
            // local builder so the final ToolResult carries the full output.
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (stdout) stdout.AppendLine(e.Data);
                ctx.Stream?.Emit(e.Data + "\n");
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (stderr) stderr.AppendLine(e.Data);
                ctx.Stream?.Emit("[stderr] " + e.Data + "\n");
            };
            proc.Start();
            // astra-1 H: expose the lightweight lifecycle to the Activity plugin
            // (only processes the Tools plugin started — never unrelated OS procs).
            _tracker?.Started(Name, command, effectiveWorkdir, ctx.SessionId, proc.Id);
            // Close stdin (EOF) so the shell does not wait for interactive input
            // when the host's own stdin is a long-open pipe.
            try { proc.StandardInput.Close(); } catch { }
            proc.BeginOutputReadLine();

            proc.BeginErrorReadLine();

            var exitTask = proc.WaitForExitAsync();
            var completed = await Task.WhenAny(exitTask, Task.Delay(timeoutMs, ct)) == exitTask;
            if (!completed)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                _tracker?.Finished(proc.Id, null); // killed on timeout — no exit code
                return new ToolResult(Name, Name, [new TextPart($"Timed out after {timeoutMs}ms.\n{Args.Truncate(stdout.ToString())}")], IsError: true);
            }
            await exitTask;

            var code = proc.ExitCode;
            _tracker?.Finished(proc.Id, code);
            var outp = Args.Truncate(stdout.ToString());
            var err = Args.Truncate(stderr.ToString());
            var text = $"[exit code: {code}]\n{outp}{(err.Trim().Length > 0 ? "\n[stderr]\n" + err : "")}";
            return new ToolResult(Name, Name, [new TextPart(text)], IsError: code != 0);
        }
        catch (Exception ex)
        {
            return new ToolResult(Name, Name, [new TextPart($"Command failed to start: {ex.Message}")], IsError: true);
        }
    }
}

/// <summary>Run a bash/sh command (POSIX shells).</summary>
public sealed class BashTool(ShellDetection? detected = null, IForegroundProcessTracker? tracker = null) : ShellToolBase(detected, tracker)
{
    protected override string ShellId => "bash";
    protected override IReadOnlyList<string> CommandPrefix => ["bash", "-c"];
}

/// <summary>Run a PowerShell command (Windows).</summary>
public sealed class PowerShellTool(ShellDetection? detected = null, IForegroundProcessTracker? tracker = null) : ShellToolBase(detected, tracker)
{
    protected override string ShellId => "powershell";
    protected override IReadOnlyList<string> CommandPrefix => ["pwsh", "-NoProfile", "-Command"];
}


/// <summary>
/// Resolves a command string for a specific shell into a concrete
/// <see cref="ResolvedCommand"/> (PLAN §26). Owned by the tools plugin and
/// briefly leased by the background-tasks plugin before it spawns a process.
/// </summary>
public sealed class ShellCommandResolver : IShellCommandResolver
{
    private readonly string _shellId;

    private readonly ShellDetection? _detected;
    public ShellCommandResolver(string shellId, ShellDetection? detected) { _shellId = shellId; _detected = detected; }
    public string ShellId => _shellId;
    public ValueTask<ResolvedCommand> ResolveAsync(string command, string workingDirectory, CancellationToken ct)
    {
        // PLAN §23/§26: resolve to the DETECTED executable, never an assumed name.
        var fileName = _detected is { } d ? d.Executable
            : ("bash".Equals(_shellId, StringComparison.OrdinalIgnoreCase) ? "bash" : "pwsh");
        var prefix = _detected is { } dd ? dd.Arguments
            : ("bash".Equals(_shellId, StringComparison.OrdinalIgnoreCase) ? new[] { "-c" } : new[] { "-NoProfile", "-Command" });
        var wd = _detected is { IsWsl: true } ? ShellDetector.ConvertToWslPath(workingDirectory) : workingDirectory;
        var rc = new ResolvedCommand(fileName, [.. prefix, command], wd, null);
        return ValueTask.FromResult(rc);
    }
}





/// <summary>
/// The reloadable tools plugin (PLAN §18-§26). Registers read/write/edit/grep/
/// bash/powershell into a shared <see cref="IToolRegistry"/> under id "tools".
/// </summary>
public sealed class ToolsPlugin : INetPiPlugin
{
    private ToolRegistryImpl? _registry;
    private IAgentTool[] _tools = [];
    private IDisposable[] _registrations = [];
    // astra-1 H: the lightweight foreground-process lifecycle the shell tools
    // record; exposed to the Activity plugin under id "foreground-processes".
    private ForegroundProcessTracker _tracker = new();

    public PluginInfo Info { get; } = new("netPI.Tools", "Tools", "0.1.0");

    public async ValueTask LoadAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _tracker = new ForegroundProcessTracker(context.Events);
        // PLAN §23/§24: probe the backends ONCE at load (re-probed on reload).
        // Preference: config override → PATH/native → Git Bash → MSYS → WSL.
        var bash = await ShellDetector.DetectAsync("bash", context.OwnConfig, cancellationToken);
        var pwsh = await ShellDetector.DetectAsync("powershell", context.OwnConfig, cancellationToken);
        context.Log.Information($"Bash: {bash.Label}");
        context.Log.Information($"PowerShell: {pwsh.Label}");

        _tools = [new ReadTool(), new WriteTool(), new EditTool(), new ReplaceTool(), new GrepTool(),
            new BashTool(bash, _tracker), new PowerShellTool(pwsh, _tracker)];
        _registry = new ToolRegistryImpl();
        _registrations = _tools.Select(t => _registry.Register(t)).ToArray();
        context.Services.Register<IToolRegistry>("tools", _registry);
        // astra-1 H: expose the foreground-process lifecycle to the Activity plugin.
        context.Services.Register<IForegroundProcessTracker>("foreground-processes", _tracker);
        // Shell-command resolvers (PLAN §26) — owned by the tools plugin, briefly
        // leased by the background-tasks plugin before it spawns a process. They
        // now resolve to the DETECTED executable, not an assumed name.
        context.Services.Register<IShellCommandResolver>("resolver:bash",
            new ShellCommandResolver("bash", bash));
        context.Services.Register<IShellCommandResolver>("resolver:powershell",
            new ShellCommandResolver("powershell", pwsh));
        context.Log.Information($"Tools registered: {string.Join(", ", _tools.Select(t => t.Name))}");
        await ValueTask.CompletedTask;

    }

    public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _registry = null;
        _tools = [];
        _registrations = [];
        await ValueTask.CompletedTask;
    }

    public ValueTask UnloadAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

/// <summary>Simple tool registry snapshot.</summary>
internal sealed class ToolRegistryImpl : IToolRegistry
{
    private readonly List<IAgentTool> _tools = [];

    public IDisposable Register(IAgentTool tool)
    {
        lock (_tools) _tools.Add(tool);
        return new Unregister(this, tool);
    }

    public IReadOnlyList<IAgentTool> All()
    {
        lock (_tools) return _tools.ToList();
    }

    public IAgentTool? Find(string name)
    {
        lock (_tools) return _tools.FirstOrDefault(t => t.Name == name);
    }

    private void Remove(IAgentTool tool)
    {
        lock (_tools) _tools.Remove(tool);
    }

    private sealed class Unregister(ToolRegistryImpl owner, IAgentTool tool) : IDisposable
    {
        public void Dispose() => owner.Remove(tool);
    }
}


/// <summary>
/// astra-1 H: thread-safe, bounded implementation of
/// <see cref="IForegroundProcessTracker"/>. The Tools plugin registers one; the
/// Activity plugin reads it. Only processes the Tools plugin started are tracked.
/// </summary>
public sealed class ForegroundProcessTracker : IForegroundProcessTracker
{
    /// <summary>The most recently finished processes (bounded tail).</summary>
    private const int MaxRecent = 256;

    private readonly object _gate = new();
    private readonly List<ForegroundProcessInfo> _running = [];
    private readonly List<ForegroundProcessInfo> _recent = [];
    private readonly IEventBus? _events;

    public ForegroundProcessTracker(IEventBus? events = null) => _events = events;

    public void Started(string toolShellId, string command, string? workingDirectory, string? sessionId, int processId)
    {
        var info = new ForegroundProcessInfo(toolShellId, command, workingDirectory, sessionId, processId, DateTimeOffset.UtcNow, null, null);
        lock (_gate) _running.Add(info);
        Publish(info);
    }

    public void Finished(int processId, int? exitCode)
    {
        lock (_gate)
        {
            for (int i = _running.Count - 1; i >= 0; i--)
            {
                if (_running[i].ProcessId != processId) continue;
                var done = _running[i] with
                {
                    ExitCode = exitCode,
                    ExitedAt = DateTimeOffset.UtcNow,
                };
                _running.RemoveAt(i);
                _recent.Insert(0, done); // newest first
                if (_recent.Count > MaxRecent) _recent.RemoveAt(_recent.Count - 1);
                Publish(done);
                return;
            }
            // no matching running process — a duplicate/late finish; ignore.
        }
    }

    public IReadOnlyList<ForegroundProcessInfo> Running()
    {
        lock (_gate) return _running.ToList(); // already oldest first (FIFO add)
    }

    public IReadOnlyList<ForegroundProcessInfo> Recent(int max = 50)
    {
        lock (_gate)
        {
            var take = Math.Min(max, _recent.Count);
            var outp = new ForegroundProcessInfo[take];
            for (int i = 0; i < take; i++) outp[i] = _recent[i];
            return outp;
        }
    }

    private void Publish(ForegroundProcessInfo info)
    {
        try { if (_events is not null) _ = _events.PublishAsync(new ForegroundProcessChangedEvent(info)); }
        catch { /* telemetry must not break process tracking */ }
    }
}
