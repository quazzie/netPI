using System.Text.Json.Nodes;

namespace NetPI.Plan;

/// <summary>
/// The plans, in this plugin's data (collection <c>plans</c>, indexed by chat, status and time). One document per plan
/// episode of a chat: <c>{ id, sessionId, projectId?, status, revision, title, plan, revisions: [{ n, at, plan, feedback? }],
/// callId?, ideaId?, ideaSectionId?, ideaRevision?, filePath?, newSessionId?, createdAt, updatedAt }</c>. Status: <c>awaiting</c>
/// (the user decides), <c>revising</c> (changes were asked for, the next revision is being written), <c>approved</c>,
/// <c>cancelled</c>. The chat's session meta holds only the mode (<see cref="PlanMode"/>), never a plan.
/// </summary>
internal sealed class PlanStore(IPluginContext ctx)
{
    public const string Awaiting = "awaiting";
    public const string Revising = "revising";
    public const string Approved = "approved";
    public const string Cancelled = "cancelled";

    private IDataCollection Plans => ctx.Data.Collection("plans", new CollectionSpec().Text("sessionId").Text("status").Integer("updatedMs"));

    public JsonObject? Get(string id) => Plans.Get(id);

    public void Put(JsonObject doc)
    {
        var now = DateTimeOffset.UtcNow;
        doc["updatedAt"] = now.ToString("O");
        doc["updatedMs"] = now.ToUnixTimeMilliseconds();
        Plans.Put(doc["id"]!.GetValue<string>(), doc);
    }

    /// <summary>A chat's plans, newest first; <paramref name="status"/> narrows them.</summary>
    public IReadOnlyList<JsonObject> ForSession(string sessionId, string? status = null)
    {
        var q = new DataQuery().Eq("sessionId", sessionId).Order("updatedMs", descending: true);
        if (status is not null) q.Eq("status", status);
        return [.. Plans.Find(q).Select(d => d.Doc)];
    }

    public IReadOnlyList<JsonObject> WithStatus(params string[] statuses) =>
        [.. Plans.Find(new DataQuery().In("status", statuses.Cast<object?>()).Order("updatedMs", descending: true)).Select(d => d.Doc)];

    /// <summary>A deleted chat's plans go with it (what was saved as an idea or a file stays).</summary>
    public void DeleteSession(string sessionId) => Plans.DeleteWhere(new DataQuery().Eq("sessionId", sessionId));

    /// <summary>Starts a plan episode of the chat: its first revision is written by <see cref="Revise"/>.</summary>
    public JsonObject Create(SessionInfo session)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        return new JsonObject
        {
            ["id"] = "plan_" + Ids.Short(12),
            ["sessionId"] = session.Id,
            ["projectId"] = session.ProjectId,
            ["status"] = Revising,
            ["revision"] = 0,
            ["title"] = "",
            ["plan"] = null,
            ["revisions"] = new JsonArray(),
            ["createdAt"] = now,
        };
    }

    /// <summary>Writes the next revision (or, when the plan is the one already waiting, nothing new): the document goes back to awaiting.</summary>
    public void Revise(JsonObject doc, PlanBody body, string callId)
    {
        var n = doc["revision"]!.GetValue<int>() + 1;
        doc["revision"] = n;
        doc["title"] = body.Title;
        doc["plan"] = body.ToJson();
        doc["status"] = Awaiting;
        doc["callId"] = callId;
        ((JsonArray)doc["revisions"]!).Add(new JsonObject { ["n"] = n, ["at"] = DateTimeOffset.UtcNow.ToString("O"), ["plan"] = body.ToJson() });
    }

    /// <summary>The user's feedback on the latest revision (what the next one answers).</summary>
    public static void NoteFeedback(JsonObject doc, string feedback)
    {
        if (doc["revisions"] is JsonArray { Count: > 0 } revisions && revisions[^1] is JsonObject last) last["feedback"] = feedback;
    }

    public static PlanBody Body(JsonObject doc) => PlanBody.FromJson(doc["plan"]);

    /// <summary>The document as the UI and RPC callers get it: the latest plan and its markdown, without the revisions' bodies unless asked.</summary>
    public static JsonObject View(JsonObject doc, bool revisions)
    {
        var view = (JsonObject)doc.DeepClone();
        view["markdown"] = doc["plan"] is null ? null : Body(doc).Markdown();
        if (!revisions && view["revisions"] is JsonArray all)
            view["revisions"] = new JsonArray([.. all.OfType<JsonObject>().Select(r => (JsonNode)new JsonObject { ["n"] = r["n"]?.DeepClone(), ["at"] = r["at"]?.DeepClone(), ["feedback"] = r["feedback"]?.DeepClone() })]);
        return view;
    }
}
