// WebSocket RPC client for the NetPI host (see docs/PROTOCOL.md).
//  - one socket at /ws, envelope { t: 'rpc'|'sub'|'ping' } → { t: 'hello'|'res'|'ev'|'pong' }
//  - id-correlated calls with timeouts; calls made while disconnected wait for the socket and fall back
//    to HTTP (POST /api/rpc/{method}) if it does not come back quickly
//  - reconnect with exponential backoff; subscriptions are re-sent on every (re)connect
import { getToken, authHeaders } from './auth.js';
import { bus } from './bus.js';

export class RpcError extends Error {
  constructor(code, message, method) {
    super(message || code);
    this.code = code;
    this.method = method;
  }
}

/** Reactive connection state (read by the top bar indicator). */
export const conn = $state({
  status: 'connecting', // connecting | open | reconnecting
  attempt: 0,
  retryAt: 0,
  clientId: null,
  version: null,
  everConnected: false,
});

const DEFAULT_TIMEOUT = 30_000;
const FALLBACK_AFTER = 3_000;
const BACKOFF = [400, 1000, 2000, 4000, 7000, 10_000];

let ws = null;
let nextId = 1;
const pending = new Map(); // id -> { resolve, reject, timer, m, sent }
let outbox = []; // [{ id, frame, fallbackTimer, m, p }]
let subscription = [];
let retryTimer = null;
let pingTimer = null;
let lastRx = 0;
const openListeners = new Set();

export function wsUrl() {
  const u = new URL('/ws', location.href);
  u.protocol = u.protocol === 'https:' ? 'wss:' : 'ws:';
  const t = getToken();
  if (t) u.searchParams.set('token', t);
  return u.href;
}

export function connect() {
  clearTimeout(retryTimer);
  if (ws && (ws.readyState === 0 || ws.readyState === 1)) return;
  conn.status = conn.everConnected ? 'reconnecting' : 'connecting';
  let sock;
  try {
    sock = new WebSocket(wsUrl());
  } catch {
    scheduleReconnect();
    return;
  }
  ws = sock;
  sock.onopen = () => {
    if (ws !== sock) return;
    const reconnect = conn.everConnected;
    conn.status = 'open';
    conn.attempt = 0;
    conn.everConnected = true;
    lastRx = Date.now();
    if (subscription.length) rawSend({ t: 'sub', sessions: subscription });
    flushOutbox();
    startPing();
    for (const fn of openListeners) {
      try {
        fn({ reconnect });
      } catch (e) {
        console.error(e);
      }
    }
  };
  sock.onmessage = (e) => {
    lastRx = Date.now();
    let msg;
    try {
      msg = JSON.parse(e.data);
    } catch {
      return;
    }
    handle(msg);
  };
  sock.onclose = (e) => {
    if (ws !== sock) return;
    ws = null;
    stopPing();
    // Calls already on the wire are lost with the socket. The host's reason is worth passing on: it is what a
    // message over the limit gets us ("message too big: 2 MB per message").
    for (const [id, p] of pending) {
      if (!p.sent) continue;
      pending.delete(id);
      clearTimeout(p.timer);
      const why = e?.reason ? `Connection lost: ${e.reason}` : 'Connection lost';
      p.reject(new RpcError('disconnected', why, p.m));
    }
    scheduleReconnect();
  };
  sock.onerror = () => {};
}

function handle(msg) {
  switch (msg.t) {
    case 'hello':
      conn.clientId = msg.clientId ?? null;
      conn.version = msg.version ?? null;
      break;
    case 'res': {
      const p = pending.get(msg.id);
      if (!p) return;
      pending.delete(msg.id);
      clearTimeout(p.timer);
      if (msg.e) p.reject(new RpcError(msg.e.code, msg.e.message, p.m));
      else p.resolve(msg.r);
      break;
    }
    case 'ev':
      bus.emit(msg);
      break;
    default:
      break;
  }
}

function scheduleReconnect() {
  const delay = BACKOFF[Math.min(conn.attempt, BACKOFF.length - 1)] * (0.85 + Math.random() * 0.3);
  conn.attempt++;
  conn.status = conn.everConnected ? 'reconnecting' : 'connecting';
  conn.retryAt = Date.now() + delay;
  clearTimeout(retryTimer);
  retryTimer = setTimeout(connect, delay);
}

/** Retry right now (e.g. user clicked the connection indicator). */
export function reconnectNow() {
  conn.attempt = 0;
  if (ws) {
    try {
      ws.close();
    } catch {}
  } else connect();
}

function startPing() {
  stopPing();
  pingTimer = setInterval(() => {
    if (!ws || ws.readyState !== 1) return;
    if (Date.now() - lastRx > 45_000) {
      // dead connection: force a reconnect
      try {
        ws.close();
      } catch {}
      return;
    }
    rawSend({ t: 'ping' });
  }, 15_000);
}
function stopPing() {
  clearInterval(pingTimer);
  pingTimer = null;
}

function rawSend(obj) {
  if (ws && ws.readyState === 1) {
    ws.send(JSON.stringify(obj));
    return true;
  }
  return false;
}

function flushOutbox() {
  const items = outbox;
  outbox = [];
  for (const it of items) {
    clearTimeout(it.fallbackTimer);
    const p = pending.get(it.id);
    if (!p) continue; // timed out meanwhile
    if (ws && ws.readyState === 1) {
      ws.send(it.frame);
      p.sent = true;
    } else outbox.push(it);
  }
}

/**
 * Call an RPC method. Options: { timeout (ms), http: true to force the HTTP endpoint, fallback: false to
 * never use HTTP }.
 */
export function rpc(m, p, opts = {}) {
  if (opts.http) return http(m, p);
  const timeout = opts.timeout ?? DEFAULT_TIMEOUT;
  return new Promise((resolve, reject) => {
    const id = nextId++;
    const frame = JSON.stringify({ t: 'rpc', id, m, p: p ?? {} });
    const entry = { resolve, reject, m, sent: false, timer: 0 };
    entry.timer = setTimeout(() => {
      pending.delete(id);
      reject(new RpcError('timeout', `${m} timed out`, m));
    }, timeout);
    pending.set(id, entry);
    if (ws && ws.readyState === 1) {
      ws.send(frame);
      entry.sent = true;
      return;
    }
    const item = { id, frame, fallbackTimer: 0 };
    if (opts.fallback !== false) {
      item.fallbackTimer = setTimeout(() => {
        const idx = outbox.indexOf(item);
        if (idx < 0 || !pending.has(id)) return;
        outbox.splice(idx, 1);
        pending.delete(id);
        clearTimeout(entry.timer);
        http(m, p).then(resolve, reject);
      }, FALLBACK_AFTER);
    }
    outbox.push(item);
  });
}

/** HTTP fallback: POST /api/rpc/{method}. */
export async function http(m, p) {
  let res;
  try {
    res = await fetch(`/api/rpc/${encodeURIComponent(m)}`, {
      method: 'POST',
      credentials: 'same-origin',
      headers: authHeaders({ 'Content-Type': 'application/json' }),
      body: JSON.stringify(p ?? {}),
    });
  } catch (e) {
    throw new RpcError('network', e?.message || 'Network error', m);
  }
  let body = null;
  const text = await res.text();
  if (text) {
    try {
      body = JSON.parse(text);
    } catch {
      body = text;
    }
  }
  if (!res.ok) {
    const err = body?.error ?? {};
    throw new RpcError(err.code || `http_${res.status}`, err.message || res.statusText, m);
  }
  return body;
}

/** Replace the set of sessions whose scoped events we receive. */
export function subscribe(sessionIds) {
  const next = [...new Set(sessionIds.filter(Boolean))];
  if (next.length === subscription.length && next.every((s, i) => s === subscription[i])) return;
  subscription = next;
  rawSend({ t: 'sub', sessions: subscription });
}

/** Called on every successful (re)connect with { reconnect: boolean }. */
export function onOpen(fn) {
  openListeners.add(fn);
  return () => openListeners.delete(fn);
}

if (typeof window !== 'undefined') {
  window.addEventListener('online', () => {
    if (!ws) reconnectNow();
  });
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible' && !ws) reconnectNow();
  });
}
