using System.Diagnostics;
using System.Text.RegularExpressions;

namespace NetPI.Tools.Files;

/// <summary>
/// <c>files.open</c>: open a path from the chat or the file tree with the operating system. <para>
/// Two modes, chosen by <c>user</c> — whether the user deliberately picked this file themselves (a double click in the
/// file tree, a chat file link they confirmed):
/// <list type="bullet">
/// <item><c>user: true</c>: exactly what a double click in the file manager does. Every file opens with its default
/// program, executables and scripts included; a file nothing is associated with gets the operating system's "choose a
/// program" dialog.</item>
/// <item>without it (the default): folders open in the file manager; text and code (any extension) open for editing in an
/// editor (the "edit" verb) rather than running; a small allowlist of passive viewers (images without svg, pdf) opens
/// with the default program; everything else — unknown binaries, Office documents, rdp/iso/vhd, add-ins, installers — is
/// only revealed, never launched. This is for callers showing a path the model wrote: a click there must not become a
/// launch vector.</item>
/// </list>
/// </para>
/// <para>Either way: relative paths resolve against the session's working directory, like the file tools; <c>file://</c>
/// URLs and a trailing <c>:line[:col]</c> or <c>#L…</c> are accepted. A network path (<c>\\server\share</c>) is
/// refused, and a path outside the session's workspace comes back as <c>confirm</c> instead of opening, so the caller
/// asks the user first — that question stands in both modes.</para>
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

    /// <summary>
    /// The only binary types whose default program is a passive viewer and is therefore safe to launch. Everything else
    /// that is not text (Office with or without macros, rdp, iso/vhd, chm, jnlp, xll/wll add-ins, …) is revealed, not
    /// opened — an allowlist, not a blocklist of the known-bad.
    /// </summary>
    private static readonly HashSet<string> Viewable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".avif", ".tif", ".tiff", ".pdf",
    };

    /// <summary>What to do: <c>folder</c>, <c>edit</c> (text/code), <c>open</c> (the default program), <c>reveal</c>
    /// (everything else) or <c>confirm</c> (outside the workspace: the caller asks the user, then calls again with
    /// <paramref name="confirmed"/>).</summary>
    internal static (string Path, string Action) Decide(IPluginContext context, string root, string raw, bool confirmed = false, bool user = false)
    {
        raw = raw.Trim().Trim('"', '\'', '`', '<', '>');
        if (raw.Length == 0) throw new RpcException("bad_request", "No path given");
        if (raw.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(raw, UriKind.Absolute, out var fileUri)) raw = fileUri.LocalPath;
        // A network path is refused before the path is even probed: resolving one makes Windows authenticate against the
        // other machine with the user's credentials, and a model that read such a path off a page can ask for that
        // behind a link the user believes is just text.
        if (raw.StartsWith(@"\\", StringComparison.Ordinal) || raw.StartsWith("//", StringComparison.Ordinal))
            throw new RpcException("bad_request", $"Not opened: {raw} is a network path (copy the file here first)");
        var resolver = new ToolContext { SessionId = "", AgentId = "", CallId = "", Cwd = root, Services = context.Services, Events = context.Events };

        string? full = null;
        foreach (var candidate in Candidates(raw))
        {
            var p = resolver.ResolvePath(candidate);
            if (File.Exists(p) || Directory.Exists(p)) { full = p; break; }
        }
        if (full is null) throw new RpcException("not_found", $"Not found: {resolver.ResolvePath(raw)}");
        // Outside the workspace the session works in: nothing is opened until the user has said so.
        if (!confirmed && !IsUnder(full, root)) return (full, "confirm");
        if (Directory.Exists(full)) return (full, "folder");
        // The user picked this file themselves, so it is opened the way a double click in the file manager opens it: the
        // default program for every kind of file. Nothing here has to know which kinds are safe to launch, because the
        // caller has already put a question in front of the user.
        if (user) return (full, "open");
        var ext = Path.GetExtension(full);
        if (Executable.Contains(ext) || (!OperatingSystem.IsWindows() && IsUnixExecutable(full) && !Script.Contains(ext))) return (full, "reveal");
        // A text file — any code, config or script extension — opens in an editor: the editor shows it, it does not run it.
        if (!TextCodec.IsBinaryFile(full)) return (full, "edit");
        // A binary is launched only when its default program is a known passive viewer; any other binary is revealed.
        return (full, Viewable.Contains(ext) ? "open" : "reveal");
    }

    /// <summary>Whether <paramref name="path"/> is the root itself or below it (the comparison follows the platform's
    /// case rules, and anything that cannot be resolved is not inside).</summary>
    private static bool IsUnder(string path, string root)
    {
        try
        {
            var rel = Path.GetRelativePath(Path.GetFullPath(root), path);
            return !Path.IsPathRooted(rel) && !rel.StartsWith("..", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
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
