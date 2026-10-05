#!/usr/bin/env node
// What a plugin tab bundle is built against now: `svelte` and `@netpi/kit` resolve to shims that read the copies the
// host UI exports on globalThis.__netpiHost (web/scripts/host-shims.mjs). This is that boundary's unit suite — it is
// where a tab either shares the page's reactive system or quietly brings its own, so each property is checked:
//
//   node web/scripts/check-host-shims.mjs
//
//   1 a shim hands out the host's own functions, not copies (one runtime, one scheduler)
//   2 a component compiled against the shims mounts through them, and a kit component from the host renders in it
//   3 a $derived in the tab that reads host state re-renders when the host changes it — what two runtimes cannot do
//   4 no host, another svelte, or a host missing an export each fail with a message that says which
//   5 the shims cover every export of svelte's three entry points and of the kit index
//   6 a tab importing a svelte entry point that is not shimmed fails the build (a second runtime would be bundled);
//     a tab on the shimmed entry points, without svelte at all, or with its own copy through a path the aliases
//     do not rewrite, still builds
//
// 1-3 run in a headless browser (the shims need a window and a real DOM); 4-6 need only Node.
import fs from 'node:fs/promises';
import { execFileSync } from 'node:child_process';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { compile, compileModule } from 'svelte/compiler';
import { loadPlaywright, launchBrowser } from '../mock/pw.mjs';
import { writeHostShims, exportNames } from './host-shims.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const KIT = path.join(root, 'web/src/lib/kit');
const SVELTE = path.join(root, 'node_modules/svelte');

// The shims, as build-plugins aliases them: regenerate them rather than trusting what is on disk.
const { aliases, version } = writeHostShims();
const shimOf = (spec) => path.resolve(aliases.find((a) => a.find.test(spec)).replacement);
const shimNamed = (file) => path.resolve(aliases.find((a) => path.basename(a.replacement) === file).replacement);

const results = [];
const check = (what, ok, detail = '') => results.push({ what, ok, detail });

// 5: what the host has to export is exactly what svelte and the kit export
const svelteNames = async (from) => exportNames(await fs.readFile(path.join(SVELTE, from), 'utf8'));
const shimNames = async (file) => {
  const src = await fs.readFile(file, 'utf8');
  return [...src.matchAll(/^export \{ (.+) \};/gm)].flatMap((m) => m[1].split(',').map((s) => s.trim().split(' as ').pop()));
};
const shimDiff = (a, b) => a.filter((n) => !b.includes(n)).concat(b.filter((n) => !a.includes(n))).join(', ');

for (const [spec, from] of [
  ['svelte', 'src/index-client.js'],
  ['svelte/internal/client', 'src/internal/client/index.js'],
  ['svelte/reactivity', 'src/reactivity/index-client.js'],
]) {
  const want = await svelteNames(from);
  const got = await shimNames(shimOf(spec));
  check(`the ${spec} shim covers every export of svelte`, got.length === want.length && shimDiff(got, want) === '', shimDiff(got, want) || `${got.length} names`);
}
{
  const want = exportNames(await fs.readFile(path.join(KIT, 'index.js'), 'utf8'));
  const got = await shimNames(shimOf('@netpi/kit'));
  check('the @netpi/kit shim covers every export of the kit index', got.length === want.length && shimDiff(got, want) === '', shimDiff(got, want) || `${got.length} names`);
}

// 4: the three ways a tab can meet a host it was not built for, each with a message that says which
const loadShim = async (spec, host) => {
  globalThis.__netpiHost = host;
  const file = pathToFileURL(shimOf(spec)).href; // a fresh url per case: a module is evaluated once and then cached
  try {
    await import(`${file}?case=${results.length}-${Math.random()}`);
    return '';
  } catch (e) {
    return e?.message ?? String(e);
  }
};
const noHost = await loadShim('svelte', undefined);
check('no host UI: the error says the tab needs the shared runtime', /shares its Svelte runtime/.test(noHost), noHost.slice(0, 80));
const skew = await loadShim('svelte', { svelte: { version: '0.0.1' } });
check('another svelte: the error names both versions', skew.includes(`svelte ${version}`) && skew.includes('0.0.1'), skew.slice(0, 120));
check(
  'and says to reload the page first (the host may have been updated), then rebuild if it still fails',
  /Reload the page/.test(skew) && /may have been updated/.test(skew) && /if it still fails, rebuild the tab/.test(skew),
  skew.slice(0, 200),
);
const trimmed = await loadShim('svelte/internal/client', { svelte: { version, client: {} } });
check('a host without the exports: the error says to rebuild both', /does not export every export/.test(trimmed), trimmed.slice(0, 80));
const noKit = await loadShim('@netpi/kit', undefined);
check('no host kit: the error says the tab needs the shared kit', /shares its kit components/.test(noKit), noKit.slice(0, 80));
delete globalThis.__netpiHost;

// 6: the build's guard. An unshimmed `svelte/…` import resolves to node_modules and bundles a second copy of the
// runtime, so build-plugins fails it with the importing file and the import. A tab that uses the shimmed entry
// points, or no svelte at all, or brings its own copy through a path the aliases do not rewrite (the deliberate
// opt-out), still builds. Run for real: build-plugins on fixture tabs under artifacts/.
const FIXTURES = path.join(root, 'artifacts', 'check-host-shims');
const buildFixture = async (name, files) => {
  const ui = path.join(FIXTURES, name, 'ui');
  for (const [file, body] of Object.entries(files)) {
    await fs.mkdir(path.dirname(path.join(ui, file)), { recursive: true });
    await fs.writeFile(path.join(ui, file), body);
  }
  try {
    execFileSync(process.execPath, [path.join(root, 'web', 'scripts', 'build-plugins.mjs'), '--only', path.dirname(ui)], {
      cwd: root,
      encoding: 'utf8',
    });
    return { code: 0, out: '' };
  } catch (e) {
    return { code: e.status ?? 1, out: `${e.stdout ?? ''}${e.stderr ?? ''}` };
  }
};
{
  const bad = await buildFixture('svelte-motion', {
    'main.js': `import { spring } from 'svelte/motion';\nexport const s = spring(0);\n`,
  });
  check('a tab importing svelte/motion fails the build', bad.code !== 0);
  check(
    'and the failure names the importing file and the import',
    /main\.js/.test(bad.out) && bad.out.includes("imports 'svelte/motion'"),
    bad.out.slice(0, 200),
  );
  const badComponent = await buildFixture('svelte-motion-component', {
    'main.js': `import Tab from './Tab.svelte';\nexport const mount = (el) => el.append(Tab);\n`,
    'Tab.svelte': `<script>\n  import { spring } from 'svelte/motion';\n  export const s = spring(0);\n</script>\n<p>tab</p>\n`,
  });
  check(
    'so does the import in a .svelte component, with the component named',
    badComponent.code !== 0 && badComponent.out.includes('Tab.svelte'),
    badComponent.out.slice(0, 200),
  );
  const shimmed = await buildFixture('shimmed-only', {
    'main.js': `import { mount } from 'svelte';\nexport const mountTab = () => mount;\n`,
  });
  check('a tab on the shimmed entry points still builds', shimmed.code === 0, shimmed.out.slice(0, 200));
  const vanilla = await buildFixture('vanilla', {
    'main.js': `export const mountLog = (el) => { el.textContent = 'vanilla'; };\n`,
  });
  check('a tab without svelte at all still builds', vanilla.code === 0, vanilla.out.slice(0, 200));
  const ownRuntime = await buildFixture('own-runtime', {
    // a tab that deliberately bundles its own runtime: svelte through a path the aliases do not rewrite
    'main.js': `import { mount } from '../../../../node_modules/svelte/src/index-client.js';\nexport const mountTab = () => mount;\n`,
  });
  check('a tab with its own runtime (the opt-out) still builds', ownRuntime.code === 0, ownRuntime.out.slice(0, 200));
  await fs.rm(FIXTURES, { recursive: true, force: true });
}

// the browser part: a component compiled the way build-plugins compiles one (its svelte imports point at the shims,
// which is what the bundler's alias does), mounted in a page that plays the host UI.
const PROBE = `<script>
  import { Badge } from '@netpi/kit';
  let { ctx } = $props();
  // host state, read from the tab: with one runtime this tracks, with a second copy of svelte in the bundle it cannot
  const label = $derived(ctx.app.label);
</script>
<Badge tone="ok">{label}</Badge>`;

const HOST = `export const app = $state({ label: 'first' });`;

/** The host UI's own component, mounted before any tab bundle loads — what main.js does with App. */
const HOST_APP = `<p>host</p>`;

/** The compiled probe's specifiers point at the shims: what the bundler's aliases do to a compiled component. */
const PROBE_IMPORTS = {
  'svelte/internal/client': '/shims/svelte-internal-client.js',
  'svelte/internal/disclose-version': '/shims/svelte-disclose-version.js',
  svelte: '/shims/svelte.js',
  '@netpi/kit': '/shims/kit.js',
};
/** svelte's own sources, served as the browser sees them: one runtime, its bare specifiers mapped to files. */
const IMPORTS = {
  'esm-env': '/esm-env.js',
  clsx: '/vendor/clsx.mjs',
  svelte: '/src/index-client.js',
  'svelte/internal/client': '/src/internal/client/index.js',
  'svelte/reactivity': '/src/reactivity/index-client.js',
  'svelte/internal/disclose-version': '/src/internal/disclose-version.js',
  '#client/constants': '/src/internal/client/constants.js',
};

const PAGE = `<!doctype html><meta charset="utf-8"><body><div id="host"></div><div id="root"></div>
<script type="importmap">${JSON.stringify({ imports: IMPORTS })}</script>
<script type="module">
// the host UI: its own svelte (the import map points the specifiers at svelte's sources), one of its kit components,
// and the state a tab can read. A shim reads these when it loads, so they are in place before anything from a bundle
// is imported — in the app the host sets globalThis.__netpiHost and mounts App long before it loads the first tab.
import * as svelte from 'svelte';
import * as client from 'svelte/internal/client';
import * as reactivity from 'svelte/reactivity';
import Badge from '/kit/Badge.svelte';
import HostApp from '/host-app.js';
import { app } from '/host.js';
globalThis.__netpiHost = {
  svelte: { version: ${JSON.stringify(version)}, svelte, client, reactivity },
  kit: new Proxy({ Badge }, { get: (kit, name) => kit[name] ?? (() => {}) }), // the parts the probe does not use
};
svelte.mount(HostApp, { target: document.getElementById('host') });
const Probe = (await import('/probe.js')).default; // the tab: everything svelte-ish in it is a shim
// the property that matters: what the shims hand a tab is the host's own functions, not a second copy of them
const shimSvelte = await import('/shims/svelte.js');
const shimClient = await import('/shims/svelte-internal-client.js');
const shimReactivity = await import('/shims/svelte-reactivity.js');
window.sameRuntime =
  shimSvelte.mount === svelte.mount &&
  shimClient.push === client.push &&
  shimClient.template_effect === client.template_effect &&
  shimReactivity.SvelteMap === reactivity.SvelteMap;
window.mountTab = (ctx) => {
  const el = document.createElement('div');
  document.getElementById('root').replaceChildren(el);
  shimSvelte.mount(Probe, { target: el, props: { ctx } });
  return el;
};
window.app = app;
</script>`;

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://x').pathname;
  const send = (type, body) => {
    res.writeHead(200, { 'content-type': type });
    res.end(body);
  };
  const compiled = (code, filename, aliasSvelte = false) =>
    compile(code, { generate: 'client', css: 'injected', filename, runes: true }).js.code.replace(
      aliasSvelte ? /(['"])(svelte(?:\/[^'"]*)?|@netpi\/kit)\1/g : /$^/g,
      (_, q, spec) => `${q}${PROBE_IMPORTS[spec] ?? spec}${q}`,
    );
  try {
    if (url === '/') return send('text/html', PAGE);
    if (url === '/esm-env.js') return send('text/javascript', 'export const BROWSER = true;\nexport const DEV = false;\n');
    if (url.startsWith('/src/')) return send('text/javascript', await fs.readFile(path.join(SVELTE, url.slice(1)), 'utf8'));
    if (url.startsWith('/shims/')) return send('text/javascript', await fs.readFile(shimNamed(path.basename(url)), 'utf8'));
    if (url === '/vendor/clsx.mjs') return send('text/javascript', await fs.readFile(path.join(root, 'node_modules/clsx/dist/clsx.mjs'), 'utf8'));
    if (url === '/host.js') return send('text/javascript', compileModule(HOST, { generate: 'client', filename: 'host.svelte.js' }).js.code);
    if (url === '/host-app.js') return send('text/javascript', compiled(HOST_APP, 'HostApp.svelte'));
    if (url === '/probe.js') return send('text/javascript', compiled(PROBE, 'Probe.svelte', true));
    if (url === '/kit/Badge.svelte') return send('text/javascript', compiled(await fs.readFile(path.join(KIT, 'Badge.svelte'), 'utf8'), 'Badge.svelte'));
  } catch (e) {
    res.writeHead(500);
    return res.end(String(e));
  }
  if (url === '/favicon.ico') {
    res.writeHead(204);
    return res.end();
  }
  res.writeHead(404);
  res.end();
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));

const pw = await loadPlaywright();
const browser = await launchBrowser(pw);
const page = await browser.newPage();
const pageErrors = [];
page.on('pageerror', (e) => pageErrors.push(e.message));
page.on('console', (m) => m.type() === 'error' && pageErrors.push(m.text()));
await page.goto(`http://127.0.0.1:${server.address().port}/`);
await page.waitForFunction(() => typeof window.mountTab === 'function', null, { timeout: 5000 }).catch(() => {
  console.error('the page did not start:', pageErrors.join(' | ') || '(no error)');
  process.exit(2);
});

// 1: the shim hands out the host's functions themselves
const same = await page.evaluate(() => window.sameRuntime);
check("a shim hands out the host's own functions", same === true, `same push: ${same}`);

// 2 + 3: the tab renders through the shims, and host state read in the tab re-renders the tab
const mounted = await page.evaluateHandle(() => window.mountTab({ app: window.app }));
await page.waitForTimeout(50);
const first = await mounted.evaluate((el) => el.textContent);
const badge = await mounted.evaluate((el) => !!el.querySelector('.np-badge'));
check('a tab compiled against the shims mounts, kit component and all', /first/.test(first) && badge, JSON.stringify(first));
await page.evaluate(() => (window.app.label = 'second'));
await page.waitForTimeout(50);
const second = await mounted.evaluate((el) => el.textContent);
check('a $derived in the tab follows host state', /second/.test(second), JSON.stringify(second));
check('no page errors', pageErrors.length === 0, pageErrors.join('; '));

await browser.close();
server.close();

let failed = 0;
for (const r of results) {
  if (!r.ok) failed++;
  console.log(`${r.ok ? 'ok  ' : 'FAIL'}  ${r.what}${r.detail ? `  (${r.detail})` : ''}`);
}
console.log(failed ? `${failed} of ${results.length} checks failed` : `all ${results.length} checks passed`);
process.exit(failed ? 1 : 0);
