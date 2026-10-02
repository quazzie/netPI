using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Runtime;

/// <summary>
/// The prompt a run builds for itself when no plugin provides an <see cref="ISystemPromptBuilder"/>: the identity, the
/// prompt sections, the tools' guidelines and the role, joined once and frozen in the session's meta
/// (<see cref="SessionPrompt"/>), so later turns send the same prefix.
/// </summary>
internal static class FallbackPromptBuilder
{
    public static async Task<string> BuildAsync(IPluginContext ctx, PromptContext pc, long capturedRevision, CancellationToken ct)
    {
        var session = pc.Session;
        var environment = $"Working directory: {pc.Cwd}\nProject: {pc.Project?.Name ?? "none"}\nModel: {pc.Model.Ref}";
        if (session.Meta?["runtimeEnvironment"]?.GetValue<string>() != environment)
        {
            ctx.Sessions.AppendMessage(session.Id, ChatMessage.NoticeText(environment, "environment"));
            ctx.Sessions.UpdateSession(session.Id, s => { s.Meta ??= new JsonObject(); s.Meta["runtimeEnvironment"] = environment; });
        }
        if (SessionPrompt.Fallback(session) is { } frozen) return frozen;
        var sections = ctx.Services.GetAll<IPromptSection>().OrderBy(s => s.Order).ToList();
        var parts = new List<string>();
        var identity = SessionIdentity.Of(session);
        if (identity is not null) parts.Add(identity);
        var renderedSections = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in sections)
        {
            if (identity is not null && section.Id == "identity") continue;
            try
            {
                var text = await section.RenderAsync(pc, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(text)) { parts.Add(text.Trim()); renderedSections.Add(section.Id); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { ctx.Logger.LogWarning(ex, "Prompt section {Section} failed", section.Id); }
        }
        if (identity is null && !renderedSections.Contains("identity")) parts.Insert(0,
            ctx.Settings.Get("context.customPrompt", "") is { Length: > 0 } custom ? custom : Fallback(pc));
        if (!renderedSections.Contains("tools"))
        {
            var guidelines = pc.Tools.SelectMany(t => t.PromptGuidelines ?? []).Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).Distinct(StringComparer.Ordinal);
            parts.Add(string.Join("\n", guidelines.Select(g => "- " + g)));
        }
        if (!renderedSections.Contains("subagent") && !string.IsNullOrWhiteSpace(pc.Instructions)) parts.Add("# Your role\n" + pc.Instructions.Trim());
        var rendered = string.Join("\n\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        ctx.Sessions.UpdateSession(session.Id, s =>
        {
            if (SessionPrompt.Revision(s) != capturedRevision) return;
            s.Meta ??= new JsonObject();
            s.Meta[SessionPrompt.FallbackKey] = rendered;
            s.Meta[SessionPrompt.FallbackRevisionKey] = capturedRevision;
        });
        return rendered;
    }

    internal static string Fallback(PromptContext pc)
    {
        var sb = new StringBuilder();
        // Only used without the context plugin (which freezes the prompt and announces the working directory in notices):
        // no date or time, but the working directory has to be here.
        sb.Append("You are a coding agent running in NetPI, an agent harness on the user's machine. ");
        sb.Append("Be concise. Act, don't just describe: use your tools to do the work and check the result.\n\n");
        sb.Append("OS: ").Append(RuntimeInformation.OSDescription).Append('\n');
        sb.Append("Working directory: ").Append(pc.Cwd).Append('\n');
        sb.Append("Project: ").Append(pc.Project is { } p ? $"{p.Name} ({p.Path})" : "none").Append('\n');
        sb.Append("Model: ").Append(pc.Model.Ref);
        return sb.ToString();
    }
}