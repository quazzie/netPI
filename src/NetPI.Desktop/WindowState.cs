using System.Text.Json;

namespace NetPI.Desktop;

/// <summary>Remembered window placement and WebView zoom (~/.netpi/window.json).</summary>
internal sealed class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 1440;
    public int Height { get; set; } = 920;
    public bool Maximized { get; set; }
    /// <summary>WebView zoom factor (Ctrl + wheel, Ctrl + / −, the Settings dialog).</summary>
    public double Zoom { get; set; } = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static WindowPlacement? Load(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            return JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(file), Json);
        }
        catch
        {
            return null;
        }
    }

    public void Save(string file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, file, overwrite: true);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>The saved bounds if they are still (mostly) visible on a connected screen.</summary>
    public Rectangle? VisibleBounds()
    {
        if (Width < 400 || Height < 300) return null;
        var r = new Rectangle(X, Y, Width, Height);
        foreach (var screen in Screen.AllScreens)
        {
            var visible = Rectangle.Intersect(screen.WorkingArea, r);
            // title bar area must be reachable
            if (visible.Width >= 200 && visible.Height >= 100 && screen.WorkingArea.Contains(new Point(r.X + Math.Min(120, r.Width / 2), r.Y + 10)))
                return r;
        }
        return null;
    }
}
