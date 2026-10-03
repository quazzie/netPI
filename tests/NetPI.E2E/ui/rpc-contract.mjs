// The RPC contract between the server, the UI mock and the UI (idea-yvcy8b): every method the mock answers and every
// method the UI calls by a literal name must exist in the real server's rpc.list. The mock and the UI are written by
// hand against the protocol, so a renamed or removed method used to surface only when the mock e2e died halfway.
//
//   node tests/NetPI.E2E/ui/rpc-contract.mjs --url http://127.0.0.1:PORT --token TOKEN
//
// Output: one "  ✓/✗ name — detail" line per check, then {"ok", "checks", "errors"} as the last line.
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const args = process.argv.slice(2);
const opt = (name) => { const i = args.indexOf(`--${name}`); return i >= 0 ? args[i + 1] : undefined; };
const BASE = opt('url');
const TOKEN = opt('token');
if (!BASE || !TOKEN) { console.error('usage: rpc-contract.mjs --url <server> --token <token>'); process.exit(2); }

// Methods that are not the server's: the mock's own test knobs, and what the desktop shell answers itself.
const NOT_THE_SERVERS = [/^mock\./, /^desktop\./];
const own = (m) => NOT_THE_SERVERS.some((r) => r.test(m));

const checks = [];
const errors = [];
const check = (name, ok, detail = '') => {
  checks.push({ name, ok: !!ok, detail });
  console.log(`  ${ok ? '✓' : '✗'} ${name}${detail ? ' — ' + detail : ''}`);
};

async function list(base, headers = {}) {
  const res = await fetch(`${base}/api/rpc/rpc.list`, { method: 'POST', headers: { 'Content-Type': 'application/json', ...headers }, body: '{}' });
  if (!res.ok) throw new Error(`rpc.list at ${base}: HTTP ${res.status} ${await res.text()}`);
  return new Set((await res.json()).map((m) => m.method));
}

function freePort() {
  return new Promise((resolve, reject) => {
    const s = net.createServer();
    s.once('error', reject);
    s.listen(0, '127.0.0.1', () => { const { port } = s.address(); s.close(() => resolve(port)); });
  });
}

async function startMock() {
  const port = await freePort();
  const proc = spawn(process.execPath, [path.join(REPO, 'web/mock/server.mjs'), '--port', String(port), '--no-auth'], { stdio: ['ignore', 'pipe', 'pipe'] });
  let out = '';
  await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('the mock did not start within 20 s: ' + out)), 20_000);
    const seen = (chunk) => { out += chunk; if (out.includes('NetPI mock server on')) { clearTimeout(timer); resolve(); } };
    proc.stdout.on('data', seen);
    proc.stderr.on('data', (c) => (out += c));
    proc.once('exit', (code) => { clearTimeout(timer); reject(new Error(`the mock exited (${code}): ${out}`)); });
  });
  return { url: `http://127.0.0.1:${port}`, stop: () => proc.kill() };
}

/** The method names the UI calls by a literal: rpc('x.y'), call('x.y'), optional('x.y'), action('x.y'), hasRpc('x.y'). */
function uiCalls() {
  const roots = [path.join(REPO, 'web/src')];
  for (const p of fs.readdirSync(path.join(REPO, 'plugins'))) {
    const ui = path.join(REPO, 'plugins', p, 'ui');
    if (fs.existsSync(ui)) roots.push(ui);
  }
  const calls = new Map(); // method → first file that calls it
  const pattern = /\b(?:rpc|call|optional|action|hasRpc)\s*\(\s*['"`]([a-z]\w*(?:\.\w+)+)['"`]/g;
  const walk = (dir) => {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      if (e.name === 'node_modules' || e.name === 'dist') continue;
      const full = path.join(dir, e.name);
      if (e.isDirectory()) walk(full);
      else if (/\.(svelte|js|mjs|ts)$/.test(e.name)) {
        const text = fs.readFileSync(full, 'utf8');
        for (const m of text.matchAll(pattern)) if (!calls.has(m[1])) calls.set(m[1], path.relative(REPO, full).replaceAll('\\', '/'));
      }
    }
  };
  roots.forEach(walk);
  return calls;
}

let mock;
try {
  const real = await list(BASE, { 'X-NetPI-Token': TOKEN });
  check('the server lists its methods', real.size > 50, `${real.size} methods`);

  mock = await startMock();
  const mocked = await list(mock.url);
  const invented = [...mocked].filter((m) => !own(m) && !real.has(m)).sort();
  check('every method the mock answers exists on the server', invented.length === 0,
    invented.length ? `the mock answers ${invented.length} the server does not have: ${invented.join(', ')}` : `${mocked.size} mocked`);

  const calls = uiCalls();
  const unknown = [...calls].filter(([m]) => !own(m) && !real.has(m)).map(([m, f]) => `${m} (${f})`).sort();
  check('every method the UI calls by name exists on the server', unknown.length === 0,
    unknown.length ? `${unknown.length} unknown: ${unknown.join(', ')}` : `${calls.size} called`);
} catch (e) {
  errors.push(String(e?.stack ?? e));
  check('the contract check ran', false, String(e?.message ?? e));
} finally {
  mock?.stop();
}

const ok = checks.every((c) => c.ok) && errors.length === 0;
console.log(JSON.stringify({ ok, checks, errors }));
process.exit(ok ? 0 : 1);
