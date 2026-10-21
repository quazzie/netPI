#!/usr/bin/env node
// One-off: moves the Context plugin's prompt history (collections context_sent and context_prompts of plugin netpi.context)
// into the session meta, which becomes the only record of the prompts a session was sent:
//   - meta.sentPromptHistory gets, per entry, `tools` ([{ name, def, revision? }]) and `createdAt`; a session without a history
//     gets one built from its context_sent rows (its own and the inherited ones of a fork);
//   - meta.runtimePrompt / runtimePromptRevision are set from context_prompts when the session has none for its promptRevision;
//   - each tool definition goes once into the content-addressed collection context_tooldefs (key = def hash,
//     doc { name, description, parameters }); def = first 16 hex chars of SHA-256 of name + "\n" + description + "\n" + the
//     parameters as System.Text.Json writes them (compact, default encoder: non-ASCII and < > & ' + ` as \uXXXX);
//   - then every row of context_sent and context_prompts is deleted.
//
//   node scripts/migrate-context-prompts.mjs [--home <netpi home>] [--dry-run]
//
// Run it with NetPI closed: it refuses while <home>/server.json names a live process or when netpi.db / netpi.lock cannot be
// had exclusively. --dry-run does everything inside a transaction and rolls it back. Safe to run twice (the second run finds no
// rows). Needs node >= 22 (node:sqlite). Back up netpi.db first if the data matters.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { DatabaseSync } from 'node:sqlite';

const PLUGIN = 'netpi.context';
const args = process.argv.slice(2);
const dryRun = args.includes('--dry-run');
const home = path.resolve(args.includes('--home') ? args[args.indexOf('--home') + 1] : (process.env.NETPI_HOME || path.join(os.homedir(), '.netpi')));
const dbFile = path.join(home, 'netpi.db');
if (!fs.existsSync(dbFile)) { console.error(`no netpi.db in ${home}`); process.exit(1); }

// --- refuse to run against a live NetPI -------------------------------------------------------------------------------
try {
  const server = JSON.parse(fs.readFileSync(path.join(home, 'server.json'), 'utf8'));
  let alive = false;
  try { process.kill(Number(server.pid), 0); alive = true; } catch (e) { alive = e.code === 'EPERM'; }
  if (alive) { console.error(`NetPI is running on this home (pid ${server.pid}, ${server.url}). Close it first.`); process.exit(1); }
} catch { /* no server.json (or unreadable): not running */ }
try {
  // HomeLock holds netpi.lock without sharing: on Windows opening it fails while NetPI runs.
  const lock = path.join(home, 'netpi.lock');
  if (fs.existsSync(lock)) fs.closeSync(fs.openSync(lock, 'r+'));
} catch (e) { console.error(`netpi.lock is held (${e.code}): NetPI is running on this home. Close it first.`); process.exit(1); }

const db = new DatabaseSync(dbFile);
try { db.exec('PRAGMA busy_timeout = 1000; BEGIN IMMEDIATE'); }
catch (e) { console.error(`cannot get netpi.db exclusively: ${e.message}`); process.exit(1); }

// --- helpers ----------------------------------------------------------------------------------------------------------
/** JSON as System.Text.Json's default options write it: only what the default encoder escapes differs from JSON.stringify. */
function stjson(value) {
  const s = JSON.stringify(value);
  let out = '';
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (c === '\\') {
      const n = s[++i];
      out += n === '"' ? '\\u0022' : '\\' + n;   // the other short escapes (\\ \n \r \t \b \f \uXXXX) are the same
      if (n === 'u') { out += s.slice(i + 1, i + 5).toUpperCase(); i += 4; }
    } else {
      const code = c.charCodeAt(0);
      out += code > 0x7e || '&\'+<>`'.includes(c) ? '\\u' + code.toString(16).toUpperCase().padStart(4, '0') : c;
    }
  }
  return out;
}
const defHash = (t) => createHash('sha256').update(`${t.name}\n${t.description ?? ''}\n${stjson(t.parameters ?? {})}`, 'utf8').digest('hex').slice(0, 16);
const parse = (text) => { try { return JSON.parse(text); } catch { return null; } };
const isObj = (v) => v && typeof v === 'object' && !Array.isArray(v);

/** The table of a collection of the Context plugin (created with its catalog row when `create`), or null. */
function table(name, create = false) {
  const row = db.prepare('SELECT id FROM _collections WHERE plugin = ? AND name = ?').get(PLUGIN, name);
  if (row) return 'c' + row.id;
  if (!create) return null;
  const id = db.prepare('INSERT INTO _collections(plugin, name, spec) VALUES(?, ?, ?)').run(PLUGIN, name, '{}').lastInsertRowid;
  db.exec(`CREATE TABLE c${id} (k TEXT PRIMARY KEY, doc TEXT NOT NULL)`);
  return 'c' + id;
}

const sentTable = table('context_sent');
const promptTable = table('context_prompts');
const sent = new Map();      // sessionId -> rows ordered by version
for (const r of sentTable ? db.prepare(`SELECT k, doc FROM ${sentTable}`).all() : []) {
  const d = parse(r.doc);
  if (!isObj(d)) { console.warn(`skipping unreadable context_sent row ${r.k}`); continue; }
  const sessionId = d.sessionId ?? r.k.slice(0, r.k.lastIndexOf(':'));
  if (!sent.has(sessionId)) sent.set(sessionId, []);
  sent.get(sessionId).push({ ...d, version: Number(d.version ?? r.k.slice(r.k.lastIndexOf(':') + 1)) });
}
for (const rows of sent.values()) rows.sort((a, b) => a.version - b.version);
const frozen = new Map();    // sessionId -> { prompt, promptRevision }
for (const r of promptTable ? db.prepare(`SELECT k, doc FROM ${promptTable}`).all() : []) {
  const d = parse(r.doc);
  if (isObj(d) && typeof d.prompt === 'string') frozen.set(r.k, d);
}

let defTable = null;
const defsSeen = new Set();
const defsWritten = new Set();
function toTools(row) {
  const tools = [];
  for (const t of Array.isArray(row.tools) ? row.tools : []) {
    if (!isObj(t) || typeof t.name !== 'string') continue;
    const def = defHash(t);
    if (!defsSeen.has(def)) {
      defsSeen.add(def);
      defTable ??= table('context_tooldefs', true);
      const doc = JSON.stringify({ name: t.name, description: t.description ?? '', parameters: t.parameters ?? {} });
      if (db.prepare(`INSERT INTO ${defTable}(k, doc) VALUES(?, ?) ON CONFLICT(k) DO NOTHING`).run(def, doc).changes > 0) defsWritten.add(def);
    }
    tools.push(typeof t.revision === 'string' && t.revision ? { name: t.name, def, revision: t.revision } : { name: t.name, def });
  }
  return tools;
}

// --- migrate ----------------------------------------------------------------------------------------------------------
const stats = { sessions: 0, entriesFilled: 0, entriesBuilt: 0, fallbacks: 0, unmatched: 0, missingSessions: 0 };
const ids = new Set([...sent.keys(), ...frozen.keys()]);
const getSession = db.prepare('SELECT meta FROM sessions WHERE id = ?');
const putMeta = db.prepare('UPDATE sessions SET meta = ? WHERE id = ?');
for (const id of ids) {
  const s = getSession.get(id);
  if (!s) { stats.missingSessions++; continue; }
  const meta = isObj(parse(s.meta)) ? parse(s.meta) : {};
  const rows = sent.get(id) ?? [];
  let changed = false;

  if (rows.length) {
    if (Array.isArray(meta.sentPromptHistory) && meta.sentPromptHistory.length) {
      // 1. fill tools + createdAt of the existing entries from the row with the same prompt
      for (const entry of meta.sentPromptHistory) {
        if (!isObj(entry) || (Array.isArray(entry.tools) && entry.createdAt)) continue;
        const same = rows.filter((r) => r.prompt === entry.prompt);
        const before = same.filter((r) => Number(r.afterSeq ?? 0) <= Number(entry.afterSeq ?? 0));
        const row = before.length ? before.reduce((a, b) => (Number(b.afterSeq ?? 0) >= Number(a.afterSeq ?? 0) ? b : a)) : same[0];
        if (!row) { stats.unmatched++; continue; }
        if (!Array.isArray(entry.tools)) entry.tools = toTools(row);
        if (!entry.createdAt && row.createdAt) entry.createdAt = row.createdAt;
        stats.entriesFilled++; changed = true;
      }
    } else {
      // 2. no history in meta: it is the rows (a fork's inherited rows are its fork-point history)
      const revision = Number(meta.promptRevision ?? 0);
      meta.sentPromptHistory = rows.map((r) => ({ afterSeq: Number(r.afterSeq ?? 0), revision, prompt: r.prompt ?? '', createdAt: r.createdAt, tools: toTools(r) }));
      stats.entriesBuilt += rows.length; changed = true;
    }
  }

  // 3. the frozen prompt of the current revision
  const revision = Number(meta.promptRevision ?? 0);
  const f = frozen.get(id);
  const hasFallback = meta.runtimePromptRevision !== undefined && Number(meta.runtimePromptRevision) === revision && typeof meta.runtimePrompt === 'string';
  if (f && !hasFallback && Number(f.promptRevision ?? 0) === revision) {
    meta.runtimePrompt = f.prompt;
    meta.runtimePromptRevision = revision;
    stats.fallbacks++; changed = true;
  }

  if (changed) { putMeta.run(JSON.stringify(meta), id); stats.sessions++; }
}

let deleted = 0;
for (const t of [sentTable, promptTable]) if (t) deleted += Number(db.prepare(`DELETE FROM ${t}`).run().changes);

if (dryRun) db.exec('ROLLBACK'); else db.exec('COMMIT');
db.close();
console.log(`${dryRun ? '[dry run, nothing written] ' : ''}sessions touched: ${stats.sessions}; history entries filled: ${stats.entriesFilled}; built: ${stats.entriesBuilt}; ` +
  `runtime prompts restored: ${stats.fallbacks}; tool definitions written: ${defsWritten.size}; rows deleted: ${deleted}` +
  (stats.unmatched ? `; entries without a matching row: ${stats.unmatched}` : '') +
  (stats.missingSessions ? `; sessions already gone: ${stats.missingSessions}` : ''));
