// The host takes 64 unanswered rpc frames per connection and answers `busy` past that (docs/PROTOCOL.md). The client
// (web/src/lib/rpc.svelte.js) keeps at most 60 on the wire; the rest wait in order and go out as answers come back.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

const source = fs
  .readFileSync(new URL('../src/lib/rpc.svelte.js', import.meta.url), 'utf8')
  .replace(/^import .*;\r?\n/gm, '')
  .replaceAll('export ', '');

/** The client on an open fake socket; `sent` lists the frames that went out. */
const world = () => {
  const sent = [];
  class FakeSocket {
    constructor() {
      this.readyState = 0;
      world.sock = this;
    }
    send(frame) {
      sent.push(JSON.parse(frame));
    }
    close() {}
  }
  const context = vm.createContext({
    $state: (x) => x,
    console,
    URL,
    location: { href: 'http://localhost/' },
    Date,
    Math, Map, Set, Promise, Error, JSON,
    AbortController,
    setTimeout: () => 0,
    clearTimeout: () => {},
    setInterval: () => 0,
    clearInterval: () => {},
    WebSocket: FakeSocket,
    getToken: () => null,
    authHeaders: (h) => h,
    bus: { emit() {} },
    fetch: () => new Promise(() => {}),
  });
  vm.runInContext(source, context);
  vm.runInContext('connect()', context);
  const sock = world.sock;
  sock.readyState = 1;
  sock.onopen();
  const answer = (id) => sock.onmessage({ data: JSON.stringify({ t: 'res', id, r: { id } }) });
  return { run: (code) => vm.runInContext(code, context), sent, answer };
};

const rpcFrames = (w) => w.sent.filter((f) => f.t === 'rpc');

test('calls past 60 unanswered wait, and go out in order as answers come back', async () => {
  const w = world();
  const calls = Array.from({ length: 70 }, (_, i) => w.run(`rpc('m${i}', {}, { timeout: 60000 })`));
  assert.equal(rpcFrames(w).length, 60, 'only 60 are on the wire');
  assert.equal(rpcFrames(w)[0].m, 'm0');

  w.answer(rpcFrames(w)[0].id);
  assert.equal(rpcFrames(w).length, 61, 'one answer frees one slot');
  assert.equal(rpcFrames(w)[60].m, 'm60', 'the waiting calls go in the order they were made');

  for (let i = 1; i < 10; i++) w.answer(rpcFrames(w)[i].id);
  assert.equal(rpcFrames(w).length, 70);
  assert.equal(rpcFrames(w)[69].m, 'm69');

  const first = await calls[0];
  assert.equal(first.id, rpcFrames(w)[0].id);
});

test('a call made while others wait queues behind them, not ahead', () => {
  const w = world();
  for (let i = 0; i < 62; i++) w.run(`rpc('m${i}', {}, { timeout: 60000 })`);
  assert.equal(rpcFrames(w).length, 60);
  w.answer(rpcFrames(w)[0].id); // m60 goes out, m61 still waits
  w.run("rpc('late', {}, { timeout: 60000 })");
  assert.equal(rpcFrames(w).length, 61);
  w.answer(rpcFrames(w)[1].id);
  w.answer(rpcFrames(w)[2].id);
  assert.deepEqual(rpcFrames(w).slice(60).map((f) => f.m), ['m60', 'm61', 'late']);
});
