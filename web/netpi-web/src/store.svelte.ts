import type {
  AgentState,
  Block,
  AssistantBlock,
  ToolCall,
  ModelInfo,
  PluginStatus,
  SessionInfo,
  RunStats,
  Usage,
  WebPanelInfo,
} from "./types";

let counter = 0;
export function uid(prefix = "b"): string {
  return `${prefix}_${Date.now().toString(36)}_${(counter++).toString(36)}`;
}

function saved(key: string): string {
  try { return localStorage.getItem(key) ?? ""; } catch { return ""; }
}
function save(key: string, value: string): void {
  try {
    if (value) localStorage.setItem(key, value);
    else localStorage.removeItem(key);
  } catch {}
}

/** A single steering message queued for the next turn boundary. */
export interface QueuedSteer {
  id: string;
  text: string;
}

export class NetPIStore {
  // ---- connection / runtime -------------------------------------------
  connection = $state<"connecting" | "open" | "closed">("connecting");
  agentState = $state<AgentState>("Idle");
  busy = $derived(this.agentState !== "Idle" && this.agentState !== "Cancelling");
  activity = $state<string | null>(null);
  /** Last model.wire notice for the in-flight run, e.g. "responses → chat (function_call_output …)". */
  wireNote = $state<string | null>(null);
  requestPending = $state(false);

  // ---- session ----------------------------------------------------------
  session = $state<SessionInfo | null>(null);
  /** Sessions loaded so far (paginated); `sessionTotal` is the full server count. */
  sessions = $state<SessionInfo[]>([]);
  sessionTotal = $state(0);
  sessionMoreLoading = $state(false);
  /** Older sessions still hidden beyond the loaded page. */
  get sessionRemaining(): number {
    return Math.max(0, this.sessionTotal - this.sessions.length);
  }
  queuedSteer = $state<QueuedSteer[]>([]);

  // ---- astra-1 G1: open-session tabs + per-session transient state --------
  /** Ordered ids of open tabs; the visible session is `session.id`.
   *  G1: seeded from the persisted list — ids for sessions that no longer
   *  exist are trimmed against the first session list. */
  openTabIds = $state<string[]>(
    typeof localStorage === "undefined" ? [] : NetPIStore.loadTabs().tabs,
  );
  /** Per-session run state (all sessions, not just the visible one). */
  busySessions = $state<Record<string, AgentState>>({});
  /** Tabs that received activity while not selected. */
  unreadTabs = $state<Record<string, boolean>>({});
  /** Per-session composer drafts (bounded: 50 sessions, oldest dropped). */
  drafts = $state<Record<string, string>>({});
  /** Outer scroll follow intent per session (bounded: 50 entries). */
  scrollStick = $state<Record<string, boolean>>({});
  /** Per thinking-block disclosure override (block id → open/closed). */
  thinkingDisclosure = $state<Record<string, "open" | "closed">>({});

  static readonly TAB_STORAGE_KEY = "netpi.openTabs.v1";

  private boundedPut<T>(
    map: Record<string, T>,
    id: string,
    value: T,
    cap = 50,
  ): void {
    if (Object.keys(map).length >= cap && !(id in map)) {
      const first = Object.keys(map)[0];
      delete map[first];
    }
    map[id] = value;
  }

  openTab(id: string | null | undefined): void {
    if (!id) return;
    if (!this.openTabIds.includes(id))
      this.openTabIds = [...this.openTabIds, id];
    this.persistTabs();
  }

  closeTab(id: string): void {
    this.openTabIds = this.openTabIds.filter((x) => x !== id);
    delete this.unreadTabs[id];
    // Closing a tab is NOT cancelling a run or deleting the session — the run
    // keeps going server-side and the session stays in the global list.
    if (this.session?.id === id) {
      this.session = null;
      this.resetTranscript();
    }
    this.persistTabs();
  }

  private persistTabs(): void {
    if (typeof localStorage === "undefined") return;
    try {
      localStorage.setItem(
        NetPIStore.TAB_STORAGE_KEY,
        JSON.stringify({
          tabs: this.openTabIds,
          selected: this.session?.id ?? null,
        }),
      );
    } catch {}
  }

  static loadTabs(): { tabs: string[]; selected: string | null } {
    try {
      if (typeof localStorage === "undefined")
        return { tabs: [], selected: null };
      const raw = JSON.parse(
          localStorage.getItem(NetPIStore.TAB_STORAGE_KEY) || "{}",
        ) as { tabs?: unknown; selected?: unknown };
      const tabs = Array.isArray(raw.tabs)
        ? raw.tabs.filter((x): x is string => typeof x === "string")
        : [];
      const selected = typeof raw.selected === "string" ? raw.selected : null;
      return { tabs, selected };
    } catch {
      return { tabs: [], selected: null };
    }
  }

  /** G1: per-session run state — tab indicators (metadata, not the visible chat). */
  setBusySession(sid: string | null, state: AgentState): void {
    if (!sid) return;
    if (state === "Idle") delete this.busySessions[sid];
    else this.busySessions[sid] = state;
  }

  markUnread(sid: string | null): void {
    if (!sid || sid === this.session?.id) return;
    this.unreadTabs[sid] = true;
  }

  clearUnread(sid: string): void {
    delete this.unreadTabs[sid];
  }

  getDraft(sid: string | null): string {
    return (sid && this.drafts[sid]) || "";
  }

  setDraft(sid: string | null, text: string): void {
    if (!sid) return;
    this.boundedPut(this.drafts, sid, text);
  }

  setScrollStick(sid: string | null, stick: boolean): void {
    if (!sid) return;
    this.boundedPut(this.scrollStick, sid, stick);
  }

  setThinkingDisclosure(blockId: string, value: "open" | "closed" | null): void {
    if (value === null) delete this.thinkingDisclosure[blockId];
    else this.boundedPut(this.thinkingDisclosure, blockId, value, 500);
}

  // ---- transcript -------------------------------------------------------
  blocks = $state<Block[]>([]);
  /** Count of head blocks in `blocks` that are not rendered ("load earlier"). */
  hidden = $state(0);
  activeAssistantId = $state<string | null>(null);
  olderSeq = $state(0);
  moreAvailable = $state(false);
  olderLoading = $state(false);
  prependVersion = $state(0);

  // ---- model / reasoning ------------------------------------------------
  models = $state<ModelInfo[]>([]);
  currentModel = $state<string>(saved("netpi.lastModel"));
  reasoningLevel = $state<string>(saved("netpi.lastReasoning"));
  modelsRefreshing = $state(false);
  modelRefreshError = $state<string | null>(null);

  // ---- plugins / extension UI ------------------------------------------
  plugins = $state<PluginStatus[]>([]);
  webPanels = $state<WebPanelInfo[]>([]);
  /** astra-1 P5: in-flight plugin-update operations (operationId → kind).
   *  Set on the `ack` of plugin.reload/reloadAll/scan; cleared when the
   *  matching completion event arrives (runner-driven). Survives a reconnect
   *  so a UI busy indicator is never stranded, and lets a reconnecting client
   *  reconcile via the facade's GetOperation. */
  pendingPluginOps = $state<Record<string, string>>({});

  // ---- run stats (kept for diagnostics, not rendered as a status bar) ---
  stats = $state<RunStats>({ turns: 0, toolSteps: 0 });
  lastUsage = $state<Usage | null>(null);
  /** astra-1 F/G2: the session `lastUsage` belongs to — the context meter must
   *  not read another session's usage. */
  lastUsageSession = $state<string | null>(null);

  // ---- UI state ----------------------------------------------------------
  errorBanner = $state<string | null>(null);
  _modelQuery = $state("");

  // ----------------------------------------------------------------------
  // Transcript mutation helpers. Only the active block mutates.
  // ----------------------------------------------------------------------

  appendUser(text: string): string {
    const id = uid("u");
    this.blocks.push({ kind: "user", id, text, createdAt: Date.now() });
    return id;
  }

  startAssistant(): string {
    // The model may emit duplicate start markers during a transparent wire
    // fallback. Re-use an empty active shell rather than creating blank rows.
    const existing = this.active();
    if (existing && !existing.done && !existing.text && !existing.thinking && existing.toolCalls.length === 0)
      return existing.id;

    const id = uid("a");
    const block: AssistantBlock = {
      kind: "assistant",
      id,
      createdAt: Date.now(),
      text: "",
      toolCalls: [],
      done: false,
    };
    this.blocks.push(block);
    this.activeAssistantId = id;
    this.activity = "Model connected · waiting for output…";
    return id;
  }

  prependUser(text: string): string {
    const id = uid("u");
    this.blocks.unshift({ kind: "user", id, text, createdAt: Date.now() });
    return id;
  }

  prependSystem(text: string): string {
    const id = uid("s");
    this.blocks.unshift({ kind: "system", id, text, createdAt: Date.now() });
    return id;
  }

  prependAssistantShell(): string {
    const id = uid("a");
    const block: AssistantBlock = {
      kind: "assistant",
      id,
      createdAt: Date.now(),
      text: "",
      toolCalls: [],
      done: false,
    };
    this.blocks.unshift(block);
    this.activeAssistantId = id;
    return id;
  }

  private active(): AssistantBlock | null {
    if (!this.activeAssistantId) return null;
    const b = this.blocks.find((x) => x.id === this.activeAssistantId);
    return b && b.kind === "assistant" ? b : null;
  }

  startThinking(): void {
    const a = this.active();
    if (!a) return;
    if (!a.thinking) {
      a.thinking = {
        kind: "thinking",
        id: uid("t"),
        text: "",
        done: false,
        startedAt: Date.now(),
      };
    }
    this.activity = "Thinking…";
  }

  appendThinkingDelta(text: string): void {
    const a = this.active();
    if (!a?.thinking) return;
    a.thinking.text += text;
    this.activity = "Thinking…";
  }

  completeThinking(): void {
    const a = this.active();
    if (!a?.thinking) return;
    a.thinking.done = true;
    if (a.thinking.startedAt)
      a.thinking.durationMs = Math.max(0, Date.now() - a.thinking.startedAt);
    this.activity = a.text ? "Responding…" : "Preparing response…";
  }

  appendTextDelta(text: string): void {
    const a = this.active();
    if (!a) return;
    a.text += text;
    this.activity = "Responding…";
  }

  startToolCall(id: string, name: string, interrupted = false): void {
    const a = this.active();
    if (!a) return;
    if (a.toolCalls.some((t) => t.id === id)) return;
    const call: ToolCall = { id, name, argsJson: "", interrupted };
    a.toolCalls.push(call);
    this.stats.toolSteps += 1;
    if (!interrupted) this.activity = `Running ${name}…`;
  }

  appendToolArgsDelta(id: string, delta: string): void {
    const a = this.active();
    const c = a?.toolCalls.find((t) => t.id === id);
    if (c) c.argsJson += delta;
  }

  setToolResult(id: string, output: string, isError: boolean, append = false): void {
    const a = this.active();
    const c = a?.toolCalls.find((t) => t.id === id);
    if (c) {
      c.result = append ? (c.result ?? "") + output : output;
      if (!append) c.isError = isError;
    }
  }

  completeToolCall(id: string, durationMs: number): void {
    const a = this.active();
    const c = a?.toolCalls.find((t) => t.id === id);
    if (c) c.durationMs = durationMs;
    this.activity = "Continuing…";
  }

  completeAssistant(usage?: Usage): void {
    const a = this.active();
    if (!a) return;
    a.done = true;
    if (a.thinking) {
      a.thinking.done = true;
      if (!a.thinking.durationMs && a.thinking.startedAt)
        a.thinking.durationMs = Math.max(0, Date.now() - a.thinking.startedAt);
    }
    if (usage) {
      a.usage = usage;
      this.lastUsage = usage;
      this.lastUsageSession = this.session?.id ?? null;
    }
    this.stats.turns += 1;
    this.activeAssistantId = null;
    this.drainSteering();
    if (this.agentState !== "Idle") this.activity = "Continuing…";
  }

  resetAssistantForRetry(): void {
    if (!this.activeAssistantId) return;
    const i = this.blocks.findIndex((x) => x.id === this.activeAssistantId);
    if (i >= 0) this.blocks.splice(i, 1);
    this.activeAssistantId = null;
    this.activity = "Retrying model request…";
  }

  failAssistant(message: string): void {
    const a = this.active();
    if (a) {
      a.text = a.text + (a.text ? "\n" : "") + message;
      a.done = true;
      if (a.thinking) a.thinking.done = true;
      this.activeAssistantId = null;
    }
    this.activity = null;
  }

  private drainSteering(): void {
    if (this.queuedSteer.length === 0) return;
    for (const q of this.queuedSteer) this.appendUser(q.text);
    this.queuedSteer = [];
  }

  // ----------------------------------------------------------------------
  // Composer / control surface.
  // ----------------------------------------------------------------------

  beginSubmit(): void {
    this.requestPending = true;
    this.activity = this.busy ? "Queueing steering message…" : "Sending to agent…";
  }

  requestAccepted(): void {
    // Keep the optimistic running indicator alive until the first authoritative
    // agent.state arrives. The WS ack can beat AgentStarting/Preparing by a few
    // milliseconds; clearing here caused the exact "did it send?" dead-air the
    // chat UI must avoid.
    this.requestPending = this.agentState === "Idle";
    if (this.activity === "Sending to agent…") this.activity = "Accepted · preparing…";
    if (this.activity === "Queueing steering message…") this.activity = "Steering queued";
  }

  requestFailed(message: string): void {
    this.requestPending = false;
    this.activity = null;
    this.setError(message);
  }

  /** Send when idle, steer when busy. */
  submit(message: string): "sent" | "steered" {
    const text = message.trim();
    if (!text) return "sent";
    if (this.busy) {
      this.queuedSteer.push({ id: uid("q"), text });
      return "steered";
    }
    this.appendUser(text);
    return "sent";
  }

  cancel(): void {
    this.agentState = "Cancelling";
    this.activity = "Stopping…";
  }

  // ----------------------------------------------------------------------
  // Server-driven state.
  // ----------------------------------------------------------------------

  /** Record the wire decision for the current run (model.wire event, PLAN §47). */
  noteModelWire(wire: string, fallback: boolean, reason: string | null): void {
    if (!fallback) {
      this.wireNote = null;
      return;
    }
    const text = `⚠ responses wire failed → fell back to ${wire} (${reason ?? "unknown"}); full transcript resent, session chain off for this run`;
    this.wireNote = text;
    this.appendSystem(text);
  }

  applyAgentState(state: AgentState): void {
    this.agentState = state;
    this.requestPending = false;
    if (state === "Idle" || state === "Preparing") this.wireNote = null;
    switch (state) {
      case "Idle": this.activity = null; break;
      case "Preparing": this.activity = "Preparing context…"; break;
      case "CallingModel": this.activity = "Waiting for model…"; break;
      case "ExecutingTools":
        if (!this.activity?.startsWith("Running ")) this.activity = "Running tools…";
        break;
      case "Compacting": this.activity = "Compacting context…"; break;
      case "Retrying": this.activity = "Retrying model request…"; break;
      case "Cancelling": this.activity = "Stopping…"; break;
    }
  }

  applySession(info: SessionInfo): void {
    this.session = info;
    // astra-1 G1: making a session visible registers it as an open tab and
    // clears its unread marker (a run completing must never move a tab).
    this.openTab(info.id);
    this.clearUnread(info.id);
    if (info.modelId) this.setModel(info.modelId, false);
    if (info.reasoningLevel) this.setReasoning(info.reasoningLevel, false);
    else if (info.modelId) this.syncReasoning();
  }

  /** Insert or refresh a session in the loaded drawer page (PLAN §43 pagination). */
  upsertSession(info: SessionInfo): void {
    const i = this.sessions.findIndex((s) => s.id === info.id);
    if (i >= 0) {
      this.sessions[i] = info;
      return;
    }
    // Only add unseen sessions at the head while they belong to the newest page.
    if (this.sessions.length === 0 || info.updatedAt >= (this.sessions[0]?.updatedAt ?? 0))
      this.sessions.unshift(info);
    this.sessionTotal = Math.max(this.sessionTotal, this.sessions.length);
  }

  loadModels(models: ModelInfo[]): void {
    this.models = models;
    this.modelsRefreshing = false;

    if (!this.currentModel || !models.some((m) => m.id === this.currentModel)) {
      const sessionModel = this.session?.modelId;
      this.currentModel =
        (sessionModel && models.some((m) => m.id === sessionModel) ? sessionModel : "") ||
        models[0]?.id ||
        "";
    }
    save("netpi.lastModel", this.currentModel);
    this.syncReasoning();
  }

  setModel(id: string, persist = true): void {
    this.currentModel = id;
    if (persist) save("netpi.lastModel", id);
    this.syncReasoning();
  }

  setReasoning(level: string, persist = true): void {
    this.reasoningLevel = level;
    if (persist) save("netpi.lastReasoning", level);
  }

  private syncReasoning(): void {
    const profile = this.models.find((x) => x.id === this.currentModel)?.reasoning;
    const levels = profile?.levels ?? [];
    if (!levels.length) {
      this.reasoningLevel = "";
      return;
    }
    if (this.reasoningLevel && (this.reasoningLevel === "off" || levels.includes(this.reasoningLevel))) return;

    const sessionLevel = this.session?.reasoningLevel;
    if (sessionLevel && (sessionLevel === "off" || levels.includes(sessionLevel))) {
      this.reasoningLevel = sessionLevel;
      return;
    }

    const advertisedDefault = profile?.defaultLevel;
    if (advertisedDefault && levels.includes(advertisedDefault)) {
      this.reasoningLevel = advertisedDefault;
      return;
    }

    // Do not invent a reasoning level. The composer shows "off" until one is
    // explicitly selected or the provider advertises a default.
    this.reasoningLevel = "";
  }

  get currentModelInfo(): ModelInfo | undefined {
    return this.models.find((m) => m.id === this.currentModel);
  }

  setPluginReloadState(id: string, state: PluginStatus["state"]): void {
    const p = this.plugins.find((x) => x.id === id);
    if (p) p.state = state;
  }

  /** astra-1 P5: mark an in-flight plugin-update op complete (clears the
   *  pending id; a no-op if it was never registered, e.g. the ack was missed). */
  notePluginOpDone(operationId: string | undefined): void {
    if (!operationId) return;
    delete this.pendingPluginOps[operationId];
  }

  /** astra-1 P5: register an in-flight plugin-update op (operationId → kind). */
  notePluginOpPending(operationId: string, kind: string): void {
    this.pendingPluginOps[operationId] = kind;
  }

  /** astra-1 P5: is any plugin-update operation still in flight? */
  get pluginOpPending(): boolean {
    return Object.keys(this.pendingPluginOps).length > 0;
  }

  setError(msg: string | null): void {
    this.errorBanner = msg;
    if (msg) this.modelRefreshError = msg;
  }

  appendSystem(text: string): void {
    this.blocks.push({
      kind: "system",
      id: uid("s"),
      text,
      createdAt: Date.now(),
    });
  }

  resetTranscript(): void {
    this.blocks = [];
    this.hidden = 0;
    this.activeAssistantId = null;
    this.queuedSteer = [];
    this.stats = { turns: 0, toolSteps: 0 };
    this.lastUsage = null;
    this.lastUsageSession = null;
    this.olderSeq = 0;
    this.moreAvailable = false;
    this.olderLoading = false;
    this.activity = null;
  }

  noteOlderLoaded(fetched: number): void {
    this.hidden += fetched;
    this.prependVersion++;
    this.olderLoading = false;
  }

  /** Blocks currently rendered (tail of `blocks`, head hidden). */
  get revealed(): Block[] {
    return this.hidden > 0 ? this.blocks.slice(this.hidden) : this.blocks;
  }

  /** After (re)loading a session: show only the newest `count` blocks. */
  revealTail(count: number): void {
    this.hidden = Math.max(0, this.blocks.length - count);
  }

  /** Reveal up to `count` hidden head blocks (the "load earlier" action). */
  revealMore(count: number): void {
    this.hidden = Math.max(0, this.hidden - count);
  }
}

/** How many tail blocks are shown right after a session (re)load. */
export const REVEAL_INITIAL = 40;
/** How many hidden blocks "load earlier" reveals per click. */
export const REVEAL_STEP = 40;

export const store = new NetPIStore();
