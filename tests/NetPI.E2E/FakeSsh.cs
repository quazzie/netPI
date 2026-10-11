using System.Diagnostics;

namespace NetPI.E2E;

/// <summary>
/// The runner doubles as an ssh and scp client for the ui tests (ui/tool-cards.mjs): <c>ssh.path</c> and <c>ssh.scpPath</c>
/// point at this exe, and a call with the SSH tools' arguments (they always carry <c>-o BatchMode=yes</c>) runs here
/// instead of on a host. The remote command runs in Git Bash, in the folder <c>home</c> beside the ssh config the test
/// wrote (the tools pass a config of their own as <c>-F</c>), with stdin and stdout passed through; an scp copies between
/// that folder and the local one. Nothing connects anywhere. The tools' calls need <c>ssh.reuseConnections</c> off: a kept
/// connection runs a shell loop over one long-lived ssh, which this does not imitate.
/// </summary>
internal static class FakeSsh
{
    public static bool Handles(string[] args) =>
        args.Contains("BatchMode=yes") || args.Length > 1 && args[^1] == "localhost" && (args.Contains("-G") || args.Contains("-O"));

    public static async Task<int> RunAsync(string[] args)
    {
        string? config = null;
        var ssh = false;
        var operands = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--") { operands.AddRange(args[(i + 1)..]); break; }
            switch (a)
            {
                case "-F": config = args[++i]; break;
                case "-o" or "-S" or "-l" or "-i" or "-P": i++; break;
                case "-O": await Console.Error.WriteLineAsync("fake ssh: no connection sharing"); return 255;
                case "-G": return 0; // an option probe: accepted, nothing to print
                case "-T": ssh = true; break;
                default:
                    if (a.StartsWith('-')) break; // -q -p -r
                    operands.Add(a);
                    break;
            }
        }
        if (config is null) { await Console.Error.WriteLineAsync("fake ssh: no -F config (the test passes its own)"); return 255; }
        var home = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(config))!, "home");
        Directory.CreateDirectory(home);

        if (!ssh)
        {
            // scp [-q -p -r] -- from to: one side is host:path, relative to the host's home
            if (operands.Count != 2) { await Console.Error.WriteLineAsync("fake scp: needs a source and a target"); return 1; }
            string Local(string p) => p.IndexOf(':') is var c and > 1 ? Path.Combine(home, p[(c + 1)..]) : Path.GetFullPath(p);
            var (from, to) = (Local(operands[0]), Local(operands[1]));
            if (!File.Exists(from)) { await Console.Error.WriteLineAsync($"scp: {operands[0]}: No such file or directory"); return 1; }
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, overwrite: true);
            return 0;
        }

        // ssh [options] -T -- host command…: the command runs as the host's login shell would run it
        if (operands.Count < 2) { await Console.Error.WriteLineAsync("fake ssh: needs a host and a command"); return 255; }
        var psi = new ProcessStartInfo(Bash())
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = home,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(string.Join(' ', operands.Skip(1)));
        psi.Environment["HOME"] = home;
        using var p = Process.Start(psi)!;
        var output = Console.OpenStandardOutput();
        var error = Console.OpenStandardError();
        var copyOut = p.StandardOutput.BaseStream.CopyToAsync(output);
        var copyErr = p.StandardError.BaseStream.CopyToAsync(error);
        await Console.OpenStandardInput().CopyToAsync(p.StandardInput.BaseStream);
        p.StandardInput.Close();
        await p.WaitForExitAsync();
        await Task.WhenAll(copyOut, copyErr);
        return p.ExitCode;
    }

    /// <summary>Git Bash on Windows (never System32\bash.exe, which is WSL), bash elsewhere.</summary>
    private static string Bash()
    {
        if (!OperatingSystem.IsWindows()) return File.Exists("/bin/bash") ? "/bin/bash" : "bash";
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs") };
        foreach (var root in roots.Where(r => r.Length > 0))
            if (Path.Combine(root, "Git", "bin", "bash.exe") is var b && File.Exists(b)) return b;
        var git = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(d => Path.Combine(d, "git.exe")).FirstOrDefault(File.Exists);
        if (git is not null && Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(git)!)!, "bin", "bash.exe") is var g && File.Exists(g)) return g;
        throw new FileNotFoundException("fake ssh: Git Bash not found");
    }
}
