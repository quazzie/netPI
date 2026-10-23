using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

[assembly: InternalsVisibleTo("NetPI.Providers.Tests")]

namespace NetPI.Providers.ClaudeCli;

/// <summary>Offers the <c>claude-cli</c> provider type and runs one provider per entry of <c>providers.instances</c> of that type.</summary>
[NetPiPlugin("netpi.providers.claudecli", Name = "Claude CLI provider",
    Description = "Claude models through the installed Claude Code CLI (claude -p) on the owner's subscription, working in the session's workspace with the CLI's own tools.",
    Order = 12)]
public sealed class ClaudeCliPlugin : INetPiPlugin
{
    public const string Type = "claude-cli";
    private const string UsageCollection = "usage";
    private const string UsageKey = "latest";

    // one client per plugin instance, disposed with it (a static one outlives every reload's handler until its idle timeout)
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private IDisposable? _settingsSub;
    private ProviderSet<ClaudeCliProvider>? _set;

    /// <summary>The first Claude CLI provider the owner added, if any.</summary>
    public ClaudeCliProvider? Provider => _set?.All.FirstOrDefault();

    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "provider-claude-cli", Title = "Claude CLI", Group = "Models", Order = 52, ProviderType = Type,
            Help = "Claude on your subscription through the installed `claude` CLI, signed in once with `claude auth login`. The CLI works with its own tools (read, edit, shell, search) in the session's workspace; NetPI's tools are not used. Add one per account.",
            Settings =
            [
                SettingInfo.Folder("configDir", "Config folder",
                    "The CLI's own config folder (CLAUDE_CONFIG_DIR), where its login lives: another folder is another account (sign in with `CLAUDE_CONFIG_DIR=<folder> claude auth login`). Empty = the CLI's default (~/.claude).", "~/.claude"),
                SettingInfo.FilePath("executable", "Executable",
                    "Name on PATH or a full path. A bare `claude` that is not on PATH is looked for in ~/.local/bin, where the installer puts it.", "claude"),
                SettingInfo.List("models", "Models", [.. ClaudeCliOptions.DefaultModels], "Ids passed to `claude --model`."),
                SettingInfo.Int("timeoutSeconds", "Timeout", 3600,
                    "The longest one call may run before it is killed. A backstop only: a CLI that makes progress should not be stopped, and a dead one ends by itself.", 10, 14400, "s"),
                SettingInfo.Int("maxConcurrent", "Concurrent runs", 2,
                    "How many CLI processes run at once (each is a full Claude Code, and the subscription's rate limits are shared); the rest wait.", 1, 16),
                SettingInfo.Str("tools", "CLI tools", "default",
                    "default: every built-in tool of the CLI. none: text only. Or the tools to give it (--tools), e.g. Read, Grep, Glob for read-only. Only an agent's own turns get tools; a title or a compaction summary is always text only.")
                    with { Type = "pickText", Options = ["=default", "=none", .. ClaudeCliOptions.KnownTools] },
                SettingInfo.Choice("permissionMode", "Permissions", "bypassPermissions", [.. ClaudeCliOptions.PermissionModes],
                    "A print-mode call cannot ask, so bypassPermissions lets the CLI's tools run without prompts; the workspace guard of NetPI does not apply to them."),
                SettingInfo.Str("settingSources", "Settings files", "",
                    "Which settings the CLI loads: user, project, local. None loads none; they bring hooks and permission rules.")
                    with { Type = "pickText", Options = ["user", "project", "local"] },
                SettingInfo.Bool("connectors", "Account connectors", false, "Let the CLI load the account's MCP connectors."),
                SettingInfo.Bool("usage", "Usage pill", true,
                    "Shows the Claude subscription usage (session and weekly windows) in the context ring's popout; only the first Claude CLI provider reports it. It comes from the CLI's own output after each call; pressing the ring also asks Anthropic for the live figure with the CLI's login token (at most once a minute)."),
                SettingInfo.Int("stallTimeoutSeconds", "Silence tolerance", 1800,
                    "How long the CLI may stay silent (its own tools and subagents can run for minutes) before the call is declared stalled and retried. 0 never declares a stall: a dead CLI ends the stream on its own.", 0, 7200, "s"),
                SettingInfo.Bool("enabled", "Enabled", true),
            ],
        });

        var usage = new ClaudeUsageTracker(snap => SaveUsage(context, snap), context.Logger);
        try { usage.Restore(context.Data.Collection(UsageCollection, new CollectionSpec()).Get(UsageKey)); }
        catch (Exception ex) { context.Logger.LogDebug(ex, "claude-cli: no stored usage reading"); }
        usage.OnChanged = snap => context.Events.Publish(EventTypes.UsageClaude, snap);
        context.Rpc.Register(new RpcMethod("claudecli.usage", "The Claude subscription usage as the Claude CLI reported it after its last call (session and weekly windows); empty before the first call", ReadOnly: true, Params: []),
            (_, _) => Task.FromResult<object?>(UsageOn(context) ? usage.Get() : new JsonObject { ["limits"] = new JsonArray() }));
        context.Rpc.Register(new RpcMethod("claudecli.usage.refresh", "Asks Anthropic for the live subscription usage with the Claude CLI's login token (at most once a minute; within it the stored reading is the answer) → the snapshot, as claudecli.usage; also published as usage.claude", ReadOnly: false, Params: []),
            async (_, rpcCt) =>
            {
                if (!UsageOn(context)) return new JsonObject { ["limits"] = new JsonArray() };
                return await usage.RefreshAsync(fetchCt => ClaudeUsageApi.FetchAsync(_http, ClaudeUsageApi.CredentialsPath(), fetchCt), rpcCt).ConfigureAwait(false);
            });
        // The plan comes from `claude auth status`: only once a provider is on (a NetPI that does not use the CLI starts
        // no CLI process, idea-irqqkj), and again when one is added later.
        void PlanWhenEnabled()
        {
            if (FirstOptions(context) is { Enabled: true } first && Interlocked.Exchange(ref _planAsked, 1) == 0)
                _ = Task.Run(() => LoadPlanAsync(context, usage, first.Executable), ct);
        }

        var sessions = new DataCliSessions(context.Data.Collection("cli_sessions", new CollectionSpec().Integer("updatedAt")));
        try { sessions.Prune(); }
        catch (Exception ex) { context.Logger.LogDebug(ex, "claude-cli: could not prune the stored sessions"); }
        // the prompt files and a side call's folder live in NetPI's home (owner-only), not in a temp folder other users share
        var workDir = Path.Combine(context.Paths.Home, "claude-cli");
        // netpi.mjs ships beside the app, so a CLI with a shell is told how it reaches NetPI itself (the ideas, memory.search,
        // the MCP servers NetPI holds) and the command works from any project's folder
        var rpcScript = File.Exists(Path.Combine(AppContext.BaseDirectory, "netpi.mjs")) ? Path.Combine(AppContext.BaseDirectory, "netpi.mjs") : null;
        _set = new ProviderSet<ClaudeCliProvider>(context, Type, (id, config, read) =>
        {
            var provider = new ClaudeCliProvider(read, context.Logger, ProcessRunner.Run,
                sessionId => CwdOf(context, sessionId), () => HiddenEnvironment(context.Settings),
                // one subscription's usage: the first provider reports it, the others (other accounts) would mix their figures into it
                string.Equals(ProviderInstances.Of(context.Settings, Type).FirstOrDefault().Id, id, StringComparison.OrdinalIgnoreCase) ? usage : null,
                sessions, workDir,
                // a CLI tool is held back only where NetPI has its counterpart (registered, even if disabled in tools.disabled) and does
                // not offer it to the run (idea-hghbmg)
                () => [.. context.Tools.Registrations.Select(r => r.Tool.Definition.Name).Distinct(StringComparer.OrdinalIgnoreCase)])
            {
                Id = id, DisplayName = ProviderSet<ClaudeCliProvider>.NameOf(id, config, "Claude CLI"), RpcScript = rpcScript, Home = context.Paths.Home,
            };
            // the CLI reports each model's real window with its first answer: the catalog (and the context ring) take it up
            provider.OnModelsLearned = () => context.Events.Publish(EventTypes.ModelsChanged, new JsonObject { ["provider"] = id });
            return provider;
        });
        _set.Reconcile();
        PlanWhenEnabled();
        // Adding or removing a provider or editing its models changes the catalog: say so, as the other providers do.
        _settingsSub = context.Events.Subscribe(EventTypes.SettingsChanged, _ =>
        {
            if (_set is not { } set) return;
            if (set.Reconcile()) context.Events.Publish(EventTypes.ModelsChanged, new JsonObject { ["provider"] = Type });
            foreach (var p in set.All.Where(p => p.ModelsChangedSinceLastList()))
                context.Events.Publish(EventTypes.ModelsChanged, new JsonObject { ["provider"] = p.Id });
            PlanWhenEnabled();
        });
        return Task.CompletedTask;
    }

    private int _planAsked;

    private static ClaudeCliOptions? FirstOptions(IPluginContext context) =>
        ProviderInstances.Of(context.Settings, Type) is [var (_, config), ..] ? ClaudeCliOptions.Parse(config) : null;

    private static bool UsageOn(IPluginContext context) => FirstOptions(context)?.Usage ?? false;

    private static void SaveUsage(IPluginContext context, JsonObject snap) =>
        context.Data.Collection(UsageCollection, new CollectionSpec()).Put(UsageKey, snap.DeepClone().AsObject());

    /// <summary>The plan (<c>max</c>, <c>pro</c>…) from <c>claude auth status</c>: no tokens involved, and no usage spent.</summary>
    private static async Task LoadPlanAsync(IPluginContext context, ClaudeUsageTracker usage, string executable)
    {
        try
        {
            var exe = ClaudeCliProvider.ResolveExecutable(executable);
            var psi = new ProcessStartInfo(exe, "auth status") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var name in ProcessRunner.AlwaysHidden.Concat(HiddenEnvironment(context.Settings))) psi.Environment.Remove(name);
            using var p = Process.Start(psi);
            if (p is null) return;
            ChildJob.Assign(p);
            p.ErrorDataReceived += (_, _) => { };   // drained: a chatty stderr must not fill its pipe and stall the CLI
            p.BeginErrorReadLine();
            string outText;
            try { outText = await p.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* gone */ }
                throw;
            }
            usage.SetPlan((JsonNode.Parse(outText) as JsonObject)?.Str("subscriptionType"));
        }
        catch (Exception ex) { context.Logger.LogDebug(ex, "claude-cli: could not read the plan from `claude auth status`"); }
    }

    /// <summary>
    /// The variables of NetPI's environment the CLI (and the shell commands it runs) must not inherit, besides the ones that would
    /// redirect it away from the subscription: every variable a secret setting reads (<c>env:NAME</c> / <c>$NAME</c> under
    /// <c>providers</c> and <c>mcp</c>) and <c>shell.hideEnv</c>, the same policy as NetPI's own shell tool.
    /// </summary>
    internal static IReadOnlyCollection<string> HiddenEnvironment(ISettings settings)
    {
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var root = settings.Snapshot();
            foreach (var section in new[] { "providers", "mcp" })
                if (root[section] is { } node) CollectSecretSources(node, hidden);
            foreach (var name in settings.GetStrings("shell.hideEnv")) hidden.Add(name);
        }
        catch { /* the fixed list still applies */ }
        return hidden;
    }

    private static void CollectSecretSources(JsonNode node, HashSet<string> into)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (_, child) in o) if (child is not null) CollectSecretSources(child, into);
                break;
            case JsonArray a:
                foreach (var child in a) if (child is not null) CollectSecretSources(child, into);
                break;
            case JsonValue v when v.TryGetValue<string>(out var text):
                var value = text.Trim();
                var name = value.StartsWith("env:", StringComparison.OrdinalIgnoreCase) ? value[4..].Trim()
                    : value.StartsWith('$') && value.Length > 1 ? value[1..].Trim() : null;
                if (!string.IsNullOrEmpty(name)) into.Add(name);
                break;
        }
    }

    /// <summary>The folder the session works in (its workspace, else its project), or null when there is none.</summary>
    private static string? CwdOf(IPluginContext context, string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        try
        {
            if (context.Sessions.GetSession(sessionId) is not { } session) return null;
            var resolver = context.Services.Get<IWorkspaceResolver>();
            return SessionWorkspace.Of(session) is not null && resolver is not null ? BoundFolder(() => resolver.CwdOf(session)) : context.Sessions.GetCwd(session);
        }
        catch (ModelException) { throw; }
        catch (Exception ex)
        {
            context.Logger.LogWarning(ex, "claude-cli: could not resolve the working folder of {Session}", sessionId);
            return null;
        }
    }

    /// <summary>The folder of a session bound to a workspace. A bound session has no fallback: a workspace that cannot be
    /// resolved, or whose folder is gone, fails the run instead of silently running in the plugin's own folder.</summary>
    internal static string BoundFolder(Func<string?> cwd)
    {
        string? folder;
        try { folder = cwd(); }
        catch (Exception ex) when (ex is not ModelException)
        {
            throw new ModelException($"Claude CLI: the session's workspace cannot be used: {ex.Message}", false, null, "invalid_config", ex);
        }
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            throw new ModelException($"Claude CLI: the session's workspace folder does not exist ('{folder}').", false, null, "invalid_config");
        return folder;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _settingsSub?.Dispose();
        _settingsSub = null;
        _http.Dispose();
        _set?.Dispose();
        _set = null;
        return Task.CompletedTask;
    }
}
