<script>
  import { onMount } from 'svelte';
  import { Empty, StatusDot, timeAgo } from '@netpi/kit';

  /**
   * Background idea checks (ideas.work): the Ideas plugin's waits, running work and recent outcomes — the save check,
   * recall, the verifier and the commit sweep. The last 10, newest first; the server keeps the last 40. Event-driven
   * (ideas.workChanged) with a slow poll while visible as the safety net (a plugin reload drops the subscription).
   */
  let { ctx, visible = true } = $props();

  let work = $state.raw(null);
  let error = $state('');
  let disposed = false;

  async function load() {
    try {
      const answer = await ctx.rpc('ideas.work');
      if (disposed) return;
      work = answer ?? [];
      error = '';
    } catch (e) {
      if (!disposed) error = e?.message ?? String(e);
    }
  }
  let pollTimer = 0;
  function poll() {
    if (disposed) return;
    clearTimeout(pollTimer);
    pollTimer = setTimeout(() => {
      if (disposed) return;
      if (visible) load();
      poll();
    }, 30_000);
  }
  onMount(() => {
    load();
    poll();
    const off = ctx.on('ideas.workChanged', (d) => (work = d?.work ?? null));
    return () => {
      disposed = true;
      clearTimeout(pollTimer);
      off();
    };
  });

  const shown = $derived((work ?? []).toReversed().slice(0, 10));
  const dot = (s) =>
    s === 'finished' || s === 'done'
      ? 'ok'
      : s === 'dropped' || s === 'failed'
        ? 'error'
        : s === 'skipped' || s === 'cancelled'
          ? 'cancelled'
          : 'running';
</script>

<div class="iv">
  {#if work == null && !error}
    <Empty><span class="np-spinner"></span></Empty>
  {:else if error && !work}
    <Empty icon="alert">ideas.work unavailable: {error}</Empty>
  {:else}
    {#each shown as w (w.id)}
      <div class="row" data-background-work={w.id}>
        <div class="top np-line">
          <StatusDot status={dot(w.status)} />
          <span class="purpose np-grow np-ellipsis">{w.purpose}</span>
          <span class="st">{w.status}</span>
        </div>
        <div class="sub np-line">
          <span class="np-grow np-ellipsis">{w.reason ?? w.model ?? ''}</span>
          {#if w.finishedAt}<span class="ago">{timeAgo(w.finishedAt)}</span>{/if}
        </div>
      </div>
    {:else}
      <Empty>No background idea checks{work?.length ? '' : ' since NetPI started'}</Empty>
    {/each}
  {/if}
</div>

<style>
  .iv {
    padding: 4px 10px 10px 12px;
    overflow-y: auto;
  }
  .row {
    padding: 4px;
    margin: 0 -4px;
    border-radius: var(--radius-sm);
  }
  .row:hover {
    background: var(--bg-2);
  }
  .top {
    gap: 7px;
  }
  .purpose {
    font-size: 12px;
    color: var(--fg);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    min-width: 0;
  }
  .st {
    flex: none;
    font-size: 11px;
    color: var(--fg-muted);
  }
  .sub {
    gap: 7px;
    margin: 1px 0 0 14px;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
  }
  .ago {
    flex: none;
  }
</style>
