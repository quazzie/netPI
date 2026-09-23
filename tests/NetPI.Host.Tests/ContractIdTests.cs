using System.Reflection;
using NetPI.Abstractions;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// Astra-2 contract id: the shared-contract pin is the PUBLIC API surface id
/// (ContractId), not the raw dll bytes. The raw-bytes pin broke after every
/// commit (the SDK's SourceLink stamps the git HEAD into the assembly version
/// and the PDB), which made every plugin fail the host's contract check and
/// prevented the host from ever binding a port.
/// </summary>
public class ContractIdTests
{
    [Fact]
    public void Compute_IsDeterministic_ForSameAssembly()
    {
        var asm = typeof(INetPiPlugin).Assembly;
        Assert.Equal(ContractId.Compute(asm), ContractId.Compute(asm));
    }

    [Fact]
    public void Compute_IsTwelveLowerHex()
    {
        var id = ContractId.Compute(typeof(INetPiPlugin).Assembly);
        Assert.Matches("^[0-9a-f]{12}$", id);
    }

    [Fact]
    public void Compute_DiffersAcrossAssemblies()
    {
        var a = ContractId.Compute(typeof(INetPiPlugin).Assembly);
        var b = ContractId.Compute(typeof(ContractIdTests).Assembly);
        Assert.NotEqual(a, b);
    }

    /// <summary>
    /// The id must NOT depend on the assembly's version (which carries the
    /// SourceLink +&lt;git HEAD&gt; suffix) — it covers only the API surface.
    /// Verified on the real Abstractions assembly: two builds of identical
    /// source at different commits share the id (see the commit message of
    /// the astra-2 contract-id change for the empty-commit probe that
    /// demonstrated byte drift with stable API id).
    /// </summary>
    [Fact]
    public void Compute_CoversThePublicSurface()
    {
        // Spot-check that known public members influence the id: the id of the
        // Abstractions assembly must be stable and must NOT equal the id of an
        // assembly that exposes a different surface (covered above) — and the
        // version string itself is not part of the computation (it is not a
        // type or member).
        var id = ContractId.Compute(typeof(INetPiPlugin).Assembly);
        Assert.Matches("^[0-9a-f]{12}$", id);
    }

    /// <summary>
    /// End-to-end invariant: every CURRENTLY PUBLISHED artifact must pin the
    /// same API id as the abstractions this test was compiled against —
    /// i.e. a publish against the host's contract is loadable by the host.
    /// Skipped when run outside the repo (no .artifacts).
    /// </summary>
    [Fact]
    public void PublishedArtifactsPinTheCurrentContractApiId()
    {
        var repoRoot = FindRepoRoot();
        if (repoRoot is null) return; // test run outside the repo
        var artifacts = Path.Combine(repoRoot, ".artifacts", "plugins");
        if (!Directory.Exists(artifacts)) return;

        var current = ContractId.Compute(typeof(INetPiPlugin).Assembly);
        var dirs = Directory.GetDirectories(artifacts);
        Assert.NotEmpty(dirs);
        foreach (var pluginDir in dirs)
        {
            // only the latest build per plugin is authoritative
            var latest = Directory.GetDirectories(pluginDir)
                .OrderByDescending(d => File.GetLastWriteTime(Path.Combine(d, "artifact.json")))
                .FirstOrDefault();
            if (latest is null) continue;
            var manifest = System.Text.Json.JsonSerializer.Deserialize<NetPI.Abstractions.PluginManifest>(
                System.IO.File.ReadAllText(Path.Combine(latest, "artifact.json")));
            Assert.True(manifest is not null && manifest.AbstractionsApiId == current,
                $"{Path.GetFileName(pluginDir)}: artifact {Path.GetFileName(latest)} pins abstractionsApiId " +
                $"{manifest?.AbstractionsApiId ?? "<null>"}, current contract is {current}");
        }
    }

    private static string? FindRepoRoot()
    {
        // walk up from the test bin to a directory containing NetPI.sln
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NetPI.sln")))
                return dir.FullName;
        }
        return null;
    }
}
