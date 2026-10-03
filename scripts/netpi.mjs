#!/usr/bin/env node
// Inspect the running NetPI through its RPC API: the way a person or a debugging agent looks inside the app.
// Finds the instance through <home>/server.json (its url and this run's token). See docs/DEBUGGING.md.
//
//   node scripts/netpi.mjs                          the overview (diag.overview): start here
//   node scripts/netpi.mjs <method> [params]        any RPC method; params as JSON or key=value pairs
//   node scripts/netpi.mjs <method> --params-file f  params from a JSON file (`-` = standard input): a long prompt or an image
//                                                    need no shell quoting and no command-line length limit
//   node scripts/netpi.mjs methods [prefix]         every method with what it does (rpc.list)
//
//   node scripts/netpi.mjs diag.problems
//   node scripts/netpi.mjs diag.calls limit=20 errors=true
//   node scripts/netpi.mjs diag.run sessionId=ses_abc
//   node scripts/netpi.mjs diag.journal '{"sessionId":"ses_abc","limit":200}'
//
// Options: --home <dir> (default: NETPI_HOME, else ~/.netpi), --write (allow methods that change something: they are
// refused without it, so looking around never changes the app), --params-file <file|->, --compact (one-line JSON), --timeout <seconds> (give up
// on a server that does not answer in that time; without it a call waits as long as the server works: backup.create,
// compaction.run and the model-backed methods take minutes, and abandoning one that is still running invites a rerun).
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
  if (value === undefined) fail(1, `${name} needs a value.`);
  args.splice(i, 2);
  return value;
};

const write = flag('--write');
const compact = flag('--compact');
// Declared before its first use: the read-only map from rpc.list, fetched on demand (see declaredReadOnly below).
let declared = null;
const home = option('--home') ?? process.env.NETPI_HOME ?? path.join(os.homedir(), '.netpi');
const paramsFile = option('--params-file');

// A bounded call is opt-in (--timeout <seconds>): build.ps1 passes it, because it runs this inside the install lock to say
// what a publish would disturb, and a server that never answers (half-dead, or a port held by something else) must not
// hold the lock forever. An ordinary call waits for the server.
const timeoutSeconds = Number(option('--timeout') ?? 0);
if (!Number.isFinite(timeoutSeconds) || timeoutSeconds < 0) fail(1, '--timeout takes a number of seconds (0 or more).');
const CALL_TIMEOUT_MS = timeoutSeconds * 1000;
// Anything still starting with -- is an option this script does not have: say so, instead of sending it as a parameter.
const unknown = args.find((a) => a.startsWith('--'));
if (unknown) fail(1, `Unknown option ${unknown}. Options: --home, --write, --compact, --timeout, --params-file.`);
let method = args.shift() ?? 'diag.overview';
let params = {};

if (method === 'methods' || method === 'help') {
  const prefix = args.shift() ?? '';
  const list = await call('rpc.list', {});
  const rows = (Array.isArray(list) ? list : [])
    .filter((m) => m.method.startsWith(prefix))
    .sort((a, b) => a.method.localeCompare(b.method));
  for (const m of rows) console.log(`${m.method.padEnd(24)} ${m.readOnly === true ? '   ' : 'W  '}${m.description ?? ''}`);
  console.log('\nW = changes something (or the host does not say it only reads): needs --write. Blank = the host marks it read-only.');
  process.exit(0);
}

// params: a file (or standard input), one JSON object, or key=value pairs (values are JSON when they parse: numbers, true, null, …)
if (paramsFile !== null) {
  if (args.length > 0) fail(1, `--params-file replaces the parameters on the command line; got ${args.map((a) => `"${a}"`).join(' ')} as well.`);
  let text;
  try {
    text = fs.readFileSync(paramsFile === '-' ? 0 : paramsFile, 'utf8');
  } catch (e) {
    fail(1, `Can't read the parameters from ${paramsFile === '-' ? 'standard input' : paramsFile}: ${e.message}`);
  }
  try {
    params = JSON.parse(text);
  } catch (e) {
    fail(1, `The parameters in ${paramsFile === '-' ? 'standard input' : paramsFile} are not JSON: ${e.message}`);
  }
  if (params === null || typeof params !== 'object' || Array.isArray(params)) fail(1, 'The parameters must be one JSON object.');
} else if (args.length === 1 && args[0].trim().startsWith('{')) {
  try {
    params = JSON.parse(args[0]);
  } catch (e) {
    fail(1, `The parameters are not JSON: ${e.message}`);
  }
} else
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

if (!write && (await declaredReadOnly()).get(method) !== true)
  fail(1, `${method} changes something (or the host does not mark it read-only); pass --write to call it. (Looking around: the methods rpc.list marks read-only.)`);

const result = await call(method, params);
console.log(compact ? JSON.stringify(result) : JSON.stringify(result, null, 2));

/**
 * The host's own answer, not a guess: `rpc.list` carries `readOnly` per method (idea-de1s7t), so a method cannot become
 * writable — or blocked — because of its name. A method the host does not mark (or a host that does not answer rpc.list)
 * is treated as writing: it needs --write.
 */
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
      signal: CALL_TIMEOUT_MS > 0 ? AbortSignal.timeout(CALL_TIMEOUT_MS) : undefined,
    });
  } catch (e) {
    fail(2, `NetPI at ${server.url} (pid ${server.pid}) doesn't answer: ${e.cause?.code ?? e.message}. The file may be left from a crash: ${file}`);
  }
  let text;
  try {
    text = await res.text();
  } catch (e) {
    fail(2, `NetPI at ${server.url} (pid ${server.pid}) stopped answering ${m}: ${e.cause?.code ?? e.message}.`);
  }
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
