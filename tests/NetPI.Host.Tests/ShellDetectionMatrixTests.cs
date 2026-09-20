using System.Runtime.InteropServices;
using NetPI.Tools;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// PLAN §50 (Shell) detection matrix, deterministic on any host: PATH env var,
/// ProgramFiles env var and SystemRoot env var are swapped for the duration of
/// each test so the candidate search lands in a temp tree we control. The
/// injected probe fails for every executable EXCEPT the one the test allows,
/// so pre-installed shells on the machine can never satisfy the wrong branch.
/// </summary>
/// <summary>
/// PLAN §50 (Shell) detection matrix. Runs in a dedicated, non-parallel
/// collection: it rewrites PATH/SystemRoot/ProgramFiles env vars per test.
/// </summary>
[Collection("ShellDetection")]
public sealed class ShellDetectionMatrixTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "netpi-shell-" + Guid.NewGuid().ToString("n"));
    private readonly string _savedPath = Environment.GetEnvironmentVariable("PATH") ?? "";
    private readonly string _savedSystemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? "";
    private readonly string _savedProgramFiles = Environment.GetEnvironmentVariable("ProgramFiles") ?? "";

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _savedPath);
        Environment.SetEnvironmentVariable("SystemRoot", _savedSystemRoot);
        Environment.SetEnvironmentVariable("ProgramFiles", _savedProgramFiles);
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private string Dir(string name)
    {
        var d = Path.Combine(_tmp, name);
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Touch(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, "x");
    }

    /// <summary>Probe that succeeds only for <paramref name="allowed"/> and echoes the banner.</summary>
    private static Func<string, Task<(bool ok, string output)>> ProbeFor(string allowed, string banner) =>
        async exec =>
        {
            await Task.CompletedTask;
            return (string.Equals(exec, allowed, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(exec, Path.GetFullPath(allowed), StringComparison.OrdinalIgnoreCase),
                banner);
        };

    private static Task<(bool ok, string output)> Fail(string exec) => Task.FromResult((false, ""));

    private const string BashBanner = "GNU bash, version 5.2.21(1)-release (x86_64-pc-linux-gnu)";
    private const string WslBanner = "GNU bash, version 5.2.21(1)-release (x86_64-pc-linux-gnu)";
    private const string PwshBanner = "PowerShell 7.4.0";
    private const string WinPwBanner = "Windows PowerShell\r\nCopyright (C) Microsoft Corporation.";

    // ------------------------------------------------------------------ bash

    [Fact]
    [Trait("OS", "Windows")]
    public async Task Bash_PathsToNativeBashOnPath()
    {
        var bin = Dir("pathbin");
        Touch(Path.Combine(bin, "bash.exe"));
        Environment.SetEnvironmentVariable("PATH", bin);
        Environment.SetEnvironmentVariable("SystemRoot", Dir("sysroot-empty"));

        var det = await ShellDetector.DetectAsync("bash", null, ProbeFor(Path.Combine(bin, "bash.exe"), BashBanner), ShellDetector.DefaultWslProbe, null, CancellationToken.None);

        Assert.Equal(Path.Combine(bin, "bash.exe"), det.Executable);
        Assert.Equal("5.2.21", det.Version);
        Assert.Equal(["-c"], det.Arguments);
        Assert.False(det.IsWsl);
    }

    [Fact]
    [Trait("OS", "Windows")]
    public async Task Bash_PathsToGitBashWhenNoNativeBash()
    {
        var bin = Dir("pathbin"); // empty PATH dir — no bash
        var pf = Dir("pf");
        var gitBash = Path.Combine(pf, "Git", "bin", "bash.exe");
        Touch(gitBash);
        Environment.SetEnvironmentVariable("PATH", bin);
        Environment.SetEnvironmentVariable("ProgramFiles", pf);
        Environment.SetEnvironmentVariable("SystemRoot", Dir("sysroot-empty"));

        var det = await ShellDetector.DetectAsync("bash", null, ProbeFor(gitBash, BashBanner),
            ShellDetector.DefaultWslProbe, wslAvailable: _ => false, CancellationToken.None);

        Assert.Equal(gitBash, det.Executable);
        Assert.Equal("Git Bash", det.Flavor);
        Assert.Equal("5.2.21", det.Version);
        Assert.False(det.IsWsl);
    }

    [Fact]
    [Trait("OS", "Windows")]
    public async Task Bash_FallsBackToWslAsLastResort()
    {
        var bin = Dir("pathbin"); // no native bash anywhere
        var pfEmpty = Dir("pf-empty");
        Environment.SetEnvironmentVariable("PATH", bin);
        Environment.SetEnvironmentVariable("ProgramFiles", pfEmpty);

        var det = await ShellDetector.DetectAsync("bash", null,
            probe: Fail,
            wslProbe: _ => Task.FromResult((true, WslBanner)),
            wslAvailable: _ => true,
            CancellationToken.None);

        // The branch, not the exact path, is under test (the real system
        // wsl.exe is used; the probe override is what the test controls).
        Assert.EndsWith("wsl.exe", det.Executable);
        Assert.Equal("WSL bash", det.Flavor);
        Assert.Equal("5.2.21", det.Version);
        Assert.Equal(["bash", "-c"], det.Arguments);
        Assert.True(det.IsWsl);
    }

    [Fact]
    [Trait("OS", "Windows")]
    public async Task Bash_WslBroken_ReportsNotFound()
    {
        var bin = Dir("pathbin");
        var sysroot = Dir("sysroot-wsl-broken");
        Touch(Path.Combine(sysroot, "wsl.exe"));
        Environment.SetEnvironmentVariable("PATH", bin);
        Environment.SetEnvironmentVariable("ProgramFiles", Dir("pf-empty"));
        Environment.SetEnvironmentVariable("SystemRoot", sysroot);

        var det = await ShellDetector.DetectAsync("bash", null,
            probe: Fail, wslProbe: Fail, wslAvailable: _ => true, CancellationToken.None);

        Assert.Contains("not found", det.Flavor);
        Assert.Null(det.Version);
        Assert.False(det.IsWsl);
    }

    [Fact]
    [Trait("OS", "Windows")]
    public async Task Bash_NoBashAvailableAnywhere()
    {
        Environment.SetEnvironmentVariable("PATH", Dir("pathbin"));
        Environment.SetEnvironmentVariable("ProgramFiles", Dir("pf-empty"));
        Environment.SetEnvironmentVariable("SystemRoot", Dir("sysroot-no-wsl")); // no wsl.exe

        var det = await ShellDetector.DetectAsync("bash", null, Fail, ShellDetector.DefaultWslProbe, wslAvailable: _ => false, CancellationToken.None);

        Assert.Contains("not found", det.Flavor);
        Assert.Null(det.Version);
        Assert.False(det.IsWsl);
    }

    [Fact]
    [Trait("OS", "Linux")]
    public async Task Bash_LinuxTriesBashThenSlashBinBash()
    {
        if (OperatingSystem.IsWindows()) return;
        // "bash" fails, /bin/bash succeeds.
        var det = await ShellDetector.DetectAsync("bash", null,
            exec => string.Equals(exec, "/bin/bash", StringComparison.Ordinal)
                ? Task.FromResult((true, BashBanner))
                : Task.FromResult((false, "")),
            CancellationToken.None);
        Assert.Equal("/bin/bash", det.Executable);
        Assert.Equal("5.2.21", det.Version);
        Assert.False(det.IsWsl);
    }

    [Fact]
    [Trait("OS", "Linux")]
    public async Task Bash_LinuxNothingUsableFallsBackToBareName()
    {
        if (OperatingSystem.IsWindows()) return;
        var det = await ShellDetector.DetectAsync("bash", null, Fail, ShellDetector.DefaultWslProbe, null, CancellationToken.None);
        Assert.Equal("bash", det.Executable);
        Assert.Null(det.Version);
    }

    // ------------------------------------------------------------ powershell

    [Fact]
    [Trait("OS", "Windows")]
    public async Task PowerShell_UsesPwshWhenOnPath()
    {
        var bin = Dir("pathbin");
        Touch(Path.Combine(bin, "pwsh.exe"));
        Environment.SetEnvironmentVariable("PATH", bin);

        var det = await ShellDetector.DetectAsync("powershell", null,
            ProbeFor(Path.Combine(bin, "pwsh.exe"), PwshBanner), CancellationToken.None);

        Assert.Equal("pwsh", det.Flavor);
        Assert.Equal("7.4.0", det.Version);
        Assert.Equal(["-NoProfile", "-Command"], det.Arguments);
    }

    [Fact]
    [Trait("OS", "Windows")]
    public async Task PowerShell_FallsBackToWindowsPowerShell()
    {
        var bin = Dir("pathbin");
        Touch(Path.Combine(bin, "powershell.exe")); // pwsh.exe intentionally absent
        Environment.SetEnvironmentVariable("PATH", bin);

        var det = await ShellDetector.DetectAsync("powershell", null,
            ProbeFor(Path.Combine(bin, "powershell.exe"), WinPwBanner), CancellationToken.None);

        Assert.Equal("Windows PowerShell", det.Flavor);
        Assert.Equal("PowerShell", det.Version); // first banner line has no version number
    }

    // ------------------------------------------------- WSL path conversion

    [Fact]
    public void WslPathConversion_RoundTripsDrivePaths()
    {
        var toWsl = ShellDetector.ConvertToWslPath(@"C:\AI\Projects\NetPI");
        Assert.Equal("/mnt/c/AI/Projects/NetPI", toWsl);
        Assert.Equal(@"C:\AI\Projects\NetPI", ShellDetector.ConvertFromWslPath(toWsl));
    }

    [Fact]
    public void WslPathConversion_NonDrivePathsArePassthrough()
    {
        Assert.Equal("/usr/bin/bash", ShellDetector.ConvertFromWslPath("/usr/bin/bash"));
    }
}
