<script>
  /**
   * One agent in the Work tab: a model with a number of instances ("slots"). It always draws one row per slot, the same
   * height busy or free, and a running job keeps its row (placeSlots), so nothing in the tab moves when work starts or
   * ends. A busy row says who holds the slot and what it is doing; a free one says so. Under the rows a single line
   * says who waits (the names open in a menu, which floats instead of pushing the rest down). Model calls without an
   * agent (a summarizer on another model) show the same way but without the switch.
   */
  import { Elapsed, Icon, IconButton, Menu, Pips } from '@netpi/kit';
  import { shortModel, placeSlots } from './util.js';

  let { pool, agentById, titles, ctx } = $props();

  const agent = $derived(!!pool.configured);
  const usable = $derived(pool.available !== false && !pool.disabled);
  const capacity = $derived(usable ? (pool.capacity ?? 0) : 0);
  const owners = $derived(pool.owners ?? []);
  const waiters = $derived(pool.waiters ?? []);
  const busyCount = $derived(owners.length);
  const freeCount = $derived(Math.max(0, capacity - busyCount));
  // one-line rows once an agent has many instances (the tab would be all slots otherwise)
  const compact = $derived(capacity > 4);

  // a job keeps its row: a plain (non-reactive) memory of the last placement, recomputed from the owners each time
  let placed = [];
  const rows = $derived.by(() => (placed = placeSlots(placed, owners, capacity)));

  const look = $derived.by(() => {
    if (pool.disabled) return { chip: 'off', tone: 'dim' };
    if (pool.unavailable === 'removed') return { chip: 'removed', tone: 'dim' };
    if (pool.available === false) return { chip: /isn't loaded/.test(pool.unavailable ?? '') ? 'not loaded' : 'inactive', tone: 'warn' };
    if (freeCount > 0) return { chip: `${freeCount} free`, tone: 'ok' };
    return { chip: 'full', tone: 'accent' };
  });
  const slash = $derived(pool.key.indexOf('/'));
  const name = $derived(agent ? pool.key : slash > 0 ? pool.key.slice(slash + 1) : pool.key);
  // the second line: the model (the provider, for another model call); why it cannot take work when it cannot
  const second = $derived.by(() => {
    if (!usable && !pool.disabled && pool.unavailable && pool.unavailable !== 'removed') return pool.unavailable;
    return agent ? shortModel(pool.model) : slash > 0 ? pool.key.slice(0, slash) : '';
  });

  const runOf = (o) => agentById.get(o.agentId);
  const titleOf = (o) => titles?.get(o.sessionId ?? runOf(o)?.sessionId);
  // top-level agents are all called "main": their chat title tells them apart; a subagent goes by its name
  const who = (o) => {
    const run = runOf(o);
    if (run?.isSubagent) return run.name ?? o.label ?? o.agentId;
    return titleOf(o) ?? run?.name ?? o.label ?? o.agentId;
  };
  const forWhom = (o) => {
    const run = runOf(o);
    if (!run?.isSubagent || !run.parentAgentId) return '';
    return titles?.get(agentById.get(run.parentAgentId)?.sessionId) ?? '';
  };
  const doing = (o) => runOf(o)?.activity || runOf(o)?.status || o.label || 'running';
  const tip = (o) => {
    const run = runOf(o);
    const bits = [who(o), forWhom(o) && `for ${forWhom(o)}`, run?.task, run && `${shortModel(run.model)} · ${run.turns ?? 0} turns · ${run.toolCalls ?? 0} tools`];
    return bits.filter(Boolean).join('\n') + '\n(open session)';
  };

  const oldestWait = $derived(waiters.reduce((min, w) => (!min || Date.parse(w.since) < Date.parse(min) ? w.since : min), null));
  const waitItems = $derived(
    waiters
      .slice()
      .sort((a, b) => (Date.parse(a.since) || 0) - (Date.parse(b.since) || 0))
      .map((w) => ({
        label: titles?.get(w.sessionId ?? agentById.get(w.agentId)?.sessionId) ?? agentById.get(w.agentId)?.name ?? w.label ?? w.agentId,
        hint: w.since ? waited(w.since) : '',
        onclick: () => w.sessionId && ctx.app.openSession(w.sessionId),
      })),
  );
  function waited(since) {
    const s = Math.max(0, Math.floor((Date.now() - Date.parse(since)) / 1000));
    return s < 90 ? `${s}s` : s < 5400 ? `${Math.round(s / 60)}m` : `${Math.round(s / 3600)}h`;
  }

  let toggling = $state(false);
  async function toggle(e) {
    const enabled = e.currentTarget.checked;
    toggling = true;
    try {
      await ctx.rpc('agents.setEnabled', { id: pool.key, enabled });
    } catch (err) {
      e.currentTarget.checked = !enabled;
      ctx.app.toast(err?.message ?? String(err), 'error');
    } finally {
      toggling = false;
    }
  }

  let stopping = $state(null);
  async function stop(e, o) {
    e?.stopPropagation();
    const sessionId = o.sessionId ?? runOf(o)?.sessionId;
    const label = who(o);
    stopping = o.agentId;
    try {
      const ok = await ctx.rpc('agent.abort', { sessionId });
      if (!ok) ctx.app.toast(`${label} was not running`, 'warn');
    } catch (err) {
      ctx.app.toast(`Abort failed: ${err.message}`, 'error');
    } finally {
      stopping = null;
    }
  }
</script>

<div class="pool" class:inactive={!usable} data-agent={agent ? pool.key : null}>
  <div
    class="head np-line"
    title="{agent ? `Agent ${pool.key} on ${pool.model}` : pool.key} — {busyCount}/{pool.capacity} busy{waiters.length ? `, ${waiters.length} waiting` : ''}{pool.use ? `\n${pool.use}` : ''}"
  >
    {#if agent}
      <button class="key np-mono np-grow" title="Open in Settings → Agents" onclick={() => ctx.app.openSettings?.('agents', pool.key)}><b>{name}</b></button>
    {:else}
      <span class="key np-mono np-grow"><b>{name}</b></span>
    {/if}
    {#if usable && capacity > 0}<Pips busy={busyCount} {capacity} max={8} />{/if}
    {#if agent}
      <input
        type="checkbox"
        class="np-switch"
        checked={!pool.disabled}
        disabled={toggling || ctx.hasRpc?.('agents.setEnabled') === false}
        onchange={toggle}
        aria-label="Agent {pool.key} on"
        title={pool.disabled ? 'Switched off: switch it on to let chats and subagents run on it' : 'Switch off: runs on it finish, new ones are refused'}
      />
    {/if}
  </div>
  <div class="cap np-line">
    <span class="second np-mono np-grow" title={second}>{second}</span>
    <span class="st" data-tone={look.tone}>{look.chip}</span>
  </div>

  {#each rows as o, i (i)}
    {#if o}
      <div
        class="slot busy np-hover-row"
        class:compact
        role="button"
        tabindex="0"
        title={tip(o)}
        onclick={() => (o.sessionId ?? runOf(o)?.sessionId) && ctx.app.openSession(o.sessionId ?? runOf(o).sessionId)}
        onkeydown={(e) => e.key === 'Enter' && (o.sessionId ?? runOf(o)?.sessionId) && ctx.app.openSession(o.sessionId ?? runOf(o).sessionId)}
      >
        <span class="dot"></span>
        <span class="t">
          <span class="l1 np-line np-baseline">
            <span class="name">{who(o)}</span>
            {#if runOf(o)?.isSubagent}<span class="sub">sub</span>{/if}
            {#if forWhom(o)}<span class="for np-grow">for {forWhom(o)}</span>{/if}
            {#if compact}<Elapsed since={o.since} class="np-mono el" />{/if}
          </span>
          {#if !compact}
            <span class="l2 np-line np-baseline">
              <span class="act np-grow">{doing(o)}</span>
              <Elapsed since={o.since} class="np-mono el" />
            </span>
          {/if}
        </span>
        <span class="np-hover-actions"
          ><IconButton icon="stop" title="Stop ({who(o)})" size="sm" disabled={stopping === o.agentId || ctx.hasRpc?.('agent.abort') === false} onclick={(e) => stop(e, o)} /></span
        >
      </div>
    {:else}
      <div class="slot free" class:compact>
        <span class="dot"></span>
        <span class="t">
          <span class="l1">free</span>
          {#if !compact}<span class="l2">ready for work</span>{/if}
        </span>
      </div>
    {/if}
  {/each}

  {#if usable}
    <div class="wait">
      {#if waiters.length}
        <Menu items={waitItems} minWidth={240} placement="bottom-start">
          {#snippet trigger({ toggle, open })}
            <button class="waiting np-line" aria-expanded={open} title="Who is waiting for a free instance" onclick={toggle}>
              <Icon name={open ? 'chevron-down' : 'chevron-right'} size={12} />
              <span class="np-grow np-ellipsis">{waiters.length} waiting<span class="np-dim">, longest </span><Elapsed since={oldestWait} class="np-mono" /></span>
            </button>
          {/snippet}
        </Menu>
      {:else}
        <span class="nobody np-line">no one waiting</span>
      {/if}
    </div>
  {/if}
</div>

<style>
  .pool {
    padding: 6px 0 6px;
  }
  .pool + :global(.pool) {
    border-top: 1px dashed var(--border);
  }
  .head {
    min-height: 20px;
    gap: 8px;
  }
  .key {
    font-size: 12px;
    color: var(--fg);
    padding: 0;
    border: 0;
    background: none;
    font: inherit;
    text-align: left;
    cursor: default;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  button.key {
    cursor: pointer;
  }
  button.key:hover {
    text-decoration: underline;
  }
  .key b {
    font-weight: 600;
  }
  .inactive .key b {
    color: var(--fg-muted);
  }
  .cap {
    margin: 1px 0 4px;
    gap: 8px;
    min-height: 18px;
  }
  .second {
    min-width: 0;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
    color: var(--fg-dim);
    font-size: 11px;
  }
  .inactive .second {
    font-family: var(--font-sans);
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
  .st[data-tone='ok'] {
    color: var(--ok);
  }

  /* a slot: the same height busy or free, so the tab does not move when work starts or ends */
  .slot {
    display: flex;
    align-items: center;
    gap: 8px;
    box-sizing: border-box;
    height: 40px;
    margin: 3px 0 0 6px;
    padding: 0 6px 0 8px;
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--bg-1);
    min-width: 0;
    overflow: hidden;
  }
  .slot.compact {
    height: 24px;
  }
  .slot.busy {
    cursor: pointer;
    outline: none;
  }
  .slot.busy:hover,
  .slot.busy:focus-within {
    --row-bg: var(--bg-2);
    background: var(--bg-2);
  }
  .slot.busy:focus-visible {
    box-shadow: 0 0 0 2px var(--accent-line);
  }
  .slot.free {
    border-style: dashed;
    border-color: var(--border-strong);
    background: transparent;
  }
  .dot {
    flex: none;
    width: 7px;
    height: 7px;
    border-radius: 50%;
    background: var(--accent);
  }
  .free .dot {
    background: transparent;
    box-shadow: inset 0 0 0 1.5px var(--ok);
  }
  .t {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    justify-content: center;
  }
  .l1,
  .l2 {
    gap: 6px;
    min-width: 0;
    line-height: 1.3;
  }
  .l1 {
    font-size: var(--fs-sm);
  }
  .l2 {
    font-size: var(--fs-xs);
    color: var(--fg-muted);
  }
  .name {
    flex: 0 1 auto;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    font-weight: 500;
  }
  .sub {
    flex: none;
    font-size: 10px;
    padding: 0 4px;
    border-radius: 3px;
    background: var(--bg-3);
    color: var(--fg-dim);
  }
  .for {
    min-width: 0;
    color: var(--fg-dim);
    font-size: var(--fs-xs);
  }
  .free .l1 {
    color: var(--ok);
  }
  .free .l2 {
    color: var(--fg-dim);
  }
  .act {
    min-width: 0;
  }
  .slot :global(.el) {
    flex: none;
    font-size: 11px;
    color: var(--fg-dim);
    white-space: nowrap;
  }

  /* one row for the queue, always there, so a waiting run appearing or leaving moves nothing */
  .wait {
    margin: 3px 0 0 6px;
    min-height: 22px;
    display: flex;
    align-items: center;
  }
  .wait :global(.np-menu-anchor) {
    display: flex;
    width: 100%;
  }
  .waiting {
    gap: 4px;
    width: 100%;
    min-height: 22px;
    padding: 0 4px;
    border: 0;
    border-radius: 4px;
    background: transparent;
    color: var(--warn);
    font: inherit;
    font-size: var(--fs-xs);
    text-align: left;
    cursor: pointer;
  }
  .waiting:hover {
    background: var(--bg-2);
  }
  .waiting :global(.np-mono) {
    font-size: 11px;
  }
  .nobody {
    padding: 0 4px;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
  }
</style>
