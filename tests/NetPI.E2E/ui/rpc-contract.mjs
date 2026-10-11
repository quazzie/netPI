// The RPC contract between the server and the UI (idea-yvcy8b): every method the UI calls by a literal name must exist in
// the real server's rpc.list. The UI is written by hand against the protocol, so a renamed or removed method would
// otherwise surface only when the page that calls it is opened.
//
//   node tests/NetPI.E2E/ui/rpc-contract.mjs --url http://127.0.0.1:PORT --token TOKEN
//
// Output: one "  ✓/✗ name — detail" line per check, then {"ok", "checks", "errors"} as the last line.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const args = process.argv.slice(2);
const opt = (name) => { const i = args.indexOf(`--${name}`); return i >= 0 ? args[i + 1] : undefined; };
const BASE = opt('url');
const TOKEN = opt('token');
if (!BASE || !TOKEN) { console.error('usage: rpc-contract.mjs --url <server> --token <token>'); process.exit(2); }

// Methods that are not the server's: what the desktop shell answers itself.
const NOT_THE_SERVERS = [/^desktop\./];
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

try {
  const real = await list(BASE, { 'X-NetPI-Token': TOKEN });
  check('the server lists its methods', real.size > 50, `${real.size} methods`);

  const calls = uiCalls();
  const unknown = [...calls].filter(([m]) => !own(m) && !real.has(m)).map(([m, f]) => `${m} (${f})`).sort();
  check('every method the UI calls by name exists on the server', unknown.length === 0,
    unknown.length ? `${unknown.length} unknown: ${unknown.join(', ')}` : `${calls.size} called`);
} catch (e) {
  errors.push(String(e?.stack ?? e));
  check('the contract check ran', false, String(e?.message ?? e));
}

const ok = checks.every((c) => c.ok) && errors.length === 0;
console.log(JSON.stringify({ ok, checks, errors }));
process.exit(ok ? 0 : 1);
