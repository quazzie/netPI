// rpc (web/src/lib/rpc.svelte.js) keeps one deadline per call: it is set when the call is made and is kept through
// the switch to the HTTP fallback (the fetch continues with the time still left in it, as an AbortSignal) and
// through the reading of the response body. A call with no timeout waits for the server — no default is invented
// that would cut off the long operations (backups, compactions) that pass their own.
//
// The real client source runs in a vm with a controllable clock and fake timers, and a fetch that never answers
// unless the test tells it to — so only a deadline can end a call.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

// The client's imports become context entries: only the token header and the bus are left behind, and the paths
// under test use neither (the bus would only matter for a socket that answers).
const source = fs
  .readFileSync(new URL('../src/lib/rpc.svelte.js', import.meta.url), 'utf8')
  .replace(/^import .*;\r?\n/gm, '')
  .replaceAll('export ', '');

/** A fresh world: the client's module state, its own clock, timers and fetch. */
const world = () => {
  let now = 0;
  let id = 0;
  const timers = new Map();
  const requests = [];
  const schedule = (fn, ms) => {
    const t = { fn, ms, due: now + ms, id: ++id };
    timers.set(t.id, t);
    return t.id;
  };
  const context = vm.createContext({
    $state: (x) => x,
    console,
    Date: { now: () => now },
    Math, Map, Set, Promise, Error, JSON,
    AbortController,
    setTimeout: schedule,
    clearTimeout: (i) => timers.delete(i),
    authHeaders: (h) => h,
    // a fetch that never answers until the check resolves it (or the signal goes)
    fetch: (url, opts) => {
      const req = { url, opts, done: false };
      requests.push(req);
      return new Promise((resolve, reject) => {
        req.resolve = (body, { status = 200, bodyDelay = 0 } = {}) => {
          if (req.done) return;
          req.done = true;
          let bodyReady;
          const ready = new Promise((r) => (bodyReady = r));
          schedule(bodyReady, bodyDelay); // the body arrives bodyDelay ms later, in this world's time
          resolve({
            ok: status < 400,
            status,
            statusText: '',
            text: () => ready.then(() => body),
          });
        };
        const onAbort = () => {
          if (req.done) return;
          req.done = true;
          reject(Object.assign(new Error('aborted'), { name: 'AbortError' }));
        };
        if (opts?.signal) {
          if (opts.signal.aborted) onAbort();
          else opts.signal.addEventListener('abort', onAbort);
        }
      });
    },
  });
  vm.runInContext(source, context);
  return {
    /** Run an expression in the client's scope. */
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
    requests,
  };
};

const timerOf = (w, ms) => [...w.timers.values()].some((t) => t.ms === ms);
const failure = (call) => call.then(() => null, (e) => e);

test('the switch to HTTP keeps the deadline, and the fetch carries what is left of it', async () => {
  const w = world();
  const call = w.run("rpc('example', {}, { timeout: 8000 })");
  w.advance(3000); // the socket did not come back: the call switched to HTTP
  assert.ok(timerOf(w, 8000), "the fallback keeps the call's deadline");
  assert.ok(w.requests[0].opts.signal instanceof AbortSignal, 'the fallback fetch carries the deadline as an AbortSignal');
  assert.ok(timerOf(w, 5000), 'the abort is scheduled for the remaining time (5000 ms)');

  // the deadline ends the call, and the error says what a timeout means for a call that left the client
  w.advance(5000);
  const err = await failure(call);
  assert.equal(err?.code, 'timeout');
  assert.equal(err?.method, 'example');
  assert.match(err?.message ?? '', /may still have completed on the server/);
  assert.equal(w.requests[0].opts.signal.aborted, true, 'the fetch was aborted with the deadline');
});

test('forced HTTP honours the timeout of the call, for the whole of it', async () => {
  const w = world();
  const call = w.run("rpc('example', {}, { http: true, timeout: 8000 })");
  assert.ok(w.requests[0].opts.signal instanceof AbortSignal);
  assert.ok(timerOf(w, 8000));
  w.advance(8000);
  const err = await failure(call);
  assert.equal(err?.code, 'timeout');
  assert.match(err?.message ?? '', /may still have completed on the server/);
});

test('forced HTTP with no timeout waits for the server: no signal, no timer, no default invented', () => {
  const w = world();
  const before = w.timers.size;
  w.run("rpc('example', {}, { http: true })");
  assert.equal(w.requests[0].opts.signal, undefined);
  assert.equal(w.timers.size, before);
});

test('a call with no timeout on the socket path keeps the 30 s default it always had', () => {
  const w = world();
  w.run("rpc('example', {})");
  assert.ok(timerOf(w, 30_000));
});

test('the deadline covers the reading of the body too', async () => {
  const w = world();
  const call = w.run("rpc('example', {}, { timeout: 8000 })");
  w.advance(3000); // to HTTP, 5000 ms left
  w.requests[0].resolve('{}', { bodyDelay: 9000 }); // the body takes longer than what is left
  w.advance(5000); // the deadline hits while it is being read
  const err = await failure(call);
  assert.equal(err?.code, 'timeout');
  assert.equal(w.requests[0].opts.signal.aborted, true);
});

test('a call that finishes resolves with the response and releases its timers', async () => {
  const w = world();
  const call = w.run("rpc('example', {}, { timeout: 8000 })");
  w.advance(3000); // to HTTP
  w.requests[0].resolve(JSON.stringify({ done: true }));
  w.advance(0); // the body arrives immediately: fire its timer
  const r = await call;
  assert.equal(r?.done, true, JSON.stringify(r));
  assert.ok([...w.timers.values()].every((t) => t.ms !== 8000 && t.ms !== 5000));
});
