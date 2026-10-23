using System.Text.Json.Nodes;

namespace NetPI.Autonomy;

/// <summary>
/// Standing grants for the guardrails (<see cref="IToolGrants"/>): an ask rule allowed for good for one origin ("your chats",
/// "webhook Checkmk"). In an unattended chat with no grant the ask is refused instead of waiting for nobody, and kept, so the
/// owner can grant it from the tab.
/// </summary>
internal sealed class Grants(IPluginContext ctx, Chats chats, AutonomyStore store, Options options) : IToolGrants
{
    public ToolGrant? Find(GuardAsk ask)
    {
        var origin = chats.OriginOf(ask.SessionId);
        if (store.FindGrant(origin.Id, ask.Rule) is not { } doc) return null;
        var id = doc["id"]!.GetValue<string>();
        store.Used(id);
        return new ToolGrant(id, origin.Id, origin.Label, ask.Rule);
    }

    public string? Refuse(GuardAsk ask)
    {
        var session = chats.Get(ask.SessionId);
        if (!chats.Unattended(session)) return null;
        // asks wait for the owner when so set, except in a subagent: nobody watches its chat
        if (!options.RefuseAsks && session!.Kind != "subagent") return null;
        var origin = chats.OriginOf(session);
        store.Refused(origin, session, ask.Tool, "ask", ask.Subject, ask.Kind, ask.Rule);
        Changed("refusal");
        return $"Nobody answers questions in this chat (it works for {origin.Label}), so it did not run. The owner sees it in the " +
               $"Autonomy tab and can allow it for {origin.Label} from then on. Go on without it, and say in your report what you needed and why.";
    }

    public string? OriginLabel(string sessionId) => chats.OriginOf(sessionId).Label;

    public ToolGrant Grant(GuardAsk ask)
    {
        var origin = chats.OriginOf(ask.SessionId);
        var doc = store.Grant(origin, ask.Rule, ask.Tool, ask.Kind);
        Changed("grant");
        return new ToolGrant(doc["id"]!.GetValue<string>(), origin.Id, origin.Label, ask.Rule);
    }

    /// <summary>The tab's "allow from now on" on a refusal: a grant of its rule to its origin.</summary>
    public JsonObject GrantRefusal(string refusalId)
    {
        var r = store.Refusal(refusalId) ?? throw new RpcException("not_found", "No refusal with that id (it may have been dropped after its keep time).");
        if (r["grantable"]?.GetValue<bool>() != true || (string?)r["rule"] is not { Length: > 0 } rule)
            throw new RpcException("bad_request", "Only a guardrail's question can be allowed from now on; this refusal is what a chat that read outside text may never do (autonomy.taintedBlocked).");
        var origin = new Origin((string)r["origin"]!, (string?)r["label"] ?? (string)r["origin"]!);
        var doc = store.Grant(origin, rule, (string?)r["tool"] ?? "", (string?)r["kind"] ?? "");
        Changed("grant");
        return doc;
    }

    private void Changed(string what) => ctx.Events.Publish("autonomy.changed", new JsonObject { ["what"] = what });
}
