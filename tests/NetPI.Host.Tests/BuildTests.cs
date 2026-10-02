using System.Diagnostics;
using System.Runtime.CompilerServices;

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
        r.Add("build: installing into artifacts/app is opt-in (-Publish), and build.sh and the UI bundle scripts agree", PublishIsOptIn);
        r.Add("build: a publish can be pointed at another app folder and takes an install lock", PublishTargetAndLock);
        r.Add("build: the one-plugin recipe builds into the folder the host loads plugins from", OnePluginRecipe);
        r.Add("build: a failed install frees the lock, a held one is waited for, a staged plugin is a whole folder", InstallRobustness);
        r.Add("build: the UI bundles are reproducible and CI compares them with the committed ones", BundlesAreReproducible);
        r.Add("build: the build scripts parse (a text check cannot see a PowerShell syntax error)", ScriptsParse);
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

    private static void PublishIsOptIn()
    {
        var ps = File.ReadAllText(Path.Combine(T.RepoRoot, "build.ps1"));
        // the build itself: no -p:AppOutDir back into the app folder, the dev tree is the default
        var buildLine = ps.Split('\n').FirstOrDefault(l => l.Contains("dotnet build NetPI.slnx")) ?? "";
        Check.NotContains(buildLine, "artifacts\\app", "the build does not write into artifacts/app");
        Check.Contains(ps, "[switch] $Publish", "-Publish exists");
        // the install is the one thing behind it, and it happens after the build
        Check.Contains(ps, "if ($Publish)", "the install is guarded by -Publish");
        Check.True(ps.IndexOf("if ($Publish)", StringComparison.Ordinal) > ps.IndexOf("dotnet build NetPI.slnx", StringComparison.Ordinal),
            "the build comes first, the install after it");
        Check.Contains(ps, "Get-LiveChats", "a publish says what it will disturb (chats mid-turn)");
        Check.Contains(ps, "The running app was not touched", "a plain build says so");
        foreach (var flag in new[] { "-NextStart", "-Pending", "-Discard", "-WaitUntilIdle" })
            Check.Contains(ps, flag, $"{flag} is documented in the script");

        var sh = File.ReadAllText(Path.Combine(T.RepoRoot, "build.sh"));
        Check.Contains(sh, "--publish", "build.sh installs on request too");
        Check.Contains(sh, "--next-start", "build.sh can stage for the next start");
        Check.Contains(sh, "DEV=artifacts/dev/app", "build.sh builds into the dev tree");
        Check.False(sh.Split('\n').Any(l => l.StartsWith("rm -rf artifacts/app", StringComparison.Ordinal)),
            "build.sh never empties the app folder outside --publish");

        // the UI bundle scripts: installing into the app folder is opt-in there too
        foreach (var (file, optIn) in new[]
                 {
                     ("web/scripts/sync-dist.mjs", "process.env.NETPI_COPY"),
                     ("web/scripts/build-plugins.mjs", "process.env.NETPI_COPY"),
                 })
        {
            var text = File.ReadAllText(Path.Combine(T.RepoRoot, file.Replace('/', Path.DirectorySeparatorChar)));
            Check.Contains(text, optIn, $"{file}: the copy into the app folder needs NETPI_COPY");
            Check.Contains(text, "NETPI_NO_COPY", $"{file}: NETPI_NO_COPY still forces it off");
        }
    }

    // Publishing from a worktree must reach the app that is actually running, and two publishers must not interleave.
    private static void PublishTargetAndLock()
    {
        var ps = File.ReadAllText(Path.Combine(T.RepoRoot, "build.ps1"));
        Check.Contains(ps, "[string] $AppDir", "-AppDir names another app folder");
        Check.Contains(ps, "server.json", "and the running app's own folder is discovered from its server.json");
        Check.Contains(ps, "$running.appDir", "by the appDir it writes there");
        Check.Contains(ps, "Enter-InstallLock", "one install at a time");
        Check.Contains(ps, "$lockFile = Join-Path $app '.install.lock'", "the lock lives in the app folder it guards");
        Check.Contains(ps, "$installLock.Dispose()", "and is released when the install ends");
        Check.True(ps.IndexOf("Waiting for", StringComparison.Ordinal) < ps.IndexOf("Enter-InstallLock $lockFile", StringComparison.Ordinal),
            "-WaitUntilIdle waits before taking the lock, so a patient publisher does not block another one");

        var sh = File.ReadAllText(Path.Combine(T.RepoRoot, "build.sh"));
        Check.Contains(sh, "--app-dir", "build.sh can be pointed at another app folder too");
        Check.Contains(sh, "APP=${APP_DIR:-artifacts/app}", "and defaults to its own");

        // the UI bundle script installs into the same app folder, not a hardcoded one
        var plugins = File.ReadAllText(Path.Combine(T.RepoRoot, "web/scripts/build-plugins.mjs".Replace('/', Path.DirectorySeparatorChar)));
        Check.Contains(plugins, "process.env.NETPI_APP_DIR", "build-plugins.mjs honours NETPI_APP_DIR");
        Check.NotContains(plugins, "path.join(repo, 'artifacts/app/plugins'", "and no longer hardcodes artifacts/app");
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

        // the chat report runs inside the lock, so its call must be bounded
        var mjs = File.ReadAllText(Path.Combine(T.RepoRoot, "scripts", "netpi.mjs"));
        Check.Contains(mjs, "signal: AbortSignal.timeout(", "scripts/netpi.mjs does not hang a publish forever");

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
    }
}
