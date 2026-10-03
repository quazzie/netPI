using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Tools.Ssh;

/// <summary>
/// Remote work over the system OpenSSH client (category "ssh"): <c>ssh_hosts</c>, <c>ssh_run</c>, <c>ssh_read</c>,
/// <c>ssh_write</c>, <c>ssh_edit</c>, <c>ssh_copy</c>. Scripts and file contents go through ssh's stdin, never through
/// arguments, so nothing the agent sends needs quoting. Hosts are the aliases in the user's ssh config (read on every
/// call); unknown host keys are rejected and nothing prompts (BatchMode).
/// </summary>
[NetPiPlugin("netpi.tools.ssh", Name = "SSH tools", Description = "ssh: run, read, write, edit and copy on the hosts in ~/.ssh/config", Order = 22)]
public sealed class SshPlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "ssh", Title = "SSH", Group = "Tools", Order = 50,
            Settings =
            [
                SettingInfo.Int("ssh.timeoutSeconds", "Default ssh_run timeout", 120, null, 1, 1800, "s"),
                SettingInfo.Int("ssh.connectTimeoutSeconds", "Connect timeout", 10, null, 2, 120, "s"),
                SettingInfo.FilePath("ssh.config", "ssh config", "The host aliases come from here.", "~/.ssh/config"),
                SettingInfo.FilePath("ssh.path", "ssh", "Empty: the one found.", SshOptions.FindSsh()),
                SettingInfo.FilePath("ssh.scpPath", "scp", "Empty: the one next to ssh.", SshOptions.Sibling(SshOptions.FindSsh(), OperatingSystem.IsWindows() ? "scp.exe" : "scp")),
                SettingInfo.Bool("ssh.reuseConnections", "Reuse connections", true, "Keep a connection to a host open between calls where the ssh client cannot share one itself (Windows), so a run of calls pays the handshake once."),
                SettingInfo.Int("ssh.connectionsPerHost", "Connections per host", 3, "Calls that run at the same time on one host; one more opens its own connection.", 1, 8),
                SettingInfo.Int("ssh.idleSeconds", "Close idle connections after", 300, null, 10, 3600, "s"),
            ],
        });
        // One broker for every action: its connections outlive a call, and a reload's Track disposal closes them.
        var broker = context.Track(new SshBroker(context.Logger));
        foreach (var tool in SshToolSet.Create(context, new ProcessLauncher(), broker)) context.Tools.Register(tool);
        return Task.CompletedTask;
    }
}

internal static class SshToolSet
{
    public static IAgentTool[] Create(IPluginContext ctx, ISshLauncher launcher, SshBroker? broker = null) => [new SshTool(ctx, launcher, broker)];
}

/// <summary>
/// <c>ssh</c>: one tool, an action per job (hosts, run, read, write, edit, copy), each carried out by the class below that
/// did it as a tool of its own. hosts and read only read (<see cref="IReadOnlyCalls"/>).
/// </summary>
internal sealed class SshTool : IAgentTool, IReadOnlyCalls
{
    private readonly Dictionary<string, SshToolBase> _actions;

    public SshTool(IPluginContext ctx, ISshLauncher launcher, SshBroker? broker = null)
    {
        var actions = new SshToolBase[]
        {
            new SshHostsTool(ctx),
            new SshRunTool(ctx, launcher),
            new SshReadTool(ctx, launcher),
            new SshWriteTool(ctx, launcher),
            new SshEditTool(ctx, launcher),
            new SshCopyTool(ctx, launcher),
        };
        foreach (var action in actions) action.Broker = broker;
        _actions = actions.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        Definition = new ToolDefinition
        {
            Name = "ssh",
            Label = "SSH",
            Category = "ssh",
            SummaryArg = "action",
            Description = "Work on remote hosts (the aliases in ~/.ssh/config): hosts, run {host, script}, read, write, edit or copy (scp).",
            Help = string.Join("\n", actions.Select(t => $"- {t.Name}: {t.Summary}")),
            Parameters = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("hosts", "run", "read", "write", "edit", "copy") },
                    ["host"] = S(), ["script"] = S(), ["cwd"] = S(), ["timeout"] = I(),
                    ["path"] = S(), ["offset"] = I(), ["limit"] = I(), ["content"] = S(), ["append"] = B(), ["replace_all"] = B(),
                    ["edits"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject { ["oldText"] = S(), ["newText"] = S(), ["replace_all"] = B() },
                            ["required"] = new JsonArray("oldText", "newText"),
                        },
                    },
                    ["direction"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("upload", "download") },
                    ["from"] = S(), ["to"] = S(), ["recursive"] = B(),
                },
                ["required"] = new JsonArray("action"),
            },
            PromptGuidelines = ["For work on other machines use the ssh tool, not ssh in bash."],
        };
    }

    public ToolDefinition Definition { get; }

    /// <summary>The action of a call ("run" when a script is given without one).</summary>
    internal static string? ActionOf(JsonElement args)
    {
        var a = new ToolArgs(args);
        var action = a.Str("action", "verb", "command")?.Trim().ToLowerInvariant();
        if (action is null && a.Str("script") is not null) action = "run";
        return action switch { "exec" or "execute" => "run", "list" => "hosts", "cat" => "read", "scp" or "upload" or "download" => "copy", _ => action };
    }

    public bool IsReadOnly(JsonElement args) => ActionOf(args) is { } a && _actions.TryGetValue(a, out var t) && t.ReadOnly;

    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        var action = ActionOf(args);
        if (action is null || !_actions.TryGetValue(action, out var tool))
            return Task.FromResult(ToolResult.Error($"{(action is null ? "Give an action" : $"Unknown action \"{action}\"")}: hosts, run, read, write, edit or copy."));
        // "action": "upload" (or "download") is copy in that direction
        var a = new ToolArgs(args);
        if (a.Str("action")?.Trim().ToLowerInvariant() is "upload" or "download" && a.Str("direction") is null)
        {
            var o = JsonNode.Parse(a.Raw.GetRawText())!.AsObject();
            o["direction"] = a.Str("action")!.Trim().ToLowerInvariant();
            args = JsonSerializer.SerializeToElement(o);
        }
        return tool.ExecuteAsync(context, args, ct);
    }

    private static JsonObject S() => new() { ["type"] = "string" };
    private static JsonObject I() => new() { ["type"] = "integer" };
    private static JsonObject B() => new() { ["type"] = "boolean" };
}

/// <summary>An action of the <c>ssh</c> dispatcher: the job, the one or two lines the dispatcher's help shows, and whether it only reads.</summary>
internal abstract class SshToolBase(IPluginContext ctx, ISshLauncher launcher)
{
    internal const int MaxReadBytes = 8 * 1024 * 1024;
    /// <summary>The most <c>ssh_write</c> streams in one call. A slow link that stops the stream mid-way must not leave a
    /// multi-megabyte half-written file behind, and a capped transfer cannot outgrow the tool's timeout.</summary>
    internal const int MaxWriteBytes = 16 * 1024 * 1024;
    protected IPluginContext Ctx => ctx;
    protected ISshLauncher Launcher => launcher;
    /// <summary>The per-host connections, where the client cannot multiplex (<see cref="SshBroker"/>); null: one ssh per call.</summary>
    internal SshBroker? Broker { get; set; }
    /// <summary>The action name ("hosts", "run", …): the dispatcher's key.</summary>
    internal abstract string Name { get; }
    /// <summary>What the action does: the dispatcher's help shows it under the name.</summary>
    internal abstract string Summary { get; }
    internal virtual bool ReadOnly => false;

    /// <summary>
    /// The workspace rule for a write on this machine, the one the file tools apply (a private copy: plugins do not share
    /// code): an unbound session may write anywhere, an isolated one may not write into another checkout of the same
    /// repository. The workspace hook already blocks these; this is the tool's own answer for a call made without hooks.
    /// </summary>
    protected static string? WorkspaceRefusal(ToolContext context, string fullPath)
    {
        var binding = context.Workspace();
        if (binding is null || !binding.Isolated) return null;
        var probe = context.Services?.Get<IWorkspaceRepoProbe>();
        var verdict = WorkspacePaths.CheckMutation(binding, fullPath, probe);
        return verdict is WorkspacePathVerdict.ForeignCheckout or WorkspacePathVerdict.Unverifiable
            ? WorkspacePaths.Refusal(binding, fullPath, probe, verdict)
            : null;
    }

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        var a = new ToolArgs(args);
        // The control socket's directory must exist before ssh uses it (ssh will not make parent folders);
        // when it cannot be made the call runs without multiplexing rather than failing.
        var controlDir = Path.Combine(ctx.Paths.Home, "ssh");
        try { Directory.CreateDirectory(controlDir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ctx.Logger.LogDebug(ex, "SSH control directory {Dir} is not writable: calls fall back to one connection each", controlDir);
            controlDir = null;
        }
        // A client that cannot reuse connections gets none: every call then does its own handshake, the way it did
        // before connection reuse, instead of failing on a control socket it cannot use. NetPI's own client on Windows
        // (OpenSSH_for_Windows) is such a client, so this is the normal case there, not an edge case. Asked once, and
        // only when a directory for the sockets exists at all.
        if (controlDir is not null && !SshClient.SupportsMultiplexing(SshOptions.Read(ctx.Settings, ctx.Paths.Home).Ssh, Path.Combine(controlDir, "netpi-probe")))
        {
            ctx.Logger.LogDebug("The ssh client at {Ssh} cannot multiplex connections: one handshake per call", SshOptions.Read(ctx.Settings, ctx.Paths.Home).Ssh);
            controlDir = null;
        }
        var o = SshOptions.Read(ctx.Settings, ctx.Paths.Home) with { ControlDir = controlDir };
        SshHost? host = null;
        if (this is not SshHostsTool)
        {
            var name = a.Str("host", "server", "alias")?.Trim();
            var hosts = SshConfig.Read(o.Config);
            if (string.IsNullOrEmpty(name))
                return ToolResult.Error($"ssh needs a host: one of {Names(hosts)}.");
            host = hosts.FirstOrDefault(h => h.Alias.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (host is null)
                return ToolResult.Error(hosts.Count == 0
                    ? $"No hosts: {o.Config} has no Host entries. Add the host there first."
                    : $"Unknown host \"{name}\". The hosts are the aliases in {o.Config}: {Names(hosts)}.");
        }
        try { return await RunAsync(context, a.Raw, o, host!, ct).ConfigureAwait(false); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return ToolResult.Error($"Could not start {o.Ssh}: {ex.Message}. Install OpenSSH or set ssh.path in the settings.");
        }
    }

    protected abstract Task<ToolResult> RunAsync(ToolContext context, JsonElement args, SshOptions o, SshHost host, CancellationToken ct);

    private static string Names(List<SshHost> hosts) => hosts.Count == 0 ? "(none)" : string.Join(", ", hosts.Select(h => h.Alias));

    /// <summary>
    /// ssh [common options] -T -- host remoteCommand — over a kept connection when the client cannot share one itself
    /// (no control directory: Windows) and ssh.reuseConnections is on, else one ssh for this call.
    /// </summary>
    protected async Task<SshExec> Ssh(SshOptions o, SshHost host, string remoteCommand, byte[]? stdin, TimeSpan timeout, CancellationToken ct,
        Action<string>? onStdout = null, Action<string>? onStderr = null)
    {
        if (o.ControlDir is null && Broker is { } broker && ctx.Settings.Get("ssh.reuseConnections", true)
            && await broker.TryRunAsync(o, host, remoteCommand, stdin, timeout,
                Math.Clamp(ctx.Settings.Get("ssh.connectionsPerHost", 3), 1, 8), TimeSpan.FromSeconds(Math.Clamp(ctx.Settings.Get("ssh.idleSeconds", 300), 10, 3600)),
                onStdout, onStderr, ct).ConfigureAwait(false) is { } kept)
            return kept;
        var args = o.CommonArgs();
        args.AddRange(["-T", "--", host.Alias, remoteCommand]);
        return await launcher.RunAsync(o.Ssh, args, stdin, null, onStdout, onStderr, timeout, ct).ConfigureAwait(false);
    }

    /// <summary>ssh's own failures (exit 255) with a hint for the common ones.</summary>
    protected static string Failure(SshHost host, SshExec r)
    {
        var err = r.Stderr.Trim().TrimEnd('.');
        var hint = err.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase) || err.Contains("No ED25519 host key is known", StringComparison.OrdinalIgnoreCase) || err.Contains("host key is known", StringComparison.OrdinalIgnoreCase)
            ? $" The host key of {host.Alias} is not in known_hosts: the user has to connect once (ssh {host.Alias}) to accept it."
            : err.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
                ? $" The key for {host.Alias} was not accepted."
                : "";
        return $"ssh to {host.Alias} failed: {(err.Length > 0 ? err : "exit code 255")}.{hint}";
    }

    protected static string Where(SshHost host, string path) => $"{host.Alias}:{path}";

    /// <summary>123456 → "123.5 KB" (notes like "Line N is X long").</summary>
    internal static string Size(long bytes) => bytes < 1024 * 1024
        ? (bytes / 1024.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " KB"
        : (bytes / (1024.0 * 1024)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " MB";
}

// ---------------------------------------------------------------------------------------------------------------- hosts

internal sealed class SshHostsTool(IPluginContext ctx) : SshToolBase(ctx, new ProcessLauncher())
{
    internal override string Name => "hosts";
    internal override bool ReadOnly => true;
    internal override string Summary => "The remote hosts: the aliases in the user's ~/.ssh/config, with user and address.";

    protected override Task<ToolResult> RunAsync(ToolContext context, JsonElement args, SshOptions o, SshHost host, CancellationToken ct)
    {
        var hosts = SshConfig.Read(o.Config);
        if (hosts.Count == 0) return Task.FromResult(ToolResult.Ok($"No hosts: {o.Config} has no Host entries.", new { hosts = Array.Empty<object>() }));
        var lines = hosts.Select(h => $"{h.Alias}: {(h.User is null ? "" : h.User + "@")}{h.HostName ?? h.Alias}{(h.Port is { } p ? $":{p}" : "")}");
        return Task.FromResult(ToolResult.Ok($"Hosts ({o.Config}):\n" + string.Join('\n', lines),
            new { hosts = hosts.Select(h => new { alias = h.Alias, hostName = h.HostName, user = h.User, port = h.Port }).ToArray() }));
    }
}

// ---------------------------------------------------------------------------------------------------------------- run

internal sealed class SshRunTool(IPluginContext ctx, ISshLauncher launcher) : SshToolBase(ctx, launcher)
{
    private const string PgidMarker = "__netpi_pgid=";

    internal override string Name => "run";
    internal override string Summary =>
        "Run a bash script on a remote host. The script is sent as it is: quotes, $, " +
        "backslashes and heredocs need no escaping, and it can be long. cwd sets the remote working directory (default " +
        "the home folder). stdout and stderr are merged; the exit code is reported when it is not 0. timeout (default " +
        "120 s, max 1800) ends the whole remote process tree, and so does stopping the run.";

    protected override async Task<ToolResult> RunAsync(ToolContext context, JsonElement args, SshOptions o, SshHost host, CancellationToken ct)
    {
        var a = new ToolArgs(args);
        var script = a.Str("script", "command", "cmd", "code");
        if (string.IsNullOrWhiteSpace(script)) return ToolResult.Error("ssh_run needs a script.");
        var cwd = a.Str("cwd", "dir", "directory", "workdir");
        var timeout = Math.Clamp(a.Int("timeout", "timeout_seconds") ?? o.Timeout, 1, 1800);

        // The remote login shell only sees this fixed line: it saves stdin (the script) to a temp file and runs it under
        // timeout in a process group of its own, which a kill can end whole; the first output line is that group's id. The
        // group comes from job control (set -m: the background job is its own group, and its pid is the id), not from
        // setsid --wait: BusyBox's setsid (the Home Assistant SSH add-on, any Alpine) has no --wait. BusyBox's timeout also
        // kills only its child and exits 143 (137 after -k) where GNU's ends the group and exits 124, so a timeout is
        // recognised by the exit code together with the elapsed time ($SECONDS), the group is ended here, and the exit code
        // is the one the tool reports for a timeout (124). The outer shell's stderr is only its job notices ("Terminated"):
        // the script's own stderr is merged into stdout inside.
        var remote = $$"""
            t=$(mktemp) && cat >"$t" && bash -c 'set -m; bash -c "echo {{PgidMarker}}\$\$; exec timeout -k 5 {{timeout}} bash \"\$0\" </dev/null 2>&1" "$0" & p=$!; wait $p; c=$?; if [ $SECONDS -ge {{timeout}} ] && { [ $c -eq 124 ] || [ $c -eq 137 ] || [ $c -eq 143 ]; }; then kill -TERM -- -$p; sleep 1; kill -KILL -- -$p; c=124; fi 2>/dev/null; exit $c' "$t" 2>/dev/null; c=$?; rm -f "$t"; exit $c
            """;
        var body = Sh.Cd(cwd) + "\n" + Sh.Lf(script).TrimStart('\uFEFF');
        if (!body.EndsWith('\n')) body += "\n";

        var pgid = (string?)null;
        var head = new StringBuilder();
        var passed = false;
        void Live(string chunk)
        {
            // hide the PGID line from the live output
            if (passed) { context.Output?.Invoke(chunk); return; }
            head.Append(chunk);
            var text = head.ToString();
            var nl = text.IndexOf('\n');
            if (nl < 0 && text.Length < 64) return;
            passed = true;
            if (text.StartsWith(PgidMarker, StringComparison.Ordinal) && nl > 0)
            {
                pgid = text[PgidMarker.Length..nl].Trim();
                text = text[(nl + 1)..];
            }
            if (text.Length > 0) context.Output?.Invoke(text);
        }
        var started = DateTime.UtcNow;
        var r = await Ssh(o, host, remote, Encoding.UTF8.GetBytes(body), TimeSpan.FromSeconds(timeout + 20), ct, Live, s => context.Output?.Invoke(s))
            .ConfigureAwait(false);
        var durationMs = (long)(DateTime.UtcNow - started).TotalMilliseconds;

        var stdout = r.Stdout;
        if (stdout.StartsWith(PgidMarker, StringComparison.Ordinal))
        {
            var nl = stdout.IndexOf('\n');
            pgid ??= nl > 0 ? stdout[PgidMarker.Length..nl].Trim() : null;
            stdout = nl >= 0 ? stdout[(nl + 1)..] : "";
        }
        if ((r.Aborted || r.TimedOut) && pgid is not null && int.TryParse(pgid, out var group) && group > 1)
            _ = KillGroupAsync(o, host, group);

        var ssh = r.ExitCode == 255 && !r.Aborted && !r.TimedOut && pgid is null;
        var output = ToolOutput.ResolveCarriageReturns(stdout + (r.Stderr.Length > 0 && !ssh ? (stdout.Length > 0 && !stdout.EndsWith('\n') ? "\n" : "") + r.Stderr : ""));
        var (tail, truncated, total, shown) = ToolOutput.TailLines(output, ToolOutput.ModelMaxLines, ToolResultLimit.Fit(Ctx.Settings, ToolOutput.ModelMaxBytes));
        truncated |= r.Cut;
        var fullOutputPath = truncated && !ssh ? Save(host, output) : null;
        var timedOut = r.TimedOut || r.ExitCode == 124;
        var details = new
        {
            host = host.Alias,
            command = script,
            shell = "ssh",
            cwd,
            exitCode = r.Aborted ? (int?)null : r.ExitCode,
            durationMs,
            truncated,
            fullOutputPath,
            timedOut = timedOut ? true : (bool?)null,
            aborted = r.Aborted ? true : (bool?)null,
        };
        if (ssh) return ToolResult.Error(Failure(host, r), details);

        var sb = new StringBuilder();
        if (truncated)
            sb.Append(ToolOutput.Note(shown,
                r.Cut ? $"; only the last {ProcessLauncher.MaxChars / 1024 / 1024} MB of the output were kept" : " of " + total,
                fullOutputPath,
                r.Cut ? "Those are" : "Full output")).Append('\n');
        sb.Append(tail.Length > 0 ? tail : "(no output)");
        if (timedOut) sb.Append($"\n[timed out after {timeout}s; the remote process group was ended. Use a longer timeout for long-running work.]");
        else if (r.Aborted) sb.Append("\n[aborted; the remote process group is being ended]");
        else if (r.ExitCode == 125 && output.Contains("cd:", StringComparison.Ordinal)) sb.Append($"\n[the working directory {cwd} does not exist on {host.Alias}]");
        else if (r.ExitCode != 0) sb.Append($"\n[exit code {r.ExitCode}]");
        return new ToolResult { Content = sb.ToString(), IsError = timedOut || r.Aborted, Details = details };
    }

    private async Task KillGroupAsync(SshOptions o, SshHost host, int group)
    {
        try
        {
            await Ssh(o, host, $"kill -TERM -- -{group} 2>/dev/null; sleep 2; kill -KILL -- -{group} 2>/dev/null; true", null,
                TimeSpan.FromSeconds(20), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception) { }
    }

    /// <summary>The whole output, for read/grep, when the result shows only its tail (in the temp folder, like bash's logs).</summary>
    private static string? Save(SshHost host, string output)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "netpi");
            Directory.CreateDirectory(dir);
            var name = string.Concat(host.Alias.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
            var path = Path.Combine(dir, $"ssh-{name}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
            File.WriteAllText(path, output);
            return path;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

// ---------------------------------------------------------------------------------------------------------------- read

internal sealed class SshReadTool(IPluginContext ctx, ISshLauncher launcher) : SshToolBase(ctx, launcher)
{
    public const int MaxLines = 2000;
    public const int MaxChars = 50 * 1024;
    private const string StatMarker = "__netpi_stat=";
    private const string HashMarker = "__netpi_hash=";

    /// <summary>
    /// Sets <c>$h</c> to the content hash of <c>$p</c>, computed on the host with the first command of this chain that
    /// exists: <c>sha256sum</c> (expected — the GNU coreutils the <c>stat -c</c> in the same script already assumes),
    /// then <c>shasum</c> (macOS), then <c>openssl</c>, then <c>cksum</c> as a last resort (weak: 32 bits, but still
    /// better than no check). The value carries the algorithm name, so a host whose tooling changes between the read
    /// and the write can never produce a false match; both calls run this same chain on the same host, so they always
    /// pick the same tool. The hash is of the whole file; for the files <see cref="Fetch"/> accepts (it refuses the rest
    /// as over the cap) that is exactly the content the caller reads back.
    /// </summary>
    internal const string HashScript =
        "if command -v sha256sum >/dev/null 2>&1; then h=\"sha256 $(sha256sum < \"$p\" | cut -d ' ' -f 1)\"; " +
        "elif command -v shasum >/dev/null 2>&1; then h=\"sha256 $(shasum -a 256 < \"$p\" | cut -d ' ' -f 1)\"; " +
        "elif command -v openssl >/dev/null 2>&1; then h=\"sha256 $(openssl dgst -sha256 -r < \"$p\" | cut -d ' ' -f 1)\"; " +
        "else h=\"cksum $(cksum < \"$p\" | cut -d ' ' -f 1)\"; fi";

    internal override string Name => "read";
    internal override bool ReadOnly => true;
    internal override string Summary =>
        $"Read a text file on a remote host, like read: at most {MaxLines} lines / {MaxChars / 1024}KB per " +
        "call; page with offset (1-based; negative counts from the end) and limit. Relative paths start at cwd or the home folder.";

    protected override async Task<ToolResult> RunAsync(ToolContext context, JsonElement args, SshOptions o, SshHost host, CancellationToken ct)
    {
        var a = new ToolArgs(args);
        var path = a.Str("path", "file", "file_path")?.Trim();
        if (string.IsNullOrEmpty(path)) return ToolResult.Error("ssh_read needs a path.");
        var cwd = a.Str("cwd");
        var offsetArg = a.Int("offset", "start_line", "line") ?? 1;
        // A negative offset counts from the end of the file, which the first 8 MB (the default capture) does not reach;
        // fetch the tail instead, so "from the end" means the file's end, not the window's end.
        var file = await Fetch(o, host, path, cwd, hash: false, tail: offsetArg < 0, ct).ConfigureAwait(false);
        if (file.Error is not null) return ToolResult.Error(file.Error);
        var text = file.Text!;
        if (text.Contains('\0'))
            return ToolResult.Error($"{Where(host, path)} appears to be a binary file ({file.Size} bytes); ssh_read only shows text. Use ssh_copy to download it.");

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var total = text.EndsWith('\n') ? lines.Length - 1 : lines.Length;
        if (total == 0) return ToolResult.Ok("(empty file)", Details(host, path, 0, 0, 0, false, file.Size));
        var offset = offsetArg < 0 ? Math.Max(1, total + offsetArg + 1) : offsetArg;
        if (offset == 0) offset = 1;
        if (offset > total)
            return ToolResult.Error(file.Cut
                ? $"offset {offset} is beyond the {(offsetArg < 0 ? "last" : "first")} {MaxReadBytes / 1024 / 1024} MB of {Where(host, path)}, which is all ssh_read can see. The file is {file.Size / 1024 / 1024} MB; read a further window with ssh_run (tail, sed -n)."
                : $"offset {offset} is past the end of the file: {Where(host, path)} has {total} lines.");
        var limit = Math.Clamp(a.Int("limit", "lines", "count") ?? MaxLines, 1, MaxLines);
        var sb = new StringBuilder();
        var taken = 0;
        string? lineNote = null;
        for (var i = offset - 1; i < total && taken < limit; i++)
        {
            if (sb.Length + lines[i].Length + 1 > MaxChars)
            {
                if (taken > 0) break;
                // A single line that outruns the whole page budget (a minified bundle, a JSON dump): show its
                // beginning and say so, the way the local read does — the page is not one line of 8 MB.
                sb.Append(lines[i].Length > MaxChars ? lines[i][..MaxChars] : lines[i]);
                taken = 1;
                lineNote = $"[Line {i + 1} is {Size(Encoding.UTF8.GetByteCount(lines[i]))} long; showing its first {Size(MaxChars)}. Use ssh_run (e.g. cut -c, fold) to inspect the rest.]";
                break;
            }
            if (taken > 0) sb.Append('\n');
            sb.Append(lines[i]);
            taken++;
        }
        var end = offset + taken - 1;
        var truncated = end < total || (file.Cut && offsetArg > 0) || lineNote is not null;
        // Beyond this window: a head window on a bigger file has content that no offset can reach (ssh_run's job),
        // a tail window at its end is the file's end.
        var more = end < total || (file.Cut && offsetArg > 0);
        // A cut file always gets a window note (even at the file's end); a small file notes "continue" only when more is reachable.
        if (more || file.Cut)
        {
            if (end < total)
                sb.Append(file.Cut
                    ? $"\n\n[Showing lines {offset}-{end} of the {(offsetArg < 0 ? "last" : "first")} {MaxReadBytes / 1024 / 1024} MB of a {file.Size / 1024 / 1024} MB file. Use offset={end + 1} to continue within it.]"
                    : $"\n\n[Showing lines {offset}-{end} of {total}. Use offset={end + 1} to continue.]");
            else
                sb.Append(file.Cut
                    ? (offsetArg < 0
                        ? $"\n\n[Showing lines {offset}-{end}, the last {MaxReadBytes / 1024 / 1024} MB of a {file.Size / 1024 / 1024} MB file — this is the end of the file.]"
                        : $"\n\n[Showing lines {offset}-{end}, the first {MaxReadBytes / 1024 / 1024} MB of a {file.Size / 1024 / 1024} MB file. ssh_read only sees the first {MaxReadBytes / 1024 / 1024} MB; read further with ssh_run (tail, sed -n).]")
                    : $"\n\n[Showing lines {offset}-{end} of {total}]");
        }
        if (lineNote is not null) sb.Append("\n\n").Append(lineNote);
        return ToolResult.Ok(sb.ToString(), Details(host, path, offset, end, total, truncated, file.Size));
    }

    private static object Details(SshHost host, string path, int start, int end, int total, bool truncated, long size) =>
        new { host = host.Alias, path = Where(host, path), startLine = start, endLine = end, totalLines = total, truncated, bytes = size };

    internal sealed record Fetched(string? Text, long Size, string Stat, string Hash, bool Cut, string? Error);

    /// <summary>
    /// The file's text (up to <see cref="SshToolBase.MaxReadBytes"/> — the first part, or the last when <paramref name="tail"/>),
    /// its <c>size mtime</c> stat, and — when <paramref name="hash"/> — the file's content hash
    /// (<see cref="HashScript"/>), what <c>ssh_edit</c> re-checks before it writes back.
    /// </summary>
    /// <param name="hash">Compute the whole-file hash. Only <c>ssh_edit</c> needs it; a read that hashed a multi-GB log
    /// on every page paid for nothing and could time out on the hash alone.</param>
    /// <param name="tail">Capture the last <see cref="SshToolBase.MaxReadBytes"/> instead of the first, so a negative offset
    /// counts from the file's end rather than the end of the captured window.</param>
    internal async Task<Fetched> Fetch(SshOptions o, SshHost host, string path, string? cwd, bool hash, bool tail, CancellationToken ct)
    {
        var hashStep = hash ? $"{HashScript} && echo \"{HashMarker}$h\" && " : "";
        var grab = tail ? "tail" : "head";
        var remote = Sh.Cd(cwd) + $"p={Sh.Path(path)}; if [ ! -e \"$p\" ]; then echo \"No such file: $p\" >&2; exit 2; fi; " +
                     "if [ -d \"$p\" ]; then echo \"$p is a directory\" >&2; exit 3; fi; " +
                     $"stat -c '{StatMarker}%s %Y' -- \"$p\" && {hashStep}{grab} -c {MaxReadBytes} -- \"$p\"";
        var timeout = TimeSpan.FromSeconds(Math.Max(60, o.Timeout));
        var r = await Ssh(o, host, remote, null, timeout, ct).ConfigureAwait(false);
        if (r.Aborted) return new Fetched(null, 0, "", "", false, $"Reading {Where(host, path)} was aborted.");
        if (r.TimedOut) return new Fetched(null, 0, "", "", false, $"Reading {Where(host, path)} timed out after {timeout.TotalSeconds:0}s{(hash ? " (hashing the whole file first)" : "")}. Read a smaller window with ssh_run (tail, sed -n) or raise ssh.timeoutSeconds.");
        if (r.ExitCode == 255) return new Fetched(null, 0, "", "", false, Failure(host, r));
        if (r.ExitCode == 125) return new Fetched(null, 0, "", "", false, $"The working directory {cwd} does not exist on {host.Alias}.");
        if (r.ExitCode != 0) return new Fetched(null, 0, "", "", false, r.Stderr.Trim() is { Length: > 0 } e ? $"{host.Alias}: {e}" : $"Reading {Where(host, path)} failed (exit {r.ExitCode}).");
        var nl = r.Stdout.IndexOf('\n');
        if (!r.Stdout.StartsWith(StatMarker, StringComparison.Ordinal) || nl < 0) return new Fetched(null, 0, "", "", false, $"Unexpected reply reading {Where(host, path)}.");
        var stat = r.Stdout[StatMarker.Length..nl].Trim();
        var rest = r.Stdout[(nl + 1)..];
        string fileHash = "";
        if (hash)
        {
            var nl2 = rest.IndexOf('\n');
            if (!rest.StartsWith(HashMarker, StringComparison.Ordinal) || nl2 < 0) return new Fetched(null, 0, stat, "", false, $"Unexpected reply reading {Where(host, path)}.");
            fileHash = rest[HashMarker.Length..nl2].Trim();
            rest = rest[(nl2 + 1)..];
        }
        long.TryParse(stat.Split(' ')[0], out var size);
        return new Fetched(rest, size, stat, fileHash, size > MaxReadBytes, null);
    }
}

// ---------------------------------------------------------------------------------------------------------------- write

internal sealed class SshWriteTool(IPluginContext ctx, ISshLauncher launcher) : SshToolBase(ctx, launcher)
{
    internal override string Name => "write";
    internal override string Summary =>
        $"Write a file on a remote host. The content goes as it is (UTF-8, no escaping), up to {MaxWriteBytes / 1024 / 1024} MB; " +
        "larger content is refused with what to do instead. The content streams to a temporary file in the target's own " +
        "directory and is put in place by an atomic rename, so a slow or dropped transfer never leaves the target truncated " +
        "or half-written. Parent folders are created; an existing file keeps its owner and permissions; " +
        "append (cat >>) adds to the end instead of replacing.";

    protected override async Task<ToolResult> RunAsync(ToolContext context, JsonElement args, SshOptions o, SshHost host, CancellationToken ct)
    {
        var a = new ToolArgs(args);
        var path = a.Str("path", "file", "file_path")?.Trim();
        if (string.IsNullOrEmpty(path)) return ToolResult.Error("ssh_write needs a path.");
        var content = a.Str("content", "text", "data");
        if (content is null) return ToolResult.Error("ssh_write needs content (an empty string empties the file).");
        var append = a.Bool("append") ?? false;
        var bytes = Encoding.UTF8.GetBytes(content);
        if (bytes.Length > MaxWriteBytes)
            return ToolResult.Error($"The content is {Size(bytes.Length)}; ssh_write streams at most {Size(MaxWriteBytes)} per call, so a transfer that stops short cannot leave a half-written file behind. " +
                "For larger content, write it on the host with ssh_run (a heredoc, or a download and unpack), or upload a local file with ssh_copy.");
        // A replace goes to a temporary file in the target's own directory and is put in place with an atomic rename (the
        // target's mode copied over, 644 for a new file), like ssh_edit: an interruption before the rename leaves the old
        // content exactly as it was — the target is never truncated. append streams straight in: cat >> cannot shorten it.
        var remote = append
            ? Sh.Cd(a.Str("cwd")) +
              $"p={Sh.Path(path)}; mkdir -p -- \"$(dirname -- \"$p\")\" && cat >>\"$p\""
            : Sh.Cd(a.Str("cwd")) +
              $"p={Sh.Path(path)}; if [ -e \"$p\" ]; then echo existed; fi; " +
              "mkdir -p -- \"$(dirname -- \"$p\")\" || exit 4; " +
              "t=$(mktemp -- \"${p}.netpi.XXXXXX\") || exit 5; " +
              "cat >\"$t\" || { echo \"writing the temporary copy failed\" >&2; rm -f -- \"$t\"; exit 6; }; " +
              "if [ -e \"$p\" ]; then chmod -- \"$(stat -c '%a' -- \"$p\")\" \"$t\" || { echo \"restoring the mode failed\" >&2; rm -f -- \"$t\"; exit 7; }; " +
              "else chmod 644 \"$t\" || { echo \"setting the mode failed\" >&2; rm -f -- \"$t\"; exit 7; }; fi; " +
              "mv -f -- \"$t\" \"$p\" || { echo \"renaming into place failed\" >&2; rm -f -- \"$t\"; exit 8; }";
        var r = await Ssh(o, host, remote, bytes, TimeSpan.FromSeconds(Math.Max(60, o.Timeout)), ct).ConfigureAwait(false);
        if (r.ExitCode == 255) return ToolResult.Error(Failure(host, r));
        if (r.ExitCode == 125) return ToolResult.Error($"The working directory does not exist on {host.Alias}.");
        if (r.ExitCode != 0) return ToolResult.Error($"Writing {Where(host, path)} failed: {r.Stderr.Trim()}");
        var created = !r.Stdout.Contains("existed", StringComparison.Ordinal);
        var lines = content.Length == 0 ? 0 : content.Count(c => c == '\n') + (content.EndsWith('\n') ? 0 : 1);
        var verb = append ? "Appended" : created ? "Created" : "Wrote";
        return ToolResult.Ok($"{verb} {Where(host, path)} ({bytes.Length} bytes, {lines} lines).",
            new { host = host.Alias, path = Where(host, path), created, append, bytes = bytes.Length, lines });
    }
}

// ---------------------------------------------------------------------------------------------------------------- edit

internal sealed class SshEditTool(IPluginContext ctx, ISshLauncher launcher) : SshToolBase(ctx, launcher)
{
    internal override string Name => "edit";
    internal override string Summary =>
        "Edit a file on a remote host with exact replacements, like edit: each oldText must match " +
        "exactly once (replace_all replaces every match). Line endings (CRLF/LF) are kept. Fails without writing if the file " +
        "changed on the host while it was being edited.";

    protected override async Task<ToolResult> RunAsync(ToolContext context, JsonElement args, SshOptions o, SshHost host, CancellationToken ct)
    {
        var a = new ToolArgs(args);
        var path = a.Str("path", "file", "file_path")?.Trim();
        if (string.IsNullOrEmpty(path)) return ToolResult.Error("ssh_edit needs a path.");
        var edits = Edits(args);
        if (edits.Count == 0) return ToolResult.Error("ssh_edit needs edits: [{ \"oldText\", \"newText\" }].");
        var cwd = a.Str("cwd");

        var reader = new SshReadTool(Ctx, Launcher) { Broker = Broker };
        var file = await reader.Fetch(o, host, path, cwd, hash: true, tail: false, ct).ConfigureAwait(false);
        if (file.Error is not null) return ToolResult.Error(file.Error);
        if (file.Cut) return ToolResult.Error($"{Where(host, path)} is larger than {MaxReadBytes / 1024 / 1024} MB; edit it with ssh_run (sed, python) instead.");
        if (file.Text!.Contains('\0')) return ToolResult.Error($"{Where(host, path)} is a binary file.");
        if (file.Text.Contains('�'))
            return ToolResult.Error($"{Where(host, path)} is not valid UTF-8; writing it back would change its bytes. Edit it with ssh_run (sed, python) instead.");

        EditOutcome outcome;
        try { outcome = TextEdits.Apply(file.Text, edits, path); }
        catch (EditException ex) { return ToolResult.Error(ex.Message); }

        // Write back only if the file is still what was read. The marker is the content hash the read recorded, not
        // size plus whole-second mtime: a same-size edit inside one second changed neither and was clobbered. The new
        // text goes to a temporary file in the target's own directory and is put in place with an atomic rename (the
        // target's mode copied over), so a failure or an interruption before the rename leaves the old content exactly
        // as it was — the target is never truncated. What this does not close: an external writer that rewrites the
        // same file between the check and the rename still wins; an arbitrary writer takes no locks, and a hash alone
        // cannot guard a check-then-act gap against it. Writers that must coexist with this tool need coordination —
        // a lock, or editing through ssh_edit itself.
        var remote = Sh.Cd(cwd) + $"p={Sh.Path(path)}; " +
                     "if [ ! -e \"$p\" ]; then echo \"file is gone: $p\" >&2; exit 2; fi; " +
                     SshReadTool.HashScript + "; " +
                     $"[ \"$h\" = {Sh.Quote(file.Hash)} ] || {{ echo \"changed: $h\" >&2; exit 3; }}; " +
                     "t=$(mktemp -- \"${p}.netpi.XXXXXX\") || exit 4; " +
                     "cat >\"$t\" || { echo \"writing the temporary copy failed\" >&2; rm -f -- \"$t\"; exit 5; }; " +
                     "chmod -- \"$(stat -c '%a' -- \"$p\")\" \"$t\" || { echo \"restoring the mode failed\" >&2; rm -f -- \"$t\"; exit 6; }; " +
                     "mv -f -- \"$t\" \"$p\" || { echo \"renaming into place failed\" >&2; rm -f -- \"$t\"; exit 7; }";
        var r = await Ssh(o, host, remote, Encoding.UTF8.GetBytes(outcome.Text), TimeSpan.FromSeconds(Math.Max(60, o.Timeout)), ct).ConfigureAwait(false);
        if (r.ExitCode == 255) return ToolResult.Error(Failure(host, r));
        if (r.ExitCode == 125) return ToolResult.Error($"The working directory {cwd} does not exist on {host.Alias}.");
        if (r.ExitCode == 3) return ToolResult.Error($"{Where(host, path)} changed on the host while it was being edited; nothing was written. Read it again and redo the edit.");
        if (r.ExitCode == 2) return ToolResult.Error($"{Where(host, path)} was removed on the host before the write; nothing was written. Read it again.");
        if (r.ExitCode != 0) return ToolResult.Error($"Writing {Where(host, path)} failed: {r.Stderr.Trim()}");

        return ToolResult.Ok($"Applied {outcome.Replacements} edit{(outcome.Replacements == 1 ? "" : "s")} to {Where(host, path)} (+{outcome.Added} −{outcome.Removed})",
            new
            {
                host = host.Alias,
                path = Where(host, path),
                diff = outcome.Diff,
                added = outcome.Added,
                removed = outcome.Removed,
                edits = outcome.Replacements,
                firstChangedLine = outcome.FirstChangedLine,
                eol = file.Text.Contains("\r\n", StringComparison.Ordinal) ? "crlf" : "lf",
            });
    }

    private static List<TextEdit> Edits(JsonElement args)
    {
        var a = new ToolArgs(args);
        var list = new List<TextEdit>();
        var replaceAllTop = a.Bool("replace_all", "replaceAll") ?? false;
        if (a.TryGet(out var arr, "edits", "changes") && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
            {
                var ea = new ToolArgs(e);
                if (ea.Str("oldText", "old_text", "old", "search") is { } old && ea.Str("newText", "new_text", "new", "replace") is { } neu)
                    list.Add(new TextEdit(old, neu, ea.Bool("replace_all", "replaceAll") ?? replaceAllTop));
            }
        }
        else if (a.Str("oldText", "old_text", "old") is { } old && a.Str("newText", "new_text", "new") is { } neu)
            list.Add(new TextEdit(old, neu, replaceAllTop));
        return list;
    }
}

// ---------------------------------------------------------------------------------------------------------------- copy

internal sealed class SshCopyTool(IPluginContext ctx, ISshLauncher launcher) : SshToolBase(ctx, launcher)
{
    internal override string Name => "copy";
    internal override string Summary =>
        "Copy files or folders between this machine and a remote host with scp: direction " +
        "upload (from = local path, to = remote path) or download (from = remote, to = local). recursive for folders. " +
        "Use it for large or binary files.";

    protected override async Task<ToolResult> RunAsync(ToolContext context, JsonElement args, SshOptions o, SshHost host, CancellationToken ct)
    {
        var a = new ToolArgs(args);
        var direction = (a.Str("direction", "mode") ?? "").Trim().ToLowerInvariant();
        var from = a.Str("from", "source", "src")?.Trim();
        var to = a.Str("to", "destination", "dest", "target")?.Trim();
        if (direction is not ("upload" or "download")) return ToolResult.Error("ssh_copy needs direction: upload or download.");
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) return ToolResult.Error("ssh_copy needs from and to.");
        var recursive = a.Bool("recursive", "r") ?? false;

        // scp reads "C:\x" as host "C": run it in the local folder and pass a relative name
        string local, workDir, localArg;
        if (direction == "upload")
        {
            local = context.ResolvePath(from);
            if (!File.Exists(local) && !Directory.Exists(local)) return ToolResult.Error($"No such local file or folder: {local}");
            if (Directory.Exists(local) && !recursive) return ToolResult.Error($"{local} is a folder: set recursive to copy it.");
            workDir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(local)) ?? local;
            localArg = "./" + Path.GetFileName(Path.TrimEndingDirectorySeparator(local));
        }
        else
        {
            local = context.ResolvePath(to);
            if (WorkspaceRefusal(context, local) is { } refusal) return ToolResult.Error(refusal);
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(local)) ?? local;
            Directory.CreateDirectory(parent);
            workDir = parent;
            localArg = "./" + Path.GetFileName(Path.TrimEndingDirectorySeparator(local));
        }
        var remote = $"{host.Alias}:{(direction == "upload" ? to : from)}";
        var scpArgs = new List<string> { "-q", "-p" };
        if (recursive) scpArgs.Add("-r");
        scpArgs.AddRange(o.CommonArgs());
        scpArgs.Add("--");
        if (direction == "upload") scpArgs.AddRange([localArg, remote]);
        else scpArgs.AddRange([remote, localArg]);

        var r = await Launcher.RunAsync(o.Scp, scpArgs, null, workDir, null, null, TimeSpan.FromSeconds(Math.Max(600, o.Timeout)), ct).ConfigureAwait(false);
        if (r.Aborted) return ToolResult.Error("[aborted]");
        if (r.TimedOut) return ToolResult.Error($"scp timed out after {Math.Max(600, o.Timeout)}s.");
        if (r.ExitCode != 0) return ToolResult.Error($"scp {(direction == "upload" ? "to" : "from")} {host.Alias} failed: {(r.Stderr.Trim() is { Length: > 0 } e ? e : $"exit code {r.ExitCode}")}");
        var bytes = File.Exists(local) ? new FileInfo(local).Length : Directory.Exists(local) ? Size(local) : 0;
        var text = direction == "upload" ? $"Uploaded {local} to {remote}" : $"Downloaded {remote} to {local}";
        return ToolResult.Ok($"{text} ({bytes} bytes).", new { host = host.Alias, direction, from, to, local, remote, bytes, recursive });
    }

    private static long Size(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch (Exception) { return 0; }
    }
}
