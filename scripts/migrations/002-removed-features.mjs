#!/usr/bin/env node
// One-off cleanup 002: the data of the features the 2026-10-11 slimming pass removed (docs/archive/2026-10-11-slim.md).
// Nothing reads it any more; this takes it out of a home:
//
//   - the store collections of the removed Ideas checks (netpi.ideas: cards, resolutions, checks, repos, unread, meta,
//     decisions) and of the Coordinator's task queue (netpi.coordinator: tasks) — the row in _collections and its table;
//   - the `synonyms` of every MCP server in settings.json (mcp.servers.<id>.synonyms).
//
//   node scripts/migrations/002-removed-features.mjs [--home <dir>]            dry run: says what it would remove
//   node scripts/migrations/002-removed-features.mjs [--home <dir>] --apply    removes it (NetPI must be closed)
//   node scripts/migrations/002-removed-features.mjs --selftest                runs against a scratch home
//
// The home defaults to NETPI_HOME, else ~/.netpi. --apply refuses while a NetPI runs on that home (its server.json names
// a live process): close NetPI first, run this, start NetPI again. Before it changes anything it copies netpi.db to
// netpi.db.pre-002 and settings.json to settings.json.pre-002 (delete those once the app has started fine); then it
// checks that only the listed collections went and every other one kept its documents. Running it twice is harmless.
// Exit codes: 0 done (or nothing to do), 1 the check after the change failed, 2 a precondition.
// Only the sqlite storage provider is handled (the one the owner's home uses).
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { DatabaseSync } from 'node:sqlite';

const REMOVED = [
  ...['cards', 'resolutions', 'checks', 'repos', 'unread', 'meta', 'decisions'].map((name) => ({ plugin: 'netpi.ideas', name })),
  { plugin: 'netpi.coordinator', name: 'tasks' },
];

class Abort extends Error {}

function args(argv) {
  const out = { home: process.env.NETPI_HOME || path.join(os.homedir(), '.netpi'), apply: false, selftest: false };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--home') out.home = argv[++i] ?? '';
    else if (a === '--apply') out.apply = true;
    else if (a === '--selftest') out.selftest = true;
    else throw new Abort(`unknown argument ${a} (--home <dir>, --apply, --selftest)`);
  }
  return out;
}

/** The pid in server.json when that process is alive: a NetPI runs on this home. */
function runningPid(home) {
  let server;
  try { server = JSON.parse(fs.readFileSync(path.join(home, 'server.json'), 'utf8')); } catch { return null; }
  const pid = Number(server?.pid);
  if (!Number.isInteger(pid) || pid <= 0) return null;
  try { process.kill(pid, 0); return pid; } catch (e) { return e.code === 'EPERM' ? pid : null; }
}

/** What the store holds of the removed collections, and the document count of every other collection (for the check). */
function survey(db) {
  const rows = db.prepare('SELECT id, plugin, name FROM _collections ORDER BY id').all();
  const count = (id) => Number(db.prepare(`SELECT COUNT(*) AS n FROM c${Number(id)}`).get().n);
  const removed = [], kept = new Map();
  for (const r of rows) {
    const hit = REMOVED.some((x) => x.plugin === r.plugin && x.name === r.name);
    if (hit) removed.push({ id: Number(r.id), plugin: r.plugin, name: r.name, docs: count(r.id) });
    else kept.set(`${r.plugin}/${r.name}`, count(r.id));
  }
  return { removed, kept };
}

/** The settings paths to remove: mcp.servers.<id>.synonyms. */
function settingsPaths(settings) {
  const servers = settings?.mcp?.servers;
  if (!servers || typeof servers !== 'object') return [];
  return Object.keys(servers).filter((id) => servers[id] && typeof servers[id] === 'object' && 'synonyms' in servers[id]);
}

export function run(home, apply, log = console.log) {
  home = path.resolve(home);
  const dbFile = path.join(home, 'netpi.db');
  const settingsFile = path.join(home, 'settings.json');
  if (!fs.existsSync(dbFile)) throw new Abort(`no database at ${dbFile} (only the sqlite storage provider is handled)`);
  if (apply) {
    const pid = runningPid(home);
    if (pid) throw new Abort(`NetPI is running on ${home} (pid ${pid}): close it, run this again, then start it.`);
  }

  log(`cleanup 002 (data of the features removed on 2026-10-11) in ${home}${apply ? '' : ' — dry run'}`);
  let db = new DatabaseSync(dbFile, { readOnly: !apply });
  let before;
  try { before = survey(db); } finally { db.close(); }
  const settingsText = fs.existsSync(settingsFile) ? fs.readFileSync(settingsFile, 'utf8') : null;
  const settings = settingsText ? JSON.parse(settingsText) : null;
  const servers = settingsPaths(settings);

  for (const c of before.removed) log(`  collection ${c.plugin}/${c.name} (table c${c.id}): ${c.docs} document${c.docs === 1 ? '' : 's'}`);
  for (const id of servers) log(`  setting mcp.servers.${id}.synonyms`);
  if (before.removed.length + servers.length === 0) { log('nothing to remove.'); return 0; }
  if (!apply) { log('dry run: nothing changed. Close NetPI and run with --apply to remove it.'); return 0; }

  // A consistent copy first: the WAL folded into the file, then the file copied.
  db = new DatabaseSync(dbFile);
  try { db.exec('PRAGMA wal_checkpoint(TRUNCATE)'); } finally { db.close(); }
  fs.copyFileSync(dbFile, dbFile + '.pre-002');
  log(`  copied ${path.basename(dbFile)} to ${path.basename(dbFile)}.pre-002`);

  db = new DatabaseSync(dbFile);
  try {
    db.exec('BEGIN IMMEDIATE');
    try {
      for (const c of before.removed) {
        db.prepare('DELETE FROM _collections WHERE id = ?').run(c.id);
        db.exec(`DROP TABLE IF EXISTS c${c.id}`);
      }
      db.exec('COMMIT');
    } catch (e) { db.exec('ROLLBACK'); throw e; }
  } finally { db.close(); }

  if (servers.length) {
    fs.copyFileSync(settingsFile, settingsFile + '.pre-002');
    for (const id of servers) delete settings.mcp.servers[id].synonyms;
    const nl = settingsText.includes('\r\n') ? '\r\n' : '\n';
    fs.writeFileSync(settingsFile, JSON.stringify(settings, null, 2).replace(/\n/g, nl) + nl);
    log(`  copied settings.json to settings.json.pre-002 and removed ${servers.length} synonyms list${servers.length === 1 ? '' : 's'}`);
  }

  // The check: the listed collections are gone, every other one is there with the documents it had, the file is sound.
  db = new DatabaseSync(dbFile, { readOnly: true });
  const problems = [];
  try {
    const after = survey(db);
    if (after.removed.length) problems.push(`still there: ${after.removed.map((c) => `${c.plugin}/${c.name}`).join(', ')}`);
    for (const [key, n] of before.kept) if (after.kept.get(key) !== n) problems.push(`${key}: ${n} documents before, ${after.kept.get(key) ?? 'gone'} after`);
    for (const key of after.kept.keys()) if (!before.kept.has(key)) problems.push(`${key} appeared`);
    const tables = new Set(db.prepare("SELECT name FROM sqlite_master WHERE type = 'table'").all().map((r) => r.name));
    for (const c of before.removed) if (tables.has(`c${c.id}`)) problems.push(`table c${c.id} still exists`);
    const check = db.prepare('PRAGMA quick_check').all().map((r) => Object.values(r)[0]);
    if (check.join() !== 'ok') problems.push(`quick_check: ${check.join('; ')}`);
  } finally { db.close(); }
  if (settings && settingsPaths(JSON.parse(fs.readFileSync(settingsFile, 'utf8'))).length) problems.push('settings.json still has synonyms');
  if (problems.length) {
    log(`the check FAILED:\n  ${problems.join('\n  ')}\nRoll back: copy netpi.db.pre-002 over netpi.db (and settings.json.pre-002 over settings.json).`);
    return 1;
  }
  log(`done: ${before.removed.length} collection${before.removed.length === 1 ? '' : 's'} removed, ${before.kept.size} others unchanged, the database checks ok.`);
  log('Start NetPI; once it runs fine, netpi.db.pre-002 and settings.json.pre-002 can be deleted.');
  return 0;
}

/** A scratch home with an old-shaped store and settings: dry run, apply, apply again, and the running-app refusal. */
function selftest() {
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'netpi-002-'));
  const lines = [];
  const quiet = (s) => lines.push(s);
  const fail = (m) => { console.error(`selftest FAILED: ${m}\n${lines.join('\n')}`); return 1; };
  try {
    const db = new DatabaseSync(path.join(home, 'netpi.db'));
    db.exec('PRAGMA journal_mode=WAL');
    db.exec('CREATE TABLE _collections (id INTEGER PRIMARY KEY AUTOINCREMENT, plugin TEXT NOT NULL, name TEXT NOT NULL, spec TEXT NOT NULL, UNIQUE(plugin, name))');
    const add = (plugin, name, docs) => {
      const id = Number(db.prepare('INSERT INTO _collections(plugin, name, spec) VALUES(?, ?, ?)').run(plugin, name, '{}').lastInsertRowid);
      db.exec(`CREATE TABLE c${id} (k TEXT PRIMARY KEY, doc TEXT NOT NULL)`);
      for (let i = 0; i < docs; i++) db.prepare(`INSERT INTO c${id}(k, doc) VALUES(?, ?)`).run(`k${i}`, '{}');
      return id;
    };
    add('netpi.ideas', 'items', 3);
    add('netpi.ideas', 'cards', 2);
    add('netpi.ideas', 'decisions', 5);
    add('netpi.ideas', 'vectors', 3);
    add('netpi.coordinator', 'tasks', 0);
    add('netpi.agents', 'counters', 1);
    add('netpi.other', 'cards', 4); // another plugin's collection of the same name stays
    db.close();
    const settings = { defaultModel: 'x', mcp: { servers: { ha: { url: 'http://h', synonyms: ['home'] }, plain: { url: 'http://p' } } } };
    fs.writeFileSync(path.join(home, 'settings.json'), JSON.stringify(settings, null, 2).replace(/\n/g, '\r\n') + '\r\n');

    if (run(home, false, quiet) !== 0) return fail('the dry run did not succeed');
    if (fs.existsSync(path.join(home, 'netpi.db.pre-002'))) return fail('the dry run wrote a backup');
    const names = () => { const d = new DatabaseSync(path.join(home, 'netpi.db'), { readOnly: true }); try { return d.prepare('SELECT plugin, name FROM _collections ORDER BY id').all().map((r) => `${r.plugin}/${r.name}`).join(','); } finally { d.close(); } };
    if (names() !== 'netpi.ideas/items,netpi.ideas/cards,netpi.ideas/decisions,netpi.ideas/vectors,netpi.coordinator/tasks,netpi.agents/counters,netpi.other/cards') return fail('the dry run changed the store: ' + names());

    fs.writeFileSync(path.join(home, 'server.json'), JSON.stringify({ pid: process.pid }));
    try { run(home, true, quiet); return fail('--apply ran while a NetPI (this process) was running on the home'); } catch (e) { if (!(e instanceof Abort)) throw e; }
    fs.rmSync(path.join(home, 'server.json'));

    if (run(home, true, quiet) !== 0) return fail('--apply did not pass its own check');
    if (names() !== 'netpi.ideas/items,netpi.ideas/vectors,netpi.agents/counters,netpi.other/cards') return fail('wrong collections left: ' + names());
    for (const f of ['netpi.db.pre-002', 'settings.json.pre-002']) if (!fs.existsSync(path.join(home, f))) return fail(`no ${f}`);
    const raw = fs.readFileSync(path.join(home, 'settings.json'), 'utf8');
    const s = JSON.parse(raw);
    if ('synonyms' in s.mcp.servers.ha || s.mcp.servers.ha.url !== 'http://h' || s.defaultModel !== 'x' || !raw.includes('\r\n')) return fail('settings.json: ' + raw);
    if (run(home, true, quiet) !== 0 || !lines.at(-1).includes('nothing to remove')) return fail('a second --apply was not a no-op');
    console.log('selftest passed');
    return 0;
  } finally {
    fs.rmSync(home, { recursive: true, force: true });
  }
}

const isMain = import.meta.url === new URL(`file:///${path.resolve(process.argv[1] ?? '').replace(/\\/g, '/').replace(/^\//, '')}`).href;
if (isMain) {
  try {
    const a = args(process.argv.slice(2));
    process.exitCode = a.selftest ? selftest() : run(a.home, a.apply);
  } catch (e) {
    console.error(e instanceof Abort ? `aborted: ${e.message}` : `cleanup failed: ${e.stack ?? e}`);
    process.exitCode = 2;
  }
}
