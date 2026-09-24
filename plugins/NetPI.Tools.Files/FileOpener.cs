using System.Diagnostics;
using System.Text.RegularExpressions;

namespace NetPI.Tools.Files;

/// <summary>
/// <c>files.open</c>: open a path from the chat or the file tree with the operating system, the way a double click in the
/// file manager would. Folders open in the file manager; scripts open for editing (the "edit" verb) rather than running;
/// executables and installers are only revealed. Relative paths resolve against the session's working directory, like
/// the file tools; <c>file://</c> URLs and a trailing <c>:line[:col]</c> or <c>#L…</c> are accepted.
/// </summary>
internal static partial class FileOpener
{
    private static readonly HashSet<string> Executable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".msi", ".msp", ".msix", ".appx", ".scr", ".cpl", ".lnk", ".pif", ".application", ".appref-ms",
        ".gadget", ".msc", ".hta", ".reg", ".jar", ".url", ".scf", ".inf", ".sys", ".dll",
    };
    private static readonly HashSet<string> Script = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".py", ".pyw", ".sh", ".bash",
    };

    /// <summary>What to do: <c>folder</c>, <c>open</c>, <c>edit</c> (scripts) or <c>reveal</c> (executables).</summary>
    internal static (string Path, string Action) Decide(IPluginContext context, string root, string raw)
    {
        raw = raw.Trim().Trim('"', '\'', '`', '<', '>');
        if (raw.Length == 0) throw new RpcException("bad_request", "No path given");
        if (raw.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(raw, UriKind.Absolute, out var fileUri)) raw = fileUri.LocalPath;
        var resolver = new ToolContext { SessionId = "", AgentId = "", CallId = "", Cwd = root, Services = context.Services, Events = context.Events };

        string? full = null;
        foreach (var candidate in Candidates(raw))
        {
            var p = resolver.ResolvePath(candidate);
            if (File.Exists(p) || Directory.Exists(p)) { full = p; break; }
        }
        if (full is null) throw new RpcException("not_found", $"Not found: {resolver.ResolvePath(raw)}");
        if (Directory.Exists(full)) return (full, "folder");
        var ext = Path.GetExtension(full);
        if (Executable.Contains(ext) || (!OperatingSystem.IsWindows() && IsUnixExecutable(full) && !Script.Contains(ext))) return (full, "reveal");
        return (full, Script.Contains(ext) ? "edit" : "open");
    }

    /// <summary>The path as given, then without a line suffix (<c>:12</c>, <c>:12:3</c>, <c>#L12</c>, <c>#L12-L20</c>).</summary>
    private static IEnumerable<string> Candidates(string raw)
    {
        yield return raw;
        var noAnchor = Anchor().Replace(raw, "");
        if (noAnchor != raw) yield return noAnchor;
        var noLine = LineSuffix().Replace(noAnchor, "");
        if (noLine != noAnchor) yield return noLine;
        if (raw.Contains('%'))
        {
            string decoded;
            try { decoded = Uri.UnescapeDataString(noLine); } catch (UriFormatException) { yield break; }
            if (decoded != noLine) yield return decoded;
        }
    }

    [GeneratedRegex(@"#L\d+(-L?\d+)?$")]
    private static partial Regex Anchor();

    [GeneratedRegex(@"(?<=[^:\\/]):\d+(:\d+)?$")]
    private static partial Regex LineSuffix();

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static bool IsUnixExecutable(string path)
    {
        try { return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0; }
        catch (Exception) { return false; }
    }

    internal static void Run(string path, string action)
    {
        switch (action)
        {
            case "reveal":
                Reveal(path);
                break;
            case "edit":
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "edit" })?.Dispose(); }
                catch (Exception) { Reveal(path); } // no "edit" verb for this type: show it instead of running it
                break;
            default:
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
                break;
        }
    }

    private static void Reveal(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();
            return;
        }
        var dir = Path.GetDirectoryName(path) ?? path;
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true })?.Dispose();
    }
}
