using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.AgentsMd;

/// <summary>
/// Project instruction files in the system prompt (section order 500): the global <c>~/.netpi/AGENTS.md</c>, then the
/// first existing file of <c>agentsMd.fileNames</c> (default <c>["AGENTS.md", "CLAUDE.md"]</c>) in every directory from
/// the filesystem root down to the session cwd, then <c>agentsMd.extraFiles</c>. Each file is capped at 32KB.
/// <para>RPC: <c>agentsmd.list { sessionId }</c> → <c>[{ path, bytes, scope }]</c> (scope: global | project | extra).</para>
/// </summary>
[NetPiPlugin("netpi.agentsmd", Name = "AGENTS.md", Description = "Global and project AGENTS.md / CLAUDE.md instructions in the system prompt", Order = 41)]
public sealed class AgentsMdPlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        var loader = new AgentsMdLoader(context);
        context.Services.Register<IPromptSection>(new AgentsMdSection(loader));
        context.Rpc.Register("agentsmd.list", (req, _) =>
        {
            var sessionId = req.Required("sessionId");
            var session = context.Sessions.GetSession(sessionId) ?? throw new RpcException("not_found", $"No session {sessionId}");
            var cwd = context.Sessions.GetCwd(session);
            var arr = new JsonArray();
            foreach (var f in loader.Discover(cwd))
                arr.Add(new JsonObject { ["path"] = f.Path, ["bytes"] = f.Bytes, ["scope"] = f.Scope });
            return Task.FromResult<object?>(arr);
        }, "Instruction files that apply to a session: { sessionId } → [{ path, bytes, scope }]");
        return Task.CompletedTask;
    }
}

internal sealed record InstructionFile(string Path, string Scope, long Bytes);

internal sealed class AgentsMdLoader(IPluginContext ctx)
{
    public const int MaxBytes = 32 * 1024;
    public static readonly string[] DefaultFileNames = ["AGENTS.md", "CLAUDE.md"];

    private sealed record CacheEntry(DateTime MtimeUtc, long Length, string Content);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(PathComparer);

    private static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private List<string> Setting(string path, IReadOnlyList<string> fallback)
    {
        try
        {
            var node = ctx.Settings.GetNode(path);
            if (node is JsonArray a)
                return a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
                    .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).ToList();
            if (node is JsonValue jv && jv.TryGetValue<string>(out var one) && !string.IsNullOrWhiteSpace(one)) return [one.Trim()];
        }
        catch { }
        return [.. fallback];
    }

    /// <summary>Instruction files in prompt order: global, root → cwd, extra.</summary>
    public List<InstructionFile> Discover(string cwd)
    {
        var result = new List<InstructionFile>();
        var seen = new HashSet<string>(PathComparer);

        void Add(string path, string scope)
        {
            try
            {
                var full = Path.GetFullPath(path);
                var fi = new FileInfo(full);
                if (!fi.Exists || !seen.Add(full)) return;
                result.Add(new InstructionFile(full, scope, fi.Length));
            }
            catch { /* unreadable path */ }
        }

        Add(ctx.Paths.GlobalAgentsMd, "global");

        var names = Setting("agentsMd.fileNames", DefaultFileNames);
        var chain = new List<string>();
        try
        {
            for (var dir = new DirectoryInfo(cwd); dir is not null; dir = dir.Parent)
            {
                foreach (var name in names)
                {
                    var candidate = Path.Combine(dir.FullName, name);
                    if (File.Exists(candidate)) { chain.Add(candidate); break; }
                }
            }
        }
        catch (Exception ex)
        {
            ctx.Logger.LogDebug(ex, "AGENTS.md discovery failed for {Cwd}", cwd);
        }
        chain.Reverse(); // root → leaf
        foreach (var p in chain) Add(p, "project");

        foreach (var extra in Setting("agentsMd.extraFiles", []))
        {
            var path = extra.StartsWith("~/", StringComparison.Ordinal) || extra.StartsWith("~\\", StringComparison.Ordinal)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), extra[2..])
                : Path.IsPathRooted(extra) ? extra : Path.Combine(cwd, extra);
            Add(path, "extra");
        }
        return result;
    }

    /// <summary>File content capped at <see cref="MaxBytes"/> (with a note), cached by path + mtime + size.</summary>
    public string? Read(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            if (_cache.TryGetValue(path, out var hit) && hit.MtimeUtc == fi.LastWriteTimeUtc && hit.Length == fi.Length) return hit.Content;

            string content;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var len = (int)Math.Min(fs.Length, MaxBytes);
                var buf = new byte[len];
                var read = 0;
                while (read < len)
                {
                    var n = fs.Read(buf, read, len - read);
                    if (n == 0) break;
                    read += n;
                }
                content = Encoding.UTF8.GetString(buf, 0, read);
                if (content.Length > 0 && content[0] == '﻿') content = content[1..];
                content = content.Replace("\r\n", "\n");
                if (fi.Length > MaxBytes)
                {
                    content = content.TrimEnd('�');
                    content += $"\n\n[Truncated: this file is {fi.Length / 1024} KB; only the first {MaxBytes / 1024} KB are included. Read the file for the rest.]";
                }
            }
            _cache[path] = new CacheEntry(fi.LastWriteTimeUtc, fi.Length, content);
            return content;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogDebug(ex, "Cannot read {Path}", path);
            return null;
        }
    }
}

internal sealed class AgentsMdSection(AgentsMdLoader loader) : IPromptSection
{
    public string Id => "agents-md";
    public int Order => 500;

    public ValueTask<string?> RenderAsync(PromptContext context, CancellationToken ct)
    {
        var files = loader.Discover(context.Cwd);
        var sb = new StringBuilder();
        foreach (var f in files)
        {
            var content = loader.Read(f.Path);
            if (string.IsNullOrWhiteSpace(content)) continue;
            if (sb.Length == 0)
                sb.Append("# Project instructions\n\nInstruction files that apply here (global first, then from the filesystem root down to the working directory; later files are more specific). Follow them.\n");
            sb.Append("\n## ").Append(f.Path);
            if (f.Scope == "global") sb.Append(" (global)");
            sb.Append("\n\n").Append(content.Trim()).Append('\n');
        }
        return ValueTask.FromResult<string?>(sb.Length == 0 ? null : sb.ToString().TrimEnd());
    }
}
