<script>
  /**
   * The waiting line of an agent ("4 waiting, longest 14m") and, opened, who waits. A floating list (position: fixed, so
   * opening it never pushes the tab down): each row opens its chat, shows how long it has waited, and has an x that
   * cancels that run (agent.abort: a run waiting for an instance is cancelled before it ever starts).
   */
  import { tick } from 'svelte';
  import { Elapsed, Icon, IconButton } from '@netpi/kit';

  // what: how the line says who they wait for ("for any agent"), when it is not the agent the line sits under
  let { waiters, label, ctx, what = '' } = $props();

  const sorted = $derived(waiters.slice().sort((a, b) => (Date.parse(a.since) || 0) - (Date.parse(b.since) || 0)));
  const oldest = $derived(sorted[0]?.since ?? null);

  let open = $state(false);
  let anchor = $state();
  let panel = $state();
  let x = $state(0);
  let y = $state(0);
  let cancelling = $state.raw(new Set());

  async function place() {
    if (!anchor?.isConnected) return;
    const r = anchor.getBoundingClientRect();
    x = r.left;
    y = r.bottom + 4;
    await tick();
    if (!panel) return;
    const p = panel.getBoundingClientRect();
    if (x + p.width > window.innerWidth - 6) x = Math.max(6, window.innerWidth - p.width - 6);
    if (y + p.height > window.innerHeight - 6) y = Math.max(6, r.top - p.height - 4);
  }
  function toggle() {
    open = !open;
    if (open) place();
  }

  $effect(() => {
    if (!open) return;
    const onDown = (e) => {
      if (panel?.contains(e.target) || anchor?.contains(e.target)) return;
      open = false;
    };
    const onKey = (e) => {
      if (e.key !== 'Escape') return;
      e.preventDefault();
      e.stopPropagation();
      open = false;
    };
    const onScroll = (e) => {
      if (!panel?.contains(e.target)) place();
    };
    const close = () => (open = false);
    window.addEventListener('pointerdown', onDown, true);
    window.addEventListener('keydown', onKey, true);
    window.addEventListener('scroll', onScroll, true);
    window.addEventListener('resize', onScroll);
    window.addEventListener('blur', close);
    return () => {
      window.removeEventListener('pointerdown', onDown, true);
      window.removeEventListener('keydown', onKey, true);
      window.removeEventListener('scroll', onScroll, true);
      window.removeEventListener('resize', onScroll);
      window.removeEventListener('blur', close);
    };
  });

  async function cancel(w) {
    const name = label(w);
    cancelling = new Set(cancelling).add(w.agentId);
    try {
      const ok = await ctx.rpc('agent.abort', { sessionId: w.sessionId });
      if (!ok) ctx.app.toast(`${name} was not waiting any more`, 'warn');
    } catch (err) {
      ctx.app.toast(`Cancel failed: ${err?.message ?? err}`, 'error');
    } finally {
      const next = new Set(cancelling);
      next.delete(w.agentId);
      cancelling = next;
    }
  }
</script>

<button bind:this={anchor} class="waiting np-line" aria-expanded={open} title="Who is waiting for a free instance" onclick={toggle}>
  <Icon name={open ? 'chevron-down' : 'chevron-right'} size={12} />
  <span class="np-grow np-ellipsis">{waiters.length} waiting{what ? ` ${what}` : ''}<span class="np-dim">, longest</span><Elapsed since={oldest} class="np-mono longest" /></span>
</button>

{#if open}
  <div class="pop" role="menu" bind:this={panel} style:left="{x}px" style:top="{y}px">
    {#each sorted as w (w.agentId + w.since)}
      <div class="row" role="menuitem">
        <button
          class="name np-ellipsis"
          title="Open {label(w)}"
          onclick={() => {
            if (w.sessionId) ctx.app.openSession(w.sessionId);
            open = false;
          }}>{label(w)}</button
        >
        <Elapsed since={w.since} class="np-mono t" />
        <IconButton icon="x" title="Cancel this run: it stops waiting and never starts" size="sm" disabled={cancelling.has(w.agentId) || ctx.hasRpc?.('agent.abort') === false} onclick={() => cancel(w)} />
      </div>
    {/each}
  </div>
{/if}

<style>
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
  /* the space after "longest" collapses at the end of an inline span, so the gap is padding */
  .waiting :global(.longest) {
    margin-left: 6px;
  }
  .pop {
    position: fixed;
    z-index: 70;
    min-width: 240px;
    max-width: calc(100vw - 12px);
    max-height: 60vh;
    overflow-y: auto;
    padding: 4px;
    border: 1px solid var(--border-strong);
    border-radius: var(--radius);
    background: var(--bg-1);
    box-shadow: var(--shadow);
  }
  .row {
    display: flex;
    align-items: center;
    gap: 6px;
    height: 28px;
    padding: 0 2px 0 6px;
    border-radius: 5px;
  }
  .row:hover {
    background: var(--bg-3);
  }
  .name {
    flex: 1;
    min-width: 0;
    padding: 0;
    border: 0;
    background: none;
    color: var(--fg);
    font: inherit;
    font-size: var(--fs);
    text-align: left;
    cursor: pointer;
  }
  .row :global(.t) {
    flex: none;
    font-size: 11px;
    color: var(--fg-dim);
  }
</style>
