#!/usr/bin/env node
// Builds plugin UI bundles.
//
// For every plugins/<P>/ui/main.js (or main.ts) — plus any plugin directories passed as arguments — build ONE
// ES module with Svelte (component CSS injected into the JS, everything incl. svelte and @netpi/kit bundled)
// to plugins/<P>/wwwroot/ui.js. With --copy (or NETPI_COPY=1) the bundle also goes to <app>/plugins/<P>/wwwroot/ui.js,
// so UI edits hot-reload without a .NET build. <app> is NETPI_APP_DIR, else --app-dir, else artifacts/app: point it at
// the running app's own folder (server.json's appDir) when building in a worktree.
//
//   node web/scripts/build-plugins.mjs                     all plugins/*/ui
//   node web/scripts/build-plugins.mjs web/mock/sample-plugin   … plus extra plugin dirs
//   node web/scripts/build-plugins.mjs --only <dir>…       only the given dirs
//   node web/scripts/build-plugins.mjs --watch             rebuild on change
//   node web/scripts/build-plugins.mjs --copy              also install the bundle into <app>/plugins/<P>/, so
//                                                          a running NetPI hot-reloads the UI without a .NET build
//                                                          (NETPI_COPY=1 does the same; build.ps1 -Publish sets it)
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { build } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const kit = path.join(repo, 'web/src/lib/kit/index.js');
const argv = process.argv.slice(2);
const watch = argv.includes('--watch');
const only = argv.includes('--only');
// Installing into the app folder is opt-in: a plain bundle build must not write where a running NetPI watches. The app
// folder is NETPI_APP_DIR (what build.ps1 -Publish sets, to the running app) or --app-dir, else this repo's artifacts/app.
const appDir = path.resolve(process.env.NETPI_APP_DIR || argValue('--app-dir') || path.join(repo, 'artifacts/app'));
const copy = argv.includes('--copy') || !!process.env.NETPI_COPY;
const noCopy = !copy || !!process.env.NETPI_NO_COPY;

/** The value of a `--flag value` or `--flag=value` argument. */
function argValue(flag) {
  const i = argv.indexOf(flag);
  if (i < 0) return undefined;
  const next = argv[i + 1];
  return next && !next.startsWith('--') ? next : undefined;
}
// the extra plugin dirs are the arguments that are neither a flag nor a flag's value
const extra = argv.filter((a, i) => !a.startsWith('--') && argv[i - 1] !== '--app-dir').map((d) => path.resolve(d));

function findEntry(dir) {
  for (const f of ['main.js', 'main.ts', 'main.mjs']) {
    const p = path.join(dir, 'ui', f);
    if (fs.existsSync(p)) return p;
  }
  return null;
}

const dirs = [];
if (!only) {
  const root = path.join(repo, 'plugins');
  if (fs.existsSync(root))
    for (const name of fs.readdirSync(root).sort()) {
      const d = path.join(root, name);
      if (fs.statSync(d).isDirectory() && findEntry(d)) dirs.push(d);
    }
}
for (const d of extra) if (!dirs.includes(d)) dirs.push(d);

if (!dirs.length) {
  console.log('build-plugins: no plugin UIs found (plugins/*/ui/main.js)');
  process.exit(0);
}

/** Copy the fresh bundle next to the built plugin (<app>/plugins/<P>/wwwroot/ui.js). */
function copyToArtifacts(dir) {
  return {
    name: 'netpi-copy-to-artifacts',
    writeBundle() {
      const name = path.basename(dir);
      const target = path.join(appDir, 'plugins', name);
      const src = path.join(dir, 'wwwroot/ui.js');
      const size = fs.existsSync(src) ? fs.statSync(src).size : 0;
      let note = '';
      if (!noCopy && fs.existsSync(target)) {
        fs.mkdirSync(path.join(target, 'wwwroot'), { recursive: true });
        fs.copyFileSync(src, path.join(target, 'wwwroot/ui.js'));
        note = ` → ${path.relative(repo, path.join(target, 'wwwroot/ui.js'))}`;
      }
      console.log(`  ✓ ${path.relative(repo, src)} (${(size / 1024).toFixed(1)} KB)${note}`);
    },
  };
}

let failed = 0;
for (const dir of dirs) {
  const entry = findEntry(dir);
  if (!entry) {
    console.warn(`  ✗ ${path.relative(repo, dir)}: no ui/main.js`);
    failed++;
    continue;
  }
  console.log(`build-plugins: ${path.relative(repo, dir) || dir}`);
  try {
    await build({
      configFile: false,
      root: dir,
      logLevel: 'warn',
      publicDir: false,
      plugins: [
        svelte({ configFile: false, emitCss: false, compilerOptions: { css: 'injected' } }),
        copyToArtifacts(dir),
      ],
      resolve: {
        alias: { '@netpi/kit': kit },
        dedupe: ['svelte'],
      },
      define: { 'process.env.NODE_ENV': JSON.stringify(watch ? 'development' : 'production') },
      build: {
        lib: { entry, formats: ['es'], fileName: () => 'ui.js' },
        outDir: path.join(dir, 'wwwroot'),
        emptyOutDir: false,
        copyPublicDir: false,
        target: 'es2022',
        minify: !watch,
        sourcemap: false,
        reportCompressedSize: false,
        watch: watch ? {} : null,
        // one self-contained file; full minification (lib mode keeps whitespace for es by default)
        rolldownOptions: { output: { codeSplitting: false, ...(watch ? {} : { minify: true }) } },
      },
    });
  } catch (e) {
    failed++;
    console.error(`  ✗ ${path.relative(repo, dir)}: ${e.message}`);
  }
}
if (!watch && failed) process.exit(1);
