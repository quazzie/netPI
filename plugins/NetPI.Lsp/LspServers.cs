using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Lsp;

/// <summary>A language server NetPI can start: the files it serves, how it is installed and how it is run.</summary>
internal sealed class LspServer
{
    public required string Id { get; init; }
    /// <summary>The install folder under the plugin's servers folder (the JS servers share one npm install).</summary>
    public required string Install { get; init; }
    public required string[] Extensions { get; init; }
    public required Func<string, string> LanguageIdOf { get; init; }
    public required Func<string, (string File, string[] Args)> CommandOf { get; init; }
    /// <summary>Installed by the user, not by NetPI (gopls): nothing is downloaded, the command finds the binary itself.</summary>
    public bool External { get; init; }
    public Func<string, JsonNode?> InitOptions { get; init; } = _ => null;
    /// <summary>What the server needs on the machine, said when it cannot be started.</summary>
    public string Needs { get; init; } = "";
    /// <summary>The first start loads the whole project (csharp-ls runs MSBuild): it gets longer than a request.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public (string File, string[] Args) Command(string installDir) => CommandOf(installDir);
    public string LanguageId(string path) => LanguageIdOf(path);
    public JsonNode? InitializationOptions(string installDir) => InitOptions(installDir);

    public static readonly LspServer CSharp = new()
    {
        Id = "csharp", Install = LspInstaller.CSharpFolder, Extensions = [".cs"], LanguageIdOf = _ => "csharp",
        CommandOf = dir => ("dotnet", [Path.Combine(dir, "tools", "net10.0", "any", "CSharpLanguageServer.dll")]),
        Needs = "csharp-ls runs on the .NET SDK: dotnet on PATH, with an SDK to load the projects.",
        StartTimeout = TimeSpan.FromSeconds(180), RequestTimeout = TimeSpan.FromSeconds(60),
    };

    public static readonly LspServer Svelte = new()
    {
        Id = "svelte", Install = LspInstaller.JsFolder, Extensions = [".svelte"], LanguageIdOf = _ => "svelte",
        CommandOf = dir => ("node", [Path.Combine(dir, "node_modules", "svelte-language-server", "bin", "server.js"), "--stdio"]),
        Needs = "svelte-language-server runs on Node.js: node on PATH.",
    };

    public static readonly LspServer TypeScript = new()
    {
        Id = "typescript", Install = LspInstaller.JsFolder, Extensions = [".js", ".mjs", ".cjs", ".jsx", ".ts", ".mts", ".cts", ".tsx"],
        LanguageIdOf = p => Path.GetExtension(p).ToLowerInvariant() switch
        {
            ".ts" or ".mts" or ".cts" => "typescript", ".tsx" => "typescriptreact", ".jsx" => "javascriptreact", _ => "javascript",
        },
        CommandOf = dir => ("node", [Path.Combine(dir, "node_modules", "typescript-language-server", "lib", "cli.mjs"), "--stdio"]),
        InitOptions = dir => new JsonObject { ["tsserver"] = new JsonObject { ["path"] = Path.Combine(dir, "node_modules", "typescript", "lib") } },
        Needs = "typescript-language-server runs on Node.js: node on PATH.",
    };

    public const string GoplsInstall = "go install golang.org/x/tools/gopls@latest";

    public static readonly LspServer Go = new()
    {
        Id = "go", Install = "", External = true, Extensions = [".go"], LanguageIdOf = _ => "go",
        CommandOf = _ => (FindGopls(), []),
        Needs = $"gopls is not installed by NetPI: run `{GoplsInstall}` (needs Go), or set NETPI_GOPLS to the binary.",
        // the first request loads the module and its dependencies (a big module, a cold build cache: minutes)
        StartTimeout = TimeSpan.FromSeconds(180), RequestTimeout = TimeSpan.FromSeconds(90),
    };

    public static readonly LspServer Cpp = new()
    {
        Id = "cpp", Install = "", External = true,
        Extensions = [".c", ".cpp", ".cc", ".cxx", ".h", ".hpp", ".hh", ".hxx", ".inl"],
        LanguageIdOf = p => Path.GetExtension(p).ToLowerInvariant() is ".c" ? "c" : "cpp",
        CommandOf = _ => (FindClangd(), []),
        Needs = "clangd is not installed by NetPI: install LLVM (which provides clangd), or set NETPI_CLANGD to the binary; " +
                "accurate results need a compile_commands.json or .clangd in the project.",
        // the first request indexes the project
        StartTimeout = TimeSpan.FromSeconds(180), RequestTimeout = TimeSpan.FromSeconds(60),
    };

    public static readonly LspServer[] All = [CSharp, Svelte, TypeScript, Go, Cpp];

    /// <summary>gopls: NETPI_GOPLS, then PATH, GOBIN, GOPATH/bin, ~/go/bin and Go's own folder; an <see cref="LspException"/> naming the install command when it is nowhere.</summary>
    internal static string FindGopls()
    {
        var exe = OperatingSystem.IsWindows() ? "gopls.exe" : "gopls";
        var explicitPath = Environment.GetEnvironmentVariable("NETPI_GOPLS");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? explicitPath : throw new LspException($"NETPI_GOPLS points to {explicitPath}, which does not exist.");
        var dirs = new List<string>();
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        if (Environment.GetEnvironmentVariable("GOBIN") is { Length: > 0 } gobin) dirs.Add(gobin);
        foreach (var gopath in (Environment.GetEnvironmentVariable("GOPATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            dirs.Add(Path.Combine(gopath, "bin"));
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "go", "bin"));
        dirs.Add(OperatingSystem.IsWindows() ? @"C:\Program Files\Go\bin" : "/usr/local/go/bin");
        foreach (var dir in dirs)
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* a malformed PATH entry */ }
        }
        throw new LspException($"gopls was not found (looked in NETPI_GOPLS, PATH, GOBIN, GOPATH/bin, ~/go/bin). Install it with: {GoplsInstall}");
    }

    /// <summary>clangd: NETPI_CLANGD, then PATH, then LLVM's own folder (C:\Program Files\LLVM\bin on Windows, /usr/bin and
    /// /usr/local/bin elsewhere); an <see cref="LspException"/> naming the install when it is nowhere.</summary>
    internal static string FindClangd()
    {
        var exe = OperatingSystem.IsWindows() ? "clangd.exe" : "clangd";
        var explicitPath = Environment.GetEnvironmentVariable("NETPI_CLANGD");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? explicitPath : throw new LspException($"NETPI_CLANGD points to {explicitPath}, which does not exist.");
        var dirs = new List<string>();
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        var looked = new List<string> { "NETPI_CLANGD", "PATH" };
        if (OperatingSystem.IsWindows())
        {
            dirs.Add(@"C:\Program Files\LLVM\bin");
            looked.Add(@"C:\Program Files\LLVM\bin");
        }
        else
        {
            dirs.Add("/usr/bin");
            dirs.Add("/usr/local/bin");
            looked.Add("/usr/bin, /usr/local/bin");
        }
        foreach (var dir in dirs)
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* a malformed PATH entry */ }
        }
        throw new LspException($"clangd was not found (looked in {string.Join(", ", looked)}). It is not installed by NetPI: install LLVM (which provides clangd), " +
                              "or set NETPI_CLANGD to the binary; accurate results need a compile_commands.json or .clangd in the project.");
    }

    /// <summary>The server for a file, by its extension; null for a file none of them serves.</summary>
    public static LspServer? For(string path) =>
        All.FirstOrDefault(s => s.Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Installs the servers on first use into the plugin's own folder (never PATH, never a system location), pinned and checked
/// before anything runs (the owner's decision on idea-fe26oh). csharp-ls: the nupkg from nuget.org, its SHA-256 compared
/// with the pinned one before it is unpacked. The JS servers: <c>npm ci --ignore-scripts</c> from the package.json and
/// package-lock.json this plugin ships, so npm checks every package against the lockfile's sha512. A failed or refused
/// install is an <see cref="LspException"/> that says what happened; nothing half-installed is used.
/// </summary>
internal sealed class LspInstaller(string serversDir, string pluginDir, HttpClient http, ILogger log)
{
    public const string CSharpFolder = "csharp-ls-0.28.0";
    public const string CSharpUrl = "https://api.nuget.org/v3-flatcontainer/csharp-ls/0.28.0/csharp-ls.0.28.0.nupkg";
    public const string CSharpSha256 = "8f815e4f22674106a8244beba07e4b4b062e31528e404aa8489312effb8c0e0c";
    public const string JsFolder = "js-svelte-0.18.4-ts-6.0.1";
    private const string Marker = ".installed";
    // one install at a time per folder (the JS servers share theirs); the C# one does not wait for npm
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    public string Folder(LspServer server) => Path.Combine(serversDir, server.Install);
    public bool Installed(LspServer server) => File.Exists(Path.Combine(Folder(server), Marker));

    /// <summary>The server's install folder, installing it first when it is not there yet.</summary>
    public async Task<string> EnsureAsync(LspServer server, CancellationToken ct)
    {
        if (server.External) return "";
        var dir = Folder(server);
        if (Installed(server)) return dir;
        var gate = _gates.GetOrAdd(server.Install, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Installed(server)) return dir;
            var temp = dir + ".partial-" + Guid.NewGuid().ToString("N")[..8];
            Directory.CreateDirectory(temp);
            try
            {
                log.LogInformation("lsp: installing {Server} into {Dir}", server.Id, dir);
                if (server.Install == CSharpFolder) await InstallNupkgAsync(temp, ct).ConfigureAwait(false);
                else await InstallNpmAsync(temp, ct).ConfigureAwait(false);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                Directory.Move(temp, dir);
                await File.WriteAllTextAsync(Path.Combine(dir, Marker), DateTimeOffset.UtcNow.ToString("O"), ct).ConfigureAwait(false);
                return dir;
            }
            catch
            {
                try { Directory.Delete(temp, recursive: true); } catch { /* left for the next attempt to replace */ }
                throw;
            }
        }
        finally { gate.Release(); }
    }

    private async Task InstallNupkgAsync(string target, CancellationToken ct)
    {
        byte[] bytes;
        try { bytes = await http.GetByteArrayAsync(CSharpUrl, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) { throw new LspException($"csharp-ls could not be downloaded ({ex.Message}): the C# server is unavailable."); }
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (hash != CSharpSha256)
            throw new LspException($"The csharp-ls download does not match its pinned SHA-256 ({hash}, not {CSharpSha256}): it was not installed.");
        using var zip = new ZipArchive(new MemoryStream(bytes));
        zip.ExtractToDirectory(target); // refuses an entry that would land outside the folder
    }

    private async Task InstallNpmAsync(string target, CancellationToken ct)
    {
        foreach (var file in new[] { "package.json", "package-lock.json" })
            File.Copy(Path.Combine(pluginDir, "servers", "js", file), Path.Combine(target, file));
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/s", "/c", "npm ci --ignore-scripts --no-audit --no-fund" } }
            : new ProcessStartInfo("npm") { ArgumentList = { "ci", "--ignore-scripts", "--no-audit", "--no-fund" } };
        psi.WorkingDirectory = target;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        Process? npm;
        try { npm = Process.Start(psi); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new LspException($"npm could not be started ({ex.Message}): the Svelte and JavaScript servers need Node.js with npm.");
        }
        using var process = npm ?? throw new LspException("npm did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromMinutes(10));
        try { await process.WaitForExitAsync(limit.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* gone */ }
            throw new LspException("npm ci did not finish within 10 minutes: the Svelte and JavaScript servers are unavailable.");
        }
        if (process.ExitCode != 0)
        {
            var tail = (await stderr.ConfigureAwait(false)).Trim();
            throw new LspException($"npm ci failed (exit {process.ExitCode}): {(tail.Length > 600 ? tail[^600..] : tail)}");
        }
        _ = await stdout.ConfigureAwait(false);
        foreach (var (package, version) in new[] { ("svelte-language-server", "0.18.4"), ("typescript-language-server", "6.0.1"), ("typescript", "5.9.3") })
        {
            var manifest = Path.Combine(target, "node_modules", package, "package.json");
            var installed = File.Exists(manifest) ? JsonNode.Parse(await File.ReadAllTextAsync(manifest, ct).ConfigureAwait(false))?["version"]?.GetValue<string>() : null;
            if (installed != version) throw new LspException($"npm ci installed {package} {installed ?? "nothing"}, not the pinned {version}.");
        }
    }
}
