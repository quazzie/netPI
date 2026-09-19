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
} from "./types";

let counter = 0;
export function uid(prefix = "b"): string {
  return `${prefix}_${Date.now().toString(36)}_${(counter++).toString(36)}`;
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

  // ---- session ----------------------------------------------------------
  session = $state<SessionInfo | null>(null);
  sessions = $state<SessionInfo[]>([]);
  queuedSteer = $state<QueuedSteer[]>([]);

  // ---- transcript -------------------------------------------------------
  blocks = $state<Block[]>([]);
  // The id of the block that is currently streaming, or null when idle.
  activeAssistantId = $state<string | null>(null);

  // ---- model / reasoning ------------------------------------------------
  models = $state<ModelInfo[]>([]);
  currentModel = $state<string>("");
  reasoningLevel = $state<string>("");
  modelsRefreshing = $state(false);
  modelRefreshError = $state<string | null>(null);

  // ---- plugins ----------------------------------------------------------
  plugins = $state<PluginStatus[]>([]);

  // ---- run stats --------------------------------------------------------
  stats = $state<RunStats>({
    turns: 0,
    toolSteps: 0,
  });
  lastUsage = $state<Usage | null>(null);

  // ---- UI state ----------------------------------------------------------
  drawerOpen = $state(false);
  overlay = $state<null | "plugins" | "settings" | "diagnostics">(null);
  errorBanner = $state<string | null>(null);
  /** Search text for the model picker (transient UI state). */
  _modelQuery = $state("");
  // ------------------------------------------------------------------------
  // Transcript mutation helpers. Only the *active* block mutates; completed
  // blocks are left untouched so they can be rendered immutably (PLAN §38).
  // ------------------------------------------------------------------------

  appendUser(text: string): string {
    const id = uid("u");
    this.blocks.push({ kind: "user", id, text, createdAt: Date.now() });
    return id;
  }

  startAssistant(): string {
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
    return id;
  }

  private active(): AssistantBlock | null {
    if (!this.activeAssistantId) return null;
    const b = this.blocks.find((x) => x.id === this.activeAssistantId);
    return b && b.kind === "assistant" ? b : null;
  }

  /** Returns true when a block was actually mutated (caller can bail early). */
  startThinking(): void {
    const a = this.active();
    if (!a) return;
    if (!a.thinking) {
      a.thinking = {
        kind: "thinking",
        id: uid("t"),
        text: "",
        done: false,
      };
    }
  }

  appendThinkingDelta(text: string): void {
    const a = this.active();
    if (!a?.thinking) return;
    a.thinking.text += text;
  }

  completeThinking(): void {
    const a = this.active();
    if (!a?.thinking) return;
    a.thinking.done = true;
  }

  appendTextDelta(text: string): void {
    const a = this.active();
    if (!a) return;
    a.text += text;
  }

  startToolCall(id: string, name: string): void {
    const a = this.active();
    if (!a) return;
    if (a.toolCalls.some((t) => t.id === id)) return;
    const call: ToolCall = { id, name, argsJson: "" };
    a.toolCalls.push(call);
    this.stats.toolSteps += 1;
  }

  appendToolArgsDelta(id: string, delta: string): void {
    const a = this.active();
    const c = a?.toolCalls.find((t) => t.id === id);
    if (c) c.argsJson += delta;
  }

  setToolResult(id: string, output: string, isError: boolean): void {
    const a = this.active();
    const c = a?.toolCalls.find((t) => t.id === id);
    if (c) {
      c.result = output;
      c.isError = isError;
    }
  }

  completeAssistant(usage?: Usage): void {
    const a = this.active();
    if (!a) return;
    a.done = true;
    if (a.thinking) a.thinking.done = true;
    if (usage) {
      a.usage = usage;
      this.lastUsage = usage;
    }
    this.stats.turns += 1;
    this.activeAssistantId = null;
    this.drainSteering();
  }

  completeToolCall(id: string, durationMs: number): void {
    const a = this.active();
    const c = a?.toolCalls.find((t) => t.id === id);
    if (c) c.durationMs = durationMs;
  }

  private drainSteering(): void {
    // Steering messages queued while busy become user turns at the boundary.
    if (this.queuedSteer.length === 0) return;
    for (const q of this.queuedSteer) {
      this.appendUser(q.text);
    }
    this.queuedSteer = [];
  }

  // ------------------------------------------------------------------------
  // Composer / control surface.
  // ------------------------------------------------------------------------

  /** Send when idle, steer when busy (PLAN §Send/Steer behavior). */
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
  }

  // ------------------------------------------------------------------------
  // Server-driven state.
  // ------------------------------------------------------------------------

  applyAgentState(state: AgentState): void {
    this.agentState = state;
  }

  loadModels(models: ModelInfo[]): void {
    this.models = models;
    this.modelsRefreshing = false;
    if (!this.currentModel && models.length) this.currentModel = models[0].id;
    this.syncReasoning();
  }

  setModel(id: string): void {
    this.currentModel = id;
    this.syncReasoning();
  }

  setReasoning(level: string): void {
    this.reasoningLevel = level;
  }

  private syncReasoning(): void {
    const m = this.models.find((x) => x.id === this.currentModel);
    if (!m?.reasoning?.levels?.length) {
      this.reasoningLevel = "";
    }
  }

  get currentModelInfo(): ModelInfo | undefined {
    return this.models.find((m) => m.id === this.currentModel);
  }

  setPluginReloadState(id: string, state: PluginStatus["state"]): void {
    const p = this.plugins.find((x) => x.id === id);
    if (p) p.state = state;
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
    this.activeAssistantId = null;
    this.queuedSteer = [];
    this.stats = { turns: 0, toolSteps: 0 };
    this.lastUsage = null;
  }
}

export const store = new NetPIStore();
