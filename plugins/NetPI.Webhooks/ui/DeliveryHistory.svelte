<script>
  // A webhook's deliveries, newest first: what started a chat, and why a delivery was skipped. Loaded on first expand and
  // re-fetched when the tab refreshes while it is open (refreshKey changes).
  import { timeAgo } from '@netpi/kit';

  let { ctx, webhookId, refreshKey = null } = $props();
  let runs = $state.raw(null);
  let limit = $state(15);
  let more = $state(false);

  async function load() {
    try {
      runs = (await ctx.rpc('webhooks.history', { id: webhookId, limit })) ?? [];
      more = runs.length >= limit;
    } catch {
      runs = [];
    }
  }

  $effect(() => {
    if (refreshKey != null) load();
  });
</script>

<div class="history">
  <div class="h-title np-small np-muted">Deliveries</div>
  {#if runs === null}
    <div class="np-small np-muted">loading…</div>
  {:else if runs.length === 0}
    <div class="np-small np-muted">None yet</div>
  {:else}
    {#each runs as h (h.at + h.status + (h.sessionId ?? '') + (h.summary ?? ''))}
      <div class="h-row" title={[h.summary, h.reason, h.from].filter(Boolean).join(' · ')}>
        <div class="np-line">
          <span class="np-mono h-when np-small">{timeAgo(h.at)}</span>
          <span class="h-status" data-status={h.status}>{h.status}</span>
          <span class="np-ellipsis np-grow np-small">{h.summary ?? ''}</span>
          {#if h.sessionId}
            <button class="h-open np-small" title="Open the chat this delivery started"
              onclick={() => ctx.app.openSession?.(h.sessionId)}>chat</button>
          {/if}
        </div>
        {#if h.reason}<div class="np-small np-muted h-reason">{h.reason}</div>{/if}
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
    gap: 3px;
  }
  .h-title {
    text-transform: uppercase;
    letter-spacing: 0.04em;
    opacity: 0.6;
  }
  .h-row {
    display: flex;
    flex-direction: column;
  }
  .np-line {
    gap: 6px;
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
  .h-status[data-status='started'],
  .h-status[data-status='appended'] { color: var(--np-ok, #4caf7d); }
  .h-status[data-status='held'] { color: var(--np-accent, #6aa3f0); }
  .h-status[data-status='duplicate'] { opacity: 0.6; }
  .h-status[data-status='skipped'] { color: var(--np-warn, #d9a441); }
  .h-status[data-status='failed'] { color: var(--np-err, #e05555); }
  .h-reason {
    padding-left: 58px;
    word-break: break-word;
  }
  .h-open,
  .h-more {
    background: none;
    border: none;
    color: var(--np-accent, #6aa3f0);
    cursor: pointer;
    padding: 0;
    font: inherit;
    text-align: left;
  }
</style>
