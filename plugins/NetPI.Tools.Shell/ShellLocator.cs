namespace NetPI.Tools.Shell;

/// <summary>Finds Git Bash / bash and PowerShell executables.</summary>
public static class ShellLocator
{
    /// <summary>
    /// bash: setting <c>shell.bashPath</c>; on Windows Git Bash only (Program Files, Program Files (x86), LocalAppData,
    /// derived from git.exe on PATH, Scoop) — never System32\bash.exe (WSL). Elsewhere /bin/bash, PATH, /bin/sh.
    /// </summary>
    public static string? FindBash(ISettings? settings)
    {
        var configured = settings.SafeGet<string?>("shell.bashPath", null);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var p = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
            if (File.Exists(p)) return Path.GetFullPath(p);
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var c in WindowsBashCandidates())
                if (c is not null && File.Exists(c) && !IsWslBash(c)) return c;
            return null;
        }

        foreach (var c in new[] { "/bin/bash", "/usr/bin/bash", FindOnPath("bash"), "/usr/local/bin/bash", "/opt/homebrew/bin/bash", "/bin/sh" })
            if (c is not null && File.Exists(c)) return c;
        return null;
    }

    private static IEnumerable<string?> WindowsBashCandidates()
    {
        string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
        var pf = Env("ProgramFiles") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Env("ProgramFiles(x86)") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Env("LocalAppData") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(pf)) yield return Path.Combine(pf, "Git", "bin", "bash.exe");
        if (!string.IsNullOrEmpty(pf86)) yield return Path.Combine(pf86, "Git", "bin", "bash.exe");
        if (!string.IsNullOrEmpty(local)) yield return Path.Combine(local, "Programs", "Git", "bin", "bash.exe");
        if (FindOnPath("git") is { } git)
            foreach (var c in BashFromGit(git)) yield return c;
        var home = Env("USERPROFILE");
        if (home is not null) yield return Path.Combine(home, "scoop", "apps", "git", "current", "bin", "bash.exe");
        yield return FindOnPath("bash");
    }

    /// <summary>
    /// Derive Git Bash from a git executable: <c>…\Git\cmd\git.exe</c>, <c>…\Git\bin\git.exe</c> or
    /// <c>…\Git\mingw64\bin\git.exe</c> → <c>…\Git\bin\bash.exe</c> (then <c>…\Git\usr\bin\bash.exe</c>).
    /// </summary>
    public static IEnumerable<string> BashFromGit(string gitExe)
    {
        var dir = Path.GetDirectoryName(gitExe);
        if (string.IsNullOrEmpty(dir)) yield break;
        var name = Path.GetFileName(dir).ToLowerInvariant();
        string? root = null;
        if (name is "cmd" or "bin")
        {
            root = Path.GetDirectoryName(dir);
            var parentName = root is null ? "" : Path.GetFileName(root).ToLowerInvariant();
            if (parentName is "mingw64" or "mingw32" or "clangarm64" or "usr") root = Path.GetDirectoryName(root);
        }
        if (string.IsNullOrEmpty(root)) yield break;
        yield return Path.Combine(root, "bin", "bash.exe");
        yield return Path.Combine(root, "usr", "bin", "bash.exe");
    }

    /// <summary>The WSL launcher (System32\bash.exe, WindowsApps) must never be used.</summary>
    public static bool IsWslBash(string path)
    {
        var full = Path.GetFullPath(path);
        var sysRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? Environment.GetEnvironmentVariable("windir") ?? @"C:\Windows";
        return full.StartsWith(sysRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
            || full.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// PowerShell: setting <c>shell.pwshPath</c>, pwsh on PATH, PowerShell 7 default locations; on Windows falls back to
    /// Windows PowerShell 5.1 (<paramref name="legacy"/> = true).
    /// </summary>
    public static string? FindPwsh(ISettings? settings, out bool legacy)
    {
        legacy = false;
        var configured = settings.SafeGet<string?>("shell.pwshPath", null);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var p = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
            if (File.Exists(p))
            {
                legacy = Path.GetFileNameWithoutExtension(p).Equals("powershell", StringComparison.OrdinalIgnoreCase);
                return Path.GetFullPath(p);
            }
        }
        if (FindOnPath("pwsh") is { } onPath) return onPath;
        if (OperatingSystem.IsWindows())
        {
            var pf = Environment.GetEnvironmentVariable("ProgramFiles") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            foreach (var c in new[] { Path.Combine(pf, "PowerShell", "7", "pwsh.exe"), Path.Combine(pf, "PowerShell", "7-preview", "pwsh.exe") })
                if (File.Exists(c)) return c;
            var sysRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            var ps51 = Path.Combine(sysRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            legacy = true;
            if (File.Exists(ps51)) return ps51;
            if (FindOnPath("powershell") is { } legacyOnPath) return legacyOnPath;
            legacy = false;
            return null;
        }
        foreach (var c in new[] { "/usr/bin/pwsh", "/usr/local/bin/pwsh", "/opt/microsoft/powershell/7/pwsh", "/snap/bin/pwsh", "/opt/homebrew/bin/pwsh" })
            if (File.Exists(c)) return c;
        return null;
    }

    /// <summary>Search PATH for an executable (PATHEXT-aware on Windows).</summary>
    public static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        var exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(e => e.Equals(".exe", StringComparison.OrdinalIgnoreCase) || e.Equals(".com", StringComparison.OrdinalIgnoreCase)).Prepend("").ToArray()
            : [""];
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts)
            {
                if (OperatingSystem.IsWindows() && ext.Length == 0 && !Path.HasExtension(name)) continue;
                try
                {
                    var candidate = Path.Combine(dir.Trim().Trim('"'), name + ext);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch { /* malformed PATH entry */ }
            }
        }
        return null;
    }
}
