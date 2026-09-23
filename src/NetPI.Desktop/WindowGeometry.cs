using System.Text.Json;

namespace NetPI.Desktop;

/// <summary>
/// The shell's persisted window geometry (size + maximized state): one JSON
/// file under LocalApplicationData, next to the WebView2 data dir.
/// Best-effort everywhere — a failed read yields null (defaults apply), a
/// failed write is ignored; geometry must never block startup or shutdown.
/// </summary>
public sealed class WindowGeometry
{
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }

    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "netPI.Desktop", "window.json");

    /// <summary>
    /// Fit <paramref name="saved"/> into [minimum, the largest connected
    /// working area] — never below the minimum, never beyond every screen on
    /// this machine. Returns <c>clamped</c> = true when the value changed,
    /// i.e. the saved size came from a larger screen and must not be written
    /// back as-is.
    /// </summary>
    public static (Size Size, bool Clamped) Fit(Size saved, Size minimum, Size maxWork)
    {
        var w = Math.Clamp(saved.Width, minimum.Width, Math.Max(minimum.Width, maxWork.Width));
        var h = Math.Clamp(saved.Height, minimum.Height, Math.Max(minimum.Height, maxWork.Height));
        return (new Size(w, h), (w, h) != (saved.Width, saved.Height));
    }

    /// <summary>Read the persisted geometry; null when the file is missing,
    /// malformed, or non-positive (the defaults apply).</summary>
    public static WindowGeometry? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var g = JsonSerializer.Deserialize<WindowGeometry>(File.ReadAllText(path));
            return g is { Width: > 0, Height: > 0 } ? g : null;
        }
        catch { return null; }
    }

    /// <summary>Write the persisted geometry atomically (tmp + move), so a
    /// crash mid-write cannot leave a torn file behind.</summary>
    public static void Save(string path, WindowGeometry g)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(g));
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* best-effort — saving the size must never block shutdown */ }
    }
}
