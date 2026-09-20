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
}

/// <summary>A file-read tool.</summary>
public sealed class ReadTool : IAgentTool
{
    public string Name => "read";
    public string Description => "Read the contents of a text file at the given path.";
    public JsonElement Parameters => Args.Schema(
        ("path", "string", "File path to read."),
        ("offset", "number", "Line number to start reading from (1-indexed). Optional."),
        ("limit", "number", "Maximum number of lines to read. Optional."));

    public async ValueTask<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct)
    {
        var path = Args.Str(arguments, "path");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return Error($"File not found: {path}");
        try
        {
            var offset = Args.Int(arguments, "offset", 1);
            var limit = Args.Int(arguments, "limit", int.MaxValue);
            var lines = await File.ReadAllLinesAsync(path, ct);
            var slice = lines.Skip(Math.Max(0, offset - 1)).Take(limit).ToList();
            var text = string.Join("\n", slice);
            return Ok(Args.Truncate(text));
        }
        catch (Exception ex)
        {
            return Error($"Read failed: {ex.Message}");
        }
    }

    private static ToolResult Ok(string text) => new("tool", "read", [new TextPart(text)], false);
    private static ToolResult Error(string message) => new("tool", "read", [new TextPart(message)], IsError: true);
}

/// <summary>A file-write tool (creates or overwrites).</summary>
public sealed class WriteTool : IAgentTool
{
    public string Name => "write";
    public string Description => "Write text content to a file, creating it (and parent dirs) if needed and overwriting if it exists.";
    public JsonElement Parameters => Args.Schema(
        ("path", "string", "File path to write."),
        ("content", "string", "Full file content to write."));

    public async ValueTask<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct)
    {
        var path = Args.Str(arguments, "path");
        var content = Args.Str(arguments, "content");
        if (string.IsNullOrEmpty(path))
            return new ToolResult("tool", "write", [new TextPart("path is required")], IsError: true);
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
/// with <c>new</c>.
/// </summary>
public sealed class EditTool : IAgentTool
{
    public string Name => "edit";
    public string Description => "Replace a unique exact text span in a file. old must occur exactly once.";
    public JsonElement Parameters => Args.Schema(
        ("path", "string", "File path to edit."),
        ("old", "string", "Exact text to find (must occur exactly once)."),
        ("new", "string", "Replacement text."));

    public async ValueTask<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct)
    {
        var path = Args.Str(arguments, "path");
        var old = Args.Str(arguments, "old");
        var newText = Args.Str(arguments, "new");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return new ToolResult("tool", "edit", [new TextPart($"File not found: {path}")], IsError: true);
        try
        {
            var content = await File.ReadAllTextAsync(path, ct);
            var count = content.Split(old, StringSplitOptions.None).Length - 1;
            if (count != 1)
                return new ToolResult("tool", "edit", [new TextPart($"Expected exactly 1 occurrence of 'old', found {count}.")], IsError: true);
            var idx = content.IndexOf(old, StringComparison.Ordinal);
            var updated = idx < 0 ? content : content.Substring(0, idx) + newText + content.Substring(idx + old.Length);
            await File.WriteAllTextAsync(path, updated, ct);
            return new ToolResult("tool", "edit", [new TextPart($"Edited {path}.")], false);
        }
        catch (Exception ex)
        {
            return new ToolResult("tool", "edit", [new TextPart($"Edit failed: {ex.Message}")], IsError: true);
        }
    }
}

/// <summary>A grep/search tool: find files whose contents match a regex.</summary>
public sealed class GrepTool : IAgentTool
{
    public string Name => "grep";
    public string Description => "Search file contents for a regex pattern within a directory (recursive).";
    public JsonElement Parameters => Args.Schema(
        ("pattern", "string", "Regular expression to search for."),
        ("path", "string", "Directory to search (defaults to current dir)."),
        ("include", "string", "Optional glob filter, e.g. \"*.ts\". Optional."));

    public async ValueTask<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct)
    {
        var pattern = Args.Str(arguments, "pattern");
        var dir = Args.Str(arguments, "path", Environment.CurrentDirectory);
        var include = Args.OptStr(arguments, "include");
        if (string.IsNullOrEmpty(pattern))
            return new ToolResult("tool", "grep", [new TextPart("pattern is required")], IsError: true);
        if (!Directory.Exists(dir))
            return new ToolResult("tool", "grep", [new TextPart($"Directory not found: {dir}")], IsError: true);
        try
        {
            var rx = new System.Text.RegularExpressions.Regex(pattern);
            var matches = new List<string>();
            var allFiles = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => include is null || System.IO.Path.GetFileName(f).EndsWith(include.Replace("*", ""), StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var file in allFiles)
            {
                try
                {
                    var text = await File.ReadAllTextAsync(file, ct);
                    foreach (var line in text.Split('\n'))
                        if (rx.IsMatch(line)) matches.Add($"{file}: {line.Trim()}");
                }
                catch { /* skip unreadable/binary */ }
            }
            return new ToolResult("tool", "grep",
                [new TextPart(matches.Count == 0 ? "No matches." : Args.Truncate(string.Join("\n", matches)))], false);
        }
        catch (Exception ex)
        {
            return new ToolResult("tool", "grep", [new TextPart($"Grep failed: {ex.Message}")], IsError: true);
        }
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

    protected ShellToolBase(ShellDetection? detected = null) => _detected = detected;

    public string Name => ShellId;
    public string Description => $"Run a {ShellId} command and return its output." +
        (_detected is { } d ? $" Backend: {d.Label}." : "");
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

    public async ValueTask<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct)
    {
        var command = Args.Str(arguments, "command");
        if (string.IsNullOrEmpty(command))
            return new ToolResult(Name, Name, [new TextPart("command is required")], IsError: true);
        try
        {
            var timeoutMs = Args.Int(arguments, "timeout_ms", 120_000);
            var (fileName, args) = Invocation;
            var psi = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add(command);
            var workdir = Args.OptStr(arguments, "workdir");
            if (workdir is not null)
                // PLAN §23: the WSL backend owns Windows↔WSL working-dir conversion.
                psi.WorkingDirectory = _detected is { IsWsl: true }
                    ? ShellDetector.ConvertToWslPath(workdir)
                    : workdir;


            using var proc = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (stdout) stdout.AppendLine(e.Data); } };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (stderr) stderr.AppendLine(e.Data); } };
            proc.Start();
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
                return new ToolResult(Name, Name, [new TextPart($"Timed out after {timeoutMs}ms.\n{Args.Truncate(stdout.ToString())}")], IsError: true);
            }
            await exitTask;

            var code = proc.ExitCode;
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
public sealed class BashTool(ShellDetection? detected = null) : ShellToolBase(detected)
{
    protected override string ShellId => "bash";
    protected override IReadOnlyList<string> CommandPrefix => ["bash", "-c"];
}

/// <summary>Run a PowerShell command (Windows).</summary>
public sealed class PowerShellTool(ShellDetection? detected = null) : ShellToolBase(detected)
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

    public PluginInfo Info { get; } = new("netPI.Tools", "Tools", "0.1.0");

    public async ValueTask LoadAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        // PLAN §23/§24: probe the backends ONCE at load (re-probed on reload).
        // Preference: config override → PATH/native → Git Bash → MSYS → WSL.
        var bash = await ShellDetector.DetectAsync("bash", context.OwnConfig, cancellationToken);
        var pwsh = await ShellDetector.DetectAsync("powershell", context.OwnConfig, cancellationToken);
        context.Log.Information($"Bash: {bash.Label}");
        context.Log.Information($"PowerShell: {pwsh.Label}");

        _tools = [new ReadTool(), new WriteTool(), new EditTool(), new GrepTool(),
            new BashTool(bash), new PowerShellTool(pwsh)];
        _registry = new ToolRegistryImpl();
        _registrations = _tools.Select(t => _registry.Register(t)).ToArray();
        context.Services.Register<IToolRegistry>("tools", _registry);
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
