using System.Text.Json;

namespace NetPI.Abstractions;

/// <summary>
/// astra-1 D: projects a persisted <see cref="EntryKind.ProjectContext"/> entry
/// into exactly ONE model-facing user message. The message identity is
/// derived deterministically from the ENTRY ID (not the content), so replay
/// and reconstruction of the same persisted entry yield the same message id —
/// provider fingerprints stay stable and a second duplicate is never stored.
/// </summary>
public static class ProjectContextProjection
{
    private static readonly JsonSerializerOptions WireOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The model-facing message for a persisted project-context change.
    /// Returns null for entries that are not project-context events.
    /// </summary>
    public static AgentMessage? Project(SessionEntry entry)
        => entry.Kind == EntryKind.ProjectContext
            ? Project(Snapshot(entry) ?? default, entry.Id, entry.CreatedAt)
            : null;

    /// <summary>
    /// Build the projected message for an explicit snapshot. The text explains
    /// the provenance and embeds the exact resolved AGENTS content (never a
    /// paraphrase); the system prompt conveys the meaning of these
    /// application-generated context messages.
    /// </summary>
    public static AgentMessage Project(
        ProjectContextSnapshot? snapshot, string entryId, DateTimeOffset createdAt)
    {
        var text = ProjectText(snapshot);
        return new AgentMessage(
            MessageIdentity.DeterministicId("project-context", entryId),
            MessageRole.User,
            [new TextPart(text)],
            createdAt);
    }

    /// <summary>The projected user message for an explicit snapshot (entry id = snapshot id).</summary>
    public static AgentMessage Project(ProjectContextSnapshot snapshot, DateTimeOffset createdAt)
        => Project(snapshot, snapshot.ProjectId, createdAt);

    /// <summary>The exact text of the projected message for a snapshot.</summary>
    public static string ProjectText(ProjectContextSnapshot? snapshot)
    {
        if (snapshot is null)
            return string.Empty;
        var sb = new System.Text.StringBuilder();
        sb.Append("Project changed to ").Append(snapshot.ProjectName).Append(".\n");
        sb.Append("Workspace: ").Append(snapshot.WorkspacePath).Append("\n\n");
        sb.Append("Use this workspace for subsequent relative tool paths.\n");
        sb.Append("These project instructions supersede the previous project's instructions.\n\n");
        if (string.IsNullOrWhiteSpace(snapshot.EffectiveInstructions))
        {
            sb.Append("<project_instructions>\n(no project instructions were found for this workspace)\n</project_instructions>\n");
        }
        else
        {
            sb.Append("<project_instructions>\n").Append(snapshot.EffectiveInstructions.TrimEnd()).Append("\n</project_instructions>\n");
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    /// <summary>
    /// Deserialize the <see cref="ProjectContextSnapshot"/> stored in a
    /// project-context entry's payload (null when absent/unreadable).
    /// </summary>
    public static ProjectContextSnapshot? Snapshot(SessionEntry entry)
    {
        if (entry.Payload is not { } payload) return null;
        try
        {
            return JsonSerializer.Deserialize<ProjectContextSnapshot>(
                payload.ToString(), WireOpts);
        }
        catch (JsonException) { return null; }
    }
}
