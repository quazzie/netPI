using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;

namespace NetPI.Desktop;

/// <summary>
/// The overlay icon on the taskbar button: blue with the count while chats work, orange while chats wait for the user
/// (orange wins), none when idle. Colours match the web UI's running and asking dots. UI thread only.
/// </summary>
internal sealed class TaskbarBadge : IDisposable
{
    private static readonly Color Working = Color.FromArgb(0x4a, 0x7d, 0xff);
    private static readonly Color Waiting = Color.FromArgb(0xe3, 0x8a, 0x1f);

    /// <summary>Windows posts this to a window when its taskbar button exists (again, after Explorer restarts).</summary>
    public static readonly int ButtonCreatedMessage = RegisterWindowMessage("TaskbarButtonCreated");

    private readonly IntPtr _hwnd;
    private ITaskbarList3? _list;
    private IntPtr _hicon;
    private int _working, _waiting;

    public TaskbarBadge(IntPtr hwnd) => _hwnd = hwnd;

    public void Set(int working, int waiting)
    {
        _working = Math.Max(0, working);
        _waiting = Math.Max(0, waiting);
        Apply();
    }

    /// <summary>The taskbar button was (re)created: its overlay is gone, so put it back.</summary>
    public void ButtonCreated()
    {
        _list = null;
        Apply();
    }

    private void Apply()
    {
        var hicon = IntPtr.Zero;
        try
        {
            if (_list is null)
            {
                var list = (ITaskbarList3)new TaskbarList();
                list.HrInit();
                _list = list;
            }
            var count = _waiting > 0 ? _waiting : _working;
            if (count == 0)
            {
                _list.SetOverlayIcon(_hwnd, IntPtr.Zero, null);
                Release();
                return;
            }
            hicon = Draw(_waiting > 0 ? Waiting : Working, count);
            var text = _waiting > 0 && _working > 0 ? $"{_working} working, {_waiting} waiting"
                : _waiting > 0 ? $"{_waiting} waiting for you" : $"{_working} working";
            _list.SetOverlayIcon(_hwnd, hicon, text);
            Release();
            _hicon = hicon;
        }
        catch
        {
            _list = null; // a badge is a nicety: never take the window down
            // no icon handle outlives a failure: the one just drawn, and the previous one (the overlay's state is unknown now)
            if (hicon != IntPtr.Zero && hicon != _hicon) DestroyIcon(hicon);
            Release();
        }
    }

    private static IntPtr Draw(Color color, int count)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var fill = new SolidBrush(color);
            using var ring = new Pen(Color.White, 2);
            g.FillEllipse(fill, 1, 1, 29, 29);
            g.DrawEllipse(ring, 1, 1, 29, 29);
            using var font = new Font("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(count > 9 ? "9+" : count.ToString(), font, Brushes.White, new RectangleF(0, 1, 32, 31), format);
        }
        return bmp.GetHicon();
    }

    private void Release()
    {
        if (_hicon != IntPtr.Zero) DestroyIcon(_hicon);
        _hicon = IntPtr.Zero;
    }

    public void Dispose()
    {
        try { _list?.SetOverlayIcon(_hwnd, IntPtr.Zero, null); } catch { /* window gone */ }
        Release();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    private class TaskbarList { }

    // ITaskbarList3 with its bases (ITaskbarList, ITaskbarList2) flattened in vtable order; unused methods take raw pointers
    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, int flags);
        void RegisterTab(IntPtr hwndTab, IntPtr hwndMdi);
        void UnregisterTab(IntPtr hwndTab);
        void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
        void SetTabActive(IntPtr hwndTab, IntPtr hwndMdi, uint reserved);
        void ThumbBarAddButtons(IntPtr hwnd, uint count, IntPtr buttons);
        void ThumbBarUpdateButtons(IntPtr hwnd, uint count, IntPtr buttons);
        void ThumbBarSetImageList(IntPtr hwnd, IntPtr imageList);
        void SetOverlayIcon(IntPtr hwnd, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string? description);
    }
}
