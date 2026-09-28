#!/usr/bin/env node
// Inspect the running NetPI through its RPC API: the way a person or a debugging agent looks inside the app.
// Finds the instance through <home>/server.json (its url and this run's token). See docs/DEBUGGING.md.
//
//   node scripts/netpi.mjs                          the overview (diag.overview): start here
//   node scripts/netpi.mjs <method> [params]        any RPC method; params as JSON or key=value pairs
//   node scripts/netpi.mjs methods [prefix]         every method with what it does (rpc.list)
//
//   node scripts/netpi.mjs diag.problems
//   node scripts/netpi.mjs diag.calls limit=20 errors=true
//   node scripts/netpi.mjs diag.run sessionId=ses_abc
//   node scripts/netpi.mjs diag.journal '{"sessionId":"ses_abc","limit":200}'
//
// Options: --home <dir> (default: NETPI_HOME, else ~/.netpi), --write (allow methods that change something: they are
// refused without it, so looking around never changes the app), --compact (one-line JSON).
// Exit codes: 0 ok, 1 the call failed, 2 NetPI isn't running or can't be reached.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const args = process.argv.slice(2);
const flag = (name) => {
  const i = args.indexOf(name);
  if (i < 0) return false;
  args.splice(i, 1);
  return true;
};
const option = (name) => {
  const i = args.indexOf(name);
  if (i < 0) return null;
  const value = args[i + 1];
  args.splice(i, 2);
  return value;
};

const write = flag('--write');
const compact = flag('--compact');
const home = option('--home') ?? process.env.NETPI_HOME ?? path.join(os.homedir(), '.netpi');
let method = args.shift() ?? 'diag.overview';
let params = {};

if (method === 'methods' || method === 'help') {
  const prefix = args.shift() ?? '';
  const list = await call('rpc.list', {});
  const declared = new Map((Array.isArray(list) ? list : []).map((m) => [m.method, m.readOnly]));
  const rows = (Array.isArray(list) ? list : [])
    .filter((m) => m.method.startsWith(prefix))
    .sort((a, b) => a.method.localeCompare(b.method));
  for (const m of rows) console.log(`${m.method.padEnd(24)} ${isReadOnly(m.method, declared) ? '   ' : 'W  '}${m.description ?? ''}`);
  console.log('\nW = changes something: needs --write. Blank = the host marks it read-only (older server: by name).');
  process.exit(0);
}

// params: one JSON object, or key=value pairs (values are JSON when they parse: numbers, true, null, …)
if (args.length === 1 && args[0].trim().startsWith('{')) params = JSON.parse(args[0]);
else
  for (const a of args) {
    const eq = a.indexOf('=');
    if (eq < 1) fail(1, `Can't read "${a}": pass key=value pairs or one JSON object.`);
    const key = a.slice(0, eq);
    const raw = a.slice(eq + 1);
    try {
      params[key] = JSON.parse(raw);
    } catch {
      params[key] = raw;
    }
  }

if (!write && !isReadOnly(method, await declaredReadOnly()))
  fail(1, `${method} changes something; pass --write to call it. (Looking around: the methods rpc.list marks read-only.)`);

const result = await call(method, params);
console.log(compact ? JSON.stringify(result) : JSON.stringify(result, null, 2));

/**
 * The host's own answer, not a guess: `rpc.list` carries `readOnly` per method (idea-de1s7t), so a method cannot become
 * writable — or blocked — because of its name. A server older than that has no flag, and the name rule below stands in.
 */
let declared = null;
async function declaredReadOnly() {
  if (declared === null) {
    try {
      const list = await call('rpc.list', {});
      declared = new Map((Array.isArray(list) ? list : []).map((m) => [m.method, m.readOnly]));
    } catch {
      declared = new Map();
    }
  }
  return declared;
}

function isReadOnly(m, declared) {
  const said = declared?.get(m);
  if (typeof said === 'boolean') return said;
  return readOnly(m);
}

/** Fallback for a server that does not declare it (by name): the diag.* views and the list/get style methods. */
function readOnly(m) {
  if (m === 'diag.reload') return false;
  if (m.startsWith('diag.') || m === 'rpc.list' || m === 'app.info' || m === 'services.list') return true;
  return /\.(list|get|recent|status|summary|preview|snapshot|schema|messages|session|queue|tools|output|search)$/.test(m);
}

async function call(m, p) {
  const file = path.join(home, 'server.json');
  let server;
  try {
    server = JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch {
    fail(2, `NetPI isn't running with the home ${home} (no ${file}). Another home: --home <dir> or NETPI_HOME.`);
  }
  let res;
  try {
    res = await fetch(`${server.url}/api/rpc/${m}`, {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'X-NetPI-Token': server.token },
      body: JSON.stringify(p ?? {}),
    });
  } catch (e) {
    fail(2, `NetPI at ${server.url} (pid ${server.pid}) doesn't answer: ${e.cause?.code ?? e.message}. The file may be left from a crash: ${file}`);
  }
  const text = await res.text();
  let body;
  try {
    body = text ? JSON.parse(text) : null;
  } catch {
    body = text;
  }
  if (!res.ok) fail(1, `${m} failed (${res.status}): ${body?.error ? `${body.error.code}: ${body.error.message}` : text}`);
  return body;
}

function fail(code, message) {
  console.error(message);
  process.exit(code);
}
