<script>
  import { SearchInput, Empty, Icon } from '@netpi/kit';
  let { tools, error } = $props();
  let q = $state('');
  let showShadowed = $state(true);
  let open = $state.raw(new Set());

  const filtered = $derived(
    (tools ?? []).filter(
      (t) => (showShadowed || t.active) && (!q || `${t.name} ${t.label} ${t.pluginId} ${t.description}`.toLowerCase().includes(q.toLowerCase())),
    ),
  );
  const groups = $derived.by(() => {
    const m = new Map();
    for (const t of filtered) {
      const c = t.category || 'general';
      if (!m.has(c)) m.set(c, []);
      m.get(c).push(t);
    }
    for (const arr of m.values()) arr.sort((a, b) => a.name.localeCompare(b.name) || b.active - a.active);
    return [...m].sort((a, b) => a[0].localeCompare(b[0]));
  });
  const shadowed = $derived((tools ?? []).filter((t) => !t.active).length);
  // the owning plugin is shown under a tool only when several plugins register that name (else: tooltip)
  const dupNames = $derived.by(() => {
    const seen = new Map();
    for (const t of tools ?? []) seen.set(t.name, (seen.get(t.name) ?? 0) + 1);
    return new Set([...seen].filter(([, n]) => n > 1).map(([k]) => k));
  });
  function toggle(k) {
    const s = new Set(open);
    s.has(k) ? s.delete(k) : s.add(k);
    open = s;
  }
</script>

<div class="bar">
  <SearchInput bind:value={q} placeholder="Filter tools" />
  {#if shadowed}
    <button class="np-chip" aria-pressed={showShadowed} onclick={() => (showShadowed = !showShadowed)} title="Registrations overridden by a higher-priority tool of the same name">
      shadowed {shadowed}
    </button>
  {/if}
</div>
{#if !tools}
  <Empty icon="wrench">{error ?? 'No tools'}</Empty>
{:else}
  <div class="list">
    {#each groups as [cat, arr] (cat)}
      <div class="cat">{cat} <span class="n">{arr.length}</span></div>
      {#each arr as t (t.name + t.pluginId)}
        {@const k = t.name + '|' + t.pluginId}
        <div class="tool" class:shadowed={!t.active} class:disabled={t.disabled}>
          <button class="row" onclick={() => toggle(k)} aria-expanded={open.has(k)} title="{t.name} — {t.pluginId}">
            <span class="name np-mono">{t.name}</span>
            <span class="label">{t.label}</span>
            <span class="np-spacer"></span>
            {#if t.readOnly}<span class="b" title="read-only: may run in parallel">ro</span>{/if}
            {#if !t.active}<span class="b warn">shadowed</span>{/if}
            {#if t.disabled}<span class="b err">disabled</span>{/if}
            {#if t.priority}<span class="b" title="priority">p{t.priority}</span>{/if}
          </button>
          {#if dupNames.has(t.name) || open.has(k)}<div class="plug np-mono">{t.pluginId}</div>{/if}
          {#if open.has(k)}<div class="desc">{t.description}</div>{/if}
        </div>
      {/each}
    {:else}
      <Empty>No tools match “{q}”</Empty>
    {/each}
  </div>
{/if}

<style>
  .bar {
    display: flex;
    gap: 6px;
    padding: 4px 10px 4px 12px;
  }
  .bar :global(.np-search) {
    flex: 1;
  }
  .bar .np-chip {
    height: 28px;
    border-radius: var(--radius-sm);
  }
  .list {
    padding: 0 10px 10px 12px;
  }
  .cat {
    margin: 10px 0 2px;
    font-size: var(--fs-xs);
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.05em;
    color: var(--fg-dim);
  }
  .n {
    font-weight: 400;
  }
  .tool {
    padding: 2px 0 3px;
  }
  .row {
    display: flex;
    align-items: center;
    gap: 8px;
    width: calc(100% + 6px);
    min-height: 22px;
    padding: 0 3px;
    margin: 0 -3px;
    border: 0;
    border-radius: 4px;
    background: transparent;
    color: var(--fg);
    font: inherit;
    text-align: left;
    cursor: pointer;
  }
  .row:hover {
    background: var(--bg-2);
  }
  .name {
    font-size: 12px;
    font-weight: 600;
  }
  .label {
    color: var(--fg-dim);
    font-size: var(--fs-xs);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    min-width: 0;
  }
  .shadowed .name,
  .disabled .name {
    color: var(--fg-dim);
    text-decoration: line-through;
    text-decoration-color: color-mix(in srgb, var(--fg-dim) 60%, transparent);
  }
  .b {
    flex: none;
    padding: 0 5px;
    border-radius: 7px;
    background: var(--bg-3);
    color: var(--fg-muted);
    font-size: 10px;
    line-height: 15px;
  }
  .b.warn {
    background: var(--warn-soft);
    color: var(--warn);
  }
  .b.err {
    background: var(--err-soft);
    color: var(--err);
  }
  .plug {
    font-size: 10.5px;
    color: var(--fg-dim);
  }
  .desc {
    margin-top: 3px;
    font-size: var(--fs-sm);
    color: var(--fg-muted);
    line-height: 1.45;
  }
</style>
