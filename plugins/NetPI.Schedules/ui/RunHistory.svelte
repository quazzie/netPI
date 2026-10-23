<script>
  // A schedule's runs, newest first: what ran, and why a run was skipped. Loaded on first expand, re-fetched
  // when the tab refreshes while it is open (refreshKey changes), "Show more" steps 20 → 50.
  import { timeAgo } from '@netpi/kit';

  let { ctx, scheduleId, refreshKey = null } = $props();
  let runs = $state.raw(null);
  let limit = $state(20);
  let more = $state(false);
  let error = $state('');

  async function load() {
    try {
      runs = (await ctx.rpc('schedules.history', { id: scheduleId, limit })) ?? [];
      more = runs.length >= limit;
      error = '';
    } catch (e) {
      error = String(e?.message ?? e);
      runs ??= [];
    }
  }

  $effect(() => {
    if (refreshKey != null) load();
  });
</script>

<div class="history">
  <div class="h-title np-small np-muted">Runs</div>
  {#if runs === null}
    <div class="np-small np-muted">loading…</div>
  {:else if error}
    <div class="np-small h-error">Could not load the runs: {error}</div>
  {:else if runs.length === 0}
    <div class="np-small np-muted">No runs yet</div>
  {:else}
    {#each runs as h (h.at + h.status)}
      <div class="h-row np-line" title={h.reason ?? ''}>
        <span class="np-mono h-when np-small">{timeAgo(h.at)}</span>
        <span class="h-status" data-status={h.status}>{h.status}</span>
        {#if h.reason}<span class="np-ellipsis np-grow np-small np-muted">{h.reason}</span>{/if}
        {#if h.sessionId}
          <button class="h-open np-small" title="Open the chat this run started"
            onclick={() => ctx.app.openSession?.(h.sessionId)}>chat</button>
        {/if}
      </div>
    {/each}
    {#if more}
      <button class="np-small h-more" onclick={() => { limit = 50; load(); }}>Show more</button>
    {/if}
  {/if}
</div>

<style>
  .history {
    display: flex;
    flex-direction: column;
    gap: 2px;
  }
  .h-title {
    text-transform: uppercase;
    letter-spacing: 0.04em;
    opacity: 0.6;
  }
  .h-row {
    gap: 6px;
    padding: 1px 0;
  }
  .h-when {
    flex: none;
    min-width: 52px;
    opacity: 0.75;
  }
  .h-status {
    flex: none;
    font-size: 11px;
    text-transform: capitalize;
  }
  .h-status[data-status='ran'] { color: var(--np-ok, #4caf7d); }
  .h-status[data-status='skipped'] { color: var(--np-warn, #d9a441); }
  .h-status[data-status='failed'] { color: var(--np-err, #e05555); }
  .h-status[data-status='missed'] { color: var(--np-warn, #d9a441); }
  .h-error { color: var(--np-err, #e05555); }
  .h-open {
    flex: none;
    background: none;
    border: none;
    color: var(--np-accent, #6aa3f0);
    cursor: pointer;
    padding: 0;
    font: inherit;
  }
  .h-more {
    background: none;
    border: none;
    color: var(--np-accent, #6aa3f0);
    cursor: pointer;
    padding: 2px 0;
    font: inherit;
    text-align: left;
  }
</style>
