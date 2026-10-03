// The host UI as a plugin tab sees it. A tab bundle shares the host's Svelte runtime and kit (idea-3pbkvg): they are read
// off globalThis.__netpiHost, which the host's main.js sets, so a fixture page that mounts a committed bundle has to load
// the host build first, exactly as the app does. `head` is the host's own <script>/<link> tags (from web/dist/index.html),
// `serve` answers the files they name; the host's App mounts into a hidden #app of the fixture page (it finds no server
// and sits disconnected, which a tab under test does not care about), and the fixture mounts the tab into its own element.
import fs from 'node:fs';
import path from 'node:path';

const types = { '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };

export function hostUi(root) {
  const dist = path.join(root, 'web/dist');
  const index = fs.readFileSync(path.join(dist, 'index.html'), 'utf8');
  const head = [...index.matchAll(/<script\b[^>]*src="\/(?:assets\/|theme\.js)[^>]*><\/script>|<link\b[^>]*href="\/assets\/[^>]*>/g)].map((m) => m[0]).join('\n');
  if (!head.includes('type="module"')) throw new Error('web/dist/index.html has no module script: build the web UI first (npm run build)');
  // The host App finds no server in a fixture page: it must neither fail loudly (a console error fails these tests) nor
  // ask for ever, so its socket never opens and its requests never answer. The tab under test talks to its own stub ctx.
  const quiet = '<script>window.WebSocket = class { constructor() { this.readyState = 0; } addEventListener() {} removeEventListener() {} send() {} close() {} };'
    + 'window.fetch = () => new Promise(() => {});</script>';
  return {
    head: quiet + head,
    /** Answers a request for one of the host build's files; false when it is not one. */
    serve(req, res) {
      const file = path.join(dist, decodeURIComponent((req.url ?? '/').split('?')[0]));
      if (!file.startsWith(dist + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) return false;
      res.setHeader('Content-Type', types[path.extname(file)] ?? 'application/octet-stream');
      res.end(fs.readFileSync(file));
      return true;
    },
  };
}
