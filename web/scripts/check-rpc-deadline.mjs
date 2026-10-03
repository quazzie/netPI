#!/usr/bin/env node
// rpc (web/src/lib/rpc.svelte.js) keeps one deadline per call: it is set when the call is made and is kept through
// the switch to the HTTP fallback (the fetch continues with the time still left in it, as an AbortSignal) and
// through the reading of the response body. A call with no timeout waits for the server — no default is invented
// that would cut off the long operations (backups, compactions) that pass their own:
//
//   node web/scripts/check-rpc-deadline.mjs
//
// The real client source runs in a vm with a controllable clock and fake timers, and a fetch that never answers
// unless the check tells it to — so only a deadline can end a call.
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

const results = [];
const check = (what, ok, detail = '') => results.push({ what, ok, detail });

// the switch to HTTP: the deadline is not dropped with the socket, and the fetch carries what is left of it
{
  const w = world();
  const call = w.run("rpc('example', {}, { timeout: 8000 })");
  w.advance(3000); // the socket did not come back: the call switched to HTTP
  check('the fallback keeps the call’s deadline', [...w.timers.values()].some((t) => t.ms === 8000));
  check('the fallback fetch carries the deadline as an AbortSignal', w.requests[0].opts.signal instanceof AbortSignal);
  check('and the abort is scheduled for the remaining time (5000 ms)', [...w.timers.values()].some((t) => t.ms === 5000));

  // the deadline ends the call, and the error says what a timeout means for a call that left the client
  w.advance(5000);
  const err = await call.then(() => null, (e) => e);
  check('the deadline ends the call', err?.code === 'timeout' && err?.method === 'example', err?.code);
  check('and it says the call may still have completed on the server', /may still have completed on the server/.test(err?.message ?? ''), err?.message);
  check('the fetch was aborted with the deadline', w.requests[0].opts.signal.aborted === true);
}

// forced HTTP honours the timeout of the call, for the whole of it
{
  const w = world();
  const call = w.run("rpc('example', {}, { http: true, timeout: 8000 })");
  check('forced HTTP honours opts.timeout (AbortSignal on the fetch)', w.requests[0].opts.signal instanceof AbortSignal);
  check('for the whole timeout (8000 ms)', [...w.timers.values()].some((t) => t.ms === 8000));
  w.advance(8000);
  const err = await call.then(() => null, (e) => e);
  check('and it is cut off with the same timeout error', err?.code === 'timeout' && /may still have completed on the server/.test(err?.message ?? ''), err?.message);
}

// a call with no timeout waits for the server: no signal, no timer, no default invented
{
  const w = world();
  const before = w.timers.size;
  w.run("rpc('example', {}, { http: true })");
  check('forced HTTP with no timeout waits for the server (no signal, no timer)', !w.requests[0].opts.signal && w.timers.size === before);
}

// and on the socket path, a call with no timeout keeps the 30 s default it always had
{
  const w = world();
  w.run("rpc('example', {})");
  check('a call with no timeout on the socket path keeps the 30 s default', [...w.timers.values()].some((t) => t.ms === 30_000));
}

// the deadline covers the reading of the body too
{
  const w = world();
  const call = w.run("rpc('example', {}, { timeout: 8000 })");
  w.advance(3000); // to HTTP, 5000 ms left
  w.requests[0].resolve('{}', { bodyDelay: 9000 }); // the body takes longer than what is left
  w.advance(5000); // the deadline hits while it is being read
  const err = await call.then(() => null, (e) => e);
  check('a body read past the deadline times out', err?.code === 'timeout' && w.requests[0].opts.signal.aborted === true, err?.code);
}

// a call that finishes releases its timers
{
  const w = world();
  const call = w.run("rpc('example', {}, { timeout: 8000 })");
  w.advance(3000); // to HTTP
  w.requests[0].resolve(JSON.stringify({ done: true }));
  w.advance(0); // the body arrives immediately: fire its timer
  const r = await call;
  check('a finished call resolves with the response', r?.done === true, JSON.stringify(r));
  check('and releases its timers', [...w.timers.values()].every((t) => t.ms !== 8000 && t.ms !== 5000));
}

let failed = 0;
for (const r of results) {
  if (!r.ok) failed++;
  console.log(`${r.ok ? 'ok  ' : 'FAIL'}  ${r.what}${r.detail ? `  (${r.detail})` : ''}`);
}
console.log(failed ? `${failed} of ${results.length} checks failed` : `all ${results.length} checks passed`);
process.exit(failed ? 1 : 0);
