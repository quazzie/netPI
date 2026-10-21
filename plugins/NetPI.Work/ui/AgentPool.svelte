<script>
  /**
   * One agent in the Work tab: a model with a number of instances ("slots"). It always draws one row per slot, the same
   * height busy or free, and a running job keeps its row (placeSlots), so nothing in the tab moves when work starts or
   * ends. A busy row says who holds the slot and what it is doing (and its priority, when it is not normal); a free one
   * says so. Under the rows a single line says who is queued (the names open in a menu, which floats instead of pushing
   * the rest down). Model calls without an
   * agent (a summarizer on another model) show the same way but without the switch.
   */
  import { Elapsed, IconButton } from '@netpi/kit';
  import { shortModel, shortCommand, placeSlots, priorityLevel } from './util.js';
  import WaitList from './WaitList.svelte';

  // cmds: the foreground commands running now, by chat (a busy row shows its chat's command)
  let { pool, agentById, titles, ctx, cmds = new Map() } = $props();

  const agent = $derived(!!pool.configured);
  const usable = $derived(pool.available !== false && !pool.disabled);
  const capacity = $derived(usable ? (pool.capacity ?? 0) : 0);
  const owners = $derived(pool.owners ?? []);
  const waiters = $derived(pool.waiters ?? []);
  const busyCount = $derived(owners.length);
  // one-line rows once an agent has many instances (the tab would be all slots otherwise)
  const compact = $derived(capacity > 4);
  // only an agent that is off (and not finishing runs) collapses to one line; the others show their work and one free box
  const off = $derived(!usable && busyCount === 0 && waiters.length === 0);
  const freeCount = $derived(Math.max(0, capacity - busyCount));

  // a job keeps its place in the order (placeSlots); a finished one just leaves, and the free box stands for all free instances
  let placed = [];
  const rows = $derived.by(() => (placed = placeSlots(placed, owners, capacity)));
  const busyRows = $derived(rows.filter(Boolean));

  const slash = $derived(pool.key.indexOf('/'));
  const name = $derived(agent ? pool.key : slash > 0 ? pool.key.slice(slash + 1) : pool.key);
  // the second line: why it cannot take work, when it cannot (the model is in the agent's dialog)
  const second = $derived(!usable && !pool.disabled && pool.unavailable && pool.unavailable !== 'removed' ? pool.unavailable : agent ? '' : slash > 0 ? pool.key.slice(0, slash) : '');

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
  const prio = (o) => priorityLevel(o.priority);
  const doing = (o) => runOf(o)?.activity || runOf(o)?.status || o.label || 'running';
  // a running shell command says more than "tool: bash": the row shows it where the agent is
  const cmdsOf = (o) => cmds.get(o.sessionId ?? runOf(o)?.sessionId) ?? [];

  const tip = (o) => {
    const run = runOf(o);
    const bits = [who(o), runOf(o)?.isSubagent && 'subagent', prio(o) !== 'normal' && `priority ${prio(o)}`, forWhom(o) && `for ${forWhom(o)}`, run?.task, run && `${shortModel(run.model)} · ${run.turns ?? 0} turns · ${run.toolCalls ?? 0} tools`];
    return bits.filter(Boolean).join('\n') + '\n(open session)';
  };

  // a waiting run goes by its chat title, like a busy one
  const waiterLabel = (w) => titles?.get(w.sessionId ?? agentById.get(w.agentId)?.sessionId) ?? agentById.get(w.agentId)?.name ?? w.label ?? w.agentId;

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
      const ok = await ctx.rpc('agent.abort', { sessionId, subagents: !!e?.shiftKey }); // like the chat's Stop: Shift stops its subagents too
      if (!ok) ctx.app.toast(`${label} was not running`, 'warn');
    } catch (err) {
      ctx.app.toast(`Abort failed: ${err.message}`, 'error');
    } finally {
      stopping = null;
    }
  }
</script>

<div class="pool" class:inactive={!usable} class:off data-agent={agent ? pool.key : null}>
  <div
    class="head np-line"
    title="{pool.key} — {busyCount}/{pool.capacity} busy{waiters.length ? `, ${waiters.length} queued` : ''}"
  >
    {#if agent}
      <button class="key np-mono np-grow" title="Open agent {pool.key}" onclick={() => ctx.app.openAgent?.(pool.key)}><b>{name}</b></button>
    {:else}
      <span class="key np-mono np-grow"><b>{name}</b></span>
    {/if}
    {#if off}
      <span class="state np-mono">{pool.disabled ? 'off' : 'unavailable'}</span>
    {/if}
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
  {#if !off}
  {#if second}<div class="cap np-line">
    <span class="second np-mono np-grow" title={second}>{second}</span>
  </div>{/if}

  {#each busyRows as o (o.leaseId ?? `${o.agentId}@${o.since}`)}
    {#if o}
      <div
        class="slot busy np-hover-row"
        data-testid="slot"
        data-state="busy"
        data-prio={prio(o)}
        data-session={o.sessionId ?? runOf(o)?.sessionId}
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
            <span class="name np-grow">{who(o)}</span>
            {#if compact && runOf(o)?.isSubagent}<span class="arrow" title="subagent">↳</span>{/if}
            {#if compact && forWhom(o)}<span class="for">for {forWhom(o)}</span>{/if}
            <Elapsed since={o.since} class="np-mono el" />
          </span>
          {#if !compact}
            <span class="l2 np-line np-baseline">
              {#if runOf(o)?.isSubagent}<span class="arrow" title="subagent">↳</span>{/if}
              {#if forWhom(o)}<span class="for" title="for {forWhom(o)}">for {forWhom(o)} ·</span>{/if}
              {#if cmdsOf(o).length}
                <span class="act cmd np-mono np-grow" title={cmdsOf(o)[0].command}>$ {shortCommand(cmdsOf(o)[0].command)}{#if cmdsOf(o).length > 1}<span class="np-dim"> +{cmdsOf(o).length - 1}</span>{/if}</span>
              {:else}
                <span class="act np-grow">{doing(o)}</span>
              {/if}
            </span>
          {/if}
        </span>
        <span class="np-hover-actions"
          ><IconButton icon="stop" title="Stop ({who(o)}); its subagents keep working. Shift: stop them too" size="sm" disabled={stopping === o.agentId || ctx.hasRpc?.('agent.abort') === false} onclick={(e) => stop(e, o)} /></span
        >
      </div>
    {/if}
  {/each}
  {#if freeCount > 0}
    <div class="slot free" data-testid="slot" data-state="free" title="{freeCount} of {capacity} instances free">
      <span class="dot"></span>
      <span class="l1">{freeCount} free</span>
    </div>
  {/if}

  {#if usable && waiters.length}
    <div class="wait">
      <WaitList {waiters} label={waiterLabel} {ctx} />
    </div>
  {/if}
  {/if}
</div>

<style>
  .pool {
    padding: 6px 0 6px;
  }
  .pool.off {
    opacity: 0.6;
  }
  .state {
    flex: none;
    font-size: 11px;
    color: var(--fg-dim);
  }
  .state.ok {
    color: var(--ok);
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

  /* a slot: the same height busy or free, so the tab does not move when work starts or ends */
  .slot {
    display: flex;
    align-items: center;
    gap: 8px;
    box-sizing: border-box;
    height: 38px; /* two lines: the title, what it is doing */
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
  .slot.free {
    height: 26px;
  }
  /* priority is the row's left edge, not a chip that takes the title's room */
  .slot[data-prio='high'] {
    border-left: 3px solid var(--warn);
  }
  .slot[data-prio='critical'] {
    border-left: 3px solid var(--err);
  }
  .slot[data-prio='background'] {
    border-left: 3px dotted var(--fg-dim);
    opacity: 0.8;
  }
  .slot[data-prio='low'] {
    border-left: 3px dotted var(--fg-dim);
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
    line-height: 1.25;
  }
  .l1 {
    font-size: var(--fs-sm);
  }
  .l2 {
    font-size: var(--fs-xs);
    color: var(--fg-muted);
  }
  .name {
    flex: 1 0 8ch; /* the title never gives way to the chips */
    min-width: 0;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    font-weight: 500;
  }
  .arrow {
    flex: none;
    color: var(--fg-dim);
  }
  .for {
    flex: 0 1 auto;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
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

  /* one row for the queue, always there, so a queued run appearing or leaving moves nothing */
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
  .nobody {
    padding: 0 4px;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
  }
  .cmd {
    font-size: 11px;
    color: var(--fg-dim);
  }
</style>
