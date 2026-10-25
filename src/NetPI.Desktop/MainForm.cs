using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using NetPI.Host;

namespace NetPI.Desktop;

/// <summary>
/// The desktop window: starts the NetPI server in-process, shows the web UI in WebView2,
/// remembers its size/position and bridges a few native features (folder picker, external links).
/// </summary>
internal sealed class MainForm : Form
{
    private static readonly Color Background = Color.FromArgb(12, 13, 16);

    private readonly string[] _args;
    private readonly string _home;
    private readonly string _placementFile;
    private readonly WebView2 _web;
    private readonly Label _status;
    private NetPiServer? _server;
    private Task<NetPiServer>? _starting; // while the host starts: a close then waits for it and stops it
    private RemoteConnection? _remote; // connect mode: the server on the other machine (no local server at all)
    private string _origin = "";
    private IDisposable? _captureRpc;
    private IDisposable? _zoomRpc;
    private double _zoom = 1;
    private EventWaitHandle? _activate;
    private RegisteredWaitHandle? _activateWait;
    private bool _closing;
    private bool _closeReady;
    private bool _shown;

    private readonly bool _successor;

    /// <param name="successor">Started by the NetPI before it to restart: its token comes along (<see cref="Successor"/>).</param>
    public MainForm(string[] args, bool successor = false)
    {
        _args = args;
        _successor = successor;
        _home = HostKernel.ResolveHome(Arg("--home"));
        _placementFile = Path.Combine(_home, "window.json");

        Text = "netPI";
        BackColor = Background;
        ForeColor = Color.FromArgb(161, 167, 177);
        MinimumSize = new Size(720, 480);
        KeyPreview = false;
        try
        {
            using var s = typeof(MainForm).Assembly.GetManifestResourceStream("netpi.ico");
            if (s is not null) Icon = new Icon(s);
        }
        catch
        {
            // default icon
        }

        RestorePlacement();

        _status = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 11f),
            ForeColor = ForeColor,
            BackColor = Background,
            Text = "Starting netPI…",
        };
        _web = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Background,
            Visible = false,
        };
        Controls.Add(_web);
        Controls.Add(_status);
    }

    private string? Arg(string name)
    {
        var i = Array.FindIndex(_args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < _args.Length ? _args[i + 1] : null;
    }

    // ---------------------------------------------------------------- window placement

    private void RestorePlacement()
    {
        var p = WindowPlacement.Load(_placementFile);
        _zoom = p?.Zoom is >= 0.25 and <= 5 ? p.Zoom : 1;
        StartPosition = FormStartPosition.Manual;
        var bounds = p?.VisibleBounds();
        if (bounds is { } b)
        {
            Bounds = b;
        }
        else
        {
            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 1000);
            var w = Math.Min(p?.Width ?? 1440, area.Width - 40);
            var h = Math.Min(p?.Height ?? 920, area.Height - 40);
            Bounds = new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
        }
        if (p?.Maximized == true) WindowState = FormWindowState.Maximized;
    }

    private void SavePlacement()
    {
        if (!IsHandleCreated) return;
        var normal = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        if (normal.Width <= 0 || normal.Height <= 0) return;
        new WindowPlacement
        {
            X = normal.X,
            Y = normal.Y,
            Width = normal.Width,
            Height = normal.Height,
            Maximized = WindowState == FormWindowState.Maximized,
            Zoom = _zoom,
        }.Save(_placementFile);
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        SavePlacement();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        // maximize/restore via the caption buttons does not raise ResizeEnd
        if (_shown && WindowState != FormWindowState.Minimized) SavePlacement();
    }

    // ---------------------------------------------------------------- native look

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            var on = 1;
            // DWMWA_USE_IMMERSIVE_DARK_MODE (20; 19 on older Windows 10 builds)
            if (DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(Handle, 19, ref on, sizeof(int));
            // DWMWA_CAPTION_COLOR (Windows 11): COLORREF 0x00BBGGRR
            var caption = (Background.B << 16) | (Background.G << 8) | Background.R;
            DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
        }
        catch
        {
            // not available on this Windows version
        }
    }

    // ---------------------------------------------------------------- startup

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _shown = true;
        ListenForActivation();
        // Connect mode: no local server at all — the window is a client of a NetPI server on another machine.
        var connect = Arg("--connect");
        if (connect is not null)
        {
            _remote = RemoteConnection.Resolve(connect, Arg("--token"), _home);
            if (_remote is null)
            {
                ShowStatus($"No server to connect to.\n\nStart one with\n  netpi-server --listen all --token <token>\nand connect with\n  NetPI.exe --connect http://<server>:<port> --token <token>\n(a connection saved in {Path.Combine(_home, RemoteConnection.FileName)} is used for a bare --connect)");
                return;
            }
            ShowStatus($"Connecting to {_remote.Host}…");
            try
            {
                await InitWebViewRemoteAsync();
            }
            catch (WebView2RuntimeNotFoundException)
            {
                ShowStatus($"netPI is connecting to http://{_remote.Host}\n\nThe WebView2 runtime is missing, so it was opened in your browser.");
                OpenExternal(_remote.LaunchUrl);
                MessageBox.Show(this,
                    "The Microsoft Edge WebView2 Runtime is not installed, so netPI opened in your default browser instead.\n\n" +
                    "Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 to use the desktop window.",
                    "netPI", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                ShowStatus($"Connecting to http://{_remote.Host}\n\nThe embedded browser failed: {ex.Message}");
                OpenExternal(_remote.LaunchUrl);
            }
            return;
        }
        try
        {
            var options = new NetPiServerOptions { Desktop = true, ConsoleLogging = false, Home = _home, Restart = RestartAsync };
            var passed = Environment.GetEnvironmentVariable(Successor.TokenVariable);
            if (_successor) options.Token = passed;
            Environment.SetEnvironmentVariable(Successor.TokenVariable, null); // never on to what this process starts (git, LSP, browser)
            if (int.TryParse(Arg("--port"), out var port)) options.Port = port;
            _starting = Task.Run(() => NetPiServer.StartAsync(options));
            _server = await _starting;
        }
        catch (Exception ex)
        {
            _starting = null;
            if (_closing) return; // closed while starting: the shutdown has nothing to stop
            ShowStatus("netPI could not start:\n\n" + ex.Message);
            // another NetPI on this home says so in its message; anything unexpected shows its stack too
            MessageBox.Show(this, ex.Message.StartsWith("NetPI is already running", StringComparison.Ordinal) ? ex.Message : ex.ToString(),
                "netPI failed to start", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (_closing) return; // closed while starting: the shutdown stops the host
        try
        {
            await InitWebViewAsync(_server);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowStatus($"netPI is running at {_server.BaseUrl}\n\nThe WebView2 runtime is missing, so it was opened in your browser.\nClose this window to stop netPI.");
            OpenExternal(_server.LaunchUrl);
            MessageBox.Show(this,
                "The Microsoft Edge WebView2 Runtime is not installed, so netPI opened in your default browser instead.\n\n" +
                "Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 to use the desktop window.",
                "netPI", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            ShowStatus($"netPI is running at {_server.BaseUrl}\n\nThe embedded browser failed: {ex.Message}");
            OpenExternal(_server.LaunchUrl);
        }
    }

    private void ShowStatus(string text)
    {
        _status.Text = text;
        _status.Visible = true;
        _web.Visible = false;
    }

    private async Task InitWebViewAsync(NetPiServer server)
    {
        var core = await CreateWebViewAsync();

        var origin = new Uri(server.BaseUrl).GetLeftPart(UriPartial.Authority);
        _origin = origin;
        core.NavigationStarting += (_, e) =>
        {
            if (IsOwnOrigin(e.Uri, origin) || e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
            e.Cancel = true;
            OpenExternal(e.Uri);
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (IsOwnOrigin(e.Uri, origin)) core.Navigate(e.Uri);
            else OpenExternal(e.Uri);
        };
        core.DocumentTitleChanged += (_, _) =>
        {
            var t = core.DocumentTitle;
            Text = string.IsNullOrWhiteSpace(t) || t.StartsWith("127.0.0.1", StringComparison.Ordinal) ? "netPI" : t;
        };
        // Zoom: remembered in window.json, whether it changes through Ctrl + wheel / Ctrl + ± or desktop.zoom.
        // The first navigation resets the WebView to 100%, so the saved factor is applied once the page has
        // loaded, and zoom changes before that (the reset itself) are not persisted.
        var zoomRestored = false;
        _web.ZoomFactorChanged += (_, _) =>
        {
            if (!zoomRestored) return;
            _zoom = _web.ZoomFactor;
            SavePlacement();
        };
        _zoomRpc ??= server.Rpc.Register("desktop.zoom",
            (req, ct) => ZoomAsync(req.Prop("factor") is { ValueKind: System.Text.Json.JsonValueKind.Number } f ? f.GetDouble() : null, ct),
            "The desktop window's zoom (desktop app only): { factor? (0.5–3) } → { factor }");
        var renderFailures = 0; // consecutive render-process failures; a page that loads resets it
        core.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess) renderFailures = 0;
            if (!_web.Visible)
            {
                _web.Visible = true;
                _status.Visible = false;
                _web.Focus();
            }
            if (zoomRestored || !e.IsSuccess) return;
            // after the control is shown (a factor set on a hidden controller does not stick), and posted so the
            // navigation's own reset has run first
            BeginInvoke(new Action(() =>
            {
                _web.ZoomFactor = _zoom;
                zoomRestored = true;
            }));
        };
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind is not (CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)) return;
            if (++renderFailures > 3)
            {
                // a page that crashes on every load would reload in a tight loop: stop and say so
                BeginInvoke(new Action(() => ShowStatus($"The page keeps crashing, so netPI stopped reloading it.\n\nnetPI is running at {server.BaseUrl}\nClose this window and start netPI again, or open it in a browser.")));
                return;
            }
            // a pause, so a crash on load does not spin
            _ = Task.Delay(1000).ContinueWith(_ => { try { BeginInvoke(new Action(core.Reload)); } catch (InvalidOperationException) { } }, TaskScheduler.Default);
        };
        core.WebMessageReceived += OnWebMessage;
        _captureRpc ??= server.Rpc.Register("desktop.capture", (req, ct) => CaptureAsync(req.Int("maxWidth") ?? 1600, ct),
            "Screenshot of the NetPI window as the user sees it (desktop app only): { maxWidth? } → { mediaType, data, width, height }");

        core.Navigate(server.LaunchUrl);
    }

    /// <summary>
    /// Connect mode: the same window against a server on the other machine. No local server, so no
    /// <c>desktop.capture</c>/<c>desktop.zoom</c> (the screenshot tool degrades, zoom is the WebView's per-site one);
    /// the bridge keeps only what is local (toasts, external links, the taskbar badge) — every file feature is the
    /// server's, and a local path is never handed to it.
    /// </summary>
    private async Task InitWebViewRemoteAsync()
    {
        var remote = _remote!;
        var core = await CreateWebViewAsync();

        var origin = new Uri(remote.Url).GetLeftPart(UriPartial.Authority);
        _origin = origin;
        core.NavigationStarting += (_, e) =>
        {
            if (IsOwnOrigin(e.Uri, origin) || e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
            e.Cancel = true;
            OpenExternal(e.Uri);
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (IsOwnOrigin(e.Uri, origin)) core.Navigate(e.Uri);
            else OpenExternal(e.Uri);
        };
        core.DocumentTitleChanged += (_, _) =>
        {
            var t = core.DocumentTitle;
            Text = string.IsNullOrWhiteSpace(t) || t.StartsWith("127.0.0.1", StringComparison.Ordinal) ? "netPI" : t;
        };
        var renderFailures = 0; // consecutive render-process failures; a page that loads resets it
        core.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess) renderFailures = 0;
            if (!_web.Visible)
            {
                _web.Visible = true;
                _status.Visible = false;
                _web.Focus();
            }
            // the page is loaded now: tell it it is a remote client (its file features are the server's; the shell keeps
            // toasts and external links local). A reload re-posts it.
            if (e.IsSuccess) Post(new JsonObject { ["type"] = "clientMode", ["remote"] = true });
        };
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind is not (CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)) return;
            if (++renderFailures > 3)
            {
                BeginInvoke(new Action(() => ShowStatus($"The page keeps crashing, so netPI stopped reloading it.\n\nnetPI is connecting to {remote.Url}\nClose this window and start it again, or open it in a browser.")));
                return;
            }
            _ = Task.Delay(1000).ContinueWith(_ => { try { BeginInvoke(new Action(core.Reload)); } catch (InvalidOperationException) { } }, TaskScheduler.Default);
        };
        core.WebMessageReceived += OnWebMessage;

        core.Navigate(remote.LaunchUrl);
    }

    /// <summary>The WebView2 environment and control, ready for a page (local server or remote).</summary>
    private async Task<CoreWebView2> CreateWebViewAsync()
    {
        var userData = Path.Combine(_home, "webview");
        Directory.CreateDirectory(userData);
        var env = await CoreWebView2Environment.CreateAsync(null, userData, new CoreWebView2EnvironmentOptions());
        await _web.EnsureCoreWebView2Async(env);
        var core = _web.CoreWebView2;

        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = true; // connect mode: the per-site zoom (no desktop.zoom there)
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        return core;
    }

    // ---------------------------------------------------------------- zoom (desktop.zoom)

    /// <summary>Sets the WebView zoom when <paramref name="factor"/> is given; returns the current one. UI thread.</summary>
    private Task<object?> ZoomAsync(double? factor, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            BeginInvoke(new Action(() =>
            {
                try
                {
                    if (factor is { } f && double.IsFinite(f))
                    {
                        _web.ZoomFactor = Math.Clamp(f, 0.5, 3);
                        _zoom = _web.ZoomFactor;   // a programmatic set does not raise ZoomFactorChanged; persist it here
                        SavePlacement();
                    }
                    tcs.TrySetResult(new { factor = Math.Round(_web.ZoomFactor, 2) });
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }));
        }
        catch (Exception ex) { tcs.TrySetException(ex); }
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
    }

    // ---------------------------------------------------------------- window capture (desktop.capture)

    /// <summary>PNG of the WebView as shown, scaled down to <paramref name="maxWidth"/>; runs on the UI thread.</summary>
    private Task<object?> CaptureAsync(int maxWidth, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            BeginInvoke(new Action(async () =>
            {
                try
                {
                    var core = _web.CoreWebView2 ?? throw new InvalidOperationException("the window is not ready");
                    if (WindowState == FormWindowState.Minimized) throw new RpcException("unavailable", "The NetPI window is minimized; restore it to capture it.");
                    using var ms = new MemoryStream();
                    await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                    var (png, width, height) = Downscale(ms.ToArray(), maxWidth);
                    tcs.TrySetResult(new { mediaType = "image/png", data = Convert.ToBase64String(png), width, height });
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }));
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex); // the window is closing
        }
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
    }

    private static (byte[] Png, int Width, int Height) Downscale(byte[] png, int maxWidth)
    {
        using var input = new MemoryStream(png);
        using var src = new Bitmap(input);
        if (maxWidth <= 0 || src.Width <= maxWidth) return (png, src.Width, src.Height);
        var height = Math.Max(1, (int)Math.Round(src.Height * (maxWidth / (double)src.Width)));
        using var dst = new Bitmap(maxWidth, height);
        using (var g = Graphics.FromImage(dst))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, maxWidth, height);
        }
        using var output = new MemoryStream();
        dst.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        return (output.ToArray(), maxWidth, height);
    }

    // ---------------------------------------------------------------- web ⇄ native bridge

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // These reach the folder picker, the shell and a notification, so only the app's own document may post them: a
        // page that reached the window anyway (a frame, a devtools page) can ask for all three.
        if (!IsOwnOrigin(e.Source, _origin)) return;
        JsonObject? msg;
        try
        {
            msg = JsonNode.Parse(e.WebMessageAsJson) as JsonObject;
        }
        catch (JsonException)
        {
            return;
        }
        var type = Str(msg, "type");
        var id = Str(msg, "id");
        if (_remote is { } remote)
        {
            // Connect mode: everything file-related is the server's — no native dialogs, no local path ever leaves this
            // machine (an "open" or "reveal" downloads the server file and works on the local copy). What stays local:
            // toasts, external links and the taskbar badge.
            switch (type)
            {
                case "pickFolder":
                case "pickFile":
                    // the UI uses the server's fs.dirs tree in connect mode; answer "nothing chosen" so a stray caller
                    // never hangs
                    Post(new JsonObject { ["type"] = type == "pickFolder" ? "pickFolderResult" : "pickFileResult", ["id"] = id, ["path"] = null });
                    break;
                case "openExternal":
                    if (Str(msg, "url") is { } url) OpenExternal(url);
                    break;
                case "revealPath":
                    if (Str(msg, "path") is { } p) _ = RevealRemoteAsync(remote, p);
                    break;
                case "openFile":
                    if (Str(msg, "name") is { } name && Str(msg, "data") is { } data) OpenLocalFile(name, data);
                    break;
                case "revealFile":
                    if (Str(msg, "name") is { } n && Str(msg, "data") is { } d) RevealLocalFile(n, d);
                    break;
                case "notify":
                    ShowNotification(Str(msg, "title"), Str(msg, "body"), Str(msg, "sessionId"));
                    break;
                case "taskbar":
                    _badge ??= new TaskbarBadge(Handle);
                    _badge.Set(Int(msg, "working"), Int(msg, "waiting"));
                    break;
            }
            return;
        }
        switch (type)
        {
            case "pickFolder":
                var path = PickFolder(Str(msg, "initial"), Str(msg, "title"));
                Post(new JsonObject { ["type"] = "pickFolderResult", ["id"] = id, ["path"] = path });
                break;
            case "pickFile":
                Post(new JsonObject { ["type"] = "pickFileResult", ["id"] = id, ["path"] = PickFile(Str(msg, "initial"), Str(msg, "title")) });
                break;
            case "openExternal":
                if (Str(msg, "url") is { } url2) OpenExternal(url2);
                break;
            case "revealPath":
                if (Str(msg, "path") is { } p2) RevealInExplorer(p2);
                break;
            case "notify":
                ShowNotification(Str(msg, "title"), Str(msg, "body"), Str(msg, "sessionId"));
                break;
            case "taskbar":
                _badge ??= new TaskbarBadge(Handle);
                _badge.Set(Int(msg, "working"), Int(msg, "waiting"));
                break;
        }
    }

    // ---------------------------------------------------------------- connect mode: the server's files, on this machine

    /// <summary>Where a downloaded server file lands: "open" and "reveal" work on the local copy, never on the server's path.</summary>
    private string DownloadsDir => Path.Combine(_home, "downloads");

    private string SaveDownload(string name, byte[] bytes)
    {
        var dir = DownloadsDir;
        Directory.CreateDirectory(dir);
        name = SanitizeFileName(name);
        var path = Path.Combine(dir, name);
        var i = 1;
        while (File.Exists(path))
        {
            var dot = Path.GetExtension(name);
            path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)} ({i++}){dot}");
        }
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(s) ? "file" : s;
    }

    /// <summary><c>revealPath</c> in connect mode: download the server file and reveal the local copy in Explorer. A folder
    /// (or anything the server refuses) has no local copy: nothing is shown, not an error.</summary>
    private async Task RevealRemoteAsync(RemoteConnection remote, string path)
    {
        var file = await remote.ReadFileAsync(path, CancellationToken.None).ConfigureAwait(false);
        if (file is not { } f) return;
        try { RevealInExplorer(SaveDownload(f.Name, f.Bytes)); }
        catch { }
    }

    /// <summary><c>openFile</c> in connect mode: the UI downloaded the server file (files.read) and hands over the bytes —
    /// save them and open the local copy with its default program.</summary>
    private void OpenLocalFile(string name, string base64)
    {
        try
        {
            var path = SaveDownload(name, Convert.FromBase64String(base64));
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch { }
    }

    /// <summary><c>revealFile</c> in connect mode: save the downloaded bytes and reveal the local copy in Explorer.</summary>
    private void RevealLocalFile(string name, string base64)
    {
        try { RevealInExplorer(SaveDownload(name, Convert.FromBase64String(base64))); }
        catch { }
    }

    private TaskbarBadge? _badge;

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == TaskbarBadge.ButtonCreatedMessage) _badge?.ButtonCreated();
    }

    private static int Int(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;

    private static string? Str(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>
    /// Whether <paramref name="url"/> really is the app's own origin. A prefix test is not enough: in
    /// <c>http://127.0.0.1:7431@evil.example/</c> the string starts with the origin while the host is evil.example, so a
    /// model answer carrying such a link would navigate the window itself to that page — with no address bar, and with
    /// the bridge messages below it to post. The scheme, host and port are therefore compared as parsed, and any user
    /// info (the <c>@</c> above) means another host.
    /// </summary>
    private static bool IsOwnOrigin(string? url, string origin)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var own)) return false;
        return string.IsNullOrEmpty(uri.UserInfo)
            && string.Equals(uri.Scheme, own.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, own.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == own.Port;
    }

    private void Post(JsonObject message)
    {
        try
        {
            _web.CoreWebView2?.PostWebMessageAsJson(message.ToJsonString());
        }
        catch
        {
            // webview gone
        }
    }

    private string? PickFolder(string? initial, string? title)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = string.IsNullOrWhiteSpace(title) ? "Choose a folder" : title,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial)) dialog.InitialDirectory = initial;
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
    }

    private string? PickFile(string? initial, string? title)
    {
        using var dialog = new OpenFileDialog
        {
            Title = string.IsNullOrWhiteSpace(title) ? "Choose a file" : title,
            CheckFileExists = true,
            Filter = "All files (*.*)|*.*",
        };
        if (!string.IsNullOrWhiteSpace(initial))
        {
            if (File.Exists(initial)) { dialog.InitialDirectory = Path.GetDirectoryName(initial); dialog.FileName = Path.GetFileName(initial); }
            else if (Directory.Exists(initial)) dialog.InitialDirectory = initial;
        }
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    private static void OpenExternal(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is not ("http" or "https" or "mailto")) return;
        try
        {
            using var started = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // no default browser
        }
    }

    private static void RevealInExplorer(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch
        {
            // ignore
        }
    }

    // ---------------------------------------------------------------- single instance activation

    private void ListenForActivation()
    {
        try
        {
            _activate = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ActivateEventName);
            _activateWait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) =>
            {
                if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(BringToFrontNow));
            }, null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch
        {
            // activation of an existing window is a nicety
        }
    }

    private void BringToFrontNow()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        Activate();
        BringToFront();
    }

    // ---------------------------------------------------------------- notifications (the web UI asks: "notify")

    private NotifyIcon? _tray;
    private string? _notifySession;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Hwnd;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    /// <summary>
    /// A Windows notification and a flashing taskbar button, only while the window is not the active one. The notification
    /// is a tray icon's balloon, which Windows 10 and 11 show as a toast (no package needed); clicking it brings the
    /// window up and opens the chat. The tray icon goes away once the window is active again.
    /// </summary>
    private void ShowNotification(string? title, string? body, string? sessionId)
    {
        if (ActiveForm == this && WindowState != FormWindowState.Minimized) return; // the user is looking at NetPI
        _notifySession = sessionId;
        if (_tray is null)
        {
            _tray = new NotifyIcon { Icon = Icon, Text = "netPI" };
            _tray.BalloonTipClicked += (_, _) => OpenFromNotification();
            _tray.MouseClick += (_, _) => OpenFromNotification();
        }
        _tray.Visible = true;
        _tray.ShowBalloonTip(10_000, string.IsNullOrWhiteSpace(title) ? "netPI" : title,
            string.IsNullOrWhiteSpace(body) ? " " : body, ToolTipIcon.None);
        // flash the taskbar button until the window comes to the front (FLASHW_TRAY | FLASHW_TIMERNOFG)
        var flash = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Hwnd = Handle, Flags = 0x2 | 0xC };
        FlashWindowEx(ref flash);
    }

    private void OpenFromNotification()
    {
        BringToFrontNow();
        if (_notifySession is { } sessionId) Post(new JsonObject { ["type"] = "openSession", ["sessionId"] = sessionId });
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (_tray is not null) _tray.Visible = false; // seen
    }

    // ---------------------------------------------------------------- shutdown

    /// <summary>The host restarts by itself (an update waits and nothing goes on): the next NetPI starts, this window closes.</summary>
    private Task RestartAsync()
    {
        var server = _server ?? throw new InvalidOperationException("NetPI is not running");
        Successor.Start(_args, server.Token);
        BeginInvoke(new Action(Close));
        return Task.CompletedTask;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SavePlacement();
        if ((_server is not null || _starting is not null) && !_closeReady)
        {
            e.Cancel = true;
            if (_closing) return;
            _closing = true;
            Hide();
            _ = ShutdownAsync();
            return;
        }
        base.OnFormClosing(e);
    }

    private async Task ShutdownAsync()
    {
        try
        {
            _activateWait?.Unregister(null);
            _tray?.Dispose(); // else its icon lingers in the tray until the mouse passes over it
            _tray = null;
            _badge?.Dispose();
            _badge = null;
            _web.Dispose();
            var server = _server;
            if (server is null && _starting is not null)
            {
                try { server = await _starting; } // closed while the host was still starting: it is stopped like a running one
                catch { }
            }
            if (server is not null)
                await Task.WhenAny(server.StopAsync(), Task.Delay(TimeSpan.FromSeconds(8)));
        }
        catch
        {
            // closing anyway
        }
        finally
        {
            _server = null;
            _closeReady = true;
            BeginInvoke(new Action(Close));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _activate?.Dispose();
            _tray?.Dispose();
            _web.Dispose();
            _status.Dispose();
        }
        base.Dispose(disposing);
    }
}
