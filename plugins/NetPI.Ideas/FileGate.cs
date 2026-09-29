using System.Text;

namespace NetPI.Ideas;

/// <summary>
/// The boundary every Ideas write goes through: an exclusive lock on the data file, held by the OS, plus a write that
/// is atomic or does not happen at all.
/// <para>
/// The lock is a sibling file (<c>.&lt;name&gt;.lock</c>) opened without sharing. It is what a per-plugin
/// <c>SemaphoreSlim</c> cannot do: a hot reload is a swap — the new plugin instance starts while the old one is still
/// serving — so two stores with two sets of locks are briefly live on the same file, and their read-modify-write can
/// interleave and lose an idea. The OS lock lives in the handle, so it is released when the handle is closed (also
/// after a crash), never forgotten. The host already refuses a second NetPI on one home (src/NetPI.Host/HomeLock.cs);
/// this does not rely on that, and it serializes against anything else that opens the same lock file.
/// </para>
/// <para>
/// What it does <b>not</b> do: an editor that ignores the lock is not coordinated. Writes stay atomic, so such an
/// editor can lose its change to our rename (its file is replaced) but never the other way round, and the last valid
/// file is always on disk. Content that really conflicts is caught by the revision check in <c>ideas.update</c>, not by
/// the lock.
/// </para>
/// </summary>
internal sealed class FileGate
{
    /// <summary>How long to wait for the lock before the caller gets an error (short: every holder is a local file write).</summary>
    private const int LockAttempts = 50;
    private static readonly TimeSpan LockStep = TimeSpan.FromMilliseconds(40);
    private const int MoveAttempts = 8;

    /// <summary>The paths this async flow already holds. Re-entering one is not a deadlock: the outer hold is real.</summary>
    private static readonly AsyncLocal<Scope?> Held = new();

    private sealed class Scope
    {
        public string? Key;
    }

    /// <summary>The lock file of a data file.</summary>
    public static string LockPath(string path)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        var name = System.IO.Path.GetFileName(path);
        return System.IO.Path.Combine(dir ?? ".", name.StartsWith('.') ? name + ".lock" : "." + name + ".lock");
    }

    /// <summary>
    /// Run <paramref name="body"/> holding the exclusive lock on <paramref name="path"/>. Throws <see cref="IOException"/>
    /// (after a bounded wait) when the lock is still held — the caller reports it, nothing is written. A nested call on
    /// the same path inside <paramref name="body"/> reuses this hold instead of waiting for it.
    /// </summary>
    public async Task<T> WithFileAsync<T>(string path, Func<CancellationToken, Task<T>> body, CancellationToken ct)
    {
        var key = Normalize(path);
        var parent = Held.Value;
        var scope = new Scope { Key = key };
        Held.Value = scope; // set in this frame, so a nested acquire in `body` sees it
        try
        {
            if (parent is { } outer && outer.Key == key) return await body(ct).ConfigureAwait(false); // ours already
            using var hold = await AcquireAsync(key, ct).ConfigureAwait(false);
            return await body(ct).ConfigureAwait(false);
        }
        finally
        {
            Held.Value = parent;
        }
    }

    /// <summary>The OS lock itself (an open handle with no sharing).</summary>
    private static async Task<IDisposable> AcquireAsync(string path, CancellationToken ct)
    {
        var lockPath = LockPath(path);
        Exception? last = null;
        for (var attempt = 0; attempt < LockAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(lockPath)!);
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                try
                {
                    stream.SetLength(0);
                    using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
                    writer.Write(Environment.ProcessId);
                    stream.Flush();
                }
                catch { /* the pid is a nicety; the handle is the lock */ }
                return stream;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                await Task.Delay(LockStep, ct).ConfigureAwait(false);
            }
        }
        throw new IOException($"{path} is locked by another NetPI window or a running plugin swap ({last?.Message}). Nothing was written.");
    }

    /// <summary>
    /// Write the file through a temporary in the same folder and rename over it: a reader sees either the old file or
    /// the new one, never half of one. A rename that fails (an editor holds the file without sharing the delete) is
    /// retried briefly and then reported — the previous content is <b>never</b> overwritten in place, which is what
    /// could truncate it when the write is interrupted.
    /// </summary>
    public static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var full = System.IO.Path.GetFullPath(path);
        var dir = System.IO.Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        // One temporary name per write: two plugin instances (a reload swap) never share it.
        var tmp = System.IO.Path.Combine(dir, $".{System.IO.Path.GetFileName(full)}.{Ids.Short(8)}.tmp");
        try
        {
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
            Exception? last = null;
            for (var attempt = 0; attempt < MoveAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try { File.Move(tmp, full, overwrite: true); return; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    last = ex;
                    await Task.Delay(20 * (attempt + 1), ct).ConfigureAwait(false);
                }
            }
            throw new IOException($"{full} could not be replaced ({last?.Message}). The previous file is unchanged.");
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private static string Normalize(string path) => System.IO.Path.GetFullPath(path);
}
