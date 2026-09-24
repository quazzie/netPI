<script>
  import { onDestroy } from 'svelte';
  import Icon from '../../lib/kit/Icon.svelte';
  import { renderMarkdown } from '../../lib/markdown.js';
  import { prefs } from '../../lib/state/ui.svelte.js';
  import { duration } from '../../lib/format.js';

  /**
   * A thinking step. step.stream is set while the answer streams: the line then shows a spinner, the time so far and the
   * latest line, and it can be opened to follow the thinking (markdown at most every 150ms). The finished row that
   * replaces it has the same height.
   */
  let { step, chat } = $props();
  const p = $derived(step.part);
  const s = $derived(step.stream ?? null);
  const open = $derived(s ? (chat.liveThinkingOpen ?? prefs.expandThinking) : (chat.expanded.get(step.key) ?? prefs.expandThinking));
  const preview = $derived.by(() => {
    const t = (p.text || '').trim();
    if (s) {
      // the latest line while it streams
      const tail = t.slice(-400);
      const i = tail.lastIndexOf('\n');
      return (i >= 0 ? tail.slice(i + 1) : tail).replace(/[*_#`>]/g, '').slice(-160);
    }
    const i = t.indexOf('\n');
    return (i < 0 ? t : t.slice(0, i)).replace(/[*_#`>]/g, '').slice(0, 200);
  });

  function toggle() {
    if (s) chat.liveThinkingOpen = !open;
    else chat.expanded.set(step.key, !open);
  }

  // streaming: timer and throttled markdown
  let now = $state(Date.now());
  $effect(() => {
    if (!s || s.thinkingEndedAt) return;
    const t = setInterval(() => (now = Date.now()), 250);
    return () => clearInterval(t);
  });
  const live = $derived(!!s && !s.thinkingEndedAt);
  const liveMs = $derived(s?.thinkingStartedAt ? (s.thinkingEndedAt || now) - s.thinkingStartedAt : 0);

  let liveHtml = $state('');
  let timer = 0;
  let last = 0;
  function render() {
    timer = 0;
    last = performance.now();
    liveHtml = renderMarkdown(p.text, { cache: false });
  }
  $effect(() => {
    const t = p.text;
    if (!s || !open || !t || timer) return;
    const wait = 150 - (performance.now() - last);
    if (wait <= 0) render();
    else timer = setTimeout(render, wait);
  });
  onDestroy(() => clearTimeout(timer));

  /** Keep the live thinking scrolled to its end while it grows, unless the user scrolled up inside it. */
  function follow(node) {
    let atEnd = true;
    const onScroll = () => (atEnd = node.scrollHeight - node.scrollTop - node.clientHeight < 24);
    node.addEventListener('scroll', onScroll);
    node.scrollTop = node.scrollHeight;
    return {
      update() {
        if (atEnd) requestAnimationFrame(() => (node.scrollTop = node.scrollHeight));
      },
      destroy() {
        node.removeEventListener('scroll', onScroll);
      },
    };
  }
</script>

<div class="thinking" class:open class:live={!!s} class:active={live}>
  <button
    class="line"
    aria-expanded={open}
    title={s ? (open ? 'Hide the thinking' : 'Show the thinking so far') : undefined}
    onclick={toggle}
  >
    <span class="ic">{#if live}<span class="np-spinner"></span>{:else}<Icon name="brain" size={14} />{/if}</span>
    <span class="label">{live ? 'Thinking…' : p.redacted && !p.text ? 'Thinking (redacted)' : 'Thinking'}</span>
    {#if s}<span class="dur">{duration(liveMs)}</span>{:else if p.durationMs}<span class="dur">{duration(p.durationMs)}</span>{/if}
    {#if !open && preview}<span class="preview" class:tailing={!!s}><bdi>{preview}</bdi></span>{/if}
    <span class="chev" class:open><Icon name="chevron-right" size={12} /></span>
  </button>
  {#if open && p.text}
    {#if s}
      <div class="body live-body md np-scroll" use:follow={liveHtml}>{@html liveHtml}</div>
    {:else}
      <div class="body md">{@html renderMarkdown(p.text)}</div>
    {/if}
  {/if}
</div>

<style>
  .thinking {
    min-width: 0;
  }
  .line {
    display: flex;
    align-items: center;
    gap: 8px;
    width: 100%;
    height: 28px;
    padding: 0 8px 0 4px;
    margin-left: -4px;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--fg-muted);
    font-size: var(--fs-sm);
    text-align: left;
    min-width: 0;
  }
  .line:hover {
    background: var(--bg-2);
  }
  .ic {
    display: grid;
    place-items: center;
    width: 16px;
    color: var(--fg-dim);
  }
  .active .ic {
    color: var(--accent);
  }
  .label {
    flex: none;
  }
  .dur {
    flex: none;
    color: var(--fg-dim);
    font-family: var(--font-mono);
    font-size: 11px;
  }
  .preview {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
    color: var(--fg-dim);
    font-style: italic;
  }
  /* the latest line: keep its end visible */
  .preview.tailing {
    mask-image: linear-gradient(to right, transparent, #000 24px);
    direction: rtl;
    text-align: left;
    text-overflow: clip;
  }
  .chev {
    margin-left: auto;
    display: inline-grid;
    color: var(--fg-dim);
    opacity: 0;
    transition:
      transform var(--t-fast),
      opacity var(--t-fast);
  }
  .line:hover .chev,
  .chev.open {
    opacity: 1;
  }
  .chev.open {
    transform: rotate(90deg);
  }
  .body {
    margin: 2px 0 8px 7px;
    padding: 2px 0 2px 16px;
    border-left: 2px solid var(--border);
    color: var(--fg-muted);
    font-size: 13px;
    line-height: 1.55;
  }
  .live-body {
    max-height: 320px;
    overflow-y: auto;
  }
</style>
