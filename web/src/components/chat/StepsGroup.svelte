<script>
  import Icon from '../../lib/kit/Icon.svelte';
  import ThinkingRow from './ThinkingRow.svelte';
  import ToolRow from './ToolRow.svelte';
  import { prefs } from '../../lib/state/ui.svelte.js';
  import { stepCounts } from '../../lib/chatItems.js';
  import { toolMeta } from '../../lib/tools.js';
  import { duration } from '../../lib/format.js';

  /**
   * Consecutive thinking/tool rows. While the run is live they are always shown; once finished, groups
   * with more than 3 steps collapse into a one-line summary (per-group override remembered in chat.expanded).
   */
  let { item, chat, base, live = false } = $props();

  const n = $derived(item.steps.length);
  const collapsible = $derived(!live && prefs.collapseSteps && n > 3);
  const open = $derived(collapsible ? (chat.expanded.get(item.key) ?? false) : true);
  const span = $derived(Number.isFinite(item.startMs) && item.endMs > item.startMs ? item.endMs - item.startMs : null);
  const counts = $derived(collapsible ? stepCounts(item.steps) : null);
  const failed = $derived(item.steps.reduce((a, s) => a + (s.kind === 'tool' && s.result?.isError ? 1 : 0), 0));
</script>

<div class="group" class:collapsible class:open>
  {#if collapsible}
    <button class="head" aria-expanded={open} onclick={() => chat.expanded.set(item.key, !open)}>
      <span class="chev" class:open><Icon name="chevron-right" size={12} stroke={2} /></span>
      <span class="count">{n} steps</span>
      {#if span}<span class="np-dim">· {duration(span)}</span>{/if}
      <span class="summary">
        {#each counts.tools as t (t.name)}
          <span class="cnt" title="{toolMeta(t.name).label} × {t.count}"
            ><Icon name={toolMeta(t.name).icon} size={12} />{t.count}</span
          >
        {/each}
        {#if counts.thinking}<span class="cnt" title="Thinking × {counts.thinking}"
            ><Icon name="brain" size={12} />{counts.thinking}</span
          >{/if}
      </span>
      {#if failed}<span class="failed">{failed} failed</span>{/if}
    </button>
  {/if}
  {#if open}
    <div class="steps">
      {#each item.steps as step (step.key)}
        {#if step.kind === 'thinking'}
          <ThinkingRow {step} {chat} />
        {:else}
          <ToolRow {step} {chat} {base} {live} />
        {/if}
      {/each}
    </div>
  {/if}
</div>

<style>
  .group {
    display: flex;
    flex-direction: column;
    min-width: 0;
  }
  .head {
    display: flex;
    align-items: center;
    gap: 7px;
    height: 28px;
    padding: 0 8px 0 4px;
    margin-left: -4px;
    align-self: flex-start;
    max-width: 100%;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--fg-muted);
    font-size: var(--fs-sm);
  }
  .head:hover {
    background: var(--bg-2);
    color: var(--fg);
  }
  .chev {
    display: inline-grid;
    color: var(--fg-dim);
    transition: transform var(--t-fast);
  }
  .chev.open {
    transform: rotate(90deg);
  }
  .count {
    font-weight: 550;
  }
  .summary {
    display: flex;
    gap: 9px;
    margin-left: 4px;
    color: var(--fg-dim);
    font-size: var(--fs-xs);
    font-variant-numeric: tabular-nums;
  }
  .cnt {
    display: inline-flex;
    align-items: center;
    gap: 3px;
  }
  .failed {
    color: var(--err);
    font-size: var(--fs-xs);
  }
  .steps {
    display: flex;
    flex-direction: column;
    gap: 1px;
    min-width: 0;
  }
  .collapsible .steps {
    margin: 2px 0 4px 9px;
    padding-left: 10px;
    border-left: 1px solid var(--border);
  }
</style>
