/** @type {import("@sveltejs/vite-plugin-svelte").SvelteConfig} */
const config = {
  // Svelte 5 + TypeScript: use vitePreprocess (esbuild) instead of the
  // deprecated svelte-preprocess, which silently drops `lang="ts"` script
  // blocks under the Svelte 5 compiler.
  vitePreprocess: true,
};

export default config;
