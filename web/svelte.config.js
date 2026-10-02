import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Svelte derives a component's scoped-CSS class hash from its path, relative to rootDir - process.cwd() by default.
// That made the committed bundles depend on where the build ran: the same source built from web/ emitted
// svelte-110iazi where the repo root emits svelte-19lb510, and the whole bundle was rewritten for that alone.
// The repository root is the one root every checkout and every build shares.
const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

export default {
  compilerOptions: { runes: true, rootDir: repoRoot },
};