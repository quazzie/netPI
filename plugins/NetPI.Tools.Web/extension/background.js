// NetPI extension: a bridge between NetPI (the browser tool's "chrome" target and the tabs the user shares) and this
// Chrome. NetPI sends DevTools-protocol commands over a WebSocket; they reach a tab through chrome.debugger, so no
// remote-debugging switch is needed. Only tabs NetPI opened or the user shared are ever attached.
//
// Sessions: "tab-<tabId>" is a tab's page, "tab-<tabId>|<child>" one of its frames' sessions (out-of-process iframes).
// The Target.* methods NetPI uses at browser level are answered here with chrome.tabs.

const attached = new Set(); // tabs with the debugger attached
const known = new Set(); // tabs NetPI opened or the user shared: their closing is reported
let ws = null;
let config = null;
let connecting = false;
let rid = 0;
const waiting = new Map(); // rid → resolve (the popup's requests)
let shared = []; // [{ tabId, sessionId, chat }]

async function readConfig() {
  try {
    const r = await fetch(chrome.runtime.getURL('config.json'), { cache: 'no-store' });
    return await r.json();
  } catch {
    return null;
  }
}

async function connect() {
  if (connecting || (ws && ws.readyState <= 1)) return;
  connecting = true;
  try {
    config = await readConfig();
    const base = (config?.url ?? 'http://127.0.0.1:7431').replace(/^http/, 'ws').replace(/\/+$/, '');
    const socket = new WebSocket(`${base}/api/p/netpi.tools.web/extension?key=${encodeURIComponent(config?.key ?? '')}`);
    ws = socket;
    socket.onopen = () => {
      request({ op: 'hello', version: chrome.runtime.getManifest().version, browser: navigator.userAgent }).catch(() => {});
      request({ op: 'shared' }).then(setShared).catch(() => {});
      badge();
    };
    socket.onmessage = (e) => {
      let m;
      try {
        m = JSON.parse(e.data);
      } catch {
        return;
      }
      if (m.rid != null) {
        waiting.get(m.rid)?.(m);
        waiting.delete(m.rid);
      } else if (m.type === 'shared') setShared(m.tabs);
      else if (m.id != null && m.method) command(m);
    };
    socket.onclose = () => {
      if (ws === socket) ws = null;
      for (const [, resolve] of waiting) resolve({ error: { message: 'NetPI disconnected' } });
      waiting.clear();
      shared = [];
      badge();
    };
  } finally {
    connecting = false;
  }
}

function send(m) {
  if (ws?.readyState === 1) ws.send(JSON.stringify(m));
}

/** A request of the extension's own (the popup's) to NetPI. */
function request(body) {
  return new Promise((resolve, reject) => {
    if (ws?.readyState !== 1) return reject(new Error('Not connected to NetPI'));
    const id = ++rid;
    waiting.set(id, (m) => (m.error ? reject(new Error(m.error.message ?? 'failed')) : resolve(m.result)));
    send({ rid: id, ...body });
  });
}

function setShared(tabs) {
  shared = Array.isArray(tabs) ? tabs : [];
  for (const t of shared) known.add(t.tabId);
  badge();
}

async function badge() {
  const on = ws?.readyState === 1;
  await chrome.action.setBadgeBackgroundColor({ color: on ? '#2e9e5b' : '#888' });
  await chrome.action.setBadgeText({ text: on ? '' : 'off' });
  if (!on) return;
  for (const t of shared) chrome.action.setBadgeText({ tabId: t.tabId, text: 'on' }).catch(() => {});
}

// ------------------------------------------------------------------ DevTools commands from NetPI

function parse(sessionId) {
  const m = /^tab-(\d+)(?:\|(.+))?$/.exec(sessionId ?? '');
  return m ? { tabId: +m[1], child: m[2] } : null;
}

async function attach(tabId) {
  if (attached.has(tabId)) return;
  await chrome.debugger.attach({ tabId }, '1.3');
  attached.add(tabId);
  known.add(tabId);
}

async function detach(tabId) {
  if (!attached.delete(tabId)) return;
  await chrome.debugger.detach({ tabId }).catch(() => {});
}

async function run(method, params, sessionId) {
  if (!sessionId) {
    switch (method) {
      case 'Target.setDiscoverTargets':
      case 'Target.setAutoAttach':
        return {};
      case 'Target.createTarget': {
        const tab = await chrome.tabs.create({ url: params.url || 'about:blank', active: !params.background });
        known.add(tab.id);
        return { targetId: String(tab.id) };
      }
      case 'Target.attachToTarget': {
        const tabId = +params.targetId;
        await attach(tabId);
        return { sessionId: `tab-${tabId}` };
      }
      case 'Target.detachFromTarget': {
        const t = parse(params.sessionId);
        if (t) await detach(t.tabId);
        return {};
      }
      case 'Target.closeTarget': {
        const tabId = +params.targetId;
        await detach(tabId);
        await chrome.tabs.remove(tabId).catch(() => {});
        return { success: true };
      }
      case 'Target.activateTarget': {
        const tab = await chrome.tabs.update(+params.targetId, { active: true });
        await chrome.windows.update(tab.windowId, { focused: true });
        return {};
      }
      case 'Target.getTargetInfo': {
        const tab = await chrome.tabs.get(+params.targetId);
        return {
          targetInfo: { targetId: String(tab.id), type: 'page', url: tab.url || tab.pendingUrl || '', title: tab.title || '', attached: attached.has(tab.id) },
        };
      }
      default:
        throw new Error(`${method} is not available through the NetPI extension`);
    }
  }
  const t = parse(sessionId);
  if (!t) throw new Error(`unknown session ${sessionId}`);
  const target = t.child ? { tabId: t.tabId, sessionId: t.child } : { tabId: t.tabId };
  return await chrome.debugger.sendCommand(target, method, params ?? {});
}

async function command(m) {
  try {
    const result = await run(m.method, m.params ?? {}, m.sessionId);
    send({ id: m.id, result: result ?? {} });
  } catch (e) {
    send({ id: m.id, error: { message: String(e?.message ?? e) } });
  }
}

chrome.debugger.onEvent.addListener((source, method, params) => {
  if (!attached.has(source.tabId)) return;
  let sessionId = `tab-${source.tabId}`;
  if (source.sessionId) sessionId += `|${source.sessionId}`;
  // a frame's session, as NetPI will address it
  if ((method === 'Target.attachedToTarget' || method === 'Target.detachedFromTarget') && params?.sessionId)
    params = { ...params, sessionId: `tab-${source.tabId}|${params.sessionId}` };
  send({ method, params, sessionId });
});

chrome.debugger.onDetach.addListener((source, reason) => {
  if (source.tabId == null) return;
  attached.delete(source.tabId);
  // the user cancelled the debugging bar, or the tab went away
  send({ method: 'Target.detachedFromTarget', params: { sessionId: `tab-${source.tabId}`, reason } });
});

chrome.tabs.onRemoved.addListener((tabId) => {
  attached.delete(tabId);
  if (known.delete(tabId)) send({ method: 'Target.targetDestroyed', params: { targetId: String(tabId) } });
});

chrome.tabs.onCreated.addListener((tab) => {
  // a link of an agent's tab opened a new tab: NetPI follows it
  if (tab.openerTabId != null && attached.has(tab.openerTabId)) {
    known.add(tab.id);
    send({
      method: 'Target.targetCreated',
      params: { targetInfo: { targetId: String(tab.id), type: 'page', openerId: String(tab.openerTabId), url: tab.pendingUrl || tab.url || '' } },
    });
  }
});

// ------------------------------------------------------------------ the popup

chrome.runtime.onMessage.addListener((msg, _sender, reply) => {
  (async () => {
    if (msg.op === 'status') {
      await connect();
      return { connected: ws?.readyState === 1, url: config?.url ?? null, shared };
    }
    if (msg.op === 'reconnect') {
      ws?.close();
      ws = null;
      await connect();
      return { ok: true };
    }
    return await request(msg);
  })().then(
    (result) => reply({ result }),
    (e) => reply({ error: String(e?.message ?? e) }),
  );
  return true; // answered asynchronously
});

// ------------------------------------------------------------------ staying connected

chrome.alarms.create('netpi-keepalive', { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener(() => {
  if (ws?.readyState === 1) send({ type: 'ping' });
  else connect();
});
setInterval(() => (ws?.readyState === 1 ? send({ type: 'ping' }) : connect()), 20000);
chrome.runtime.onStartup.addListener(connect);
chrome.runtime.onInstalled.addListener(connect);
connect();
