using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace NetPI.Host.Tests;

/// <summary>
/// The build must not disturb a running NetPI: its plugins load from artifacts/app and one reloads as soon as that
/// file changes, so every open chat loses the tools of a plugin a build merely wrote. A build therefore lands in
/// artifacts/dev/app (one default for every project) and installing is opt-in (<c>-Publish</c>). These are the
/// invariants that keep it that way, read from the build files themselves — the E2E suite then runs the build output.
/// </summary>
public static class BuildTests
{
    public static void Register(TestRunner r)
    {
        r.Add("build: the default output is artifacts/dev/app, never the app folder a running NetPI loads plugins from", DefaultOutput);
        r.Add("build: a plain build leaves the app folder alone; -Publish installs into the named app folder (run for real)", PublishIsOptIn);
        r.Add("build: the install lock is real - one publisher at a time, a held one is waited for, a stale one is taken over (run for real)", PublishTargetAndLock);
        r.Add("build: the one-plugin recipe builds into the folder the host loads plugins from", OnePluginRecipe);
        r.Add("build: a failed install frees the lock, a held one is waited for, a staged plugin is a whole folder", InstallRobustness);
        r.Add("build: the UI bundles are reproducible and CI compares them with the committed ones", BundlesAreReproducible);
        r.Add("build: the build scripts parse (a text check cannot see a PowerShell syntax error)", ScriptsParse);
        r.Add("build: every plugin project is in the solution, so a bare build sees it too", SolutionHasEveryPlugin);
    }

    /// <summary>
    /// build.ps1 is not compiled, so a syntax error only shows when someone runs it. Parse it here, when PowerShell is
    /// around; skip when it is not (the Linux suite has none).
    /// </summary>
    private static void ScriptsParse()
    {
        var shell = new[] { "pwsh", "powershell" }.FirstOrDefault(Shell.Exists);
        if (shell is null) Check.Skip("no PowerShell to parse build.ps1 with");
        foreach (var script in new[] { "build.ps1" })
        {
            var path = Path.Combine(T.RepoRoot, script);
            var (code, errors) = Shell.Run(shell,
                $"$e=$null; [System.Management.Automation.Language.Parser]::ParseFile('{path.Replace("'", "''")}', [ref]$null, [ref]$e) > $null; " +
                "if ($e) { $e | ForEach-Object { $_.Message }; exit 1 }");
            Check.Equal(0, code, $"{script} does not parse: {errors}");
        }
    }

    /// <summary>
    /// NetPI.slnx is what a bare `dotnet build` in the repository root builds. A plugin missing from it only
    /// compiles when a test project that names it pulls it in, so the suite count and the solution can drift
    /// apart without a single build complaining.
    /// </summary>
    private static void SolutionHasEveryPlugin()
    {
        var slnx = File.ReadAllText(Path.Combine(T.RepoRoot, "NetPI.slnx"));
        var listed = new HashSet<string>(
            Regex.Matches(slnx, "<Project Path=\"([^\"]+)\"").Cast<Match>()
                .Select(m => m.Groups[1].Value.Replace('\\', '/')));
        foreach (var csproj in Directory.GetDirectories(Path.Combine(T.RepoRoot, "plugins"))
                     .SelectMany(d => Directory.GetFiles(d, "*.csproj")))
        {
            var rel = Path.GetRelativePath(T.RepoRoot, csproj).Replace('\\', '/');
            Check.True(listed.Contains(rel), $"{rel} is missing from NetPI.slnx: a bare build would not build it");
        }
    }

    /// <summary>What MSBuild itself resolves: the effective OutDir of a plugin, of the server and of a test project.</summary>
    private static Task<string?> OutDirOf(string project) => MsbuildProperty(project, "OutDir");

    private static async Task<string?> MsbuildProperty(string project, string property, params string[] extraArgs)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";
        var psi = new ProcessStartInfo(dotnet)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            WorkingDirectory = T.RepoRoot,
        };
        foreach (var a in new[] { "msbuild", project, $"-getProperty:{property}", "-nologo" }.Concat(extraArgs)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new AssertException("dotnet msbuild did not start");
        var outText = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new AssertException($"msbuild -getProperty:{property} failed: {await p.StandardError.ReadToEndAsync()}");
        return outText.Trim();
    }

    private static async Task DefaultOutput()
    {
        // what a running NetPI watches and hot-reloads from: never a build's output
        foreach (var project in new[]
                 {
                     "plugins/NetPI.Context/NetPI.Context.csproj",
                     "plugins/NetPI.Tools.Web/NetPI.Tools.Web.csproj",
                     "src/NetPI.Server/NetPI.Server.csproj",
                     "tests/NetPI.Aux.Tests/NetPI.Aux.Tests.csproj",   // references plugins: a test build rebuilt them
                 })
        {
            var dir = (await OutDirOf(project))!.Replace('\\', '/');
            Check.True(dir.EndsWith('/'), $"{project}: OutDir is a folder ({dir})");
            Check.False(dir.Contains("/artifacts/app/"), $"{project} would write into the app a running NetPI loads plugins from: {dir}");
        }
        // and the app folder is still where the installed build lives
        var props = File.ReadAllText(Path.Combine(T.RepoRoot, "Directory.Build.props"));
        Check.Contains(props, "artifacts/dev/app", "the dev tree is the default output");
    }

    /// <summary>
    /// Opt-in as behaviour, not as text: a plain build.ps1 (no -Publish) must not create or write the app folder at
    /// all, and <c>-Publish -AppDir &lt;dir&gt;</c> must install there. Run for real against a throwaway app folder, so
    /// the app a NetPI actually runs from is never touched. (build.sh and the UI bundle scripts stay opt-in too.)
    /// </summary>
    private static void PublishIsOptIn()
    {
        var shell = new[] { "pwsh", "powershell" }.FirstOrDefault(Shell.Exists);
        if (shell is null) Check.Skip("no PowerShell to run build.ps1");
        var app = T.TempDir("publish-app");

        // plain build: the app folder must not even be created
        var (code, output) = Shell.RunBuild(shell, app, publish: false, 420_000);
        Check.Equal(0, code, "plain build.ps1 failed:\n" + output);
        Check.True(Directory.EnumerateFiles(app, "*", SearchOption.AllDirectories).Count() == 0,
            "a plain build (no -Publish) wrote into the app folder");

        // -Publish installs into the named app folder
        (code, output) = Shell.RunBuild(shell, app, publish: true, 420_000);
        Check.Equal(0, code, "build.ps1 -Publish failed:\n" + output);
        Check.True(File.Exists(Path.Combine(app, "NetPI.Abstractions.dll")), "-Publish installed the contract assemblies");
        Check.True(File.Exists(Path.Combine(app, "wwwroot", "index.html")), "-Publish installed the web UI");
        Check.True(Directory.GetDirectories(Path.Combine(app, "plugins")).Length >= 30, "-Publish installed the plugins");
        Check.False(File.Exists(Path.Combine(app, ".install.lock")), "the install lock was released after the install");

        // the other installers stay opt-in too
        var sh = File.ReadAllText(Path.Combine(T.RepoRoot, "build.sh"));
        Check.Contains(sh, "--publish", "build.sh installs on request too");
        Check.Contains(sh, "DEV=artifacts/dev/app", "build.sh builds into the dev tree");
        Check.False(sh.Split('\n').Any(l => l.StartsWith("rm -rf artifacts/app", StringComparison.Ordinal)),
            "build.sh never empties the app folder outside --publish");
        foreach (var file in new[] { "web/scripts/sync-dist.mjs", "web/scripts/build-plugins.mjs" })
        {
            var text = File.ReadAllText(Path.Combine(T.RepoRoot, file.Replace('/', Path.DirectorySeparatorChar)));
            Check.Contains(text, "process.env.NETPI_COPY", $"{file}: the copy into the app folder needs NETPI_COPY");
            Check.Contains(text, "NETPI_NO_COPY", $"{file}: NETPI_NO_COPY still forces it off");
        }
    }

    // The install lock, as behaviour on a throwaway app folder: one publisher holds it at a time, a second waits
    // while it is held, a stale one is taken over, and a failed install frees it for the next publisher.
    private static void PublishTargetAndLock()
    {
        var shell = new[] { "pwsh", "powershell" }.FirstOrDefault(Shell.Exists);
        if (shell is null) Check.Skip("no PowerShell to run the install lock");
        var ps = File.ReadAllText(Path.Combine(T.RepoRoot, "build.ps1"));
        var lockFn = Slice(ps, "function Enter-InstallLock", "function Get-LiveChats");
        Check.True(lockFn.Contains("Enter-InstallLock"), "could not extract Enter-InstallLock from build.ps1");

        var app = T.TempDir("install-lock");
        Directory.CreateDirectory(app);
        var child = Path.Combine(app, "waiter.ps1");
        var parent = Path.Combine(app, "locktest.ps1");

        // the waiter: a second publisher that tries to take the same lock and records when it finally gets it
        File.WriteAllText(child, lockFn + "\r\n" + @"
$ErrorActionPreference = 'Stop'
$lock = Join-Path '@APP@' '.install.lock'
$fs = Enter-InstallLock $lock
$fs.Dispose()
Set-Content -LiteralPath (Join-Path '@APP@' 'acquired.txt') 'ACQUIRED'
".Replace("@APP@", app));

        File.WriteAllText(parent, lockFn + "\r\n" + @"
$ErrorActionPreference = 'Stop'
$app = '@APP@'
$lock = Join-Path $app '.install.lock'
$marker = Join-Path $app 'acquired.txt'

# 1. an acquire creates the lock and holds it exclusively
$fs = Enter-InstallLock $lock
if (-not (Test-Path -LiteralPath $lock)) { throw 'acquire did not create the lock file' }
try { [IO.File]::Open($lock, 'Open', 'ReadWrite', 'None').Dispose(); throw 'the lock file is not held exclusively' } catch [IO.IOException] {}

# 2. a second publisher (its own process) must wait, not interleave
$waiter = Start-Process -FilePath '@SHELL@' -ArgumentList @('-NoProfile','-File','@CHILD@') -PassThru
Start-Sleep -Seconds 3
if (Test-Path -LiteralPath $marker) { throw 'the second publisher acquired while the lock was still held' }

# 3. releasing lets the waiter in
$fs.Dispose()
Remove-Item -LiteralPath $lock -Force -ErrorAction SilentlyContinue
$waiter.WaitForExit(30000) | Out-Null
if ($waiter.ExitCode -ne 0) { throw ('the waiter failed with exit ' + $waiter.ExitCode) }
if (-not (Test-Path -LiteralPath $marker)) { throw 'the waiter never acquired after the first publisher released' }

# 4. a stale lock (older than a minute, no live holder) is taken over - fast, not the full two-minute wait
$stale = Join-Path $app 'stale.lock'
Set-Content -LiteralPath $stale 'stale'
(Get-Item -LiteralPath $stale).LastWriteTime = (Get-Date).AddMinutes(-2)
$t0 = Get-Date
$fs2 = Enter-InstallLock $stale
if (((Get-Date) - $t0).TotalSeconds -ge 10) { throw 'taking over a stale lock waited like a live one' }
$fs2.Dispose(); Remove-Item -LiteralPath $stale -Force -ErrorAction SilentlyContinue

# 5. a failed install frees the lock, so the next publisher does not wait for a publisher that is gone
$lock2 = Join-Path $app 'fail.lock'
$held = $null
try { $held = Enter-InstallLock $lock2; throw 'simulated failed install' } catch {}
finally { if ($held) { $held.Dispose() }; Remove-Item -LiteralPath $lock2 -Force -ErrorAction SilentlyContinue }
$t0 = Get-Date
$fs3 = Enter-InstallLock $lock2
if (((Get-Date) - $t0).TotalSeconds -ge 5) { throw 'a released lock still made the next publisher wait' }
$fs3.Dispose(); Remove-Item -LiteralPath $lock2 -Force -ErrorAction SilentlyContinue

Write-Output 'LOCK-OK'
".Replace("@APP@", app).Replace("@SHELL@", shell).Replace("@CHILD@", child));

        var (code, output) = Shell.RunFile(shell, parent, 120_000);
        Check.Equal(0, code, "the install-lock behaviour test failed:\n" + output);
        Check.Contains(output, "LOCK-OK");
    }

    /// <summary>
    /// The documented one-plugin install: a plugin's OutDir is $(AppOutDir)plugins/&lt;Name&gt;/, so AppOutDir is the
    /// app folder. Pointing it at the plugin's own folder built a nested plugins/&lt;Name&gt;/plugins/&lt;Name&gt;/ that
    /// nothing ever loaded - a publish that looked like it worked.
    /// </summary>
    private static async Task OnePluginRecipe()
    {
        const string project = "plugins/NetPI.Tools.Web/NetPI.Tools.Web.csproj";
        var app = T.TempDir("one-plugin-app").Replace('\\', '/') + "/";
        var outDir = (await MsbuildProperty(project, "OutDir", $"-p:AppOutDir={app}", "-p:BuildProjectReferences=false"))!.Replace('\\', '/');
        Check.Equal($"{app}plugins/NetPI.Tools.Web/", outDir);

        // and no doc may point AppOutDir at a plugin folder again
        foreach (var doc in new[] { "AGENTS.md", "docs/HANDOFF.md", "README.md" })
        {
            var path = Path.Combine(T.RepoRoot, doc);
            if (!File.Exists(path)) continue;
            foreach (var line in File.ReadAllLines(path).Where(l => l.Contains("-p:AppOutDir", StringComparison.Ordinal)))
            {
                var i = line.IndexOf("-p:AppOutDir=", StringComparison.Ordinal);
                Check.False(line.IndexOf("/plugins/", i, StringComparison.Ordinal) >= 0 || line.IndexOf("\\plugins\\", i, StringComparison.Ordinal) >= 0,
                    $"{doc} points AppOutDir at a plugin folder, where the host never loads from: {line.Trim()}");
            }
        }
    }

    /// <summary>
    /// An install that fails must not leave the next publisher waiting on a lock nobody holds, a lock that is held is
    /// waited for (in real seconds, not 120 attempts in milliseconds), and a staged plugin is a whole folder, because
    /// that is how the host installs it (src/NetPI.Host/PendingBuild.cs moves .pending/plugins/&lt;Name&gt; over the
    /// installed folder).
    /// </summary>
    private static void InstallRobustness()
    {
        var ps = File.ReadAllText(Path.Combine(T.RepoRoot, "build.ps1"));

        // the lock is released whatever the install does
        var taken = ps.IndexOf("$installLock = Enter-InstallLock $lockFile", StringComparison.Ordinal);
        Check.True(taken > 0, "the install takes the lock");
        var released = ps.IndexOf("$installLock.Dispose()", taken, StringComparison.Ordinal);
        Check.True(ps.IndexOf("finally {", taken, StringComparison.Ordinal) < released && released > 0,
            "the lock is disposed in a finally, so a failed install frees it");

        var lockFn = Slice(ps, "function Enter-InstallLock", "function Get-LiveChats");
        Check.NotContains(lockFn, "continue", "every retry sleeps, so waiting for a lock really waits");
        Check.Contains(lockFn, "Get-Item -LiteralPath $path -ErrorAction SilentlyContinue", "a released lock does not throw while we look at it");
        Check.Contains(lockFn, "Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue", "a stale lock is taken over");
        var sleep = lockFn.IndexOf("Start-Sleep", StringComparison.Ordinal);
        Check.True(sleep > lockFn.IndexOf("catch [IO.IOException]", StringComparison.Ordinal) && sleep < lockFn.IndexOf("throw", StringComparison.Ordinal),
            "a lock that survives the delete is still waited for");

        // the chat report runs inside the lock, so its calls must be bounded: netpi.mjs can bound a call (--timeout), the
        // script asks for it, and an ordinary call is not cut off (a backup or a compaction takes longer than any default)
        var mjs = File.ReadAllText(Path.Combine(T.RepoRoot, "scripts", "netpi.mjs"));
        Check.Contains(mjs, "AbortSignal.timeout(", "scripts/netpi.mjs can bound a call");
        Check.Contains(mjs, "CALL_TIMEOUT_MS > 0", "and only when asked to: an ordinary call waits for the server");
        Check.Contains(ps, "netpi.mjs diag.overview --compact --timeout", "the publish's report does not hang the install lock forever");
        Check.Contains(ps, "netpi.mjs tools.list --compact --timeout", "nor does the tool lookup");

        // what the install changed is counted before the copy, not after it (after: everything matches, so "none")
        var names = ps.IndexOf("$pluginNames = @((Get-ChildItem", StringComparison.Ordinal);
        Check.True(names > 0 && names < ps.IndexOf("Copy-Rel $dev $app $rel", StringComparison.Ordinal),
            "the not-running branch counts the changed plugins while the app folder still holds the old ones");

        // build.sh stages whole folders too
        var sh = File.ReadAllText(Path.Combine(T.RepoRoot, "build.sh"));
        var staging = sh[sh.IndexOf("# --next-start", StringComparison.Ordinal)..];
        var wholeFolders = staging[staging.IndexOf("done <<< \"$changed\"", StringComparison.Ordinal)..];
        Check.Contains(wholeFolders, "if [ \"$NEXTSTART\" = 1 ]", "the whole-folder copies are the --next-start path");
        Check.Contains(wholeFolders, "cp -R \"$DEV/plugins/$name\" \"$dest/plugins/$name\"", "a staged plugin is the whole folder");
        Check.Contains(wholeFolders, "cp -R \"$DEV/wwwroot\" \"$dest/wwwroot\"", "and so is the web UI");
    }

    /// <summary>The lines of <paramref name="text"/> from one marker to the next.</summary>
    private static string Slice(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        var end = text.IndexOf(to, Math.Max(start, 0), StringComparison.Ordinal);
        return start < 0 ? "" : text[start..(end < 0 ? text.Length : end)];
    }

    /// <summary>
    /// Svelte hashes a component's scoped CSS from its path relative to its rootDir (process.cwd() by default), so the
    /// same source built from another directory rewrote every committed bundle. Both builds pin rootDir to the
    /// repository root, and the CI ui job fails when a fresh build differs from what is committed.
    /// </summary>
    private static void BundlesAreReproducible()
    {
        var app = File.ReadAllText(Path.Combine(T.RepoRoot, "web", "svelte.config.js"));
        Check.Contains(app, "rootDir", "the app UI's Svelte build pins rootDir");
        Check.Contains(app, "repoRoot", "to the repository root, not the directory the build ran from");

        var plugins = File.ReadAllText(Path.Combine(T.RepoRoot, "web", "scripts", "build-plugins.mjs"));
        Check.Contains(plugins, "rootDir: repo", "the plugin UIs pin rootDir to the same root");

        var ci = File.ReadAllText(Path.Combine(T.RepoRoot, ".github", "workflows", "ci.yml"));
        Check.Contains(ci, "git diff --exit-code -- web/dist", "the CI ui job checks the bundles it built against the committed ones");
        Check.Contains(ci, "plugins/*/wwwroot", "plugin bundles included");
    }

    /// <summary>Run a shell command and capture its exit code and output.</summary>
    private static class Shell
    {
        public static bool Exists(string name)
        {
            var psi = new ProcessStartInfo(name, "-NoProfile -Command exit 0") { UseShellExecute = false, RedirectStandardError = true };
            try { using var p = Process.Start(psi)!; p.WaitForExit(10_000); return p.ExitCode == 0; }
            catch { return false; }
        }

        public static (int Code, string Errors) Run(string shell, string script)
        {
            var psi = new ProcessStartInfo(shell, $"-NoProfile -Command \"{script.Replace("\"", "`\"")}\"")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var errors = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            p.WaitForExit(30_000);
            return (p.ExitCode, errors.Trim());
        }

        /// <summary>Run a script file (not an inline command): arguments stay unquoted, and the output is drained while
        /// the process runs, so a chatty build cannot deadlock the read.</summary>
        public static (int Code, string Output) RunFile(string shell, string scriptPath, int timeoutMs)
        {
            var psi = new ProcessStartInfo(shell)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = T.RepoRoot,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(scriptPath);
            using var p = Process.Start(psi)!;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            var exited = p.WaitForExit(timeoutMs);
            var output = outTask.GetAwaiter().GetResult() + errTask.GetAwaiter().GetResult();
            if (exited) return (p.ExitCode, output);
            try { p.Kill(true); } catch { /* already gone */ }
            return (-1, output + "\n(the script did not finish in time)");
        }

        /// <summary>Run build.ps1 for real against a throwaway app folder: a plain build, or a publish into it.</summary>
        public static (int Code, string Output) RunBuild(string shell, string appDir, bool publish, int timeoutMs)
        {
            var psi = new ProcessStartInfo(shell)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = T.RepoRoot,
            };
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(T.RepoRoot, "build.ps1"), "-SkipWeb" })
                psi.ArgumentList.Add(a);
            if (publish) psi.ArgumentList.Add("-Publish");
            psi.ArgumentList.Add("-AppDir");
            psi.ArgumentList.Add(appDir);
            using var p = Process.Start(psi)!;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            var exited = p.WaitForExit(timeoutMs);
            var output = outTask.GetAwaiter().GetResult() + errTask.GetAwaiter().GetResult();
            if (exited) return (p.ExitCode, output);
            try { p.Kill(true); } catch { /* already gone */ }
            return (-1, output + "\n(the build did not finish in time)");
        }
    }
}
