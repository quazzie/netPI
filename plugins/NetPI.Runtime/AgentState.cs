using System.Text.Json.Nodes;

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

    /// <summary>WaitAsync callers currently waiting on this agent's run (anyone: it keeps the agent in memory).</summary>
    public int ResultWaiters { get; set; }
    /// <summary>The waiters that are the agent's parent. When &gt; 0 at run end the parent gets the result from the wait, so it is not notified as well.</summary>
    public int ParentWaiters { get; set; }
    public bool ResultConsumed { get; set; }
    /// <summary>Id of the agent-result input queued at the parent (removed again if a WaitAsync consumes the result first).</summary>
    public string? PendingNotificationId { get; set; }
    /// <summary>The parent's transcript already has the report: its run took the agent-result notice, so a wait must not return it again.</summary>
    public bool NoticeDrained { get; set; }
    /// <summary>The run was cancelled because its parent was aborted: don't notify (or wake) the parent.</summary>
    public bool CancelledByParent { get; set; }

    /// <summary>Cancelled when user steering arrives, to interrupt a long <c>agent</c> wait. Replaced after each drain.</summary>
    public CancellationTokenSource SteerSignal { get; set; } = new();

    // status throttling
    public long LastStatusTick;
    public bool StatusTimerArmed;

    /// <summary>
    /// A subagent whose turn ended while subagents of its own still run: it is not finished (no report to its parent yet) and
    /// holds no run or instance. Their reports, or a message, wake it; it completes when a run ends with none left.
    /// </summary>
    public bool Parked { get; set; }

    public bool IsActive => Run is not null || Parked;

    /// <summary>
    /// The tool batch that waits for the user (<see cref="UserWait"/>): no run, no instance, no reload hold. Set while the
    /// batch runs (a wait is answerable from the moment its tool returned it), kept when the run ends, persisted with the
    /// agent's record; taken (set back to null) by whatever closes it: the run that goes on after the answers, a message
    /// of the user's, a stop, the chat's deletion.
    /// </summary>
    public PendingBatch? Pending { get; set; }

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
    /// <summary>Completes when the run has its slot or is waiting for one (status Running or Queued is then true), or has ended.</summary>
    public TaskCompletionSource SlotDecided { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
    /// <summary>
    /// This run's hold on the host's reload gate (<c>IReloadGate</c>): no plugin is reloaded while a run lives - a reload
    /// of this plugin would end it, one of another plugin could take a tool away from it. Released when the run has ended
    /// (<c>AgentRuntime.EndRunAsync</c>, after the run it hands over to, if any, took its own).
    /// </summary>
    public IDisposable? ReloadHold { get; set; }
    /// <summary>
    /// The batch this run goes on with, the user having answered every wait of it: its calls are finished (the answers,
    /// the calls allowed to run, the calls after them) before anything else. Null once they are; a run that fails before
    /// that hands the batch back to the agent (still waiting, its answers kept).
    /// </summary>
    public PendingBatch? Resume { get; set; }
}

/// <summary>
/// A tool batch stopped at calls that wait for the user: the assistant message the calls are in, the calls that have no
/// result yet (the waiting ones among them) in the order the model made them, and the waits with their answers so far.
/// Exactly one of the run that goes on, a message of the user's, a stop or the chat's deletion writes their results.
/// </summary>
internal sealed class PendingBatch
{
    /// <summary>The id of the assistant message whose calls these are.</summary>
    public required long AssistantId { get; init; }
    /// <summary>The calls with no result yet, in the model's order; set when the batch stops (a wait is answerable before that).</summary>
    public List<OpenCall> Open { get; set; } = [];
    public List<PendingWait> Waits { get; } = [];
    public bool AllAnswered => Waits.Count > 0 && Waits.All(w => w.Resolution is not null);
}

/// <summary>A call of a stopped batch with no result yet: its id, and the name its result is stored under (the outer <c>call_tool</c>'s, for a call made through it).</summary>
internal sealed record OpenCall(string Id, string ResultName);

/// <summary>One call that waits for the user (<see cref="UserWait"/>), with the answer once there is one.</summary>
internal sealed class PendingWait
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Activity { get; init; }
    public required string CallId { get; init; }
    public required string Tool { get; init; }
    public JsonObject? Data { get; init; }
    public required ToolResult IfSteered { get; init; }
    public ToolResult? IfCancelled { get; init; }
    public DateTimeOffset Since { get; init; } = DateTimeOffset.UtcNow;
    public WaitResolution? Resolution { get; set; }

    public static PendingWait From(UserWait w, string callId, string tool) => new()
    {
        Id = w.Id, Kind = w.Kind, Activity = w.Activity, CallId = callId, Tool = tool, Data = w.Data?.DeepClone().AsObject(),
        IfSteered = AgentStore.Plain(w.IfSteered), IfCancelled = w.IfCancelled is null ? null : AgentStore.Plain(w.IfCancelled),
    };

    public WaitingCall ToInfo(AgentInfo agent) => new()
    {
        Id = Id, Kind = Kind, SessionId = agent.SessionId, AgentId = agent.Id, CallId = CallId, Tool = Tool, Activity = Activity,
        Data = Data?.DeepClone().AsObject(), Since = Since, Answered = Resolution is not null,
    };
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
