<script>
  import { onDestroy } from 'svelte';
  import Icon from '../../lib/kit/Icon.svelte';
  import { renderMarkdown } from '../../lib/markdown.js';
  import { toolMeta } from '../../lib/tools.js';
  import { duration } from '../../lib/format.js';

  /**
   * The assistant message being streamed. Text/thinking arrive through StreamState (flushed once per
   * animation frame); markdown is re-rendered at most every 100ms and never highlighted here — the final
   * message.added replaces this block.
   */
  let { stream } = $props();

  const INTERVAL = 100;
  let html = $state('');
  let timer = 0;
  let last = 0;

  function render() {
    timer = 0;
    last = performance.now();
    html = renderMarkdown(stream.text, { cache: false });
  }

  $effect(() => {
    const t = stream.text;
    if (!t) {
      html = '';
      return;
    }
    if (timer) return;
    const wait = INTERVAL - (performance.now() - last);
    if (wait <= 0) render();
    else timer = setTimeout(render, wait);
  });
  onDestroy(() => clearTimeout(timer));

  // live one-line thinking preview + timer
  let now = $state(Date.now());
  $effect(() => {
    if (!stream.active) return;
    const t = setInterval(() => (now = Date.now()), 250);
    return () => clearInterval(t);
  });
  const thinkingLive = $derived(!!stream.thinking && !stream.thinkingEndedAt);
  const thinkingMs = $derived(
    stream.thinkingStartedAt ? (stream.thinkingEndedAt || now) - stream.thinkingStartedAt : 0,
  );
  const preview = $derived.by(() => {
    const t = stream.thinking;
    if (!t) return '';
    const tail = t.slice(-400).trimEnd();
    const i = tail.lastIndexOf('\n');
    return (i >= 0 ? tail.slice(i + 1) : tail).replace(/[*_#`>]/g, '').slice(-160);
  });
</script>

<div class="streaming">
  {#if stream.thinking}
    <div class="thinking" class:live={thinkingLive}>
      <span class="ic">{#if thinkingLive}<span class="np-spinner"></span>{:else}<Icon name="brain" size={14} />{/if}</span>
      <span class="label">{thinkingLive ? 'Thinking…' : 'Thought'}</span>
      <span class="dur">{duration(thinkingMs)}</span>
      {#if thinkingLive && preview}<span class="preview"><bdi>{preview}</bdi></span>{/if}
    </div>
  {/if}
  {#if html}
    <div class="md">{@html html}<span class="caret"></span></div>
  {/if}
  {#each stream.tools as t (t.callId)}
    <div class="tool">
      <span class="ic"><span class="np-spinner"></span></span>
      <span class="label">{toolMeta(t.name).label}</span>
      <span class="np-dim">preparing…</span>
    </div>
  {/each}
  {#if !stream.text && !stream.thinking && !stream.tools.length}
    <div class="waiting"><span class="dot"></span><span class="dot"></span><span class="dot"></span></div>
  {/if}
</div>

<style>
  .streaming {
    display: flex;
    flex-direction: column;
    gap: 6px;
    min-width: 0;
    padding: 0 2px;
  }
  .thinking,
  .tool {
    display: flex;
    align-items: center;
    gap: 8px;
    height: 28px;
    color: var(--fg-muted);
    font-size: var(--fs-sm);
    min-width: 0;
  }
  .ic {
    display: grid;
    place-items: center;
    width: 16px;
    color: var(--accent);
    flex: none;
  }
  .thinking:not(.live) .ic {
    color: var(--fg-dim);
  }
  .label {
    flex: none;
  }
  .tool .label {
    color: var(--fg);
    font-weight: 500;
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
    mask-image: linear-gradient(to right, transparent, #000 24px);
    direction: rtl;
    text-align: left;
  }
  .caret {
    display: inline-block;
    width: 7px;
    height: 1.05em;
    margin-left: 2px;
    vertical-align: text-bottom;
    background: var(--accent);
    border-radius: 1px;
    animation: blink 1s steps(2, start) infinite;
  }
  .md :global(p:nth-last-child(2)) {
    display: inline;
  }
  @keyframes blink {
    to {
      visibility: hidden;
    }
  }
  .waiting {
    display: flex;
    gap: 5px;
    padding: 10px 2px;
  }
  .dot {
    width: 6px;
    height: 6px;
    border-radius: 50%;
    background: var(--fg-dim);
    animation: bounce 1.2s var(--ease) infinite;
  }
  .dot:nth-child(2) {
    animation-delay: 0.15s;
  }
  .dot:nth-child(3) {
    animation-delay: 0.3s;
  }
  @keyframes bounce {
    0%,
    60%,
    100% {
      opacity: 0.3;
      transform: translateY(0);
    }
    30% {
      opacity: 1;
      transform: translateY(-3px);
    }
  }
</style>
