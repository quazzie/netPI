// presence (web/src/lib/presence.js) is the window's side of the host's session presence: it tracks the
// sessions the window has open and marks them — at once on open and on close, and for all of them every
// 15 s (the host's 90 s expiry never drops a session a window is showing). A mark rides only an open
// connection, and the reconnect's `resend()` says the whole set at once, because the host's marks for the
// new connection are fresh.
//
// The module runs in a vm with a controllable clock and a recording rpc, as the rpc's deadline tests do.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

// The client's imports become context entries: presence.js has none, and the paths under test use only
// the fakes provided here (a recording rpc, a connection flag, fake timers).
const source = fs
  .readFileSync(new URL('../src/lib/presence.js', import.meta.url), 'utf8')
  .replace(/^import .*;\r?\n/gm, '')
  .replaceAll('export ', '');

/** A fresh world: the tracker's clock, timers and a recording rpc. */
const world = () => {
  let now = 0;
  let id = 0;
  const timers = new Map();
  const calls = [];
  let open = true;
  const schedule = (fn, ms) => {
    const t = { fn, ms, due: now + ms, id: ++id };
    timers.set(t.id, t);
    return t.id;
  };
  const context = vm.createContext({
    console,
    Date: { now: () => now },
    Math, Map, Set, Promise, Error, JSON,
    setTimeout: schedule,
    clearTimeout: (i) => timers.delete(i),
    setInterval: schedule,
    clearInterval: (i) => timers.delete(i),
    rpc: (m, p) => {
      calls.push({ m, p });
      return Promise.resolve({});
    },
    connectionOpen: () => open,
  });
  vm.runInContext(source, context);
  return {
    run: (code) => vm.runInContext(code, context),
    /** Fire every timer that comes due within `ms` of fake time, in order. */
    advance: (ms) => {
      const until = now + ms;
      for (;;) {
        let next = null;
        for (const t of timers.values())
          if (t.due <= until && (!next || t.due < next.due || (t.due === next.due && t.id < next.id))) next = t;
        if (!next) break;
        now = next.due;
        timers.delete(next.id);
        next.fn();
      }
    },
    timers,
    calls,
    setConnection: (v) => { open = v; },
    /** The tracker, wired to this world's rpc and connection flag. */
    tracker: () =>
      context.presenceTracker({ rpc: context.rpc, connectionOpen: context.connectionOpen }),
  };
};

const marks = (w) => w.calls.filter((c) => c.m === 'session.presence').map((c) => c.p);
const mark = (w, sessionId, open) => marks(w).find((p) => p.sessionId === sessionId && p.open === open);

test('a tab that opens marks its session at once, and a close unmarks it at once', () => {
  const w = world();
  const t = w.tracker();
  t.open('s1');
  assert.ok(mark(w, 's1', true), 'the open mark goes out immediately, not at the next beat');
  t.close('s1');
  assert.ok(mark(w, 's1', false), 'the close mark goes out immediately');
  assert.deepEqual([...t.openSessions()], [], 'the set is empty again');
});

test('opening a tab twice does not re-mark it', () => {
  const w = world();
  const t = w.tracker();
  t.open('s1');
  t.open('s1');
  assert.equal(marks(w).length, 1, 'one mark, one session');
});

test('the 15 s heartbeat re-marks every open session, and only the open ones', () => {
  const w = world();
  const t = w.tracker();
  t.start();
  t.open('s1');
  t.open('s2');
  t.close('s2');
  w.advance(15_000);
  assert.ok(mark(w, 's1', true), 'the open session is re-marked');
  assert.equal(marks(w).filter((p) => p.sessionId === 's2' && p.open).length, 1, 'the closed one is not re-marked (its one mark was the close)');
  w.advance(15_000);
  assert.equal(marks(w).filter((p) => p.sessionId === 's1' && p.open).length, 2, 'the beat keeps going');
  t.stop();
  w.advance(30_000);
  assert.equal(marks(w).filter((p) => p.sessionId === 's1' && p.open).length, 2, 'a stopped tracker beats no more');
});

test('nothing is sent on a closed connection, but the set keeps the session for the reconnect', () => {
  const w = world();
  const t = w.tracker();
  w.setConnection(false);
  t.open('s1');
  assert.equal(w.calls.length, 0, 'a downed window shows nothing: no mark goes out');
  assert.ok(t.isOpen('s1'), 'the window still shows the session locally');
  w.setConnection(true);
  t.resend();   // the reconnect says the whole set at once (the host's marks for the new connection are fresh)
  assert.ok(mark(w, 's1', true), 'the resend carries the mark the connection was down for');
});

test('a resend marks every open session at once, and nothing that is not open', () => {
  const w = world();
  const t = w.tracker();
  t.open('s1');
  t.open('s2');
  t.close('s2');
  t.resend();
  assert.deepEqual(
    marks(w).filter((p) => p.open).map((p) => p.sessionId),
    ['s1', 's2', 's1'],
    'the opens, then the resend of what is open now',
  );
});

test('the heartbeat interval is 15 s: well under the host\'s 90 s expiry, browser-throttling included', () => {
  const w = world();
  const t = w.tracker();
  t.start();
  assert.ok([...w.timers.values()].some((x) => x.ms === 15_000), 'the beat is scheduled for 15 s');
  assert.equal(w.run('HEARTBEAT_MS'), 15_000);
});
