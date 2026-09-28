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
        r.Add("build: the build scripts parse (a text check cannot see a PowerShell syntax error)", ScriptsParse);
    }

    /// <summary>
    /// build.ps1 is not compiled, so a syntax error only shows when someone runs it. Parse it here, when PowerShell is
    /// around; skip when it is not (the Linux suite has none).
    /// </summary>
    private static void ScriptsParse()
    {
        var shell = new[] { "pwsh", "powershell" }.FirstOrDefault(Shell.Exists);
        if (shell is null) { Console.WriteLine("        (skipped: no PowerShell to parse build.ps1 with)"); return; }
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
    private static async Task<string?> OutDirOf(string project)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";
        var psi = new ProcessStartInfo(dotnet)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            WorkingDirectory = T.RepoRoot,
        };
        foreach (var a in new[] { "msbuild", project, "-getProperty:OutDir", "-nologo" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new AssertException("dotnet msbuild did not start");
        var outText = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new AssertException($"msbuild -getProperty:OutDir failed: {await p.StandardError.ReadToEndAsync()}");
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
