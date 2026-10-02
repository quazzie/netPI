#!/usr/bin/env node
// The mechanical check that the core stays small (AGENTS.md: "Keep the core small; new behaviour goes into a plugin").
// Two rules, both about src/NetPI.Host and src/NetPI.Abstractions:
//   1. the host kernel references only the contracts — one <ProjectReference>, to NetPI.Abstractions;
//   2. neither project names a type that belongs to a plugin. A name that must not come back is listed in FORBIDDEN
//      with why, so adding one is a one-line edit.
// Comments and string literals are stripped first: a word in a comment or in a message is not a dependency.
//
//   node scripts/core-size.mjs          the check
//   node scripts/core-size.mjs --list   the forbidden identifiers and why
//
// Exit codes: 0 clean, 1 violations (one per line, file:line: identifier — reason), 2 a usage or I/O error.
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const KERNEL = 'src/NetPI.Host';
const CONTRACTS = 'src/NetPI.Abstractions';

/** The only project the kernel may reference: it holds contracts, nothing that loads. */
const ALLOWED_REFERENCE = 'NetPI.Abstractions';

/**
 * Names that do not belong in the core, in the order they were pushed out. Adding one here is how it stays out: the
 * type lives in a plugin, the plugin talks to the kernel through services, RPC and events.
 */
const FORBIDDEN = [
  // Agents, slots and budgets: the agents and guardrails plugins own them; the kernel only runs tools.
  { id: 'IBudgetGate', why: 'budget gating is guardrails-plugin policy, not kernel policy' },
  { id: 'BudgetExceededException', why: 'raised by the guardrails plugin when a budget runs out' },
  { id: 'AgentUnavailableException', why: 'raised by the agents plugin when no agent slot is free' },
  { id: 'IAgentScheduler', why: 'who runs next is the agents plugin' },
  { id: 'IAgentSlot', why: 'a slot is an agents-plugin concept' },
  { id: 'AgentSlotRequest', why: 'a slot request is an agents-plugin concept' },
  { id: 'AgentSlots', why: 'the slot table is the agents plugin' },
  { id: 'SlotHolder', why: 'holding a slot is the agents plugin' },
  { id: 'SessionAgent', why: 'the agent bound to a session is the agents plugin' },
  // Per-session model and profile choice: model selection is a plugin.
  { id: 'SessionModel', why: 'choosing the model for a session is a plugin' },
  { id: 'SessionProfile', why: 'profiles are the profiles plugin' },
  // Workspaces: plugins/NetPI.Workspaces owns them and registers the one resolver the kernel may ask.
  { id: 'WorkspaceInfo', why: 'NetPI.Workspaces owns the workspace of a session' },
  { id: 'WorkspaceBinding', why: 'the workspace a session is bound to belongs to NetPI.Workspaces' },
  { id: 'WorkspacePaths', why: 'workspace paths are NetPI.Workspaces' },
  { id: 'WorkspaceRequest', why: 'a workspace request is NetPI.Workspaces' },
  { id: 'WorkspaceOutcome', why: 'a workspace outcome is NetPI.Workspaces' },
  { id: 'WorkspaceUnavailableException', why: 'raised by NetPI.Workspaces, which owns workspaces' },
  { id: 'WorkspacePathVerdict', why: 'deciding whether a path may be written belongs to the workspace guard' },
  { id: 'SessionWorkspace', why: 'the workspace of a session is NetPI.Workspaces' },
  { id: 'IWorkspaceStore', why: 'workspaces are stored by NetPI.Workspaces' },
  { id: 'IWorkspaceResolver', why: 'the workspace resolver is NetPI.Workspaces, not a core contract' },
  { id: 'IWorkspaceRepoProbe', why: 'probing a repository is NetPI.Workspaces' },
  { id: 'IWorkspaceProvisioner', why: 'provisioning a workspace is NetPI.Workspaces' },
  { id: 'IWorkspaceProcesses', why: 'workspace processes are NetPI.Workspaces' },
  // Resource leases: who may use a key, a port or a slot concurrently is a plugin's business.
  { id: 'IResourceLeases', why: 'resource leases are a plugin concern, not core concurrency' },
  { id: 'IResourceLease', why: 'a resource lease is a plugin concern, not core concurrency' },
  { id: 'ResourceLeaseSlot', why: 'a leased slot is a plugin concern' },
  { id: 'ResourceLeaseUpdate', why: 'a lease update is a plugin concern' },
  { id: 'ResourceLeaseInfo', why: 'lease state is a plugin concern' },
  { id: 'ModelResourceSlots', why: 'per-model slots are the agents plugin' },
  // Decisions: the decide plugin owns the model call that chooses a tool.
  { id: 'IDecisionService', why: 'the decision service is the decide plugin' },
  { id: 'DecisionRequest', why: 'a decision request is the decide plugin' },
  { id: 'DecisionHints', why: 'decision hints are the decide plugin' },
  { id: 'DecisionCapabilities', why: 'decision capabilities are the decide plugin' },
  { id: 'DecisionConfidence', why: 'decision confidence is the decide plugin' },
  { id: 'IGitHistory', why: 'reading git history is the decide plugin' },
  // Deferred and indirect tools: routing a tool call through another agent is a plugin feature.
  { id: 'ToolSelection', why: 'tool selection is a plugin decision' },
  { id: 'IDeferredToolInfrastructure', why: 'deferred tools are a plugin feature' },
  { id: 'IIndirectAgentTool', why: 'an indirect agent tool is a plugin feature' },
  { id: 'ResolvedToolCall', why: 'a resolved tool call belongs to the plugin that defers it' },
  // Background work: running something after the turn is a plugin feature.
  { id: 'IBackgroundWork', why: 'background work is a plugin feature' },
  { id: 'BackgroundWorkInfo', why: 'background work is a plugin feature' },
  // Instruction files: NetPI.AgentsMd reads and tracks them.
  { id: 'GlobalAgentsMd', why: 'instruction files are read by NetPI.AgentsMd, not the kernel' },
  // Raw database access: the storage provider owns its files; the core never holds a connection.
  { id: 'IDatabase', why: 'raw SQL access belongs to the storage provider plugin, not the kernel' },
  { id: 'IDbRow', why: 'raw SQL access belongs to the storage provider plugin, not the kernel' },
];

const reasons = new Map(FORBIDDEN.map((f) => [f.id, f.why]));
// Longest first, so IResourceLeases is never tried as IResourceLease; the word boundaries do the rest.
const forbidden = new RegExp(`\\b(?:${[...FORBIDDEN].sort((a, b) => b.id.length - a.id.length).map((f) => f.id).join('|')})\\b`, 'g');

const options = process.argv.slice(2);
const list = options.includes('--list');
for (const option of options) {
  if (option !== '--list') {
    usage(2, `Unknown option: ${option}`);
  }
}

if (list) {
  for (const { id, why } of FORBIDDEN) console.log(`${id.padEnd(28)} ${why}`);
  process.exit(0);
}

/**
 * Drop comments and literals — plain, verbatim, raw, interpolated (the code in an interpolation hole is kept, because
 * it is code) and character literals — leaving every newline behind, so a line number still means what it said.
 * Tokenizer-free on purpose: this is a lint, so it only has to keep a name in code and drop a name that is not.
 */
function strip(code) {
  let out = '';
  let i = 0;
  scanCode(false);

  /** Code from i on, ending at the } that closes the hole of an interpolated string when in one (and: stop). */
  function scanCode(stop) {
    let braces = 0;
    while (i < code.length && !(stop && braces === 0 && code[i] === '}')) {
      const c = code[i];
      if (c === '{' || c === '}') braces += c === '{' ? 1 : -1;
      else if (c === '/') {
        if (code[i + 1] === '/') { while (i < code.length && code[i] !== '\n') i++; continue; }
        if (code[i + 1] === '*') {
          for (i += 2; i < code.length && !(code[i] === '*' && code[i + 1] === '/'); i++) if (code[i] === '\n') out += '\n';
          i += 2;
          continue;
        }
      }
      const end = literalEnd();
      if (end) i = end;
      else out += code[i++];
    }
  }

  /** Where the literal starting at i ends, its text dropped and its newlines kept; 0 when none starts here. */
  function literalEnd() {
    const raw = /\$*"""/y; // sticky, so a raw string only counts when it starts exactly here
    raw.lastIndex = i;
    const opening = raw.exec(code);
    if (opening) {
      const close = code.indexOf('"""', i + opening[0].length);
      const end = close < 0 ? code.length : close + 3;
      for (; i < end; i++) if (code[i] === '\n') out += '\n';
      return end;
    }
    const verbatim = code[i] === '@';
    const interpolated = code[i + (verbatim ? 1 : 0)] === '$';
    const quote = code[i + (verbatim ? 1 : 0) + (interpolated ? 1 : 0)];
    if (quote !== '"' && quote !== "'") return 0;
    i += (verbatim ? 1 : 0) + (interpolated ? 1 : 0);
    for (i++; i < code.length; i++) {
      const c = code[i];
      if (!verbatim && c === '\\') i++; // an escape, an escaped quote among them
      else if (c === quote && (verbatim ? code[i + 1] === quote : true)) {
        if (verbatim) i++; // a doubled quote is an escaped quote, not the end
        else if (interpolated && code[i + 1] === '}') i++; // the end of a hole: the } is the hole's, the string goes on
        else return i + 1;
      } else if (c === '{' && interpolated) {
        i++;
        if (code[i] !== '{') scanCode(true); // {{ is an escaped brace, not a hole
      } else if (c === '\n') {
        out += '\n';
        if (!verbatim) return i + 1; // only a raw or a verbatim literal may span lines
      }
    }
    return i;
  }

  return out;
}

/** Every .cs under dir, skipping bin/ and obj/, in a stable order. */
async function sources(dir) {
  const found = [];
  for (const entry of (await fs.readdir(dir, { withFileTypes: true })).sort((a, b) => a.name.localeCompare(b.name))) {
    if (entry.name === 'bin' || entry.name === 'obj') continue;
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) found.push(...(await sources(full)));
    else if (entry.name.endsWith('.cs')) found.push(full);
  }
  return found;
}

const violations = [];

/** Rule 1: the kernel references the contracts and nothing else. */
async function references() {
  const file = path.join(ROOT, KERNEL, 'NetPI.Host.csproj');
  const text = await fs.readFile(file, 'utf8');
  for (const match of text.matchAll(/<ProjectReference\b[^>]*?\bInclude\s*=\s*(?:"([^"]*)"|'([^']*)')/g)) {
    const include = match[1] ?? match[2];
    const name = path.basename(include, path.extname(include));
    if (name !== ALLOWED_REFERENCE) {
      const line = text.slice(0, match.index).split('\n').length;
      violations.push({
        where: `${KERNEL}/NetPI.Host.csproj:${line}`,
        what: `ProjectReference ${include}`,
        why: `the host kernel may reference only ${ALLOWED_REFERENCE}`,
      });
    }
  }
}

/** Rule 2: neither project names a type that belongs to a plugin. */
async function names() {
  const files = [...(await sources(path.join(ROOT, KERNEL))), ...(await sources(path.join(ROOT, CONTRACTS)))];
  for (const file of files) {
    const relative = path.relative(ROOT, file).split(path.sep).join('/');
    const lines = strip(await fs.readFile(file, 'utf8')).split('\n');
    lines.forEach((line, i) => {
      for (const match of line.matchAll(forbidden)) {
        violations.push({ where: `${relative}:${i + 1}`, what: match[0], why: reasons.get(match[0]) });
      }
    });
  }
}

try {
  await references();
  await names();
} catch (e) {
  console.error(`core-size: ${e.code === 'ENOENT' ? `${e.path ?? 'a source file'} is missing` : e.message}`);
  process.exit(2);
}

if (violations.length === 0) {
  console.log('Core is small: the kernel references only the contracts and names no plugin-owned type.');
  process.exit(0);
}

const files = new Set(violations.map((v) => v.where.split(':').slice(0, -1).join(':')));
for (const { where, what, why } of violations) console.log(`${where}: ${what} — ${why}`);
console.log(`\n${violations.length} violation(s) in ${files.size} file(s). Move the type to the plugin that owns it.`);
process.exit(1);

function usage(code, message) {
  if (message) console.error(`core-size: ${message}`);
  console.error('Usage: node scripts/core-size.mjs [--list]');
  process.exit(code);
}