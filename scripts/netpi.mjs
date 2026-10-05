#!/usr/bin/env node
// Inspect the running NetPI through its RPC API: the way a person or a debugging agent looks inside the app.
// Finds the instance through <home>/server.json (its url and this run's token). See docs/DEBUGGING.md.
//
//   node scripts/netpi.mjs                          the overview (diag.overview): start here
//   node scripts/netpi.mjs <method> [params]        any RPC method; params as JSON or key=value pairs
//   node scripts/netpi.mjs <method> --params-file f  params from a JSON file (`-` = standard input): a long prompt or an image
//                                                    need no shell quoting and no command-line length limit
//   node scripts/netpi.mjs methods [prefix]         every method with what it does (rpc.list)
//   node scripts/netpi.mjs events [--types a,b] [--session id | --project name] [--until text] [--json] [--activity]
//                                                    the bus as one line per event, reconnecting on its own (streamEvents)
//   node scripts/netpi.mjs runs.result sessionId=ses_abc --pick report   one field of an answer, a string printed as it is
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
// Exit codes: 0 ok, 1 the call failed, 2 NetPI isn't running or can't be reached, 4 events --until did not match within --timeout.
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
// --pick <path>: print one field of the answer (a.b.0.c); a string is printed as it is, e.g. runs.result --pick report
const pick = option('--pick');
// events: the bus as lines (see streamEvents)
const ev = {
  types: option('--types'),
  session: option('--session'),
  project: option('--project'),
  until: option('--until'),
  json: flag('--json'),
  activity: flag('--activity'),
};
// Anything still starting with -- is an option this script does not have: say so, instead of sending it as a parameter.
const unknown = args.find((a) => a.startsWith('--'));
if (unknown) fail(1, `Unknown option ${unknown}. Options: --home, --write, --compact, --timeout, --params-file, --pick; for events: --types, --session, --project, --until, --json, --activity.`);
let method = args.shift() ?? 'diag.overview';
let params = {};

if (method !== 'events' && (ev.types || ev.session || ev.project || ev.until || ev.json || ev.activity))
  fail(1, '--types, --session, --project, --until, --json and --activity are options of the events command.');
if (method === 'events') {
  await streamEvents();
  process.exit(0);
}

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

// A follow-up waits for the whole run: on a worker's run that is its whole work package (a coordinator's updates sat
// unread for an hour that way). Say so before it is queued behind a long run.
if (method === 'agent.send' && params.mode === 'queue' && params.sessionId) await warnQueueBehindLongRun(params.sessionId);

const result = await call(method, params);
if (pick !== null) {
  let v = result;
  for (const part of pick.split('.').filter(Boolean)) v = v == null ? undefined : v[/^\d+$/.test(part) ? Number(part) : part];
  if (v === undefined) fail(1, `The answer has no ${pick}.`);
  console.log(typeof v === 'string' ? v : compact ? JSON.stringify(v) : JSON.stringify(v, null, 2));
} else console.log(compact ? JSON.stringify(result) : JSON.stringify(result, null, 2));

async function warnQueueBehindLongRun(sessionId) {
  try {
    const run = await tryCall('agent.get', { sessionId });
    if (!run || !['running', 'queued', 'yielded'].includes(run.status) || !run.startedAt) return;
    const minutes = Math.floor((Date.now() - Date.parse(run.startedAt)) / 60000);
    if (minutes < 10) return;
    console.error(`warning: ${sessionId} has been running for ${minutes} min, and a queued message waits until the run ends. ` +
      'Send it with mode steer to have it read at the next step, or promote it later: agent.promote { sessionId, id }.');
  } catch {}
}

/**
 * `events`: the bus as one line per event, for a coordinator that watches instead of polling (a Monitor, a script).
 * Connects to the app's WebSocket (the token from server.json, read again on every reconnect: a restart changes it),
 * subscribes, filters, and reconnects on its own. agent.status prints only changes of status unless --activity.
 *
 *   --types a,b      event types or prefixes (default: the coordinator's set below; "all": everything but the per-token
 *                    and per-chunk streams, unless they are named)
 *   --session id     one chat (and its subagents)        --project name|id   the chats of one project
 *   --until text     exit 0 after printing the first line that contains it (e.g. "→ idle")
 *   --json           the raw frame (plus the chat's title) instead of the line
 *   --timeout s      exit 4 when --until has not matched by then
 */
async function streamEvents() {
  const DEFAULT = ['agent.status', 'guard.asked', 'ask.asked', 'plan.changed', 'process.idle', 'session.created', 'session.deleted', 'schedules.ran'];
  const NOISY = ['stream.', 'tool.output', 'process.output', 'message.updated'];
  const SCOPED = ['message.', 'messages.', 'stream.', 'tool.', 'agent.queue', 'agent.notice', 'context.prompt', 'schedules.'];
  const wanted = !ev.types ? DEFAULT : ev.types === 'all' ? null : ev.types.split(',').map((s) => s.trim()).filter(Boolean);
  const matches = (type) => (wanted ? wanted.some((w) => type === w || type.startsWith(w.endsWith('.') ? w : w + '.')) : !NOISY.some((n) => type.startsWith(n)));
  const scoped = (wanted ?? []).some((w) => SCOPED.some((s) => w.startsWith(s) || s.startsWith(w)));
  const sessions = new Map(); // id → { title, projectId, parent }
  const status = new Map(); // agent id → last status printed
  let projectId = null;
  let deadline = null;
  // --timeout: how long to watch; with --until, not matching by then is a failure (exit 4)
  if (timeoutSeconds > 0)
    deadline = setTimeout(() => {
      if (!ev.until) process.exit(0);
      console.error(`events: --until "${ev.until}" did not match in ${timeoutSeconds}s`);
      process.exit(4);
    }, timeoutSeconds * 1000);

  const remember = (s) => s?.id && sessions.set(s.id, { title: s.title || 'New session', projectId: s.projectId ?? null, parent: s.parentSessionId ?? null });
  const sidOf = (f) => f.sid ?? f.d?.sessionId ?? f.d?.agent?.sessionId ?? f.d?.session?.id ?? f.d?.process?.sessionId ?? (f.type === 'session.deleted' ? f.d?.id : null);
  const inScope = (sid) => {
    if (ev.session) return sid === ev.session || sessions.get(sid)?.parent === ev.session;
    if (projectId) return sid != null && sessions.get(sid)?.projectId === projectId;
    return true;
  };
  const time = (ts) => new Date(ts ?? Date.now()).toLocaleTimeString('en-GB', { hour12: false });
  const one = (s, n = 160) => String(s ?? '').replace(/\s+/g, ' ').slice(0, n);
  const describe = (f) => {
    const d = f.d ?? {};
    switch (f.type) {
      case 'agent.status': {
        const a = d.agent ?? {};
        const was = status.get(a.id);
        if (!ev.activity && was === a.status) return null;
        status.set(a.id, a.status);
        return `${a.name ?? 'agent'}: ${was && was !== a.status ? `${was} → ` : ''}${a.status}${a.activity && ev.activity ? ` (${one(a.activity, 60)})` : ''}${a.status === 'idle' || a.status === 'completed' || a.status === 'failed' ? ` · turns ${a.turns ?? 0}${a.error ? ` · error: ${one(a.error, 120)}` : ''}` : ''}`;
      }
      case 'guard.asked': return `${d.tool} ${one(d.subject, 100)} — rule ${d.rule} (approval ${d.approvalId})`;
      case 'ask.asked': return one(d.questions?.[0]?.question, 160);
      case 'process.idle': return `silent ${d.idleSeconds}s: ${one(d.process?.command, 140)} (${d.process?.id})`;
      case 'plan.changed': return `plan ${d.mode ?? ''} ${d.status ?? ''} ${one(d.title, 80)}`.trim();
      case 'session.created': return one(d.session?.title, 120);
      default: return one(JSON.stringify(d), 200);
    }
  };

  for (let backoff = 1000; ; backoff = Math.min(backoff * 2, 15000)) {
    let server = null;
    try { server = JSON.parse(fs.readFileSync(path.join(home, 'server.json'), 'utf8')); } catch {}
    if (server) {
      try {
        for (const s of (await tryCall('sessions.list', { includeSubagents: true, limit: 2000 })) ?? []) remember(s);
        if (ev.project && !projectId) {
          const list = (await tryCall('projects.list', {})) ?? [];
          const p = list.find((x) => x.id === ev.project || x.name?.toLowerCase() === ev.project.toLowerCase());
          if (!p) fail(1, `No project ${ev.project}. Projects: ${list.map((x) => x.name).join(', ')}.`);
          projectId = p.id;
        }
      } catch {}
      const opened = await new Promise((resolve) => {
        let open = false;
        const ws = new WebSocket(`${server.url.replace(/^http/, 'ws')}/ws?token=${encodeURIComponent(server.token)}`);
        ws.onopen = () => {
          open = true;
          backoff = 1000;
          ws.send(JSON.stringify({ t: 'sub', sessions: scoped ? (ev.session ? [ev.session] : ['*']) : [] }));
          console.error(`# events: connected to ${server.url}`);
        };
        ws.onmessage = (m) => {
          let f;
          try { f = JSON.parse(m.data); } catch { return; }
          if (f.t !== 'ev') return;
          if (f.type === 'session.created' || f.type === 'session.updated') remember(f.d?.session);
          if (!matches(f.type)) return;
          const sid = sidOf(f);
          if (!inScope(sid)) return;
          const text = describe(f);
          if (text === null) return;
          const title = sid ? sessions.get(sid)?.title : null;
          const line = ev.json
            ? JSON.stringify({ ...f, title: title ?? undefined })
            : `${time(f.ts)} ${f.type.padEnd(15)} ${title ? `${one(title, 40)} [${sid}] ` : sid ? `[${sid}] ` : ''}${text}`;
          console.log(line);
          if (ev.until && line.includes(ev.until)) {
            if (deadline) clearTimeout(deadline);
            ws.close();
            process.exit(0);
          }
        };
        ws.onerror = () => {};
        ws.onclose = () => resolve(open);
      });
      if (opened) console.error('# events: disconnected; reconnecting');
    }
    await new Promise((r) => setTimeout(r, backoff));
  }
}

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

/** call() for the long-lived paths (events, the queue warning): a failure throws instead of ending the process. */
async function tryCall(m, p) {
  const file = path.join(home, 'server.json');
  const server = JSON.parse(fs.readFileSync(file, 'utf8'));
  const res = await fetch(`${server.url}/api/rpc/${m}`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', 'X-NetPI-Token': server.token },
    body: JSON.stringify(p ?? {}),
    signal: AbortSignal.timeout(15000),
  });
  const text = await res.text();
  const body = text ? JSON.parse(text) : null;
  if (!res.ok) throw new Error(`${m} failed (${res.status}): ${body?.error?.message ?? text}`);
  return body;
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
