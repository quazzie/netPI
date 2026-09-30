// @netpi/kit — small Svelte components + helpers shared by the host UI and plugin tabs.
// Plugin UIs import it as `@netpi/kit` (aliased by web/scripts/build-plugins.mjs) and bundle it.
// Styling comes from the host's global np-* classes (web/src/styles/kit.css) and CSS variables.
export { default as Icon } from './Icon.svelte';
export { default as Section } from './Section.svelte';
export { default as Badge } from './Badge.svelte';
export { default as StatusDot } from './StatusDot.svelte';
export { default as TimeAgo } from './TimeAgo.svelte';
export { default as Elapsed } from './Elapsed.svelte';
export { default as Empty } from './Empty.svelte';
export { default as Button } from './Button.svelte';
export { default as IconButton } from './IconButton.svelte';
export { default as ConfirmButton } from './ConfirmButton.svelte';
export { default as Collapsible } from './Collapsible.svelte';
export { default as Markdown } from './Markdown.svelte';
export { default as Spinner } from './Spinner.svelte';
export { default as SearchInput } from './SearchInput.svelte';
export { default as Segmented } from './Segmented.svelte';
export { default as Menu } from './Menu.svelte';
export { default as Pips } from './Pips.svelte';
export { timeAgo, duration, tokens, usd, bytes, relPath, basename, truncate, stamp } from '../format.js';
// Image handling for anything that attaches one (the composer, the ideas tab): shrink to fit a message, refuse
// politely, and say what happened. Browser-only (canvas), which is fine — the kit is UI.
export { prepareImage, imageBudget, sendBudget, payloadBytes, formatBytes } from '../images.js';
export { host, renderMarkdown, confirm, copyText, desktop } from './host.js';
export { clockNow, secondNow } from './ticker.svelte.js';
