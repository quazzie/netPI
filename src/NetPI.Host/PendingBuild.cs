using System.Text;
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
    /// <summary>The lock build.ps1 holds while it installs (Enter-InstallLock): the same file, opened the same way.</summary>
    public const string LockName = ".install.lock";
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(60);

    public static void Install(string appDir, ILogger log) => Install(appDir, log, LockWait);

    /// <param name="lockWait">How long to wait for a publish that holds the install lock before leaving the pending build for the next start.</param>
    internal static void Install(string appDir, ILogger log, TimeSpan lockWait)
    {
        var old = Path.Combine(appDir, ".old");
        var pending = Path.Combine(appDir, ".pending");
        if (!Directory.Exists(pending) && !Directory.Exists(old)) return;
        // build.ps1 -Publish holds this lock while it installs, and a start in the middle of one would move a half-filled
        // .pending/plugins/<Name> into place, or delete .old while the publish fills it. The wait is bounded: a start
        // never hangs on a publish, and what cannot be installed now waits for the next start.
        var installLock = TakeInstallLock(appDir, lockWait, log);
        if (installLock is null)
        {
            log.LogWarning("The install lock {Lock} could not be taken: what waits in .pending stays for the next start", Path.Combine(appDir, LockName));
            return;
        }
        try
        {
            var stranded = false;
            if (Directory.Exists(pending))
            {
                var plugins = Path.Combine(pending, "plugins");
                if (Directory.Exists(plugins))
                {
                    foreach (var dir in Directory.GetDirectories(plugins))
                        stranded |= !Replace(dir, Path.Combine(appDir, "plugins", Path.GetFileName(dir)), appDir, log);
                    if (!Directory.EnumerateFileSystemEntries(plugins).Any()) PathUtil.TryDeleteDirectory(plugins);
                }
                var web = Path.Combine(pending, "wwwroot");
                if (Directory.Exists(web)) stranded |= !Replace(web, Path.Combine(appDir, "wwwroot"), appDir, log);
                if (!Directory.EnumerateFileSystemEntries(pending).Any()) PathUtil.TryDeleteDirectory(pending);
            }
            // .old holds what the publish replaced while an earlier NetPI ran, free now - unless an install above left
            // the only copy of a folder there: then it stays for this start, and the log says where it is.
            if (stranded) log.LogWarning("Keeping {Old}: it holds the only copy of a folder that could be neither installed nor put back", old);
            else PathUtil.TryDeleteDirectory(old); // fails while another NetPI from this folder still uses its files
        }
        finally
        {
            installLock.Dispose();
            try { File.Delete(Path.Combine(appDir, LockName)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the next taker treats it as stale after a minute */ }
        }
    }

    /// <summary>
    /// The lock file: created new and held open without sharing for as long as the install runs, deleted after. A second
    /// taker (build.ps1, or a NetPI starting under a publish) cannot create it until then. One a publisher left behind
    /// when it died is taken over after a minute, as the script does. Null when it could not be taken within <paramref name="wait"/>.
    /// </summary>
    private static FileStream? TakeInstallLock(string appDir, TimeSpan wait, ILogger log)
    {
        var path = Path.Combine(appDir, LockName);
        var deadline = Environment.TickCount64 + (long)wait.TotalMilliseconds;
        var waiting = false;
        while (true)
        {
            try
            {
                var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                try
                {
                    fs.Write(Encoding.UTF8.GetBytes($"pid {Environment.ProcessId}, since {DateTimeOffset.Now:o}"));
                    fs.Flush(true);
                }
                catch (IOException) { /* who holds it is a courtesy to a reader; the open handle is the lock */ }
                return fs;
            }
            catch (DirectoryNotFoundException ex)
            {
                log.LogWarning("Cannot create the install lock {Lock}: {Error}", path, ex.Message);
                return null;
            }
            catch (IOException)
            {
                // it exists: held by a live publisher (no sharing), or left by one that died
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromMinutes(1)) File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* gone meanwhile, or still held: the next attempt tells */ }
                if (Environment.TickCount64 >= deadline) return null;
                if (!waiting)
                {
                    waiting = true;
                    log.LogInformation("Waiting for the publish that holds {Lock}", path);
                }
                Thread.Sleep(500);
            }
            catch (UnauthorizedAccessException ex)
            {
                log.LogWarning("Cannot create the install lock {Lock}: {Error}", path, ex.Message);
                return null;
            }
        }
    }

    /// <summary>
    /// Moves <paramref name="source"/> to <paramref name="target"/>. An existing target goes into .old first (moved, not
    /// deleted: a folder is never left half deleted) and comes back if the move fails. False when it could not come
    /// back: the installed version is then only in .old, which this start must not delete.
    /// </summary>
    private static bool Replace(string source, string target, string appDir, ILogger log)
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
            return true;
        }
        catch (Exception ex)
        {
            if (aside is not null && !Directory.Exists(target))
            {
                try { Directory.Move(aside, target); }
                catch (Exception restore)
                {
                    log.LogError("Could not install the pending build of {Folder}: {Error}. The installed version could not be put back either ({RestoreError}): it is in {Aside}",
                        rel, ex.Message, restore.Message, aside);
                    return false;
                }
            }
            log.LogWarning("Could not install the pending build of {Folder}: {Error}", rel, ex.Message);
            return true;
        }
    }
}
