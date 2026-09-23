import { store, NetPIStore, REVEAL_INITIAL, REVEAL_STEP } from "./store.svelte";
import type {
  AssistantBlock,
  AgentAssignment,
  AgentState,
  ModelInfo,
  PluginStatus,
  SessionInfo,
  ProjectInfo,
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
  /** astra-1 F: session to make visible via the NEXT explicit open.
   *  Server-pushed session.updated for OTHER sessions refresh global
   *  metadata only — they never replace the visible session. */
  private targetSession: string | null = null;
  /** astra-1 F: reconnect window — the server replays the active session on
   *  bootstrap; that replay (agent.state + session.updated/entries) is
   *  treated as navigation until the replay lands or the window expires. */
  private reconnectNav = false;
  private reconnectNavUntil = 0;

  /** astra-1 F: explicit navigation — replace the visible session with id. */
  openSession(id: string): Promise<unknown> {
    this.targetSession = id;
    this.reconnectNav = false;
    return this.request("session.open", { sessionId: id });
  }

  /** astra-1 F: explicit navigation — a new session becomes visible.
   *  (session.created events always navigate, so no target is needed.) */
  createSession(payload: Record<string, unknown> = {}): Promise<unknown> {
    this.reconnectNav = false;
    return this.request("session.create", payload);
  }

  /** astra-1 G1: last-selected tab to restore after the bootstrap replay lands. */
  private restoreTab: string | null = null;

  /** G1: a session became visible — register it as an open tab (ordered,
   *  de-duped) and clear its unread marker. */
  private navToTab(eid: string | null): void {
    if (!eid) return;
    store.openTab(eid);
    store.clearUnread(eid);
  }

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
      this.reconnectNav = true;
      this.reconnectNavUntil = Date.now() + 10000;
      // astra-1 G1: remember the last-selected tab so the post-bootstrap
      // navigation can return to it (restored sessions are re-opened below).
      const restored = NetPIStore.loadTabs();
      this.restoreTab = restored.tabs.length ? restored.selected : null;
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
      sessionId?: string;
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
      // astra-1 P5: the plugin.reload/reloadAll/scan ack carries an
      // operationId (work runs on the host queue); register it as in-flight
      // so a UI busy indicator survives a reconnect until the matching
      // completion event (plugin.reloaded / reloadFailed / scanned / state).
      if (msg.type === "ack" && (msg.payload as Record<string, unknown> | null)?.operationId)
        store.notePluginOpPending(
          String((msg.payload as Record<string, unknown>).operationId),
          String((msg.payload as Record<string, unknown>).kind ?? ""));
      if (msg.type === "error") pending.reject(new Error(JSON.stringify(msg.payload)));
      else pending.resolve(msg.payload);
      return;
    }

    const p = msg.payload ?? {};
    // astra-1 P5: the plugin.reload/reloadAll/scan ack carries an operationId
    // (the work runs on the host queue); register it as in-flight so a UI busy
    // indicator survives a reconnect until the matching completion event.
    if (msg.type === "ack" && p.operationId && p.kind)
      store.notePluginOpPending(String(p.operationId), String(p.kind));

    // astra-1 F: events carry the session they belong to. Transcript and
    // streaming events are scoped: a background session never mutates the
    // visible chat. Events without a sessionId are global.
    const sid = (msg.sessionId ?? null) as string | null;
    const isCurrent = (sid2: string | null | undefined) =>
      sid2 == null || sid2 === (store.session?.id ?? null);
    const nav = (sid2: string | null) =>
      this.reconnectNav &&
      Date.now() < this.reconnectNavUntil &&
      sid2 != null &&
      (this.targetSession == null || this.targetSession === sid2);

    switch (msg.type) {
      case "agent.state":
        // Per-session state; during a reconnect the bootstrap state of the
        // active run may arrive before the session replay lands.
        store.setBusySession(sid, p.state as AgentState);
        if (!isCurrent(sid) && (p.state as AgentState) !== "Idle")
          store.markUnread(sid);
        if (isCurrent(sid) || nav(sid)) {
          if ((p.state as AgentState) === "Idle") store.completeRun();
          store.applyAgentState(p.state as AgentState);
        }
        break;

      // astra-2 §13: assignment/lane lifecycle events. The Work panel polls its
      // own Kestrel surface; these keep the SHELL aware (tab badges + capacity).
      case "agents.state": {
        const rows = (p.agents ?? (Array.isArray(p) ? p : [])) as AgentAssignment[];
        const bySession = new Map<string, AgentAssignment[]>();
        for (const r of rows) {
          if (r?.sessionId && r?.assignmentId) {
            const list = bySession.get(r.sessionId) ?? [];
            list.push(r);
            bySession.set(r.sessionId, list);
          }
        }
        for (const [s, list] of bySession) store.setAssignments(s, list);
        break;
      }

      case "agent.updated": {
        const row = (p.row ?? p) as AgentAssignment | null;
        if (row?.sessionId && row?.assignmentId) store.setAssignments(row.sessionId, [row]);
        break;
      }

      case "lanes.state": {
        store.setLaneSnapshot(p as Record<string, unknown>);
        break;
      }


      case "session.created":
      case "session.updated": {
        // astra-1 F: global metadata always refreshes; the VISIBLE session is
        // replaced only for explicit navigation (open/create) or the bootstrap
        // replay — everything else is a background metadata update.
        const info = p as SessionInfo;
        const eid = sid ?? info.id ?? null;
        const navigates =
          msg.type === "session.created" ||
          eid === this.targetSession ||
          isCurrent(eid) ||
          nav(eid);
        if (navigates) {
          store.applySession(info);
          this.navToTab(eid);
          if (eid === this.targetSession) this.targetSession = null;
          if (eid != null) {
            this.reconnectNav = false;
            // G1: return to the last-selected tab once the bootstrap replay
            // has landed (the server always replays the active/most-recent
            // session first — this restores the user's tab choice on top).
            if (this.restoreTab && this.restoreTab !== eid) {
              const target = this.restoreTab;
              this.restoreTab = null;
              this.openSession(target).catch(() => {});
            }
          }
        } else {
          store.upsertSession(info);
        }
        break;
      }
      // astra-1 C (G1 gap): server-backed project management. project.list is
      // a request reply (the ack for the request resolves separately) — mirror
      // its payload into the store; project.created upserts the fresh row.
      case "project.list": {
        store.setProjects((p.projects as ProjectInfo[]) ?? []);
        break;
      }

      case "project.created": {
        const proj = p.project as ProjectInfo | undefined;
        if (proj) store.upsertProject(proj);
        break;
      }


      case "session.project.pending": {
        // astra-1 D2: a project switch was enqueued while a run is in flight.
        // It applies at the run's safe boundary (session.project.applied).
        const psid = sid ?? p.sessionId ?? null;
        if (psid)
          store.setProjectPending(psid, {
            operationId: p.operationId ?? "",
            project: p.projectName ?? p.projectId ?? "…",
          });
        break;
      }
      case "session.project.applied": {
        // The boundary apply succeeded (or failed-cleared): drop the pending
        // notice; the session metadata refresh arrives via session.updated.
        const psid = sid ?? p.sessionId ?? null;
        if (psid) store.setProjectPending(psid, null);
        // astra-1 C (G1 gap): a switch (or refresh) that lands also resolves any
        // picker notice tagged with this operation (a retried op that now works).
        store.clearProjectNotice(typeof p.operationId === "string" ? p.operationId : null);
        break;
      }
      case "session.deleted": {
        const deletedId = p.sessionId as string | undefined;
        const wasVisible = !!deletedId && store.sessions.some((s) => s.id === deletedId);
        store.sessions = store.sessions.filter((s) => s.id !== deletedId);
        // astra-1 G1: a deleted session drops its tab (the run stays in
        // Activity; the tab is a view, not the session).
        if (deletedId) store.closeTab(deletedId);
        if (deletedId && store.session?.id === deletedId) {
          // The open session vanished: clear the viewport and start fresh
          // in the same workspace.
          const workspace = store.session.workspace;
          store.session = null;
          store.resetTranscript();
          // astra-1 F: explicit navigation — the fallback session becomes visible.
          this.createSession(workspace ? { workspace } : {}).catch(() => {});
        }
        // A visible row shrank the loaded page — pull the next page so older
        // sessions surface instead of the list silently staying short.
        if (wasVisible && store.sessionRemaining > 0) this.loadMoreSessions();
        break;
      }

      case "session.list":
        {
          const list = (p.sessions as SessionInfo[]) ?? [];
          const query = typeof p.query === "string" && p.query ? p.query : null;
          if (query) {
            // astra-1 G1: server-side search — a separate result set; the loaded
            // global list (tabs, "load more") is untouched by the query.
            if ((p.offset ?? 0) > 0) {
              const known = new Set(store.sessionSearch?.map((s) => s.id) ?? []);
              for (const s of list)
                if (!known.has(s.id)) (store.sessionSearch ??= []).push(s);
            } else {
              store.sessionSearch = list;
            }
            store.sessionSearchTotal = p.total ?? list.length;
          } else if ((p.offset ?? 0) > 0) {
            // Continuation page ("load more"): append unseen sessions in server order.
            const known = new Set(store.sessions.map((s) => s.id));
            for (const s of list) if (!known.has(s.id)) store.sessions.push(s);
          } else {
            store.sessions = list;
          }
          if (!query) store.sessionTotal = p.total ?? store.sessions.length;
        }
        break;

      case "session.entry":
        if (!isCurrent(sid)) break;
        this.ingestEntries(p, false);
        break;

      case "session.entries":
        // A replace replay for a session we do not view is ignored unless it
        // is the navigation target (open) or the bootstrap replay (reconnect).
        if (!isCurrent(sid) && !nav(sid)) break;
        this.ingestEntries(p, true);
        if (p.replace) store.revealTail(REVEAL_INITIAL);
        // astra-1 F: reconstruct the in-flight assistant message. The server
        // folds the unflushed deltas into the accumulator, so this snapshot is
        // the authoritative partial output; subsequent live text.delta events
        // append to the same block (no duplication). maxSequence is the
        // reconcile cursor for any late entry replay.
        if (p.streaming && typeof p.streaming.text === "string" && p.streaming.text.length) {
          store.startAssistant();
          store.appendTextDelta(p.streaming.text);
        }
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
        if (!isCurrent(sid)) break;
        store.startAssistant();
        store.applyAgentState("CallingModel");
        break;

      case "thinking.started":
        if (!isCurrent(sid)) break;
        store.startThinking();
        break;
      case "thinking.delta":
        if (!isCurrent(sid)) break;
        store.appendThinkingDelta(p.text ?? "");
        break;
      case "thinking.completed":
        if (!isCurrent(sid)) break;
        store.completeThinking();
        break;

      case "text.delta":
        if (!isCurrent(sid)) break;
        store.appendTextDelta(p.text ?? "");
        break;
      case "text.completed":
        // The streamed deltas are authoritative. This event is only a boundary.
        break;

      case "tool.started":
        if (!isCurrent(sid)) break;
        store.startToolCall(p.id, p.name);
        store.applyAgentState("ExecutingTools");
        break;
      case "tool.args":
        if (!isCurrent(sid)) break;
        store.appendToolArgsDelta(p.id, p.args ?? "");
        break;
      case "tool.output":
        if (!isCurrent(sid)) break;
        store.setToolResult(
          p.id,
          p.output ?? "",
          p.isError ?? false,
          p.append === true,
          p.images ?? [],
        );
        break;
      case "tool.completed":
        if (!isCurrent(sid)) break;
        store.completeToolCall(p.id, p.durationMs ?? 0);
        break;

      case "model.retrying":
        if (!isCurrent(sid)) break;
        store.resetAssistantForRetry();
        store.applyAgentState("Retrying");
        break;

      case "model.wire":
        if (!isCurrent(sid)) break;
        store.noteModelWire(p.wire ?? "", p.fallback === true, p.reason ?? null);
        break;

      case "model.requestFailed":
        if (!isCurrent(sid)) break;
        store.failAssistant(p.message ?? "model request failed");
        break;

      case "assistant.completed":
        // Do not force Idle here. A completed model turn may be followed by a
        // tool batch and another model turn. agent.state is the source of truth.
        // astra-1 G1: a finished turn in a background session marks its tab
        // unread; the visible session just completes its transcript.
        if (!isCurrent(sid)) {
          store.markUnread(sid);
          break;
        }
        store.completeAssistant(p.usage as Usage | undefined);
        break;

      case "usage.updated":
        // Per-session meter: updates from other sessions must not touch it.
        if (!isCurrent(sid)) break;
        store.lastUsage = p as Usage;
        store.lastUsageSession = sid ?? store.session?.id ?? null;
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
        // astra-1 P5: the runner broadcasts plugins.state on EVERY completed
        // update op (reload / reloadAll / scan), carrying the operationId — the
        // authoritative completion for all kinds. Clear the in-flight op; the
        // bootstrap plugins.state (no operationId) is skipped (no-op clear).
        store.notePluginOpDone(p.operationId as string | undefined);
        break;

      case "ui.panels":
        store.webPanels = p.panels ?? [];
        break;

      case "plugin.state":
        // astra-1 P5: per-plugin state update (ReloadAll/Scan) — not a completion.
        store.setPluginReloadState(p.pluginId, p.state);
        break;
      case "plugin.reloaded":
        // astra-1 P5: the runner announces a completed single-plugin swap; clear
        // the in-flight op (reconnects reconcile via the facade's GetOperation).
        store.notePluginOpDone(p.operationId as string | undefined);
        break;
      case "plugin.scanned":
        // astra-1 P5: scan finished — clear the in-flight op.
        store.notePluginOpDone(p.operationId as string | undefined);
        break;
      case "plugin.reloadFailed":
        store.setPluginReloadState(p.pluginId, "failed");
        // astra-1 P5: clear the in-flight op — idempotent, plugins.state is
        // the authoritative completion (this is the single-reload failure path).
        store.notePluginOpDone(p.operationId as string | undefined);
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
            store.prependUser(e.text ?? "", e.images ?? []);
            break;
          }
          const last = store.blocks[store.blocks.length - 1];
          const isEcho =
            !p.replace &&
            last?.kind === "user" &&
            (last.text ?? "") === (e.text ?? "");
          if (!isEcho) store.appendUser(e.text ?? "", e.images ?? []);
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
        // astra-1 D: project-change events (server entry type `project_context`)
        // are a distinct transcript block — provenance is the application, not
        // the model (rendered by ProjectContextBlock, never as a user message).
        case "project_context": {
          const block = {
            projectName: String(e.projectName ?? ""),
            workspace: String(e.workspace ?? ""),
            contentHash: String(e.contentHash ?? ""),
            text: String(e.text ?? ""),
          };
          if (prepend) store.prependProjectContext(block);
          else store.appendProjectContext(block);
          break;
        }
        default:
          break;
      }
    }

    // Persisted tool batches replay as separate assistant blocks. Mark the
    // last assistant block in each user-delimited turn as the artifact owner.
    for (const b of store.blocks) if (b.kind === "assistant") b.endOfRun = false;
    let lastAssistant = -1;
    for (let i = 0; i < store.blocks.length; i++) {
      const b = store.blocks[i];
      if (b.kind === "assistant" && b.done) lastAssistant = i;
      if (b.kind === "user" && lastAssistant >= 0) {
        (store.blocks[lastAssistant] as AssistantBlock).endOfRun = true;
        lastAssistant = -1;
      }
    }
    if (lastAssistant >= 0 && store.agentState === "Idle")
      (store.blocks[lastAssistant] as AssistantBlock).endOfRun = true;
  }

  private applyAssistantEntry(e: any, prepend = false): void {
    store[prepend ? "prependAssistantShell" : "startAssistant"]();
    const assistantId = store.activeAssistantId;
    const parts = e.parts ?? [];

    const resultIds = new Set((e.toolResults ?? []).map((tr: any) => tr.id));
    for (const part of parts) {
      if (part.type === "thinking" && assistantId) {
        store.startThinking();
        store.appendThinkingDelta(part.text ?? "");
        store.completeThinking();
      } else if (part.type === "text" && assistantId) {
        store.appendTextDelta(part.text ?? "");
      } else if (part.type === "tool_call" && assistantId) {
        // astra-1 G3 (same live/replay model): replayed calls with no result
        // are NOT still running — the run ended. Interrupted-flagged ones
        // were a run that died mid-batch (PLAN §46); unflagged legacy entries
        // are marked interrupted for the same reason (compat mapping).
        const interrupted =
          part.interrupted === true || !resultIds.has(part.id);
        store.startToolCall(part.id, part.name, interrupted);
        store.appendToolArgsDelta(part.id, part.argumentsJson ?? "");
      }
    }

    for (const tr of e.toolResults ?? []) {
      store.setToolResult(tr.id, tr.output ?? "", tr.isError ?? false, false, tr.images ?? []);
      // astra-1 G3: replayed results are TERMINAL — the same live/replay
      // model means a persisted result implies completion. (Live output
      // chunks are append=true and never complete.)
      store.completeToolCall(tr.id, 0);
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
