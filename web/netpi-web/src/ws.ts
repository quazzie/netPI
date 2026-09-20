import { store } from "./store.svelte";
import type {
  AgentState,
  ModelInfo,
  PluginStatus,
  SessionInfo,
  Usage,
} from "./types";

/**
 * WebSocket client for the netPI Web plugin (PLAN §36, §41).
 *
 * Envelope: every message is `{ type, requestId?, sessionId?, payload }`.
 * Reconnects with backoff; the server re-sends bootstrap state on open.
 */
class NetPIWebSocket {
  private ws: WebSocket | null = null;
  private closedByUser = false;
  private requestId = 0;
  private pending = new Map<
    string,
    { resolve: (v: unknown) => void; reject: (e: Error) => void }
  >();
  private retryTimer: ReturnType<typeof setTimeout> | null = null;
  private retries = 0;

  connect(): void {
    if (this.ws && (this.ws.readyState === 0 || this.ws.readyState === 1)) {
      return;
    }
    store.connection = "connecting";
    const proto = location.protocol === "https:" ? "wss" : "ws";
    const ws = new WebSocket(`${proto}://${location.host}/ws`);
    this.ws = ws;

    ws.onopen = () => {
      this.retries = 0;
      store.connection = "open";
      store.setError(null);
    };

    ws.onmessage = (ev) => this.onMessage(ev);

    ws.onclose = () => {
      store.connection = "closed";
      this.rejectAllPending(new Error("websocket closed"));
      if (this.closedByUser) return;
      this.scheduleReconnect();
    };

    ws.onerror = () => {
      ws.close();
    };
  }

  private scheduleReconnect(): void {
    if (this.retryTimer) return;
    const delay = Math.min(10000, 500 * 2 ** this.retries++);
    this.retryTimer = setTimeout(() => {
      this.retryTimer = null;
      this.connect();
    }, delay);
  }

  close(): void {
    this.closedByUser = true;
    this.ws?.close();
  }

  /** Fire-and-forget command (best effort, no ack needed). */
  send(type: string, payload?: Record<string, unknown>): void {
    const requestId = `r${this.requestId++}`;
    this.sendRaw({ type, requestId, payload });
  }

  /** Command that expects a `{ ok | error }` ack for its requestId. */
  request(
    type: string,
    payload?: Record<string, unknown>,
  ): Promise<unknown> {
    return new Promise((resolve, reject) => {
      if (!this.ws || this.ws.readyState !== 1) {
        reject(new Error("not connected"));
        return;
      }
      const requestId = `r${this.requestId++}`;
      this.pending.set(requestId, { resolve, reject });
      this.sendRaw({ type, requestId, payload });
      setTimeout(() => {
        if (this.pending.delete(requestId)) {
          reject(new Error(`timeout waiting for ${type}`));
        }
      }, 15000);
    });
  }

  private sendRaw(msg: Record<string, unknown>): void {
    const ws = this.ws;
    if (ws && ws.readyState === 1) ws.send(JSON.stringify(msg));
  }

  private rejectAllPending(e: Error): void {
    for (const [, p] of this.pending) p.reject(e);
    this.pending.clear();
  }

  // ------------------------------------------------------------------
  // Server → client events (§41).
  // ------------------------------------------------------------------

  private onMessage(ev: MessageEvent): void {
    let msg: {
      type: string;
      requestId?: string;
      payload?: any;
    };
    try {
      msg = JSON.parse(ev.data as string);
    } catch {
      return;
    }

    if (msg.requestId && this.pending.has(msg.requestId)) {
      const p = this.pending.get(msg.requestId)!;
      this.pending.delete(msg.requestId);
      if (msg.type === "error") p.reject(new Error(JSON.stringify(msg.payload)));
      else p.resolve(msg.payload);
      return;
    }

    const p = msg.payload ?? {};
    switch (msg.type) {
      case "agent.state":
        store.applyAgentState(p.state as AgentState);
        break;

      case "session.created":
      case "session.updated":
        store.session = p as SessionInfo;
        break;

      case "session.list":
        store.sessions = (p.sessions as SessionInfo[]) ?? [];
        break;

      case "session.entry":
      case "session.entries":
        this.ingestEntries(p);
        break;

      case "assistant.started":
        store.startAssistant();
        store.applyAgentState("CallingModel");
        break;

      case "thinking.started":
        store.startThinking();
        break;
      case "thinking.delta":
        store.appendThinkingDelta(p.text ?? "");
        break;
      case "thinking.completed":
        store.completeThinking();
        break;

      case "text.delta":
        store.appendTextDelta(p.text ?? "");
        break;

      case "text.completed":
        // PLAN §41: terminal text marker. The streaming text.delta events have
        // already built the block; this just resolves any partial state so the
        // message is marked final even on a client that skips the deltas.
        break;

      case "tool.started":
        store.startToolCall(p.id, p.name);
        store.applyAgentState("ExecutingTools");
        break;
      case "tool.output":
        store.setToolResult(p.id, p.output ?? "", p.isError ?? false);
        break;
      case "tool.completed":
        store.completeToolCall(p.id, p.durationMs ?? 0);
        break;

      case "model.retrying":
        // PLAN §34: a failed attempt is being retried — drop any partial the
        // failed attempt streamed so it is not duplicated on the retry.
        store.resetAssistantForRetry();
        store.applyAgentState("Retrying");
        break;

      case "model.requestFailed":
        // A model attempt failed (final or pre-retry). If a retry is coming the
        // next model.retrying resets the block; if this was terminal, mark the
        // in-progress block as failed so the UI resolves instead of hanging.
        store.failAssistant(p.message ?? "model request failed");
        break;

      case "assistant.completed":
        store.completeAssistant(p.usage as Usage | undefined);
        store.applyAgentState("Idle");
        break;

      case "usage.updated":
        store.lastUsage = p as Usage;
        if (p.promptTokens != null) {
          store.stats.contextTokens = p.promptTokens;
        }
        break;

      case "models.updated":
        store.loadModels((p.models as ModelInfo[]) ?? []);
        break;

      case "models.refreshFailed":
        store.setError(p.message ?? "model refresh failed");
        break;

      case "plugins.state":
        store.plugins = (p.plugins as PluginStatus[]) ?? [];
        break;

      case "plugin.state":
        store.setPluginReloadState(p.pluginId, p.state);
        break;
      case "plugin.reloaded":
        break;
      case "plugin.reloadFailed":
        store.setPluginReloadState(p.pluginId, "failed");
        break;

      case "error":
        store.setError(p.message ?? "unknown error");
        break;

      default:
        break;
    }
  }

  /** Apply persisted transcript entries sent on session.open. */
  private ingestEntries(p: any): void {
    const entries = p.entries ?? (p.entry ? [p.entry] : null);
    if (!entries) return;
    if (p.replace) store.resetTranscript();
    for (const e of entries) {
      switch (e.type) {
        case "user_message": {
          // PLAN §41: chat.send echoes the persisted user entry as a session.entry.
          // The composer already appended it optimistically (store.submit) — drop
          // the echo when the last block is a user block with identical text,
          // otherwise the message renders twice.
          const last = store.blocks[store.blocks.length - 1];
          const isEcho = !p.replace &&
            last?.kind === "user" && (last.text ?? "") === (e.text ?? "");
          if (!isEcho) store.appendUser(e.text ?? "");
          break;
        }
        case "assistant_message":
          this.applyAssistantEntry(e);
          break;
        case "compaction":
          store.appendSystem(`Compaction: ${e.summary ?? "(summary)"}`);
          break;
        default:
          break;
      }
    }
  }

  private applyAssistantEntry(e: any): void {
    store.startAssistant();
    const a = store.activeAssistantId;
    // Rebuild the block from the persisted message parts.
    const parts = e.parts ?? [];
    for (const part of parts) {
      if (part.type === "thinking" && a) {
        store.startThinking();
        store.appendThinkingDelta(part.text ?? "");
        store.completeThinking();
      } else if (part.type === "text" && a) {
        store.appendTextDelta(part.text ?? "");
      } else if (part.type === "tool_call" && a) {
        store.startToolCall(part.id, part.name);
        store.appendToolArgsDelta(part.id, part.argumentsJson ?? "");
      }
    }
    for (const tr of e.toolResults ?? []) {
      store.setToolResult(tr.id, tr.output ?? "", tr.isError ?? false);
    }
    store.completeAssistant(e.usage as Usage | undefined);
  }
}
export const ws = new NetPIWebSocket();
ws.connect();
