using System.Diagnostics;
using System.Text.RegularExpressions;
using NetPI.Tools.Windows;

namespace NetPI.Aux.Tests;

/// <summary>
/// The windows tool against tests/NetPI.WinTestApp (a small WinForms window, its own title per run) through the real UI
/// Automation helper. Windows only.
/// </summary>
public static class WindowsTests
{
    private static int N(ToolResult r, string pattern)
    {
        foreach (var line in r.Content.Split('\n'))
            if (Regex.Match(line, @"^\[(\d+)\] (.*)$") is { Success: true } m && Regex.IsMatch(m.Groups[2].Value, pattern))
                return int.Parse(m.Groups[1].Value);
        throw new InvalidOperationException($"no control matching {pattern} in:\n{r.Content}");
    }

    /// <summary>The newest build of a file under the repository (Debug or Release, whichever was built last).</summary>
    private static string? Built(params string[] candidates) =>
        candidates.Select(c => Path.Combine(NetPI.TestShared.T.RepoRoot, c)).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();

    public static void Register(TestRunner r)
    {
        r.Add("windows: open a window; numbered controls; type, click, toggle, slider through patterns; changes only; steps, find, read, a modal dialog, screenshot, use, close, journal", async () =>
        {
            if (!OperatingSystem.IsWindows()) Check.Skip("Windows only");
            var agentExe = Built("artifacts/dev/app/plugins/NetPI.Tools.Windows/agent/netpi-windows-agent.exe");
            var testExe = Built("tests/NetPI.WinTestApp/bin/Debug/netpi-wintest.exe", "tests/NetPI.WinTestApp/bin/Release/netpi-wintest.exe");
            Check.True(agentExe is not null, "the UI Automation helper is built (artifacts/dev/app/plugins/NetPI.Tools.Windows/agent)");
            Check.True(testExe is not null, "the test window is built (tests/NetPI.WinTestApp)");
            Environment.SetEnvironmentVariable("NETPI_WINDOWS_AGENT", agentExe);
            var ctx = new FakePluginContext(T.TempDir("windows-home"));
            await new WindowsPlugin().StartAsync(ctx, CancellationToken.None);
            Check.Equal("windows", ctx.ToolsFake.Tools.Single().Definition.Name);
            var title = "NetPI test " + Guid.NewGuid().ToString("N")[..8];
            Task<ToolResult> Run(object args, string session = "ses_1", ModelInfo? model = null) =>
                ctx.ToolsFake.Get("windows")!.ExecuteAsync(new ToolContext
                {
                    SessionId = session, AgentId = "agt_1", CallId = "call_1", Cwd = Path.GetTempPath(), Model = model,
                    Services = ctx.Services, Events = ctx.Events,
                }, T.Args(args), CancellationToken.None);
            async Task<ToolResult> Do(object args, string session = "ses_1")
            {
                var res = await Run(args, session);
                Check.False(res.IsError, res.Content);
                return res;
            }
            try
            {
                Check.Contains((await Run(new { action = "snapshot" })).Content, "no window yet");
                var w = await Do(new { action = "open", app = testExe, args = $"\"{title}\"", title = Regex.Escape(title) });
                Check.Contains(w.Content, "Window: " + title);
                Check.Contains(w.Content, "[button] Add");
                Check.Contains(w.Content, "[text] Ready");
                var name = N(w, @"^\[edit\] Name");
                var add = N(w, @"^\[button\] Add");
                var agree = N(w, @"^\[checkbox\] I agree");
                var volume = N(w, @"^\[slider\] Volume");
                Check.Contains(w.Content, "[checkbox] I agree (off)");

                var typed = await Do(new { action = "type", n = name, text = "Ada" });
                Check.Contains(typed.Content, "Typed \"Ada\" in");
                Check.Contains(typed.Content, "value=\"Ada\"");
                Check.NotContains(typed.Content, "[button] Add", "an unchanged control is not listed again");

                var added = await Do(new { action = "click", n = add });
                Check.Contains(added.Content, "Clicked");
                Check.Contains(added.Content, "[listitem] Ada");
                Check.Contains(added.Content, "Added Ada");

                var toggled = await Do(new { action = "click", n = agree });
                Check.Contains(toggled.Content, $"[{agree}] [checkbox] I agree (on)");
                Check.Contains(toggled.Content, "Agreed");

                Check.Contains((await Do(new { action = "type", n = volume, text = "7" })).Content, "Volume 7");

                var steps = await Do(new { action = "steps", steps = new object[] { new { action = "type", n = name, text = "Bob" }, new { action = "click", n = add } } });
                Check.Contains(steps.Content, "[listitem] Bob");
                Check.Equal(add, N(await Do(new { action = "snapshot" }), @"^\[button\] Add"), "numbers stay");

                var found = await Do(new { action = "find", text = "Added Bob" });
                Check.Contains(found.Content, "1 control(s) contain \"Added Bob\"");
                var status = N(found, "Added Bob");
                Check.Equal("Added Bob", (await Do(new { action = "read", n = status })).Content.Split('\n')[1]);

                // a modal dialog: the button's invoke waits for it, the tool does not; the dialog joins the window's controls
                var ask = await Run(new { action = "click", n = N(w, @"^\[button\] Ask") });
                Check.Contains(ask.Content, "[button] Yes");
                var yes = await Do(new { action = "click", n = N(ask, @"^\[button\] Yes") });
                Check.Contains(yes.Content, "Proceeding");

                var vision = T.Model("vision");
                vision.InputModalities = ["text", "image"];
                var shot = await Run(new { action = "screenshot" }, model: vision);
                Check.False(shot.IsError, shot.Content);
                Check.True(shot.Images is { Count: 1 }, "an image of the window");

                var list = await Do(new { action = "list" }, "ses_2");
                Check.Contains(list.Content, title);
                var used = await Do(new { action = "use", window = title }, "ses_2");
                Check.Contains(used.Content, "[listitem] Bob");

                Check.Contains((await Do(new { action = "close" })).Content, "Closed the window");
                Check.Contains((await Run(new { action = "snapshot" })).Content, "no window yet");
                var gone = await Run(new { action = "snapshot" }, "ses_2");
                Check.True(gone.IsError);

                var journal = Directory.GetFiles(Path.Combine(ctx.Paths.Home, "windows"), "journal-*.jsonl").Single();
                var lines = File.ReadAllLines(journal);
                Check.True(lines.Length >= 10, $"{lines.Length} journal lines");
                Check.Contains(string.Join("\n", lines), "\"action\":\"click\"");
            }
            finally
            {
                ctx.Unload();
                // the test window (and a dialog it shows) never outlives the test: it is this suite's own program
                foreach (var p in Process.GetProcessesByName("netpi-wintest"))
                    try { p.Kill(); } catch (Exception) { }
            }
        });
    }
}
