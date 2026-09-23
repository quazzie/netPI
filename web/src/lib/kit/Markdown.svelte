<script>
  import { renderMarkdown, highlight } from './host.js';
  /** Sanitized markdown (GFM). Code blocks get copy buttons and lazy highlighting. */
  let { text = '', class: cls = '', highlight: hl = true } = $props();
  const html = $derived(renderMarkdown(text));
</script>

{#key html}
  <div class="md {cls}" use:highlight={hl}>{@html html}</div>
{/key}
