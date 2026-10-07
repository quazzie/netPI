using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

[assembly: InternalsVisibleTo("NetPI.Providers.Tests")]

namespace NetPI.Providers.ClaudeCli;

/// <summary>Registers the <c>claude-cli</c> provider (settings <c>providers.claudeCli</c>).</summary>
[NetPiPlugin("netpi.providers.claudecli", Name = "Claude CLI provider",
    Description = "Claude models through the installed Claude Code CLI (claude -p) on the owner's subscription, working in the session's workspace with the CLI's own tools.",
    Order = 12)]
public sealed class ClaudeCliPlugin : INetPiPlugin
{
    public const string SettingsPath = "providers.claudeCli";
    private const string UsageCollection = "usage";
    private const string UsageKey = "latest";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private IDisposable? _settingsSub;

    public ClaudeCliProvider? Provider { get; private set; }

    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "claude-cli", Title = "Claude CLI", Group = "Models", Order = 52,
            Settings =
            [
                SettingInfo.Bool("providers.claudeCli.enabled", "Enabled", false,
                    "Runs the official `claude` CLI as a subprocess, signed in once with `claude auth login` (subscription, not an API key). The CLI works with its own tools (read, edit, shell, search) in the session's workspace; NetPI's tools are not used."),
                SettingInfo.Str("providers.claudeCli.executable", "Executable", "claude",
                    "Name on PATH or a full path. A bare `claude` that is not on PATH is looked for in ~/.local/bin, where the installer puts it."),
                SettingInfo.List("providers.claudeCli.models", "Models", [.. ClaudeCliOptions.DefaultModels], "Ids passed to `claude --model`."),
                SettingInfo.Int("providers.claudeCli.timeoutSeconds", "Timeout", 3600,
                    "The longest one call may run before it is killed. A backstop only: a CLI that makes progress should not be stopped, and a dead one ends by itself.", 10, 14400, "s"),
                SettingInfo.Int("providers.claudeCli.maxConcurrent", "Concurrent runs", 2,
                    "How many CLI processes run at once (each is a full Claude Code, and the subscription's rate limits are shared); the rest wait.", 1, 16),
                SettingInfo.Str("providers.claudeCli.tools", "CLI tools", "default",
                    "default: every built-in tool of the CLI. none: text only. Or a comma-separated list for --tools, e.g. Read,Grep,Glob for read-only. Only an agent's own turns get tools; a title or a compaction summary is always text only."),
                SettingInfo.Choice("providers.claudeCli.permissionMode", "Permissions", "bypassPermissions", [.. ClaudeCliOptions.PermissionModes],
                    "A print-mode call cannot ask, so bypassPermissions lets the CLI's tools run without prompts; the workspace guard of NetPI does not apply to them."),
                SettingInfo.Str("providers.claudeCli.settingSources", "Settings files", "",
                    "Which settings the CLI loads: user, project, local (comma-separated). Empty loads none; they bring hooks and permission rules."),
                SettingInfo.Bool("providers.claudeCli.connectors", "Account connectors", false, "Let the CLI load the account's MCP connectors."),
                SettingInfo.Bool("providers.claudeCli.usage", "Usage pill", true,
                    "Shows the Claude subscription usage (session and weekly windows) in the context ring's popout. It comes from the CLI's own output after each call; pressing the ring also asks Anthropic for the live figure with the CLI's login token (at most once a minute)."),
                SettingInfo.Int("providers.claudeCli.stallTimeoutSeconds", "Silence tolerance", 1800,
                    "How long the CLI may stay silent (its own tools and subagents can run for minutes) before the call is declared stalled and retried. 0 never declares a stall: a dead CLI ends the stream on its own.", 0, 7200, "s"),
            ],
        });

        var usage = new ClaudeUsageTracker(snap => SaveUsage(context, snap), context.Logger);
        try { usage.Restore(context.Data.Collection(UsageCollection, new CollectionSpec()).Get(UsageKey)); }
        catch (Exception ex) { context.Logger.LogDebug(ex, "claude-cli: no stored usage reading"); }
        usage.OnChanged = snap => context.Events.Publish(EventTypes.UsageClaude, snap);
        context.Rpc.RegisterReadOnly("claudecli.usage", (_, _) => Task.FromResult<object?>(Options(context).Usage ? usage.Get() : new JsonObject { ["limits"] = new JsonArray() }),
            "The Claude subscription usage as the Claude CLI reported it after its last call (session and weekly windows); empty before the first call");
        context.Rpc.Register("claudecli.usage.refresh", async (_, rpcCt) =>
            {
                if (!Options(context).Usage) return new JsonObject { ["limits"] = new JsonArray() };
                return await usage.RefreshAsync(fetchCt => ClaudeUsageApi.FetchAsync(Http, ClaudeUsageApi.CredentialsPath(), fetchCt), rpcCt).ConfigureAwait(false);
            },
            "Asks Anthropic for the live subscription usage with the Claude CLI's login token (at most once a minute; within it the stored reading is the answer) → the snapshot, as claudecli.usage; also published as usage.claude");
        // The plan comes from `claude auth status`: only once the provider is on (a NetPI that does not use the CLI starts
        // no CLI process, idea-irqqkj), and again when it is switched on later.
        void PlanWhenEnabled()
        {
            if (Options(context).Enabled && Interlocked.Exchange(ref _planAsked, 1) == 0)
                _ = Task.Run(() => LoadPlanAsync(context, usage), ct);
        }
        PlanWhenEnabled();

        var sessions = new DataCliSessions(context.Data.Collection("cli_sessions", new CollectionSpec().Integer("updatedAt")));
        try { sessions.Prune(); }
        catch (Exception ex) { context.Logger.LogDebug(ex, "claude-cli: could not prune the stored sessions"); }
        // the prompt files and a side call's folder live in NetPI's home (owner-only), not in a temp folder other users share
        Provider = new ClaudeCliProvider(() => context.Settings.GetNode(SettingsPath) as JsonObject, context.Logger,
            sessionId => CwdOf(context, sessionId), () => HiddenEnvironment(context.Settings), usage, sessions,
            Path.Combine(context.Paths.Home, "claude-cli"),
            // a CLI tool is held back only where NetPI has its counterpart (registered, even if disabled in tools.disabled) and does
            // not offer it to the run (idea-hghbmg)
            () => [.. context.Tools.Registrations.Select(r => r.Tool.Definition.Name).Distinct(StringComparer.OrdinalIgnoreCase)])
        {
            // a CLI with a shell is told how it reaches NetPI itself (the ideas, memory.search, the MCP servers NetPI holds):
            // netpi.mjs ships beside the app, so the command works from any project's folder
            RpcScript = File.Exists(Path.Combine(AppContext.BaseDirectory, "netpi.mjs")) ? Path.Combine(AppContext.BaseDirectory, "netpi.mjs") : null,
            Home = context.Paths.Home,
        };
        // the CLI reports each model's real window with its first answer: the catalog (and the context ring) take it up
        Provider.OnModelsLearned = () => context.Events.Publish(EventTypes.ModelsChanged, new JsonObject { ["provider"] = "claude-cli" });
        context.Services.Register<IModelProvider>(Provider);
        // Enabling the provider or editing its models changes the catalog: say so, as the other providers do.
        _settingsSub = context.Events.Subscribe(EventTypes.SettingsChanged, _ =>
        {
            if (Provider is { } p && p.ModelsChangedSinceLastList())
                context.Events.Publish(EventTypes.ModelsChanged, new JsonObject { ["provider"] = p.Id });
            PlanWhenEnabled();
        });
        return Task.CompletedTask;
    }

    private int _planAsked;

    private static ClaudeCliOptions Options(IPluginContext context) => ClaudeCliOptions.Parse(context.Settings.GetNode(SettingsPath) as JsonObject);

    private static void SaveUsage(IPluginContext context, JsonObject snap) =>
        context.Data.Collection(UsageCollection, new CollectionSpec()).Put(UsageKey, snap.DeepClone().AsObject());

    /// <summary>The plan (<c>max</c>, <c>pro</c>…) from <c>claude auth status</c>: no tokens involved, and no usage spent.</summary>
    private static async Task LoadPlanAsync(IPluginContext context, ClaudeUsageTracker usage)
    {
        try
        {
            var exe = ClaudeCliProvider.ResolveExecutable(Options(context).Executable);
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
            return SessionWorkspace.Of(session) is not null && resolver is not null ? resolver.CwdOf(session) : context.Sessions.GetCwd(session);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(ex, "claude-cli: could not resolve the working folder of {Session}", sessionId);
            return null;
        }
    }

    public Task StopAsync(CancellationToken ct)
    {
        _settingsSub?.Dispose();
        _settingsSub = null;
        return Task.CompletedTask;
    }
}
