// Shared domain types for the netPI web client.
// Mirrors the normalized event model in docs/archive/PLAN-v1.md §9 and the
// WebSocket protocol in §41. No OpenAI wire objects ever reach the browser.

export type MessageRole = "user" | "assistant" | "tool" | "system";

export type AgentState =
  | "Idle"
  | "Preparing"
  | "CallingModel"
  | "ExecutingTools"
  | "Compacting"
  | "Retrying"
  | "Cancelling";

export type InputModality = "text" | "image";

export interface ReasoningProfile {
  levels?: string[];
  defaultLevel?: string | null;
  [k: string]: unknown;
}

export interface ModelInfo {
  id: string;
  contextWindow?: number | null;
  maxOutputTokens?: number | null;
  inputModalities: InputModality[];
  reasoning?: ReasoningProfile | null;
}

// ---- Transcript blocks -----------------------------------------------------

export type Block =

  | UserBlock
  | AssistantBlock
  | ToolBlock
  | SystemBlock
  | ProjectContextBlock;
export interface UserBlock {
  kind: "user";
  id: string;
  text: string;
  createdAt: number;
}

export interface ThinkingBlock {
  kind: "thinking";
  id: string;
  text: string;
  done: boolean;
  startedAt?: number;
  durationMs?: number;
}

export interface ToolCall {
  id: string;
  name: string;
  argsJson: string;
  result?: string;
  isError?: boolean;
  durationMs?: number;
  /** PLAN §46: the call never received a result (host died mid-batch). */
  interrupted?: boolean;
  /**
   * astra-1 G3: explicit lifecycle, driven ONLY by start/completed/failure
   * events — output presence is NOT a completion signal (a running shell can
   * stream output for minutes). "running" is the terminal state of a live
   * call until tool.completed/failed lands.
   */
  status?: "running" | "done" | "failed" | "interrupted";
}

export interface AssistantBlock {
  kind: "assistant";
  id: string;
  createdAt: number;
  thinking?: ThinkingBlock;
  text: string;
  toolCalls: ToolCall[];
  done: boolean;
  usage?: Usage;
}

export interface ToolBlock {
  kind: "tool";
  id: string;
  name: string;
  argsPreview: string;
  output: string;
  isError: boolean;
  durationMs?: number;
  done: boolean;
}

export interface SystemBlock {
  kind: "system";
  id: string;
  text: string;
  createdAt: number;
}

/** astra-1 D: a project-change event (server entry type `project_context`).
 *  Provenance is the APPLICATION, not the model — the effective instructions
 *  snapshot taken at project switch/refresh. Rendered as a distinct block. */
export interface ProjectContextBlock {
  kind: "project_context";
  id: string;
  projectName: string;
  workspace: string;
  contentHash: string;
  text: string;
  createdAt: number;
}
export interface Usage {
  promptTokens?: number;
  completionTokens?: number;
  totalTokens?: number;
  reasoningTokens?: number;
  cacheHit?: number;
}

export interface RunStats {
  turns: number;
  toolSteps: number;
  tokensPerSec?: number;
  contextTokens?: number;
  contextLimit?: number;
  cacheHit?: number;
  elapsedMs?: number;
}

// ---- Server model ---------------------------------------------------------

export interface Model {
  type: "model";
  id: string;
  info: ModelInfo;
}

export interface PluginStatus {
  id: string;
  name: string;
  version?: string;

  generation: number;
  state: "active" | "draining" | "failed" | "unloading" | "collected";
  activeLeases: number;
  lastError?: string;
}

export interface SessionInfo {
  id: string;
  title: string;
  workspace: string;
  modelId?: string;
  reasoningLevel?: string;
  createdAt: number;
  updatedAt: number;
  /** astra-1 D2: the active project (F1/D2 make the project first-class). */
  project?: {
    id: string;
    name?: string;
    rootPath?: string;
    updatedAt?: number;
  };
  /** astra-1 G2 (F contract): the compaction policy (a global, static snapshot
   *  carried on the visible session's session.updated — not per-session state). */
  compaction?: { available: boolean; reserveTokens: number } | null;
}

// astra-1 C: a project row (project.list / project.created payloads —
// ToProjectJson in NetPI.Web). workspacePath is the project root.
export interface ProjectInfo {
  id: string;
  name: string;
  workspacePath: string;
  /** unix ms */
  updatedAt: number;
}

export interface WebPanelInfo {
  id: string;
  title: string;
  icon: string;
  entryUrl: string;
  order: number;
}
