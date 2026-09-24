<script>
  import { Pips, Elapsed, StatusDot } from '@netpi/kit';
  import { shortModel } from './util.js';

  let { pool, agentById, models, ctx } = $props();

  const TONE = { queued: 'warn', full: 'accent', busy: 'accent', idle: 'dim', offline: 'err', stopped: 'warn' };
  const who = (o) => agentById.get(o.agentId)?.name ?? o.label ?? o.agentId;
  const statusOf = (ref) => models.get(ref)?.status ?? models.get(`${pool.provider}/${ref}`)?.status ?? null;
  // "provider/model" pools read as "model provider" so truncation eats the provider, not the model
  const slash = $derived(pool.key.indexOf('/'));
  const name = $derived(slash > 0 ? pool.key.slice(slash + 1) : pool.key);
  const prov = $derived(slash > 0 ? pool.key.slice(0, slash) : '');
  // a pool that serves exactly its own model shows that model's status dot in the head instead of a models line
  const single = $derived(pool.models?.length === 1 && (pool.models[0] === pool.key || shortModel(pool.models[0]) === name) ? pool.models[0] : null);
</script>

<div class="pool">
  <div class="head np-line" title="{pool.key} — {pool.busy}/{pool.capacity} busy{pool.queued ? `, ${pool.queued} queued` : ''}{pool.source ? ` · capacity from ${pool.source}` : ''}">
    {#if single}<StatusDot status={statusOf(single) ?? 'idle'} title={statusOf(single) ?? 'unknown'} />{/if}
    <span class="key np-mono np-grow"><b>{name}</b>{#if prov}<span class="prov">{prov}</span>{/if}</span>
    {#if pool.status}<span class="st" data-tone={TONE[pool.status] ?? 'dim'}>{pool.status}</span>{/if}
  </div>
  <div class="cap np-line">
    <span class="nums np-mono">{pool.busy}/{pool.capacity}</span>
    <Pips busy={pool.busy} capacity={pool.capacity} queued={pool.queued} max={6} />
    {#if !single && pool.models?.length}
      <span class="models np-grow">
        {#each pool.models as m, i (m)}
          {@const st = statusOf(m)}
          <span class="model" title="{m}{st ? ` — ${st}` : ''}">{#if i}, {/if}{#if st}<StatusDot status={st} />{/if}{shortModel(m)}</span>
        {/each}
      </span>
    {/if}
  </div>
  {#each pool.owners ?? [] as o (o.agentId + o.since)}
    <button class="owner np-line" title="{who(o)}{o.label && o.label !== who(o) ? ` — ${o.label}` : ''} (open session)" onclick={() => o.sessionId && ctx.app.openSession(o.sessionId)}>
      <span class="slot on"></span>
      <span class="name">{who(o)}</span>
      <span class="label np-grow">{o.label && o.label !== who(o) ? o.label : ''}</span>
      <Elapsed since={o.since} class="np-mono el" />
    </button>
  {/each}
  {#each pool.waiters ?? [] as w (w.agentId + w.since)}
    <button class="owner waiting np-line" title="{who(w)} is waiting for a free slot (open session)" onclick={() => w.sessionId && ctx.app.openSession(w.sessionId)}>
      <span class="slot"></span>
      <span class="name">{who(w)}</span>
      <span class="label np-grow">waiting</span>
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
    min-height: 20px;
  }
  .key {
    font-size: 12px;
    color: var(--fg);
  }
  .key b {
    font-weight: 600;
  }
  .prov {
    margin-left: 6px;
    color: var(--fg-dim);
    font-size: 11px;
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
    font-size: 11px;
    color: var(--fg-dim);
    font-family: var(--font-mono);
  }
  .model :global(.np-dot) {
    margin-right: 4px;
    vertical-align: 1px;
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
  .owner > .name {
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
