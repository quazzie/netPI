import { store, REVEAL_INITIAL, REVEAL_STEP } from "./store.svelte";
import type {
  AgentState,
  ModelInfo,
  PluginStatus,
  SessionInfo,
  Usage,
} from "./types";

/**
 * WebSocket client for the netPI Web plugin.
 *
 * Envelope: every message is { type, requestId?, sessionId?, payload }.
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

  /** Command that expects a { ok | error } ack for its requestId. */
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
      const pending = this.pending.get(msg.requestId)!;
      this.pending.delete(msg.requestId);
      if (msg.type === "error") pending.reject(new Error(JSON.stringify(msg.payload)));
      else pending.resolve(msg.payload);
      return;
    }

    const p = msg.payload ?? {};
    switch (msg.type) {
      case "agent.state":
        store.applyAgentState(p.state as AgentState);
        break;

      case "session.created":
      case "session.updated":
        store.applySession(p as SessionInfo);
        store.upsertSession(p as SessionInfo);
        break;

      case "session.deleted": {
        const deletedId = p.sessionId as string | undefined;
        const wasVisible = !!deletedId && store.sessions.some((s) => s.id === deletedId);
        store.sessions = store.sessions.filter((s) => s.id !== deletedId);
        if (deletedId && store.session?.id === deletedId) {
          // The open session vanished: clear the viewport and start fresh
          // in the same workspace.
          const workspace = store.session.workspace;
          store.session = null;
          store.resetTranscript();
          this.request("session.create", workspace ? { workspace } : {}).catch(() => {});
        }
        // A visible row shrank the loaded page — pull the next page so older
        // sessions surface instead of the list silently staying short.
        if (wasVisible && store.sessionRemaining > 0) this.loadMoreSessions();
        break;
      }

      case "session.list":
        {
          const list = (p.sessions as SessionInfo[]) ?? [];
          if ((p.offset ?? 0) > 0) {
            // Continuation page ("load more"): append unseen sessions in server order.
            const known = new Set(store.sessions.map((s) => s.id));
            for (const s of list) if (!known.has(s.id)) store.sessions.push(s);
          } else {
            store.sessions = list;
          }
          store.sessionTotal = p.total ?? store.sessions.length;
        }
        break;

      case "session.entry":
        this.ingestEntries(p, false);
        break;

      case "session.entries":
        this.ingestEntries(p, true);
        if (p.replace) store.revealTail(REVEAL_INITIAL);
        break;

      case "session.older":
        store.olderSeq = p.beforeSequence ?? 0;
        store.moreAvailable = !!p.hasMore;
        const before = store.blocks.length;
        this.ingestEntries(p, false, true);
        store.noteOlderLoaded(store.blocks.length - before);
        store.revealMore(REVEAL_STEP);
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
        // The streamed deltas are authoritative. This event is only a boundary.
        break;

      case "tool.started":
        store.startToolCall(p.id, p.name);
        store.applyAgentState("ExecutingTools");
        break;
      case "tool.args":
        store.appendToolArgsDelta(p.id, p.args ?? "");
        break;
      case "tool.output":
        store.setToolResult(
          p.id,
          p.output ?? "",
          p.isError ?? false,
          p.append === true,
        );
        break;
      case "tool.completed":
        store.completeToolCall(p.id, p.durationMs ?? 0);
        break;

      case "model.retrying":
        store.resetAssistantForRetry();
        store.applyAgentState("Retrying");
        break;

      case "model.wire":
        store.noteModelWire(p.wire ?? "", p.fallback === true, p.reason ?? null);
        break;

      case "model.requestFailed":
        store.failAssistant(p.message ?? "model request failed");
        break;

      case "assistant.completed":
        // Do not force Idle here. A completed model turn may be followed by a
        // tool batch and another model turn. agent.state is the source of truth.
        store.completeAssistant(p.usage as Usage | undefined);
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

      case "ui.panels":
        store.webPanels = p.panels ?? [];
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

  /** Apply persisted transcript entries.
   * @param reset payload carries a replace flag from session.entries
   * @param prepend insert in front of the current transcript (session.older)
   */
  private ingestEntries(p: any, reset: boolean, prepend = false): void {
    const entries = p.entries ?? (p.entry ? [p.entry] : null);
    if (!entries) return;
    if (reset && p.replace) {
      store.resetTranscript();
      store.olderSeq = p.beforeSequence ?? 0;
      store.moreAvailable = !!p.hasMore;
    }

    for (const e of entries) {
      switch (e.type) {
        case "user_message": {
          if (prepend) {
            store.prependUser(e.text ?? "");
            break;
          }
          const last = store.blocks[store.blocks.length - 1];
          const isEcho =
            !p.replace &&
            last?.kind === "user" &&
            (last.text ?? "") === (e.text ?? "");
          if (!isEcho) store.appendUser(e.text ?? "");
          break;
        }
        case "assistant_message":
          this.applyAssistantEntry(e, prepend);
          break;
        case "compaction":
          if (prepend) store.prependSystem(`Compaction: ${e.summary ?? "(summary)"}`);
          else store.appendSystem(`Compaction: ${e.summary ?? "(summary)"}`);
          break;
        case "system_note":
          if (prepend) store.prependSystem(e.text ?? "");
          else store.appendSystem(e.text ?? "");
          break;
        default:
          break;
      }
    }
  }

  private applyAssistantEntry(e: any, prepend = false): void {
    store[prepend ? "prependAssistantShell" : "startAssistant"]();
    const assistantId = store.activeAssistantId;
    const parts = e.parts ?? [];

    for (const part of parts) {
      if (part.type === "thinking" && assistantId) {
        store.startThinking();
        store.appendThinkingDelta(part.text ?? "");
        store.completeThinking();
      } else if (part.type === "text" && assistantId) {
        store.appendTextDelta(part.text ?? "");
      } else if (part.type === "tool_call" && assistantId) {
        store.startToolCall(part.id, part.name);
        store.appendToolArgsDelta(part.id, part.argumentsJson ?? "");
      }
    }

    for (const tr of e.toolResults ?? []) {
      store.setToolResult(tr.id, tr.output ?? "", tr.isError ?? false);
    }
    store.completeAssistant(e.usage as Usage | undefined);
  }

  /** Request the next page of older transcript entries. */
  /** Fetch the next drawer page (server responds with a `session.list` event). */
  loadMoreSessions(): void {
    if (store.sessionMoreLoading || store.sessionRemaining <= 0) return;
    store.sessionMoreLoading = true;
    this.request("session.list", { offset: store.sessions.length })
      .catch(() => store.setError("failed to load older sessions"))
      .finally(() => (store.sessionMoreLoading = false));
  }

  loadOlder(): void {
    const seq = store.olderSeq;
    if (seq <= 0 || store.olderLoading || !store.moreAvailable) return;
    store.olderLoading = true;
    this.request("session.older", {
      beforeSequence: seq,
      count: 100,
      sessionId: store.session?.id,
    }).catch(() => {
      store.olderLoading = false;
    });
  }
}

export const ws = new NetPIWebSocket();
ws.connect();
