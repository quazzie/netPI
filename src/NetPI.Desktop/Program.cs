using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NetPI.Desktop;

/// <summary>
/// netPI desktop shell: launches the netPI host (which serves the Svelte UI on
/// Kestrel :5173) and hosts that page in a WebView2 control. The WebView2
/// runtime is Edge's application folder — no separate runtime install.
/// </summary>
public static class Program
{
    private static Process? _host;
    // astra-1 P0.5: the host's stdout/stderr drain pumps, tracked so shutdown can
    // await them — the exit code is only trustworthy once the child's output has
    // fully drained, and a wedged pump must not be silently abandoned.
    private static Task[]? _pumps;

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var form = new MainForm();
        Application.Run(form);
    }

    private sealed class MainForm : Form
    {
        private readonly WebView2 _view;
        private const int Port = 5173;
        private static readonly string Url = $"http://127.0.0.1:{Port}";

        // Window geometry persistence: the shell reopens at the size the user
        // last used (maximized state included). A saved size is clamped to the
        // largest connected screen so a huge saved window can never restore
        // entirely off every monitor.
        private Size _lastWindowedSize = new(1200, 800);
        private Size _appliedRestoredSize = new(1200, 800);
        private bool _restoreClamped;

        /// <summary>Shown while the host boots; auto-navigates once /bootstrap answers.</summary>
        private static readonly string WaitingPage =
            @"""
<!doctype html><html><head><meta charset='utf-8'><style>
body{font-family:system-ui,sans-serif;background:#0e1116;color:#c9d1d9;display:flex;align-items:center;justify-content:center;height:100vh;margin:0}
.box{text-align:center} h1{font-weight:600;letter-spacing:.02em} p{color:#8b949e}
.dot{animation:p 1.4s infinite ease-in-out} @keyframes p{0%,80%,100%{opacity:.25}40%{opacity:1}}
</style></head><body><div class='box'><h1>netPI</h1>
<p>starting host…<span class='dot'>●</span><span class='dot' style='animation-delay:.2s'>●</span><span class='dot' style='animation-delay:.4s'>●</span></p>
<script>let t=setInterval(async()=>{try{const r=await fetch('/bootstrap');if(r.ok){location.replace('__URL__');}}catch(e){}},1500);
setTimeout(()=>{clearInterval(t);document.querySelector('p').textContent='host is slow to start — keep the window open, it will load when ready.';},60000);
</script></div></body></html>
            """
            .Replace("__URL__", Url);

        public MainForm()
        {
            Trace(Path.Combine(AppContext.BaseDirectory, "host-launch.log"), "stage=form-ctor");
            Text = "netPI";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(900, 640);
            Size = new Size(1200, 800);
            RestoreWindowState();

            _view = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_view);

            _view.CoreWebView2InitializationCompleted += async (_, e) =>
            {
                if (!e.IsSuccess)
                {
                    MessageBox.Show("WebView2 failed to initialize.\n\nInstall Microsoft Edge or the WebView2 Runtime.", "netPI");
                    Close();
                    return;
                }
                // Native bridge for desktop-only affordances. The Web UI remains
                // one Svelte/WebView surface; WinForms only supplies OS dialogs.
                _view.CoreWebView2!.WebMessageReceived += OnWebMessageReceived;

                // No Electron-style shell here: map new-window intents
                // (target=_blank, e.g. the /api/file viewer) to the OS.
                _view.CoreWebView2!.NewWindowRequested += OnNewWindowRequested;

                // Show a waiting page while the host boots; it polls /bootstrap
                // and reloads into the app once the host is up.
                _view.CoreWebView2.NavigateToString(WaitingPage);
                try
                {
                    await StartHostAsync();
                    _view.CoreWebView2.Navigate(Url);
                }
                catch (Exception ex)
                {
                    Trace(Path.Combine(AppContext.BaseDirectory, "host-launch.log"), $"stage=failed {ex.Message}");
                    var r = MessageBox.Show($"netPI host did not become ready:\n{ex.Message}\n\nKeep waiting (it may still be starting), or close?", "netPI", MessageBoxButtons.OKCancel);
                    if (r == DialogResult.OK) _ = TryNavigateAgainAsync();
                    else Close();
                }
            };

            // Fully async — no blocking .Result/.Wait on the STA thread (that
            // deadlocks WebView2's UI-context completion). Fire-and-forget.
            _ = InitWebViewAsync();
            FormClosed += (_, _) => KillHost();
            FormClosing += (_, _) => SaveWindowStateOnClose();
            SizeChanged += OnSizeChanged;
        }

        private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true; // never open a second window; route to the OS
            var urlStr = e.Uri;
            if (string.IsNullOrEmpty(urlStr)) return;
            try
            {
                var target = ResolveNewWindowTarget(new Uri(urlStr));
                Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Trace(Path.Combine(AppContext.BaseDirectory, "host-launch.log"), $"new-window failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Maps a new-window URL to something the OS opens: an in-app
        /// /api/file viewer URL becomes the referenced path (opened in the
        /// default app, or Explorer for a folder -- relative against the
        /// project root, mirroring the host CWD fallback); anything else is
        /// the URL itself, opened in the default browser.
        /// </summary>
        private static string ResolveNewWindowTarget(Uri url)
        {
            if (url.AbsolutePath.EndsWith("/api/file", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var part in url.Query.TrimStart('?').Split('&'))
                {
                    var eq = part.IndexOf('=');
                    if (eq > 0 && part[..eq] == "path" && eq + 1 < part.Length)
                    {
                        var raw = Uri.UnescapeDataString(part[(eq + 1)..]);
                        return Path.IsPathRooted(raw)
                            ? raw
                            : Path.Combine(FindProjectRoot(AppContext.BaseDirectory) ?? AppContext.BaseDirectory, raw);
                    }
                }
            }
            return url.ToString();
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "native.pickFiles")
                    return;

                var requestId = root.TryGetProperty("requestId", out var reqEl)
                    ? reqEl.GetString() ?? string.Empty
                    : string.Empty;

                using var dialog = new OpenFileDialog
                {
                    Multiselect = true,
                    CheckFileExists = true,
                    CheckPathExists = true,
                    Title = "Add files to netPI context",
                    Filter = "All files (*.*)|*.*",
                };

                var paths = dialog.ShowDialog(this) == DialogResult.OK
                    ? dialog.FileNames
                    : Array.Empty<string>();

                _view.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new
                {
                    type = "native.filesPicked",
                    requestId,
                    paths,
                }));
            }
            catch
            {
                // Ignore malformed/unrecognized page messages; the browser UI
                // falls back to its workspace picker when the bridge is absent.
            }
        }

        private async Task InitWebViewAsync()
        {
            var trace = Path.Combine(AppContext.BaseDirectory, "host-launch.log");
            try
            {
                var dataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "netPI.Desktop", "wv2");
                Directory.CreateDirectory(dataDir);
                // browserExecutableFolder = null → use the installed WebView2 Runtime
                // (EdgeWebView/Application). Pointing at the full Edge app folder makes
                // CreateAsync hang.
                var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
                Trace(trace, "stage=env-created");
                await _view.EnsureCoreWebView2Async(env);
                Trace(trace, "stage=initialized");
                // The host boots before the WebView is shown (see the
                // initialization-completed handler) — nothing page-side to hook here.
            }
            catch (Exception ex)
            {
                Trace(trace, $"stage=init-failed {ex.Message}");
                MessageBox.Show($"WebView2 unavailable: {ex.Message}\n\nInstall Microsoft Edge or the WebView2 Runtime.", "netPI");
                Close();
            }
        }

        /// <summary>
        /// After a timeout: keep polling and navigate as soon as the host opens
        /// its port — a slow cold AiProxy must not leave the user on an error box.
        /// </summary>
        private async Task TryNavigateAgainAsync()
        {
            for (var i = 0; i < 300 && !PortOpen(Port); i++) await Task.Delay(1000);
            if (PortOpen(Port))
            {
                await Task.Run(() => Invoke((Action)(() =>
                {
                    _view.CoreWebView2?.Navigate(Url);
                })));
            }
            else
            {
                await Task.Run(() => Invoke((Action)(() =>
                {
                    MessageBox.Show("The netPI host never opened its port. See ~/.netpi/logs.", "netPI");
                    Close();
                })));
            }
        }
        /// <summary>Locate a Chromium-based browser's folder for WebView2.</summary>
        private static string? BrowserFolder()
        {
            var edge = @"C:\Program Files (x86)\Microsoft\Edge\Application";
            var chrome = @"C:\Program Files\Google\Chrome\Application";
            foreach (var root in new[] { edge, chrome })
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root).OrderByDescending(Directory.GetLastWriteTime))
                    if (File.Exists(Path.Combine(dir, "msedge.exe")) || File.Exists(Path.Combine(dir, "chrome.exe")))
                        return dir;
            }
            return null;
        }

        /// <summary>Start the bundled host, wait for its Kestrel port.</summary>
        private async Task StartHostAsync()
        {
            var trace = Path.Combine(AppContext.BaseDirectory, "host-launch.log");
            File.WriteAllText(trace, $"stage=enter time={DateTime.Now:HH:mm:ss}");
            var baseDir = AppContext.BaseDirectory;
            var stagingDir = Path.Combine(baseDir, "host");
            var runtimeHome = Environment.GetEnvironmentVariable("NETPI_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".netpi");
            if (PortOpen(Port))
            {
                // A TCP listener alone is not proof it is OUR netPI host — probe
                // its identity (PLAN §49 / astra-1 P0.3): an open port may belong
                // to anything else on the machine.
                var existing = await ProbeIdentityAsync(trace);
                if (!existing.HasValue)
                    throw new InvalidOperationException(
                        $"Port {Port} is in use by a process that does not answer like a netPI host. " +
                        "Stop it (or run the host elsewhere) and try again.");
                var (runningBuild, _) = existing.Value;
                var pendingBuild = ComputeBuildId(stagingDir, trace);
                if (runningBuild == pendingBuild)
                {
                    Trace(trace, "stage=port-already-open build-match (reuse existing host)");
                    return;
                }
                Trace(trace, $"stage=port-already-open RUNNING={runningBuild} PENDING={pendingBuild}");
                throw new InvalidOperationException(
                    $"A netPI host with a DIFFERENT build is already running on port {Port} " +
                    $"(running build {runningBuild[..12]}…, pending build {pendingBuild[..12]}…). " +
                    "A host/Abstractions change is not a plugin hot reload — stop the old " +
                    "instance (or its launcher window) to install the pending build.");
            }
            // Run the host from an IMMUTABLE per-launch snapshot (PLAN §49 /
            // astra-1 P0.1/P0.2): the running host never maps files from the
            // repo bin or from a directory a later launch would refresh in
            // place. A failed or incomplete stage is never launched from.
            var stagingId = ComputeStagingId(stagingDir);
            var launchDir = StageHostSnapshot(Path.Combine(runtimeHome, "app-cache"), stagingId, stagingDir, trace);
            var host = Path.Combine(launchDir, "netPI.Host.exe");
            if (!File.Exists(host))
                throw new FileNotFoundException("netPI.Host.exe", host);

            var psi = new ProcessStartInfo
            {
                FileName = host,
                WorkingDirectory = FindProjectRoot(baseDir) ?? baseDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // Explicit launcher identity: the host cannot re-derive the project
            // root from its (external) execution directory, so the launcher
            // always states it; NETPI_HOST_BUILD_ID lets the next launch compare
            // against the build that is actually running.
            var projectRoot = FindProjectRoot(baseDir) ?? baseDir;
            psi.Environment["NETPI_PROJECT_ROOT"] = projectRoot;
            // astra-1 P0.1: state the plugin root explicitly — a staged host
            // launched from ~/.netpi/app-cache must not fall back to a CWD-
            // relative ./plugins (the keep-alive launcher does the same).
            psi.Environment["NETPI_PLUGINS"] = Path.Combine(projectRoot, "plugins");
            psi.Environment["NETPI_HOST_BUILD_ID"] = ComputeBuildId(launchDir, trace);
            psi.Environment["NETPI_HOME"] = runtimeHome;

            Trace(trace, "stage=process.start");
            _host = Process.Start(psi) ?? throw new InvalidOperationException("host did not start");
            Trace(trace, $"stage=started pid={_host.Id}");
            // Drain the redirected streams continuously — otherwise the
            // child's own log output fills the 4KB pipe buffer and the process blocks.
            // Drain the redirected streams concurrently and boundedly — the 4 KB
            // pipe buffers would stall the child once full (a sequential drain
            // of one side first could deadlock on two large bursts), and an
            // unbounded buffer would grow with every host run. Each line is kept
            // in a bounded ring (last 400) for the crash-dump on exit.
            // The pump also mirrors each line to host-launch.log (Trace).
            // The pumps are tracked (astra-1 P0.5) and awaited in KillHost.
            var (_, stdoutDone) = StartDrainPump(_host.StandardOutput, trace);
            var (_, stderrDone) = StartDrainPump(_host.StandardError, trace);
            _pumps = new[] { stdoutDone, stderrDone };

            for (var i = 0; i < 60 && !_host.HasExited; i++)
            {
                if (PortOpen(Port)) { Trace(trace, "stage=port-open"); return; }
                await Task.Delay(500);
            }
            if (_host.HasExited)
            {
                Trace(trace, $"stage=child-exited code={_host.ExitCode}");
                throw new InvalidOperationException($"netPI host exited with code {_host.ExitCode}; see ~/.netpi/logs");
            }
            Trace(trace, "stage=timeout waiting for port");
            throw new TimeoutException($"netPI host did not open port {Port} in 30s; see host-launch.log");
        }

        /// <summary>
        /// astra-1 P0.2: copy the complete host payload into a NEW immutable
        /// runtime directory under <c>app-cache/host/&lt;stagingId&gt;/launch-&lt;ts&gt;</c>
        /// and verify it before returning. If any copy or check fails the staged
        /// directory is discarded and the failure is reported — no file-by-file
        /// fallback onto an old directory, no launch from a mixed payload.
        /// Prunes prior launches of the same build, best-effort: a locked
        /// directory is skipped, never blocking this launch (astra-1 P0.7).
        /// </summary>
        private static string StageHostSnapshot(string appCacheRoot, string stagingId, string stagingDir, string trace)
        {
            var buildRoot = Path.Combine(appCacheRoot, "host", stagingId);
            PruneOldLaunches(buildRoot, trace, keep: 3);

            var launchDir = Path.Combine(buildRoot, $"launch-{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.CreateDirectory(launchDir);
            try
            {
                foreach (var src in Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories))
                {
                    var dst = Path.Combine(launchDir, Path.GetRelativePath(stagingDir, src));
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(src, dst, overwrite: false);
                }
                // Verify the staged copy is complete and byte-identical before
                // anything may run from it.
                foreach (var src in Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories))
                {
                    var dst = Path.Combine(launchDir, Path.GetRelativePath(stagingDir, src));
                    if (!File.Exists(dst)
                        || new FileInfo(src).Length != new FileInfo(dst).Length
                        || !FilesEqual(src, dst))
                        throw new IOException($"staged host file differs from source: {Path.GetRelativePath(stagingDir, src)}");
                }
                Trace(trace, $"stage=host-staged {launchDir}");
                return launchDir;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Never launch from a half-staged directory: discard it.
                try { Directory.Delete(launchDir, true); } catch { }
                throw new InvalidOperationException($"could not stage a complete host snapshot: {ex.Message}", ex);
            }
        }

        private static bool FilesEqual(string a, string b)
        {
            using var fa = File.OpenRead(a);
            using var fb = File.OpenRead(b);
            var ba = new byte[8192]; var bb = new byte[8192];
            while (true)
            {
                var n = fa.Read(ba, 0, ba.Length);
                if (n != fb.Read(bb, 0, bb.Length)) return false;
                for (var i = 0; i < n; i++) if (ba[i] != bb[i]) return false;
                if (n == 0) return true;
            }
        }

        /// <summary>Content id of the staged host payload (SHA-256 of netPI.Host.dll
        /// when present, otherwise "mixed") — groups the launch directories that
        /// belong to one build so pruning is per-build.</summary>
        private static string ComputeStagingId(string stagingDir)
        {
            try
            {
                var dll = Path.Combine(stagingDir, "netPI.Host.dll");
                if (File.Exists(dll))
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                        return Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(dll))).ToLowerInvariant()[..12];
            }
            catch { }
            return "mixed";
        }

        private static string ComputeBuildId(string hostDir, string trace)
        {
            try
            {
                var dll = Path.Combine(hostDir, "netPI.Host.dll");
                if (File.Exists(dll))
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                        return Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(dll))).ToLowerInvariant();
            }
            catch { }
            return "unknown";
        }

        /// <summary>astra-1 P0.3: ask a running listener for its identity. An open
        /// port is not evidence it is a netPI host — only the /identity contract is.</summary>
        private static async Task<(string BuildId, string HostDir)? > ProbeIdentityAsync(string trace)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var json = await http.GetStringAsync($"http://127.0.0.1:{Port}/identity");
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("name", out var n)
                    || n.ValueKind != System.Text.Json.JsonValueKind.String
                    || !string.Equals(n.GetString(), "netPI", StringComparison.Ordinal))
                    return null;
                var build = root.TryGetProperty("buildId", out var b) ? b.GetString() ?? "unknown" : "unknown";
                var dir = root.TryGetProperty("hostDir", out var h) ? h.GetString() ?? "" : "";
                Trace(trace, $"stage=identity build={build} dir={dir}");
                return (build, dir);
            }
            catch (System.Text.Json.JsonException) { return null; }
            catch (HttpRequestException) { return null; }
            catch (TaskCanceledException) { return null; }
            catch (IOException) { return null; }
        }

        private static void PruneOldLaunches(string buildRoot, string trace, int keep)
        {
            try
            {
                if (!Directory.Exists(buildRoot)) return;
                foreach (var dir in Directory.GetDirectories(buildRoot)
                             .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d))
                             .Skip(keep))
                {
                    try { Directory.Delete(dir, true); Trace(trace, $"stage=prune {Path.GetFileName(dir)}"); }
                    catch (Exception ex) { Trace(trace, $"stage=prune-skipped {Path.GetFileName(dir)} ({ex.Message})"); }
                }
            }
            catch (Exception ex) { Trace(trace, $"stage=prune-error {ex.Message}"); }
        }

        /// <summary>Walk up from the output folder to the project root (the dir with plugins/).</summary>
        private static string? FindProjectRoot(string from)
        {
            // astra-1 P0.1: an explicit launcher identity always wins — a staged
            // external copy runs from ~/.netpi/app-cache, and a walk-up from there
            // can never discover the repository, so the launcher must state the
            // root (the keep-alive script does the same with NETPI_PLUGINS).
            var explicitRoot = Environment.GetEnvironmentVariable("NETPI_PROJECT_ROOT");
            if (!string.IsNullOrWhiteSpace(explicitRoot))
                return explicitRoot;
            for (var dir = new DirectoryInfo(from); dir is not null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "plugins")))
                    return dir.FullName;
            return null;
        }

        private static void Trace(string file, string msg)
        {
            try { File.AppendAllText(file, $"\n{DateTime.Now:HH:mm:ss} {msg}"); }
            catch { }
        }

        private static bool PortOpen(int port)
        {
            using var tcp = new TcpClient();
            try
            {
                var ar = tcp.BeginConnect(IPAddress.Loopback, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(200)) return false;
                tcp.EndConnect(ar);
                return true;
            }
            catch { return false; }
            finally { tcp.Close(); }
        }

        /// <summary>
        /// Pumps a redirected child stream to a bounded in-memory tail so the
        /// 4 KB pipe never blocks. Returns the (thread-safe) line buffer and the
        /// pump's completion task (astra-1 P0.5: shutdown awaits it).
        /// </summary>
        private static (Queue<string> Lines, Task Done) StartDrainPump(StreamReader reader, string trace)
        {
            var lines = new Queue<string>();
            var done = Task.Run(async () =>
            {
                try
                {
                    while (!reader.EndOfStream)
                    {
                        var line = await reader.ReadLineAsync();
                        if (line is null) break;
                        Trace(trace, $"host: {line}");
                        lock (lines)
                        {
                            lines.Enqueue(line);
                            while (lines.Count > 400) lines.Dequeue();
                        }
                    }
                }
                catch { /* child pipe closed / process killed */ }
            });
            return (lines, done);
        }

        // ------------------------------------------------------------------
        // Window geometry persistence (size + maximized state, per user) —
        // the file format + clamping live in WindowGeometry (tested in
        // scratch/window-geometry*, incl. a restore smoke test).
        // ------------------------------------------------------------------

        private void RestoreWindowState()
        {
            var saved = WindowGeometry.Load(WindowGeometry.DefaultFilePath);
            if (saved is null) return;
            var (size, clamped) = WindowGeometry.Fit(
                new Size(saved.Width, saved.Height), MinimumSize, LargestWorkingArea());
            _restoreClamped = clamped;
            _appliedRestoredSize = size;
            _lastWindowedSize = size;
            Size = size; // fires SizeChanged → tracked as the windowed size
            if (saved.Maximized) WindowState = FormWindowState.Maximized;
        }

        private void OnSizeChanged(object? sender, EventArgs e)
        {
            // The maximized bounds are a screen artifact, never the user's size:
            // only track the geometry while the window is in its normal state.
            if (WindowState == FormWindowState.Normal)
                _lastWindowedSize = Size;
        }

        /// <summary>
        /// Persist the geometry for the next launch. If the saved size had to be
        /// clamped to fit this machine and the user never resized the window, the
        /// clamped value would clobber the larger saved size — skip the write.
        /// </summary>
        private void SaveWindowStateOnClose()
        {
            if (_restoreClamped && _lastWindowedSize == _appliedRestoredSize) return;
            WindowGeometry.Save(WindowGeometry.DefaultFilePath, new WindowGeometry
            {
                Width = _lastWindowedSize.Width,
                Height = _lastWindowedSize.Height,
                Maximized = WindowState == FormWindowState.Maximized,
            });
        }

        /// <summary>The largest screen work area currently connected — a saved size
        /// may exceed it (another machine's monitor set) but must never restore
        /// beyond every screen at once.</summary>
        private static Size LargestWorkingArea()
        {
            var best = Size.Empty;
            foreach (var s in Screen.AllScreens)
                if (s.WorkingArea.Width * s.WorkingArea.Height > best.Width * best.Height)
                    best = s.WorkingArea.Size;
            return best == Size.Empty ? new Size(1200, 800) : best;
        }

        private void KillHost()
        {
            try
            {
                if (_host is { HasExited: false } p)
                {
                    try { p.StandardInput.Close(); } catch { }
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(3000);
                }
            }
            catch { }
            // astra-1 P0.5: await the drain pumps before declaring the shutdown
            // complete (bounded — a wedged pump must not hang the form).
            var pumps = _pumps;
            if (pumps is not null)
            {
                _pumps = null;
                Task.WhenAll(pumps).Wait(TimeSpan.FromSeconds(5));
            }
            _host = null;
        }

    }
}
