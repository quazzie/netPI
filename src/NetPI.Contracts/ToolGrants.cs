namespace NetPI;

/// <summary>A guardrail's ask rule matched a call: what a grant is judged on.</summary>
public sealed record GuardAsk(string SessionId, string Tool, string Kind, string Rule, string Subject);

/// <summary>A standing permission: an ask rule the owner allowed for good for one kind of chat (its origin).</summary>
public sealed record ToolGrant(string Id, string Origin, string OriginLabel, string Rule);

/// <summary>
/// Standing grants, asked by the guardrails before they ask the user (the Autonomy plugin). Resolved per use: without
/// it (the plugin disabled or unloaded) the guardrails ask as they always did. Block rules and protected paths that
/// block are judged before it and never reach it.
/// </summary>
public interface IToolGrants
{
    /// <summary>The grant that lets this ask through without asking, or null.</summary>
    ToolGrant? Find(GuardAsk ask);

    /// <summary>
    /// Nobody would answer this ask in its chat (a chat started by a schedule or a webhook, or a subagent): why it is
    /// refused instead. The refusal is kept, so the owner can grant it later. Null: ask as usual.
    /// </summary>
    string? Refuse(GuardAsk ask);

    /// <summary>Who the chat works for, as the question card names it ("your chats", "webhook Checkmk"); null: grants do not apply.</summary>
    string? OriginLabel(string sessionId);

    /// <summary>The owner's "always": a grant of the ask's rule to its chat's origin.</summary>
    ToolGrant Grant(GuardAsk ask);
}
