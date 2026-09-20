using System.Diagnostics;
using System.Text.Json;
using NetPI.Abstractions;

namespace NetPI.Tools;

/// <summary>
/// Detected backend for a shell (PLAN §23/§24). Probed once at plugin load and
/// re-probed on reload — never assumed.
/// </summary>
public sealed record ShellDetection(
    string ShellId,
    string Executable,
    IReadOnlyList<string> Arguments,
    string Flavor,
    string? Version,
    bool IsWsl)
{
    /// <summary>Human-readable "Bash: Git Bash 5.2.x" label for diagnostics.</summary>
    public string Label => $"{(string.IsNullOrEmpty(Version) ? Flavor : $"{Flavor} {Version}")}";
}

/// <summary>
/// Backend detection for the bash / powershell tools (PLAN §23/§24).
/// Preference order: explicit config override, then platform candidates.
/// WSL is the LAST bash resort and owns Windows↔WSL working-directory
/// conversion (see <see cref="ConvertToWslPath"/>).
/// </summary>
public static class ShellDetector
{
    public static async ValueTask<ShellDetection> DetectAsync(
        string shellId,
        string? configOverride,
        Func<string, Task<(bool ok, string output)>> probe,
        CancellationToken ct = default)
    {

        if (shellId.Equals("bash", StringComparison.OrdinalIgnoreCase))
            return await DetectBashAsync(configOverride, probe, ct);
        return await DetectPowerShellAsync(configOverride, probe, ct);
    }

    /// <summary>
    /// PLAN §23/§24: detect with the documented config keys (per-shell
    /// <c>executable</c> override in this plugin's own config section) and the
    /// default process probe. Re-probed on every plugin load/reload.
    /// </summary>
    public static ValueTask<ShellDetection> DetectAsync(
        string shellId, JsonElement ownConfig, CancellationToken ct = default)
    {
        string? cfg = null;
        if (ownConfig.ValueKind == JsonValueKind.Object
            && ownConfig.TryGetProperty(shellId, out var sec) && sec.ValueKind == JsonValueKind.Object)
            cfg = sec.TryGetProperty("executable", out var exe) && exe.ValueKind == JsonValueKind.String
                ? exe.GetString() : null;
        return DetectAsync(shellId, cfg, DefaultProbe, ct);
    }

    /// <summary>Default probe: run <c>exec --version</c> and capture the banner.</summary>
    public static async Task<(bool ok, string output)> DefaultProbe(string executable)
    {
        try
        {
            var psi = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("--version");
            using var proc = Process.Start(psi);
            if (proc is null) return (false, "");
            proc.StandardInput.Close();
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await Task.WhenAny(proc.WaitForExitAsync(), Task.Delay(5_000));
            return (!string.IsNullOrWhiteSpace(stdout) || !string.IsNullOrWhiteSpace(stderr), stdout + stderr);
        }
        catch { return (false, ""); }
    }


    private static async ValueTask<ShellDetection> DetectBashAsync(
        string? configOverride, Func<string, Task<(bool, string)>> probe, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (configOverride is { Length: > 0 })
                return await ProbeAsync("bash", configOverride, "config", probe, ct);
            if (await TryProbe("bash", probe, ct) is ShellDetection d) return d;
            if (await TryProbe("/bin/bash", probe, ct) is ShellDetection d2) return d2;
            return new ShellDetection("bash", "bash", ["-c"], "bash", null, IsWsl: false);
        }

        // Windows preference: 1) config override, 2) usable native bash on
        // PATH, 3) Git for Windows bash, 4) other native/MSYS bash, 5) WSL.
        if (configOverride is { Length: > 0 })
            return await ProbeAsync("bash", configOverride, "config", probe, ct);

        var pathBash = FindOnPath("bash");
        if (pathBash is not null)
            return await ProbeAsync("bash", pathBash, "PATH", probe, ct);

        foreach (var candidate in GitBashCandidates())
        {
            if (System.IO.File.Exists(candidate) && await TryProbe(candidate, probe, ct) is { } d)
                return d;
        }

        foreach (var candidate in MsyCandidates())
        {
            if (System.IO.File.Exists(candidate) && await TryProbe(candidate, probe, ct) is { } d)
                return d;
        }

        // WSL is last resort — native Windows-accessible bash is preferred.
        // Probe with `wsl bash --version`; when selected, the invocation is
        // `wsl bash -c <cmd>` (PLAN §23: the resolver owns WSL conversion).
        if (WslAvailable())
        {
            var wsl = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
            try
            {
                var psi = new ProcessStartInfo(wsl) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add("bash"); psi.ArgumentList.Add("--version");
                using var proc = Process.Start(psi);
                if (proc is not null)
                {
                    proc.StandardInput.Close();
                    var outp = await proc.StandardOutput.ReadToEndAsync();
                    await Task.WhenAny(proc.WaitForExitAsync(), Task.Delay(5_000));
                    var ver = ParseBashVersion(outp);
                    if (ver is not null || !string.IsNullOrWhiteSpace(outp))
                        return new ShellDetection("bash", wsl, ["bash", "-c"], "WSL bash", ver, IsWsl: true);
                }
            }
            catch { /* WSL not functional — skip */ }
        }

        return new ShellDetection("bash", "bash", ["-c"], "bash (not found)", null, IsWsl: false);
    }

    private static async ValueTask<ShellDetection> DetectPowerShellAsync(
        string? configOverride, Func<string, Task<(bool, string)>> probe, CancellationToken ct)
    {
        // Prefer modern pwsh whenever available (PLAN §24).
        if (configOverride is { Length: > 0 })
            return await ProbeAsync("powershell", configOverride, "config", probe, ct);

        var pwsh = FindOnPath("pwsh");
        if (pwsh is not null)
            return await ProbeAsync("powershell", pwsh, "pwsh", probe, ct);

        var powershell = FindOnPath("powershell");
        if (powershell is not null)
            return await ProbeAsync("powershell", powershell, "Windows PowerShell", probe, ct);

        return new ShellDetection("powershell", "pwsh", ["-NoProfile", "-Command"], "PowerShell (not found)", null, IsWsl: false);

    }

    private static async ValueTask<ShellDetection?> TryProbe(string exec, Func<string, Task<(bool, string)>> probe, CancellationToken ct)
    {
        var (ok, output) = await probe(exec);
        if (!ok) return null;
        var flavor = exec.EndsWith(@"\bash.exe", StringComparison.OrdinalIgnoreCase)
            ? "Git Bash" : "bash";
        var version = ParseBashVersion(output);
        return new ShellDetection("bash", exec, ["-c"], flavor, version, IsWsl: false);


    }

    private static async ValueTask<ShellDetection> ProbeAsync(
        string shellId, string exec, string flavor, Func<string, Task<(bool, string)>> probe, CancellationToken ct)
    {
        var (ok, output) = await probe(exec);
        
        var version = shellId.StartsWith("bash", StringComparison.OrdinalIgnoreCase)
            ? ParseBashVersion(output)
            : ParsePwshVersion(output);
        var effectiveFlavor = ok ? flavor : $"{flavor} (not usable)";
        IReadOnlyList<string> args = shellId.StartsWith("bash", StringComparison.OrdinalIgnoreCase)
            ? new[] { "-c" }
            : new[] { "-NoProfile", "-Command" };
        return new ShellDetection(shellId, exec, [.. args], effectiveFlavor, ok ? version : null, IsWsl: false);

    }

    private static string? ParseBashVersion(string output)
    {
        var first = output.Split('\n').FirstOrDefault()?.Trim();
        if (first is null) return null;
        var m = System.Text.RegularExpressions.Regex.Match(first, @"version\s+([\d.]+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? ParsePwshVersion(string output)
    {
        var first = output.Split('\n').FirstOrDefault()?.Trim();
        if (first is null) return null;
        var m = System.Text.RegularExpressions.Regex.Match(first, @"PowerShell\s+([\d.]+)");
        return m.Success ? m.Groups[1].Value : "PowerShell";
    }

    private static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidates = OperatingSystem.IsWindows() ? new[] { name + ".exe", name } : new[] { name };
            foreach (var candidate in candidates)
            {
                try
                {
                    var full = Path.Combine(dir, candidate);
                    if (File.Exists(full)) return full;
                }
                catch { /* ignore malformed PATH entries */ }
            }
        }
        return null;
    }

    private static IEnumerable<string> GitBashCandidates()
    {
        var pf = Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";
        var lf = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)";
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        foreach (var root in new[] { pf, lf, local })
            foreach (var sub in new[] { @"Git\bin\bash.exe", @"Git\usr\bin\bash.exe", @"Git\mingw64\bin\bash.exe" })
                yield return Path.Combine(root, sub);
    }

    private static IEnumerable<string> MsyCandidates()
    {
        var roots = new[] {
            @"C:\msys64\usr\bin\bash.exe",
            @"C:\msys32\usr\bin\bash.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"msys2\msys64\usr\bin\bash.exe"),
        };
        foreach (var r in roots) yield return r;
    }

    private static bool WslAvailable()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
        return File.Exists(wsl);
    }

    /// <summary>
    /// PLAN §23: when WSL is the selected backend, the resolver owns
    /// Windows↔WSL working-directory conversion.
    /// </summary>
    public static string ConvertToWslPath(string windowsPath)
    {
        if (!OperatingSystem.IsWindows()) return windowsPath;
        var p = windowsPath.Replace('/', '\\');
        var m = System.Text.RegularExpressions.Regex.Match(p, @"^([A-Za-z]):\\(.*)$");
        if (m.Success)
            return $"/mnt/{m.Groups[1].Value.ToLowerInvariant()}/{m.Groups[2].Value.Replace('\\', '/')}";
        return "/" + p.Replace('\\', '/');
    }

    public static string ConvertFromWslPath(string wslPath)
    {
        var m = System.Text.RegularExpressions.Regex.Match(wslPath, @"^/mnt/([A-Za-z])/(.*)$");
        if (m.Success)
            return $"{m.Groups[1].Value.ToUpperInvariant()}:\\{m.Groups[2].Value.Replace('/', '\\')}";
        return wslPath;
    }
}
