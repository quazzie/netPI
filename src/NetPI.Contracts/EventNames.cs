namespace NetPI;

// Event names of the higher abstractions. The core's own names stay in EventTypes; these live with the contract that owns them.

/// <summary>Events of the workspace concept (the Workspaces plugin publishes them).</summary>
public static class WorkspaceEvents
{
    /// <summary>A session was bound to a workspace (or unbound): <c>{ sessionId, workspaceId, cwd, binding }</c>. The binding is
    /// the resolved <see cref="WorkspaceBinding"/> (null when unbound), so a consumer does not have to resolve it again and cannot
    /// end up with a different root than the one the switch announced.</summary>
    public const string SessionBound = "session.workspace";
}

/// <summary>Events of the agent scheduler (the Agents plugin publishes them).</summary>
public static class AgentSchedulerEvents
{
    public const string Changed = "agents.changed";
}

/// <summary>Events of the shell's process registry.</summary>
public static class ProcessEvents
{
    public const string Started = "process.started";
    public const string Exited = "process.exited";
    public const string Output = "process.output";
}
