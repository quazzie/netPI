// Vite config for the NetPI web UI.
//   npm run dev        → dev server (proxies /ws, /api, /plugins to the NetPI host or the mock on :7431)
//   npm run build:web  → artifacts/app/wwwroot
import { defineConfig, loadEnv } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';
import { fileURLToPath } from 'node:url';
import fs from 'node:fs';
import path from 'node:path';

const webDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(webDir, '..');
// The svelte the app is built with, in the bundle: a plugin tab bundle is built against it and refuses to open against
// a UI built with another one (web/scripts/host-shims.mjs writes that check into every tab bundle).
const svelteVersion = JSON.parse(fs.readFileSync(path.join(repoRoot, 'node_modules/svelte/package.json'), 'utf8')).version;

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, webDir, ['VITE_', 'NETPI_']);
  const target = env.NETPI_URL || process.env.NETPI_URL || 'http://127.0.0.1:7431';

  // The host rejects WebSockets with a foreign Origin: present the target's origin to it.
  const rewriteOrigin = (proxy) => {
    proxy.on('proxyReq', (req) => req.setHeader('origin', target));
    proxy.on('proxyReqWs', (req) => req.setHeader('origin', target));
  };

  return {
    root: webDir,
    envDir: webDir,
    // public/ holds the one file the page needs before the bundle can run: theme.js applies the saved theme before the
    // first paint, and it is a file rather than an inline <script> so a CSP without 'unsafe-inline' allows it.
    publicDir: 'public',
    plugins: [svelte()],
    define: { __SVELTE_VERSION__: JSON.stringify(svelteVersion) },
    resolve: {
      alias: { '@netpi/kit': path.join(webDir, 'src/lib/kit/index.js') },
    },
    build: {
      // committed build output: `dotnet build` copies it to <app>/wwwroot, so Node is only needed to change the UI
      outDir: path.join(webDir, 'dist'),
      emptyOutDir: true,
      target: 'es2022',
      sourcemap: false,
      chunkSizeWarningLimit: 800,
      reportCompressedSize: false,
    },
    server: {
      port: 5173,
      strictPort: false,
      proxy: {
        '/ws': { target, ws: true, changeOrigin: true, configure: rewriteOrigin },
        '/api': { target, changeOrigin: true, configure: rewriteOrigin },
        '/plugins': { target, changeOrigin: true, configure: rewriteOrigin },
      },
    },
  };
});
