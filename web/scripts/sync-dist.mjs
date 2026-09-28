// Mirror web/dist into artifacts/app/wwwroot (when the app has been built) so a running NetPI picks up UI
// changes without a `dotnet build`. `dotnet build` of netpi-server / NetPI does the same copy (build/WebRoot.targets)
// into its own output, which by default is artifacts/dev/app, not the running app.
// Installing into artifacts/app is opt-in: NETPI_COPY=1 (build.ps1 -Publish) or NETPI_APP_DIR=<dir> to pick another
// app folder. NETPI_NO_COPY=1 forces neither, whatever else is set.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const dist = path.join(repo, 'web/dist');
const app = process.env.NETPI_APP_DIR ? path.resolve(process.env.NETPI_APP_DIR) : path.join(repo, 'artifacts/app');
if (!fs.existsSync(dist)) {
  console.error('sync-dist: web/dist not found (run vite build first)');
  process.exit(1);
}
if (process.env.NETPI_NO_COPY) {
  console.log('sync-dist: NETPI_NO_COPY is set; the app folder is left alone');
  process.exit(0);
}
if (!process.env.NETPI_COPY) {
  console.log('sync-dist: NETPI_COPY is not set; the app folder is left alone (the bundle goes into the .NET build output)');
  process.exit(0);
}
if (!fs.existsSync(app)) {
  console.log('sync-dist: the app folder does not exist yet; dotnet build will copy web/dist');
  process.exit(0);
}
const target = path.join(app, 'wwwroot');
fs.rmSync(target, { recursive: true, force: true });
fs.cpSync(dist, target, { recursive: true });
console.log(`sync-dist: web/dist → ${path.relative(repo, target)}`);
