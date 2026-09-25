using Microsoft.Extensions.Logging;

namespace NetPI.Host;

/// <summary>
/// What build.ps1 left for this start while an earlier NetPI ran from the app folder, installed before anything loads
/// or serves it. A running exe or DLL can be renamed, not overwritten: the build moved the host files it replaced into
/// <c>.old</c> (free once that NetPI exited) and put the new ones in place. What the running NetPI must not load yet
/// waits in <c>.pending</c>: plugins built against contracts it didn't have (hot-reloaded, they would run on the old
/// ones), and with <c>build -NextStart</c> every changed plugin and the web UI. A folder that can't be installed (in
/// use) stays pending for the next start.
/// </summary>
internal static class PendingBuild
{
    public static void Install(string appDir, ILogger log)
    {
        var old = Path.Combine(appDir, ".old");
        var pending = Path.Combine(appDir, ".pending");
        if (Directory.Exists(pending))
        {
            var plugins = Path.Combine(pending, "plugins");
            if (Directory.Exists(plugins))
            {
                foreach (var dir in Directory.GetDirectories(plugins))
                    Replace(dir, Path.Combine(appDir, "plugins", Path.GetFileName(dir)), appDir, log);
                if (!Directory.EnumerateFileSystemEntries(plugins).Any()) PathUtil.TryDeleteDirectory(plugins);
            }
            var web = Path.Combine(pending, "wwwroot");
            if (Directory.Exists(web)) Replace(web, Path.Combine(appDir, "wwwroot"), appDir, log);
            if (!Directory.EnumerateFileSystemEntries(pending).Any()) PathUtil.TryDeleteDirectory(pending);
        }
        PathUtil.TryDeleteDirectory(old); // fails while another NetPI from this folder still uses its files
    }

    /// <summary>
    /// Moves <paramref name="source"/> to <paramref name="target"/>. An existing target goes into .old first (moved, not
    /// deleted: a folder is never left half deleted) and comes back if the move fails.
    /// </summary>
    private static void Replace(string source, string target, string appDir, ILogger log)
    {
        var rel = Path.GetRelativePath(appDir, target);
        string? aside = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (Directory.Exists(target))
            {
                aside = Path.Combine(appDir, ".old", $"start-{Environment.ProcessId}", rel);
                Directory.CreateDirectory(Path.GetDirectoryName(aside)!);
                Directory.Move(target, aside);
            }
            Directory.Move(source, target);
            log.LogInformation("Installed {Folder}, built while an earlier NetPI ran", rel);
        }
        catch (Exception ex)
        {
            if (aside is not null && !Directory.Exists(target))
                try { Directory.Move(aside, target); } catch { /* reported below */ }
            log.LogWarning("Could not install the pending build of {Folder}: {Error}", rel, ex.Message);
        }
    }
}
