#!/usr/bin/env node
// Runs every section of the UI walkthrough (web/mock/e2e.mjs) on its own, several at a time, each with a mock server of its own
// on a free port, and says which ones cannot stand alone. A section that builds on state another one leaves behind must say so
// in NEEDS (e2e.mjs) or set that state up itself; this is the check that keeps that true.
//
//   node web/mock/sections.mjs [--parallel 4] [--only "name|name"] [--out dir]
//
// Exit 0 when every (selected) section passes alone. Each run is `e2e.mjs --only <section> --no-dev`, so its prerequisites run
// first, unreported. ~70 s for all sections at --parallel 6 on a 16-core machine (the whole walkthrough takes ~140 s in one go).
import { spawn, execFileSync } from 'node:child_process';
import fs from 'node:fs';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const argv = process.argv.slice(2);
const argVal = (n) => (argv.indexOf(n) >= 0 ? argv[argv.indexOf(n) + 1] : null);
const parallel = Math.max(1, Number(argVal('--parallel') ?? 4));
const outRoot = path.resolve(argVal('--out') ?? path.join(os.tmpdir(), `netpi-ui-sections-${Date.now()}`));
const only = argVal('--only') ? argVal('--only').split('|').map((s) => s.trim()).filter(Boolean) : null;

const all = execFileSync(process.execPath, [path.join(here, 'e2e.mjs'), '--list'], { encoding: 'utf8' }).split('\n').map((s) => s.trim()).filter(Boolean);
const sections = only ? all.filter((s) => only.some((o) => s === o || s.toLowerCase().includes(o.toLowerCase()))) : all;
if (!sections.length) {
  console.error(`no section matches ${only?.join(' | ')}. Sections:\n  ${all.join('\n  ')}`);
  process.exit(2);
}
fs.mkdirSync(outRoot, { recursive: true });

// free ports, taken together so that two workers never get the same one
async function freePorts(n) {
  const servers = await Promise.all(Array.from({ length: n }, () => new Promise((res) => { const s = net.createServer(); s.listen(0, '127.0.0.1', () => res(s)); })));
  const ports = servers.map((s) => s.address().port);
  await Promise.all(servers.map((s) => new Promise((r) => s.close(r))));
  return ports;
}
const ports = await freePorts(parallel);

const results = [];
let next = 0;
async function worker(w) {
  while (next < sections.length) {
    const i = next++;
    const name = sections[i];
    const t0 = Date.now();
    const out = await new Promise((resolve) => {
      const p = spawn(process.execPath, [path.join(here, 'e2e.mjs'), '--only', name, '--port', String(ports[w]), '--no-dev', '--out', path.join(outRoot, String(i))]);
      let text = '';
      p.stdout.on('data', (d) => (text += d));
      p.stderr.on('data', (d) => (text += d));
      const timer = setTimeout(() => p.kill(), 180_000);
      p.on('exit', (code) => { clearTimeout(timer); resolve({ code, text }); });
    });
    const fails = out.text.split('\n').filter((l) => l.includes('✗') || /Error|Timeout/.test(l)).slice(0, 4).map((l) => l.trim().slice(0, 170));
    const summary = (out.text.match(/(\d+)\/(\d+) checks passed/) ?? [])[0] ?? 'no summary';
    const secs = Math.round((Date.now() - t0) / 1000);
    results.push({ name, code: out.code, secs, fails });
    console.log(`${out.code === 0 ? 'ok  ' : 'FAIL'} ${String(secs).padStart(3)}s  ${name}  [${summary}]${fails.length ? '\n        ' + fails.join('\n        ') : ''}`);
  }
}
const t0 = Date.now();
await Promise.all(Array.from({ length: parallel }, (_, w) => worker(w)));
const bad = results.filter((r) => r.code !== 0);
console.log(`\n${results.length - bad.length}/${results.length} sections pass alone in ${Math.round((Date.now() - t0) / 1000)}s (${parallel} at a time); screenshots and logs: ${outRoot}`);
if (bad.length) console.log(`do not: ${bad.map((b) => b.name).join(' | ')}\nEach needs its prerequisites in NEEDS (web/mock/e2e.mjs) or must set up its own state.`);
process.exit(bad.length ? 1 : 0);
