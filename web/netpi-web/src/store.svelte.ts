import type {
  AgentAssignment,
  ImageAttachment,
  AgentState,
  Block,
  AssistantBlock,
  ToolCall,
  ModelInfo,
  PluginStatus,
  SessionInfo,
  RunStats,
  Usage,
  RunMetrics,
  WebPanelInfo,
  ProjectInfo,
} from "./types";
import type { NavState, NewDraftMeta } from "./nav-state";
import * as nav from "./nav-state";

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
  /** astra-1 G1: server-side search results (null = no active search; the picker
   *  shows these instead of the loaded global list while set). */
  sessionSearch = $state<SessionInfo[] | null>(null);
  sessionSearchTotal = $state(0);
  sessionSearchMoreLoading = $state(false);
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
  /** "New Session" draft — the ONLY non-tab selection. Set by startNewSession,
   *  cleared when a real session becomes visible (open or adopt). A draft is
   *  LOCAL: no database session exists until the first prompt (chat.send with
   *  no sessionId + the draft's projectId) creates one server-side. */
  newDraft = $state<NewDraftMeta | null>(null);
  /** A draft first-prompt (chat.send without sessionId) is in flight; its
   *  session.created is what adopts the draft into a real tab (ws-side flag). */
  draftSendPending = $state(false);
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

  // ---- astra-2 §13: logical assignment state (queued/waiting/suspended) ------
  /**
   * Nonterminal assignments per session (all sessions, not just the visible
   * one). Driven by the `agents.state` snapshot / `agent.updated` deltas. This
   * is the source for the tab "queued/waiting/suspended" badges — DISTINCT from
   * `busySessions` (the execution-phase AgentState, which reads Idle for a
   * queued/suspended record).
   */
  assignments = $state<Record<string, AgentAssignment[]>>({});
  /**
   * Explicit open-session intent from a background child being created. A
   * background child must NOT steal focus from the parent (astra-2 §13): the
   * shell publishes child creation as metadata/intent only, and the selected
   * conversation is replaced only by an explicit navigation (openSession).
   */
  pendingSessionOpen = $state<string | null>(null);
  /**
   * One-shot visible notice for a panel navigation that landed on a
   * deleted/unknown session (astra-2 §12.3: never silently navigate elsewhere).
   */
  panelNavNotice = $state<string | null>(null);
  setPanelNavNotice(message: string | null): void {
    this.panelNavNotice = message;
  }
  setPendingSessionOpen(id: string | null): void {
    this.pendingSessionOpen = id;
  }
  /** Replace the assignments for one session (snapshot / delta). */
  setAssignments(sid: string | null, rows: AgentAssignment[]): void {
    if (!sid) return;
    const next = new Map<string, AgentAssignment>();
    for (const a of this.assignments[sid] ?? []) next.set(a.assignmentId, a);
    for (const r of rows) {
      if (r.nonTerminal) next.set(r.assignmentId, r);
      else next.delete(r.assignmentId); // terminal → drop from the nonterminal set
    }
    this.assignments[sid] = [...next.values()];
    if (!this.assignments[sid].length) delete this.assignments[sid];
  }
  /**
   * The nonterminal, non-live assignments for a session (queued / waiting /
   * suspended / cancelling). A session with any of these shows a tab badge
   * even though its execution-phase state is Idle.
   */
  nonLiveAssignments(sid: string | null): AgentAssignment[] {
    if (!sid) return [];
    return (this.assignments[sid] ?? []).filter(
      (a) => a.lifecycle !== "running" && a.nonTerminal,
    );
  }
  /** True when a session has a queued/waiting/suspended (non-live) assignment. */
  hasNonLiveAssignment(sid: string | null): boolean {
    return this.nonLiveAssignments(sid).length > 0;
  }
  /**
   * Register a session in the drawer if unknown, so a Work-panel navigation to a
   * session absent from the loaded first-50 rows can still open by ID.
   */
  ensureSessionVisible(info: SessionInfo): void {
    if (this.sessions.some((s) => s.id === info.id)) {
      this.upsertSession(info);
      return;
    }
    this.sessions.unshift(info);
  }

  /** astra-2 §13: the latest lane/pool snapshot (metadata; the panel polls its own). */
  laneSnapshot = $state<Record<string, unknown> | null>(null);
  setLaneSnapshot(s: Record<string, unknown> | null): void {
    this.laneSnapshot = s;
  }


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

  // ----------------------------------------------------------------------
  // Session/tab transitions — the ONE place open-tab/selection state moves.
  // Every method applies a pure nav-state transition (src/nav-state.ts,
  // unit-tested) and returns the id whose transcript must be fetched via an
  // explicit session.open (null = nothing to fetch: already visible,
  // background-only change, or the new-session draft).
  // ----------------------------------------------------------------------

  private navState(): NavState {
    return { openIds: this.openTabIds, selected: this.session?.id ?? null, draft: this.newDraft };
  }

  /** Apply a transition result: sync tab ids + draft, resolve the selected
   *  session from the loaded global list (a placeholder until the open's
   *  session.updated lands the authoritative info), reset the transcript
   *  when the visible session actually changes. */
  private applyNavState(n: NavState): void {
    const selChanged = (this.session?.id ?? null) !== n.selected;
    this.openTabIds = [...n.openIds];
    this.newDraft = n.draft ? { ...n.draft } : null;
    this.session = n.selected
      ? this.sessions.find((s) => s.id === n.selected) ??
        { id: n.selected, title: "", workspace: "", createdAt: 0, updatedAt: 0 }
      : null;
    if (selChanged) this.resetTranscript();
    this.persistTabs();
  }

  /** The draft context captured from a session: its APPLIED project (a pending,
   *  not-yet-effective switch is never inherited) and the project root as the
   *  first prompt's workspace. */
  private draftFrom(s: SessionInfo | null): NewDraftMeta {
    return {
      workspace: s?.project?.rootPath ?? s?.workspace ?? null,
      projectId: s?.project?.id ?? null,
      projectName: s?.project?.name ?? null,
    };
  }

  /** Explicit navigation to an existing session (tab click, picker row,
   *  post-restore open). Opens/keeps the tab, selects it, drops any draft;
   *  the transcript is re-fetched by ws.openSession's session.open. */
  selectSession(id: string): void {
    const [n] = nav.openTab(this.navState(), id);
    this.applyNavState(n);
  }

  /** Close a tab (view operation — NOT a cancel, NOT a delete). When the
   *  closed tab was selected: select the nearest remaining tab (right
   *  neighbor, else left), or enter New Session mode when none remain.
   *  Returns the id to open next (null = nothing / draft). */
  closeSession(id: string): string | null {
    const lost = this.session?.id === id ? this.session : null;
    const [n, action] = nav.closeTab(this.navState(), id);
    if (n.draft && lost) n.draft = this.draftFrom(lost);
    this.applyNavState(n);
    delete this.unreadTabs[id];
    return action.kind === "open" ? action.id : null;
  }

  /** Explicit "New Session": instant local draft (see newDraft). Idempotent —
   *  hammering it never creates server sessions or resets an active draft. */
  startNewSession(): void {
    const from = this.session ? this.draftFrom(this.session) : (this.newDraft ?? { workspace: null, projectId: null, projectName: null });
    const [n] = nav.startNew(this.navState(), from);
    this.applyNavState(n);
  }

  /** The visible session (or an open tab) was deleted server-side: same
   *  transition as closing the tab — returns the id to open next. */
  sessionDeleted(id: string): string | null {
    this.sessions = this.sessions.filter((s) => s.id !== id);
    if (this.sessionSearch) this.sessionSearch = this.sessionSearch.filter((s) => s.id !== id);
    return this.closeSession(id);
  }

  /** A persisted/open tab id no longer exists server-side (stale restore or a
   *  failed open): trim it; if it was the selection, run the same transition.
   *  Returns the id to open next. */
  sessionNotFound(id: string): string | null {
    this.sessions = this.sessions.filter((s) => s.id !== id);
    const lost = this.session?.id === id ? this.session : null;
    const [n, action] = nav.sessionNotFound(this.navState(), id);
    if (n.draft && lost) n.draft = this.draftFrom(lost);
    this.applyNavState(n);
    return action.kind === "open" ? action.id : null;
  }

  /** The client's own draft first-prompt just created the server session:
   *  replace the local placeholder with the real tab. The ONLY path by which
   *  a server-created session becomes visible. The transcript is NOT reset —
   *  the optimistic user block from the send is already there. */
  adoptCreatedSession(info: SessionInfo): void {
    const [n] = nav.adoptCreated(this.navState(), info.id);
    this.upsertSession(info);
    this.openTabIds = [...n.openIds];
    this.newDraft = null;
    this.draftSendPending = false;
    this.session = info;
    this.clearUnread(info.id);
    if (info.compaction !== undefined) this.compactionPolicy = info.compaction;
    if (info.modelId) this.setModel(info.modelId, false);
    if (info.reasoningLevel) this.setReasoning(info.reasoningLevel, false);
    else if (info.modelId) this.syncReasoning();
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

  /** Monotonic "composer was prefilled" counter — the Composer focuses on bump. */
  prefillTick = $state(0);

  /**
   * Apply a panel-initiated composer prefill (`chat.prefill` from the Ideas
   * panel's "insert into chat"): append to the target session's draft (the
   * visible session when no id is given) and, for the VISIBLE session, bump
   * the tick so the composer takes focus. A prefill aimed at another session
   * still lands in that session's draft — it must not steal focus here.
   */
  prefill(text: string, sessionId: string | null): void {
    const sid = sessionId || this.session?.id;
    if (!sid) return;
    const existing = this.drafts[sid] ?? "";
    this.setDraft(sid, existing ? existing + "\n\n" + text : text);
    if (sid === this.session?.id) this.prefillTick += 1;
  }

  setScrollStick(sid: string | null, stick: boolean): void {
    if (!sid) return;
    this.boundedPut(this.scrollStick, sid, stick);
  }

  setThinkingDisclosure(blockId: string, value: "open" | "closed" | null): void {
    if (value === null) delete this.thinkingDisclosure[blockId];
    else this.boundedPut(this.thinkingDisclosure, blockId, value, 500);
  }

  /** astra-1 G3: per tool-call disclosure override (call id → open/closed);
   *  bounded like thinking — manual choices survive replays/switches. */
  toolDisclosure = $state<Record<string, "open" | "closed">>({});

  setToolDisclosure(callId: string, value: "open" | "closed" | null): void {
    if (value === null) delete this.toolDisclosure[callId];
    else this.boundedPut(this.toolDisclosure, callId, value, 500);
  }

  /** astra-1 D2: project switch enqueued while a run is in flight — shown as
   *  a pending notice until the run's safe boundary applies it (or clears it).
   *  One per session; a later selection supersedes the earlier row. */
  projectPending = $state<Record<string, { operationId: string; project: string }>>({});

  setProjectPending(sessionId: string, op: { operationId: string; project: string } | null): void {
    if (op === null) delete this.projectPending[sessionId];
    else this.projectPending[sessionId] = op;
  }

  /** astra-1 C (G1 gap): server-backed project list (project.list payload) +
   *  load state. Refreshed by ProjectPicker on open / after a create. */
  projects = $state<ProjectInfo[]>([]);
  projectsLoading = $state(false);
  setProjects(projects: ProjectInfo[]): void {
    this.projects = projects;
  }

  /** astra-1 C: a created/upserted project row (project.created payload) —
   *  upsert it in place so the picker shows the fresh name/updatedAt. */
  upsertProject(project: ProjectInfo): void {
    const i = this.projects.findIndex((p) => p.id === project.id);
    if (i >= 0) this.projects[i] = project;
    else this.projects = [project, ...this.projects];
  }

  /** astra-1 C (G1 gap): picker-local notice (project create/switch errors and
   *  the refresh-instructions result) — cleared by the matching applied event
   *  for a switch, or dismissed. */
  projectNotice = $state<{ operationId: string; message: string } | null>(null);

  setProjectNotice(message: string, operationId = ""): void {
    this.projectNotice = { operationId, message };
  }

  clearProjectNotice(operationId: string | null = null): void {
    if (operationId && this.projectNotice?.operationId !== operationId) return;
    this.projectNotice = null;
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
  /** astra-1 G2 (F contract): the compaction policy (a GLOBAL, static snapshot
   *  carried on the visible session's session.updated — availability + reserve;
   *  the meter derives the trigger from the selected model's window). Null until
   *  a visible session with a compaction policy arrives (no AutoCompact plugin →
   *  the popup shows "threshold not reported", never a fabricated zero). */
  compactionPolicy = $state<{ available: boolean; reserveTokens: number } | null>(null);

  // ---- UI state ----------------------------------------------------------
  errorBanner = $state<string | null>(null);
  _modelQuery = $state("");

  // ----------------------------------------------------------------------
  // Transcript mutation helpers. Only the active block mutates.
  // ----------------------------------------------------------------------

  appendUser(text: string, images: ImageAttachment[] = []): string {
    const id = uid("u");
    this.blocks.push({ kind: "user", id, text, images, createdAt: Date.now() });
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

  prependUser(text: string, images: ImageAttachment[] = []): string {
    const id = uid("u");
    this.blocks.unshift({ kind: "user", id, text, images, createdAt: Date.now() });
    return id;
  }

  prependSystem(text: string): string {
    const id = uid("s");
    this.blocks.unshift({ kind: "system", id, text, createdAt: Date.now() });
    return id;
  }

  /** astra-1 D: prepend (session.older pagination) variant of
   *  appendProjectContext. */
  prependProjectContext(block: {
    projectName: string;
    workspace: string;
    contentHash: string;
    text: string;
  }): string {
    const id = uid("p");
    this.blocks.unshift({ kind: "project_context", id, ...block, createdAt: Date.now() });
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
    const call: ToolCall = {
      id,
      name,
      argsJson: "",
      interrupted,
      // astra-1 G3: lifecycle starts explicitly; output does not complete.
      status: interrupted ? "interrupted" : "running",
    };
    a.toolCalls.push(call);
    this.stats.toolSteps += 1;
    if (!interrupted) this.activity = `Running ${name}…`;
  }

  appendToolArgsDelta(id: string, delta: string): void {
    const a = this.active();
    const c = a?.toolCalls.find((t) => t.id === id);
    if (c) c.argsJson += delta;
  }

  setToolResult(id: string, output: string, isError: boolean, append = false, images: ImageAttachment[] = []): void {
    const a = this.active();
    const c = a?.toolCalls.find((t) => t.id === id);
    if (c) {
      c.result = append ? (c.result ?? "") + output : output;
      if (!append) {
        c.images = images;
        c.isError = isError;
        // astra-1 G3: a terminal failure flagged by the (non-append) result
        // event ends "running" early; output chunks alone never do.
        if (isError && c.status === "running") c.status = "failed";
      }
    }
  }

  completeToolCall(id: string, durationMs: number): void {
    const a = this.active();
    const c = a?.toolCalls.find((t) => t.id === id);
    if (c) {
      c.durationMs = durationMs;
      // astra-1 G3: completion is the only terminal event that ends "running"
      // (output presence was never a completion signal).
      c.status = c.isError ? "failed" : "done";
    }
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

  /** Mark the final assistant block when the agent run reaches its terminal state. */
  completeRun(): void {
    const last = [...this.blocks].reverse().find((b) => b.kind === "assistant");
    if (last?.kind === "assistant" && last.done) last.endOfRun = true;
  }

  /**
   * Attach a run's end-of-turn metrics (run_metrics entry, live or replayed)
   * to the run's final assistant block — the status line renders from
   * `block.metrics` on the endOfRun block, identically live and after replay.
   */
  attachRunMetrics(m: RunMetrics): void {
    for (let i = this.blocks.length - 1; i >= 0; i--)
      if (this.blocks[i].kind === "assistant") {
        (this.blocks[i] as AssistantBlock).metrics = m;
        return;
      }
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

  /** astra-1 G3: the send request was REJECTED — drop the optimistic user
   *  block (the message was never persisted server-side) so a failed send
   *  does not look accepted. Text-only match on the last block; safe no-op
   *  if the transcript moved on (steer/queue/replay already overwrote it). */
  retractLastUser(text: string): void {
    const last = this.blocks[this.blocks.length - 1];
    if (last && last.kind === "user" && last.text === text)
      this.blocks.splice(this.blocks.length - 1, 1);
  }

  /** Send when idle, steer when busy. */
  submit(message: string, images: ImageAttachment[] = []): "sent" | "steered" {
    const text = message.trim();
    if (!text && !images.length) return "sent";
    if (this.busy) {
      this.queuedSteer.push({ id: uid("q"), text });
      return "steered";
    }
    this.appendUser(text, images);
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

  /**
   * Metadata refresh for the VISIBLE session only (session.updated for the
   * selected session). This NEVER opens or selects a tab — server events are
   * metadata; navigation happens only through selectSession/adoptCreatedSession
   * (explicit UI operations).
   */
  applySession(info: SessionInfo): void {
    if (this.session?.id !== info.id) return;
    this.session = info;
    if (info.compaction !== undefined) this.compactionPolicy = info.compaction;
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

  /** astra-1 D: a project-change event (entry type `project_context`).
   *  Distinct block kind — provenance is the application, not the model. */
  appendProjectContext(block: {
    projectName: string;
    workspace: string;
    contentHash: string;
    text: string;
  }): string {
    const id = uid("p");
    this.blocks.push({ kind: "project_context", id, ...block, createdAt: Date.now() });
    return id;
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
