using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
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
                // Show a waiting page while the host boots; it polls /bootstrap
                // and reloads into the app once the host is up.
                _view.CoreWebView2!.NavigateToString(WaitingPage);
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
            if (PortOpen(Port))
            {
                Trace(trace, "stage=port-already-open (host already running)");
                return; // a host already serves :5173 — reuse it
            }
            var baseDir = AppContext.BaseDirectory;
            var host = Path.Combine(baseDir, "host", "netPI.Host.exe");
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
            psi.Environment["NETPI_SKIP_CACHE"] = "1";
            Trace(trace, "stage=process.start");
            _host = Process.Start(psi) ?? throw new InvalidOperationException("host did not start");
            Trace(trace, $"stage=started pid={_host.Id}");
            // Drain the redirected streams continuously — otherwise the
            // child's own log output fills the 4KB pipe buffer and the process blocks.
            _ = Task.Run(() => { _ = _host.StandardError.ReadToEnd(); _ = _host.StandardOutput.ReadToEnd(); });

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
        /// <summary>Walk up from the output folder to the project root (the dir with plugins/).</summary>
        private static string? FindProjectRoot(string from)
        {
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

        private void KillHost()
        {
            try
            {
                if (_host is { HasExited: false } p)
                {
                    p.StandardInput.Close();
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(3000);
                }
            }
            catch { }
            _host = null;
        }

    }
}
