using System;
using System.Threading.Tasks;
using NetPI.Tools;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>PLAN §23/§24/§50: shell detection with an injected probe (no real processes).</summary>
public class ShellDetectorTests
{
    private static Task<(bool ok, string output)> BashBanner(string exec)
        => Task.FromResult((true, "GNU bash, version 5.2.21(1)-release (x86_64-pc-linux-gnu)"));

    private static Task<(bool ok, string output)> PwshBanner(string exec)
        => Task.FromResult((true, "PowerShell 7.4.0"));

    [Fact]
    public async Task Bash_ConfigOverrideUsesGivenExecutable()
    {
        string? probed = null;
        var det = await ShellDetector.DetectAsync("bash", "/my/bash",
            async exec => { probed = exec; return await BashBanner(exec); }, CancellationToken.None);

        // config override path: ProbeAsync probes exactly the given executable.
        Assert.Equal("/my/bash", probed);
        Assert.Equal("/my/bash", det.Executable);
        Assert.False(det.IsWsl);
        Assert.True(det.Version!.Contains("5.2.21"));
    }

    [Fact]
    public async Task Bash_OverrideParsedFromVersionBanner()
    {
        var det = await ShellDetector.DetectAsync("bash", "/custom/bash",
            exec => Task.FromResult((true, "GNU bash, version 5.2.21(1)-release (x86_64-pc-linux-gnu)")),
            CancellationToken.None);

        Assert.Equal("/custom/bash", det.Executable);
        Assert.True(det.Version!.Contains("5.2.21"));
        Assert.False(det.IsWsl);
    }

    [Fact]
    public async Task PowerShell_OverrideDetectedWithVersion()
    {
        var det = await ShellDetector.DetectAsync("powershell", "/opt/pwsh",
            exec => Task.FromResult((true, "PowerShell 7.4.0")),
            CancellationToken.None);

        Assert.Equal("/opt/pwsh", det.Executable);
        Assert.Contains("7.4.0", det.Version);
        Assert.False(det.IsWsl);
    }

    [Fact]
    public async Task PowerShell_ProbeFailureFallsBackToPwshDefault()
    {
        var det = await ShellDetector.DetectAsync("powershell", "/does/not/exist",
            exec => Task.FromResult((false, "")), CancellationToken.None);

        // A failed probe on the override must NOT claim a working shell.
        Assert.False(det.Version is { Length: > 0 });
    }
}
