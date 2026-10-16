#!/usr/bin/env node
// One-off: moves the provider settings of the old layout (providers.aiproxy / anthropic / openrouter / claudeCli blocks and the
// providers.openaiCompatible / providers.claudeCliExtra arrays) into providers.instances, one entry per provider, keeping the
// provider ids (so every model reference keeps working). Empty blocks are skipped: they configured nothing.
//
//   node scripts/migrate-providers.mjs [--home <netpi home>] [--remove-old] [--dry-run]
//
// Without --remove-old the old keys stay (a NetPI that has not restarted yet still reads them); run it again with --remove-old
// after the restart. Safe to run twice: an id already in providers.instances is left alone.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const args = process.argv.slice(2);
const flag = (n) => args.includes(n);
const home = args.includes('--home') ? args[args.indexOf('--home') + 1] : path.join(os.homedir(), '.netpi');
const file = path.join(home, 'settings.json');
const doc = JSON.parse(fs.readFileSync(file, 'utf8'));
const providers = (doc.providers ??= {});
const instances = (providers.instances ??= {});
const added = [];
const skipped = [];

// The host's id rule (ProviderInstances.ValidId): a provider id is the part before the slash of a model reference, and a
// settings path segment - Unicode letters and digits, plus '-' and '_'. The old ASCII-only regex would have skipped a
// perfectly good id that carries an accented letter.
const validId = (id) => typeof id === 'string' && /^[\p{L}\p{N}_-]+$/u.test(id);

function add(id, type, config) {
  if (!validId(id)) {
    skipped.push(id);
    return console.warn(`skipped entry with an unusable id: ${JSON.stringify(id)}`);
  }
  if (instances[id]) return;   // already in providers.instances: the old block is redundant, not a skip
  const { id: _id, ...rest } = config;
  instances[id] = { type, ...rest };
  added.push(`${id} (${type})`);
}
const filled = (o) => o && typeof o === 'object' && !Array.isArray(o) && Object.keys(o).length > 0;

if (filled(providers.aiproxy)) add('aiproxy', 'aiproxy', providers.aiproxy);
if (filled(providers.anthropic)) add('anthropic', 'anthropic', providers.anthropic);
if (filled(providers.openrouter)) add('openrouter', 'openrouter', providers.openrouter);
// the Claude CLI was off unless it said enabled: true; a provider entry is on unless it says enabled: false
if (filled(providers.claudeCli)) add('claude-cli', 'claude-cli', providers.claudeCli.enabled === true ? providers.claudeCli : { ...providers.claudeCli, enabled: false });
for (const e of providers.claudeCliExtra ?? []) add(e?.id, 'claude-cli', e ?? {});
for (const e of providers.openaiCompatible ?? []) add(e?.id, 'openai-compatible', e ?? {});

// --remove-old deletes the old blocks, but only when nothing was skipped: a skipped entry (an unusable id) was not
// migrated, so deleting its old block would lose it. The old blocks are kept and the user is told to fix the id.
if (flag('--remove-old')) {
  if (skipped.length) {
    console.warn(`refusing --remove-old: ${skipped.length} entr(y/ies) with unusable id(s) were not migrated: ${skipped.join(', ')}`);
    console.warn('the old blocks are kept; fix the id(s) in the old layout and re-run, or remove the old blocks by hand.');
  } else {
    for (const k of ['aiproxy', 'anthropic', 'openrouter', 'claudeCli', 'claudeCliExtra', 'openaiCompatible']) delete providers[k];
  }
}

console.log(added.length ? `added to providers.instances: ${added.join(', ')}` : 'nothing to add');
if (flag('--dry-run')) process.exit(0);
fs.writeFileSync(file, JSON.stringify(doc, null, 2) + '\n');
console.log(`written ${file}`);
