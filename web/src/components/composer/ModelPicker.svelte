<script>
  import Icon from '../../lib/kit/Icon.svelte';
  import Popover from '../Popover.svelte';
  import { app, updateSession, loadModels, sessionModelRef } from '../../lib/state/app.svelte.js';
  import { tokens } from '../../lib/format.js';

  /** Model picker grouped by provider (status dot, context window, concurrency). Sets sessions.update { model }. */
  let { session, open = $bindable(false) } = $props();

  let btn = $state();
  let q = $state('');
  let index = $state(0);
  let refreshing = $state(false);

  const current = $derived(sessionModelRef(session));
  const currentModel = $derived(current ? app.modelsByRef.get(current) : null);
  const label = $derived(currentModel?.displayName || current?.split('/').pop() || 'No model');

  const refOf = (m) => m.ref ?? `${m.provider}/${m.id}`;
  const filtered = $derived.by(() => {
    const query = q.trim().toLowerCase();
    return app.models.filter(
      (m) => !query || refOf(m).toLowerCase().includes(query) || (m.displayName ?? '').toLowerCase().includes(query),
    );
  });
  const groups = $derived.by(() => {
    const map = new Map();
    for (const m of filtered) {
      let g = map.get(m.provider);
      if (!g) map.set(m.provider, (g = []));
      g.push(m);
    }
    return [...map].map(([provider, models]) => ({ provider, models }));
  });
  const flat = $derived(groups.flatMap((g) => g.models));

  $effect(() => {
    if (open) {
      q = '';
      index = Math.max(0, flat.findIndex((m) => refOf(m) === current));
    }
  });

  async function choose(m) {
    open = false;
    if (refOf(m) !== session.model) await updateSession(session.id, { model: refOf(m) });
  }

  async function refresh() {
    refreshing = true;
    await loadModels(true);
    refreshing = false;
  }

  function onKey(e) {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      index = Math.min(flat.length - 1, index + 1);
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      index = Math.max(0, index - 1);
    } else if (e.key === 'Enter') {
      e.preventDefault();
      if (flat[index]) choose(flat[index]);
    }
  }

  function focus(node) {
    node.focus();
  }
  function scrollIntoView(node, active) {
    if (active) node.scrollIntoView({ block: 'nearest' });
    return { update: (a) => a && node.scrollIntoView({ block: 'nearest' }) };
  }
  const statusOf = (m) => m.status ?? (m.isLocal ? 'unloaded' : 'available');
</script>

<button class="pick" bind:this={btn} onclick={() => (open = !open)} title="Model: {current ?? 'default'}" aria-haspopup="listbox">
  {#if currentModel}<span class="np-dot" data-status={statusOf(currentModel)}></span>{:else}<Icon name="cpu" size={13} />{/if}
  <span class="np-ellipsis">{label}</span>
  <Icon name="chevron-down" size={11} />
</button>

{#if open}
  <Popover anchor={btn} placement="top-start" width={380} onclose={() => (open = false)}>
    <div class="head">
      <Icon name="search" size={13} />
      <input class="filter" placeholder="Filter models" bind:value={q} onkeydown={onKey} use:focus spellcheck="false" />
      <button class="np-icon-btn np-btn-sm" title="Refresh model list" onclick={refresh} disabled={refreshing}>
        {#if refreshing}<span class="np-spinner"></span>{:else}<Icon name="refresh" size={13} />{/if}
      </button>
    </div>
    <div class="list np-scroll" role="listbox">
      {#each groups as g (g.provider)}
        <div class="provider">{g.provider}</div>
        {#each g.models as m (refOf(m))}
          {@const i = flat.indexOf(m)}
          <button
            class="model"
            class:active={i === index}
            class:current={refOf(m) === current}
            role="option"
            aria-selected={refOf(m) === current}
            onclick={() => choose(m)}
            onmouseenter={() => (index = i)}
            use:scrollIntoView={i === index}
          >
            <span class="np-dot" data-status={statusOf(m)} title={statusOf(m)}></span>
            <span class="name">
              <span class="np-ellipsis">{m.displayName || m.id}</span>
              {#if m.displayName && m.displayName !== m.id}<span class="id np-mono np-ellipsis">{m.id}</span>{/if}
            </span>
            <span class="meta np-mono">
              {#if m.reasoning?.supported}<span title="Reasoning"><Icon name="brain" size={11} /></span>{/if}
              {#if m.inputModalities?.includes('image')}<span title="Images"><Icon name="image" size={11} /></span>{/if}
              {#if m.concurrency}<span title="Parallel slots">×{m.concurrency}</span>{/if}
              {#if m.contextWindow}<span title="Context window">{tokens(m.contextWindow)}</span>{/if}
            </span>
            {#if refOf(m) === current}<Icon name="check" size={13} />{/if}
          </button>
        {/each}
      {:else}
        <div class="np-empty">No models{q ? ` match “${q}”` : ''}</div>
      {/each}
    </div>
    {#if app.defaultModel}
      <div class="foot np-dim">default: <span class="np-mono">{app.defaultModel}</span></div>
    {/if}
  </Popover>
{/if}

<style>
  .pick {
    display: flex;
    align-items: center;
    gap: 6px;
    max-width: 220px;
    height: 26px;
    padding: 0 8px;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--fg-muted);
    font-size: var(--fs-sm);
  }
  .pick:hover {
    background: var(--bg-3);
    color: var(--fg);
  }
  .head {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 6px 6px 6px 10px;
    border-bottom: 1px solid var(--border);
    color: var(--fg-dim);
  }
  .filter {
    flex: 1;
    border: 0;
    outline: none;
    background: transparent;
    color: var(--fg);
    height: 24px;
  }
  .list {
    padding: 4px;
    max-height: 380px;
  }
  .provider {
    padding: 8px 8px 3px;
    font-size: var(--fs-xs);
    font-weight: 600;
    color: var(--fg-dim);
    text-transform: uppercase;
    letter-spacing: 0.05em;
  }
  .model {
    display: flex;
    align-items: center;
    gap: 9px;
    width: 100%;
    min-height: 34px;
    padding: 4px 8px;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--fg);
    text-align: left;
  }
  .model.active {
    background: var(--bg-3);
  }
  .model.current {
    color: var(--accent);
  }
  .name {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    line-height: 1.25;
  }
  .id {
    font-size: 10.5px;
    color: var(--fg-dim);
  }
  .meta {
    display: flex;
    align-items: center;
    gap: 8px;
    color: var(--fg-dim);
    font-size: 11px;
    flex: none;
  }
  .meta span {
    display: inline-flex;
  }
  .foot {
    padding: 6px 12px;
    border-top: 1px solid var(--border);
    font-size: var(--fs-xs);
  }
</style>
