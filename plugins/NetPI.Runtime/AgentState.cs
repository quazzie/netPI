namespace NetPI.Runtime;

/// <summary>Mutable runtime state of one agent. Guard every field with <see cref="Gate"/>.</summary>
internal sealed class AgentState(AgentInfo info)
{
    public readonly Lock Gate = new();
    /// <summary>Serializes status snapshot+publish so events leave in snapshot order.</summary>
    public readonly Lock PublishGate = new();

    public AgentInfo Info { get; } = info;
    /// <summary>Subagent role instructions (rendered by the context plugin's subagent section).</summary>
    public string? Instructions { get; set; }
    public bool NotifyParent { get; set; } = true;

    public List<UserInput> Steering { get; } = [];
    public List<UserInput> FollowUps { get; } = [];

    /// <summary>The active run (null when idle). Set under <see cref="Gate"/> when a run is created, cleared when it ends.</summary>
    public RunState? Run { get; set; }
    /// <summary>Completed when the current run ends (WaitAsync awaits it).</summary>
    public TaskCompletionSource RunDone { get; set; } = CompletedTcs();

    /// <summary>WaitAsync callers currently waiting on this agent's run. When &gt; 0 at run end, the result counts as delivered.</summary>
    public int ResultWaiters { get; set; }
    public bool ResultConsumed { get; set; }
    /// <summary>Id of the agent-result input queued at the parent (removed again if a WaitAsync consumes the result first).</summary>
    public string? PendingNotificationId { get; set; }
    /// <summary>The run was cancelled because its parent was aborted: don't notify (or wake) the parent.</summary>
    public bool CancelledByParent { get; set; }

    /// <summary>Cancelled when user steering arrives, to interrupt a long <c>agent</c> wait. Replaced after each drain.</summary>
    public CancellationTokenSource SteerSignal { get; set; } = new();

    // status throttling
    public long LastStatusTick;
    public bool StatusTimerArmed;

    public bool IsActive => Run is not null;

    public static TaskCompletionSource CompletedTcs()
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.SetResult();
        return t;
    }

    public List<QueuedInput> QueueSnapshot()
    {
        var list = new List<QueuedInput>(Steering.Count + FollowUps.Count);
        foreach (var i in Steering) list.Add(new QueuedInput { Id = i.Id, Text = i.Text, Mode = "steer", Source = i.Source, CreatedAt = i.CreatedAt });
        foreach (var i in FollowUps) list.Add(new QueuedInput { Id = i.Id, Text = i.Text, Mode = "queue", Source = i.Source, CreatedAt = i.CreatedAt });
        return list;
    }

    /// <summary>The queue as persisted in the agent record (caller holds <see cref="Gate"/>).</summary>
    public List<QueuedRecord> QueueRecords()
    {
        var list = new List<QueuedRecord>(Steering.Count + FollowUps.Count);
        foreach (var i in Steering) list.Add(new QueuedRecord("steer", i));
        foreach (var i in FollowUps) list.Add(new QueuedRecord("queue", i));
        return list;
    }
}

/// <summary>One run: from input until the agent stops.</summary>
internal sealed class RunState
{
    private static int _next;
    public int Id { get; } = Interlocked.Increment(ref _next);
    public CancellationTokenSource Cts { get; } = new();
    public Task? Task { get; set; }
    /// <summary>The slot held by this run (null without a scheduler, or while yielded).</summary>
    public IAgentSlot? Lease { get; set; }
    /// <summary>The model the current lease was acquired for (to re-acquire after a yield).</summary>
    public ModelInfo? Model { get; set; }
    /// <summary>User abort (don't auto-start queued input afterwards).</summary>
    public bool Aborted { get; set; }
    /// <summary>Plugin stop.</summary>
    public bool Stopping { get; set; }
    public string? CancelReason { get; set; }
    /// <summary>Last non-empty assistant text of this run (the subagent's result).</summary>
    public string? LastAssistantText { get; set; }
    /// <summary>
    /// This run took queued input into the conversation (a steer drained, a follow-up delivered). Read and written under the
    /// agent's gate. A run that failed before taking any leaves the queue as it found it, and starting another run for the
    /// same queue would fail the same way: see <c>AgentRuntime.EndRunAsync</c>.
    /// </summary>
    public bool Delivered { get; set; }
}

internal static class AgentInfoExtensions
{
    public static AgentInfo Clone(this AgentInfo a) => new()
    {
        Id = a.Id,
        SessionId = a.SessionId,
        Name = a.Name,
        ParentAgentId = a.ParentAgentId,
        ParentSessionId = a.ParentSessionId,
        IsSubagent = a.IsSubagent,
        Depth = a.Depth,
        Status = a.Status,
        Model = a.Model,
        Agent = a.Agent,
        Activity = a.Activity,
        CreatedAt = a.CreatedAt,
        StartedAt = a.StartedAt,
        FinishedAt = a.FinishedAt,
        Runs = a.Runs,
        Turns = a.Turns,
        ToolCalls = a.ToolCalls,
        InputTokens = a.InputTokens,
        OutputTokens = a.OutputTokens,
        QueuedMessages = a.QueuedMessages,
        Task = a.Task,
        Result = a.Result,
        Error = a.Error,
        ToolAllowlist = a.ToolAllowlist is null ? null : [.. a.ToolAllowlist],
        Children = [.. a.Children],
    };

    public static bool IsTerminal(this AgentStatus s) => s is AgentStatus.Completed or AgentStatus.Failed or AgentStatus.Cancelled;
    public static bool IsBusy(this AgentStatus s) => s is AgentStatus.Running or AgentStatus.Queued or AgentStatus.Yielded;
}
