<script>
  /**
   * One agent in the Work tab (a model with instances): its state (ready, busy, queued, not loaded, off), the instances
   * in use and who runs on them, who waits, and a switch to take it off or back on (agents.setEnabled). Model calls
   * without an agent (a summarizer on another model) show the same way while they run, without the switch.
   */
  import { Elapsed, StatusDot } from '@netpi/kit';
  import { shortModel } from './util.js';

  let { pool, agentById, titles, ctx } = $props();

  const agent = $derived(!!pool.configured);
  const look = $derived.by(() => {
    if (pool.disabled) return { dot: 'cancelled', chip: 'off', tone: 'dim' };
    if (pool.available === false) return { dot: 'unloaded', chip: /isn't loaded/.test(pool.unavailable ?? '') ? 'not loaded' : 'inactive', tone: 'warn' };
    if (pool.queued) return { dot: 'queued', chip: 'queued', tone: 'warn' };
    if (pool.busy >= pool.capacity) return { dot: 'running', chip: 'full', tone: 'accent' };
    if (pool.busy) return { dot: 'running', chip: 'busy', tone: 'accent' };
    return { dot: 'loaded', chip: 'ready', tone: 'dim' };
  });
  // line 1: the agent (another model call: its model); line 2: busy/instances and the model (the provider)
  const slash = $derived(pool.key.indexOf('/'));
  const name = $derived(agent ? pool.key : slash > 0 ? pool.key.slice(slash + 1) : pool.key);
  const second = $derived(agent ? shortModel(pool.model) : slash > 0 ? pool.key.slice(0, slash) : '');

  // top-level agents are all called "main": their session title tells them apart; subagents go by their name
  const who = (o) => {
    const a = agentById.get(o.agentId);
    const title = titles?.get(o.sessionId ?? a?.sessionId);
    if (title && !a?.isSubagent) return title;
    return a?.name ?? o.label ?? title ?? o.agentId;
  };
  const note = (o) => (o.label && o.label !== who(o) && o.label !== agentById.get(o.agentId)?.name ? o.label : '');

  let busy = $state(false);
  async function toggle(e) {
    const enabled = e.currentTarget.checked;
    busy = true;
    try {
      await ctx.rpc('agents.setEnabled', { id: pool.key, enabled });
    } catch (err) {
      e.currentTarget.checked = !enabled;
      ctx.app.toast(err?.message ?? String(err), 'error');
    } finally {
      busy = false;
    }
  }
</script>

<div class="pool" class:inactive={pool.available === false} data-agent={agent ? pool.key : null}>
  <div
    class="head np-line"
    title="{agent ? `Agent ${pool.key} on ${pool.model}` : pool.key} — {pool.busy}/{pool.capacity} busy{pool.queued ? `, ${pool.queued} queued` : ''}{pool.use ? `\n${pool.use}` : ''}"
  >
    <StatusDot status={look.dot} />
    <span class="key np-mono np-grow"><b>{name}</b></span>
    <span class="st" data-tone={look.tone}>{look.chip}</span>
    {#if agent}
      <input
        type="checkbox"
        class="np-switch"
        checked={!pool.disabled}
        disabled={busy}
        onchange={toggle}
        aria-label="Agent {pool.key} on"
        title={pool.disabled ? 'Switched off: switch it on to let chats and subagents run on it' : 'Switch off: runs on it finish, new ones are refused'}
      />
    {/if}
  </div>
  <div class="cap np-line">
    <span class="nums np-mono">{pool.busy}/{pool.capacity}</span>
    {#if second}<span class="second np-mono np-grow" title={agent ? pool.model : pool.provider}>{second}</span>{/if}
  </div>
  {#if agent && pool.available === false && !pool.disabled && pool.unavailable}
    <div class="why">{pool.unavailable}</div>
  {/if}
  {#each pool.owners ?? [] as o (o.agentId + o.since)}
    <button class="owner np-line" title="{who(o)}{note(o) ? ` — ${note(o)}` : ''} (open session)" onclick={() => o.sessionId && ctx.app.openSession(o.sessionId)}>
      <span class="slot on"></span>
      <span class="text np-line np-baseline np-grow">
        <span class="name">{who(o)}</span>
        <span class="label np-grow">{note(o)}</span>
        <Elapsed since={o.since} class="np-mono el" />
      </span>
    </button>
  {/each}
  {#each pool.waiters ?? [] as w (w.agentId + w.since)}
    <button class="owner waiting np-line" title="{who(w)} is waiting for a free instance (open session)" onclick={() => w.sessionId && ctx.app.openSession(w.sessionId)}>
      <span class="slot"></span>
      <span class="text np-line np-baseline np-grow">
        <span class="name">{who(w)}</span>
        <span class="label np-grow">waiting</span>
        <Elapsed since={w.since} class="np-mono el" />
      </span>
    </button>
  {/each}
</div>

<style>
  .pool {
    padding: 5px 0 7px;
  }
  .pool + :global(.pool) {
    border-top: 1px dashed var(--border);
  }
  .head {
    min-height: 20px;
    gap: 7px;
  }
  .key {
    font-size: 12px;
    color: var(--fg);
  }
  .key b {
    font-weight: 600;
  }
  .inactive .key b {
    color: var(--fg-muted);
  }
  .second {
    min-width: 0;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
    color: var(--fg-dim);
    font-size: 11px;
  }
  .why {
    margin: 0 0 2px;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
  }
  .cap {
    margin: 1px 0 3px;
    gap: 7px;
    min-height: 16px;
  }
  .nums {
    font-size: 11px;
    color: var(--fg-muted);
  }
  .st {
    flex: none;
    font-size: var(--fs-xs);
    padding: 0 6px;
    border-radius: 8px;
    line-height: 16px;
    background: var(--bg-3);
    color: var(--fg-muted);
  }
  .st[data-tone='accent'] {
    background: var(--accent-soft);
    color: var(--accent);
  }
  .st[data-tone='warn'] {
    background: var(--warn-soft);
    color: var(--warn);
  }
  .owner {
    gap: 7px;
    width: 100%;
    min-height: 22px;
    padding: 1px 4px 1px 2px;
    border: 0;
    border-radius: 4px;
    background: transparent;
    color: var(--fg);
    font: inherit;
    font-size: var(--fs-sm);
    text-align: left;
    cursor: pointer;
    min-width: 0;
  }
  .owner:hover {
    background: var(--bg-2);
  }
  .slot {
    width: 6px;
    height: 6px;
    border-radius: 50%;
    flex: none;
    box-shadow: inset 0 0 0 1.5px var(--warn);
  }
  .slot.on {
    background: var(--accent);
    box-shadow: none;
  }
  .name {
    font-weight: 500;
    max-width: 55%;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .text {
    gap: 7px;
  }
  .text > .name {
    flex: none;
  }
  .label {
    color: var(--fg-dim);
    font-size: var(--fs-xs);
  }
  .waiting .name {
    color: var(--fg-muted);
  }
  .waiting .label {
    color: var(--warn);
  }
  .owner :global(.el) {
    flex: none;
    font-size: 11px;
    color: var(--fg-dim);
    white-space: nowrap;
  }
</style>
