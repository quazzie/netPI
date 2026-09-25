<script>
  import Icon from '../../lib/kit/Icon.svelte';
  import ThinkingRow from './ThinkingRow.svelte';
  import ToolRow from './ToolRow.svelte';
  import TurnLine from './TurnLine.svelte';
  import { prefs } from '../../lib/state/ui.svelte.js';
  import { stepCounts } from '../../lib/chatItems.js';
  import { toolMeta, toolSummary, parseArgs } from '../../lib/tools.js';
  import { duration } from '../../lib/format.js';

  /**
   * Consecutive thinking/tool rows, folded by the "Steps" preference: 'open' never folds; 'done' (default) shows every
   * row while the run is live and folds groups of more than 3 steps into "N steps · time" once it ends; 'folded' folds
   * from the second step on, live too, so the chat does not grow row by row while the agent works (a folded group is one
   * line, like a single row). Per-group overrides are remembered in chat.expanded. `active` = the group the agent is
   * adding to right now: folded, its line shows the latest step.
   */
  let { item, chat, base, live = false, active = false } = $props();

  const n = $derived(item.steps.length);
  const collapsible = $derived(prefs.steps === 'folded' ? n > 1 : prefs.steps === 'done' ? !live && n > 3 : false);
  const open = $derived(collapsible ? (chat.expanded.get(item.key) ?? false) : true);
  const span = $derived(Number.isFinite(item.startMs) && item.endMs > item.startMs ? item.endMs - item.startMs : null);
  const counts = $derived(collapsible ? stepCounts(item.steps) : null);
  const failed = $derived(item.steps.reduce((a, s) => a + (s.kind === 'tool' && s.result?.isError ? 1 : 0), 0));
  // the numbers of a model turn go under its last step: a tool call of a message that ended in tool_use (its thinking
  // can sit in an earlier group, before its text; a turn that ends in an answer has them in the answer's footer)
  const endsTurn = (i) => {
    const s = item.steps[i];
    return s.kind === 'tool' && s.msg?.stopReason === 'tool_use' && item.steps[i + 1]?.msg?.id !== s.msg.id;
  };
  const latest = $derived.by(() => {
    if (!active || !collapsible || open) return null;
    const s = item.steps[n - 1];
    if (s.kind === 'thinking') return { icon: 'brain', label: s.stream && !s.stream.thinkingEndedAt ? 'Thinking…' : 'Thinking', summary: '' };
    const meta = toolMeta(s.call.name);
    return { icon: meta.icon, label: meta.label, summary: s.preparing ? 'preparing…' : (toolSummary(s.call.name, parseArgs(s.call), base) ?? '') };
  });
</script>

<div class="group" class:collapsible class:open>
  {#if collapsible}
    <button class="head" aria-expanded={open} onclick={() => chat.expanded.set(item.key, !open)}>
      <span class="chev" class:open><Icon name="chevron-right" size={12} stroke={2} /></span>
      <span class="count">{n} steps</span>
      {#if span}<span class="took np-dim">· {duration(span)}</span>{/if}
      {#if latest}
        <span class="latest">
          <span class="np-spinner"></span>
          <span class="text">
            <span class="lbl">{latest.label}</span>
            {#if latest.summary}<span class="sum np-mono">{latest.summary}</span>{/if}
          </span>
        </span>
      {:else}
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
      {/if}
    </button>
  {/if}
  {#if open}
    <div class="steps">
      {#each item.steps as step, i (step.key)}
        {#if step.kind === 'thinking'}
          <ThinkingRow {step} {chat} />
        {:else}
          <ToolRow {step} {chat} {base} {live} />
        {/if}
        {#if prefs.turnDetails && endsTurn(i)}<TurnLine msg={step.msg} />{/if}
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
    min-width: 0;
    overflow: hidden;
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
    flex: none;
    color: var(--fg-dim);
    transition: transform var(--t-fast);
  }
  .chev.open {
    transform: rotate(90deg);
  }
  /* only the latest step's summary gives way on a narrow chat; the rest never wraps */
  .count,
  .took,
  .summary,
  .failed {
    flex: none;
    white-space: nowrap;
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
  .latest {
    display: flex;
    align-items: center;
    gap: 6px;
    min-width: 0;
    margin-left: 4px;
    color: var(--fg-muted);
  }
  /* the label and the smaller mono summary share a baseline (centered, the summary would sit ~1px high); one label
     line tall, so the pair centers like "N steps" before it and all of them stay on one baseline */
  .latest .text {
    display: flex;
    align-items: baseline;
    gap: 6px;
    min-width: 0;
    height: 1lh;
  }
  .latest .lbl {
    flex: none;
    color: var(--fg);
    font-weight: 500;
  }
  .latest .sum {
    min-width: 0;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
    color: var(--fg-dim);
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
