<script>
  import Icon from '../../lib/kit/Icon.svelte';
  import { renderMarkdown } from '../../lib/markdown.js';
  import { openSession } from '../../lib/state/app.svelte.js';
  import { stamp, firstLine, truncate } from '../../lib/format.js';

  /** Harness notices (role notice) and compaction summaries (role summary) as slim dividers. */
  let { msg, chat } = $props();

  const KINDS = {
    project: { icon: 'folder', tone: 'info' },
    nudge: { icon: 'zap', tone: 'warn', label: 'Nudge' },
    'agent-message': { icon: 'message-circle', tone: 'accent', expand: true },
    'agent-result': { icon: 'bot', tone: 'ok', expand: true },
    compaction: { icon: 'layers', tone: 'info', label: 'Context compacted', expand: true },
    summary: { icon: 'layers', tone: 'info', label: 'Summary of earlier conversation', expand: true },
    retry: { icon: 'refresh', tone: 'warn' },
    error: { icon: 'alert', tone: 'err' },
    info: { icon: 'info', tone: 'muted' },
  };

  const kind = $derived(msg.role === 'summary' ? 'summary' : (msg.meta?.kind ?? 'info'));
  const k = $derived(KINDS[kind] ?? KINDS.info);
  const text = $derived(msg.parts.filter((p) => p.type === 'text').map((p) => p.text).join('\n'));
  const who = $derived(msg.meta?.agentName ?? msg.meta?.from ?? msg.meta?.name ?? null);
  const label = $derived(
    k.label ??
      (kind === 'agent-message' ? `Message from ${who ?? 'agent'}` : kind === 'agent-result' ? `${who ?? 'Subagent'} finished` : null),
  );
  const linkSession = $derived(msg.meta?.sessionId && msg.meta.sessionId !== msg.sessionId ? msg.meta.sessionId : null);
  const long = $derived(k.expand || text.length > 140 || text.includes('\n'));
  const key = $derived(`n${msg.id}`);
  const open = $derived(chat.expanded.get(key) ?? false);
  const oneLine = $derived(truncate(firstLine(text).replace(/[*_`#>]/g, ''), 160));
</script>

<div class="notice" data-tone={k.tone} class:open>
  <div class="rule"></div>
  <button
    class="pill"
    class:clickable={long}
    onclick={() => long && chat.expanded.set(key, !open)}
    title={stamp(msg.createdAt)}
    aria-expanded={long ? open : undefined}
  >
    <Icon name={k.icon} size={13} />
    {#if label}<span class="label">{label}</span>{/if}
    {#if !open && (!label || !k.expand) && oneLine}<span class="text">{oneLine}</span>{/if}
    {#if long}<span class="chev" class:open><Icon name="chevron-right" size={11} /></span>{/if}
  </button>
  {#if linkSession}
    <button class="link" onclick={() => openSession(linkSession)}><Icon name="external" size={11} /> open</button>
  {/if}
  <div class="rule"></div>
</div>
{#if open}
  <div class="expanded md">{@html renderMarkdown(text)}</div>
{/if}

<style>
  .notice {
    display: flex;
    align-items: center;
    gap: 8px;
    min-height: 26px;
    --tone: var(--fg-dim);
  }
  .notice[data-tone='info'] {
    --tone: var(--info);
  }
  .notice[data-tone='warn'] {
    --tone: var(--warn);
  }
  .notice[data-tone='err'] {
    --tone: var(--err);
  }
  .notice[data-tone='ok'] {
    --tone: var(--ok);
  }
  .notice[data-tone='accent'] {
    --tone: var(--accent);
  }
  .rule {
    flex: 1;
    min-width: 16px;
    height: 1px;
    background: var(--border);
  }
  .pill {
    display: flex;
    align-items: center;
    gap: 7px;
    max-width: 78%;
    min-width: 0;
    height: 24px;
    padding: 0 10px;
    border: 1px solid var(--border);
    border-radius: 12px;
    background: var(--bg);
    color: var(--fg-muted);
    font-size: var(--fs-sm);
    cursor: default;
  }
  .pill.clickable {
    cursor: pointer;
  }
  .pill.clickable:hover {
    border-color: var(--border-strong);
    color: var(--fg);
  }
  .pill :global(svg) {
    color: var(--tone);
  }
  .notice[data-tone='err'] .pill {
    color: var(--err);
    border-color: color-mix(in srgb, var(--err) 35%, var(--border));
    background: color-mix(in srgb, var(--err) 6%, var(--bg));
  }
  .label {
    flex: none;
    font-weight: 550;
    color: var(--fg);
  }
  .text {
    min-width: 0;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
  }
  .chev {
    display: inline-grid;
    color: var(--fg-dim);
    transition: transform var(--t-fast);
  }
  .chev.open {
    transform: rotate(90deg);
  }
  .link {
    display: inline-flex;
    align-items: center;
    gap: 3px;
    border: 0;
    padding: 2px 4px;
    border-radius: 4px;
    background: transparent;
    color: var(--accent);
    font-size: var(--fs-xs);
  }
  .link:hover {
    background: var(--accent-soft);
  }
  .expanded {
    margin: 6px 24px 4px;
    padding: 10px 14px;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--bg-1);
    font-size: 13px;
    color: var(--fg-muted);
  }
</style>
