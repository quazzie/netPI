// @netpi/kit — small Svelte components + helpers shared by the host UI and plugin tabs.
// Plugin UIs import it as `@netpi/kit` (aliased by web/scripts/build-plugins.mjs) and bundle it.
// Styling comes from the host's global np-* classes (web/src/styles/kit.css) and CSS variables.
export { default as Icon } from './Icon.svelte';
export { default as Section } from './Section.svelte';
export { default as Badge } from './Badge.svelte';
export { default as StatusDot } from './StatusDot.svelte';
export { default as TimeAgo } from './TimeAgo.svelte';
export { default as Empty } from './Empty.svelte';
export { default as Button } from './Button.svelte';
export { default as IconButton } from './IconButton.svelte';
export { default as Collapsible } from './Collapsible.svelte';
export { default as Markdown } from './Markdown.svelte';
export { default as Spinner } from './Spinner.svelte';
export { timeAgo, duration, tokens, bytes, relPath, basename, truncate } from '../format.js';
export { host, renderMarkdown } from './host.js';
