// Mirror web/dist into artifacts/app/wwwroot (when the app has been built) so a running NetPI picks up UI
// changes without a `dotnet build`. `dotnet build` of netpi-server / NetPI does the same copy (build/WebRoot.targets).
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const dist = path.join(repo, 'web/dist');
const app = path.join(repo, 'artifacts/app');
if (!fs.existsSync(dist)) {
  console.error('sync-dist: web/dist not found (run vite build first)');
  process.exit(1);
}
if (!fs.existsSync(app)) {
  console.log('sync-dist: artifacts/app does not exist yet; dotnet build will copy web/dist');
  process.exit(0);
}
const target = path.join(app, 'wwwroot');
fs.rmSync(target, { recursive: true, force: true });
fs.cpSync(dist, target, { recursive: true });
console.log(`sync-dist: web/dist → ${path.relative(repo, target)}`);
