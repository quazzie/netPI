<script>
  import { Pips, Elapsed, StatusDot } from '@netpi/kit';
  import { shortModel } from './util.js';

  let { pool, agentById, models, ctx } = $props();

  const TONE = { queued: 'warn', full: 'accent', busy: 'accent', idle: 'dim', offline: 'err', stopped: 'warn' };
  const who = (o) => agentById.get(o.agentId)?.name ?? o.label ?? o.agentId;
  const statusOf = (ref) => models.get(ref)?.status ?? models.get(`${pool.provider}/${ref}`)?.status ?? null;
</script>

<div class="pool">
  <div class="head">
    <span class="key np-mono" title={pool.key}>{pool.key}</span>
    <Pips busy={pool.busy} capacity={pool.capacity} queued={pool.queued} />
    <span class="np-spacer"></span>
    <span class="nums np-mono">{pool.busy}/{pool.capacity}</span>
    {#if pool.status}<span class="st" data-tone={TONE[pool.status] ?? 'dim'}>{pool.status}</span>{/if}
  </div>
  {#if pool.models?.length}
    <div class="models">
      {#each pool.models as m (m)}
        {@const st = statusOf(m)}
        <span class="model" title="{m}{st ? ` — ${st}` : ''}">
          {#if st}<StatusDot status={st} />{/if}{shortModel(m)}
        </span>
      {/each}
      {#if pool.source && pool.source !== 'default'}<span class="src" title="capacity source">{pool.source}</span>{/if}
    </div>
  {/if}
  {#each pool.owners ?? [] as o (o.agentId + o.since)}
    <button class="owner" title="Open session" onclick={() => o.sessionId && ctx.app.openSession(o.sessionId)}>
      <span class="slot on"></span>
      <span class="name">{who(o)}</span>
      {#if o.label && o.label !== who(o)}<span class="label np-ellipsis">{o.label}</span>{/if}
      <span class="np-spacer"></span>
      <Elapsed since={o.since} class="np-mono el" />
    </button>
  {/each}
  {#each pool.waiters ?? [] as w (w.agentId + w.since)}
    <button class="owner waiting" title="Waiting for a free slot — open session" onclick={() => w.sessionId && ctx.app.openSession(w.sessionId)}>
      <span class="slot"></span>
      <span class="name">{who(w)}</span>
      <span class="label">waiting</span>
      <span class="np-spacer"></span>
      <Elapsed since={w.since} class="np-mono el" />
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
    display: flex;
    align-items: center;
    gap: 8px;
    min-width: 0;
  }
  .key {
    font-size: 12px;
    color: var(--fg);
    font-weight: 600;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    min-width: 0;
  }
  .nums {
    font-size: 11px;
    color: var(--fg-muted);
  }
  .st {
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
  .st[data-tone='err'] {
    background: var(--err-soft);
    color: var(--err);
  }
  .models {
    display: flex;
    flex-wrap: wrap;
    gap: 4px 10px;
    margin: 3px 0 2px;
    font-size: 11.5px;
    color: var(--fg-dim);
  }
  .model {
    display: inline-flex;
    align-items: center;
    gap: 5px;
    font-family: var(--font-mono);
  }
  .src {
    color: var(--fg-dim);
    opacity: 0.8;
  }
  .owner {
    display: flex;
    align-items: center;
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
    flex: none;
    font-weight: 500;
  }
  .label {
    color: var(--fg-dim);
    font-size: var(--fs-xs);
    min-width: 0;
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
