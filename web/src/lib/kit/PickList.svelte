<script>
  /**
   * A list of names chosen in order: chips (a click opens Move first, Move up, Remove), "+ Add" with a menu of the
   * candidates not chosen yet, and, with `custom`, a field for a name the menu does not have (a model of another loadout,
   * a pattern like ha_get_*). Shared by the host UI and plugin tabs: the candidates are data.
   *   candidates: { value, label?, hint?, group? }[]   (group: a heading in the menu)
   *   value: string[] in order; onchange(string[])
   *   exclusive: values that stand alone (choosing one clears the others; choosing another clears it): "default", "none"
   *   max: at most this many (1: choosing replaces)
   */
  import Icon from './Icon.svelte';
  import Menu from './Menu.svelte';

  let {
    value = [],
    candidates = [],
    exclusive = [],
    max = 0,
    custom = true,
    placeholder = 'None',
    customPlaceholder = 'Other…',
    label = 'Choose',
    disabled = false,
    onchange = undefined,
  } = $props();

  let typed = $state('');
  const chosen = $derived(Array.isArray(value) ? value : []);
  const byValue = $derived(new Map(candidates.map((c) => [c.value, c])));
  const labelOf = (v) => byValue.get(v)?.label ?? v;

  function set(next) {
    onchange?.(next);
  }
  function add(v) {
    v = String(v ?? '').trim();
    if (!v || chosen.includes(v)) return;
    let next = exclusive.includes(v) ? [v] : [...chosen.filter((x) => !exclusive.includes(x)), v];
    if (max > 0 && next.length > max) next = next.slice(next.length - max);
    set(next);
  }
  const remove = (v) => set(chosen.filter((x) => x !== v));
  function move(v, to) {
    const rest = chosen.filter((x) => x !== v);
    rest.splice(Math.max(0, to), 0, v);
    set(rest);
  }
  function chipItems(v) {
    const i = chosen.indexOf(v);
    return [
      { header: v },
      ...(i > 0 ? [{ label: 'Move first', icon: 'arrow-up', onclick: () => move(v, 0) }, { label: 'Move up', icon: 'chevron-up', onclick: () => move(v, i - 1) }] : []),
      ...(i < chosen.length - 1 ? [{ label: 'Move down', icon: 'chevron-down', onclick: () => move(v, i + 1) }] : []),
      { label: 'Remove', icon: 'x', danger: true, onclick: () => remove(v) },
    ];
  }
  const addItems = $derived.by(() => {
    const out = [];
    let group = null;
    for (const c of candidates) {
      if (chosen.includes(c.value)) continue;
      if ((c.group ?? null) !== group) {
        group = c.group ?? null;
        if (group) out.push({ header: group });
      }
      out.push({ label: c.label ?? c.value, hint: c.hint, onclick: () => add(c.value) });
    }
    if (!out.length) out.push({ header: candidates.length ? 'All of them are chosen' : 'Nothing to choose from' });
    return out;
  });
  function onKey(e) {
    if (e.key === 'Enter') {
      e.preventDefault();
      add(typed);
      typed = '';
    }
  }
</script>

<div class="np-picklist" class:disabled role="group" aria-label={label} data-testid="pick-list">
  {#each chosen as v, i (v)}
    {@const c = byValue.get(v)}
    <Menu items={chipItems(v)} minWidth={170} placement="bottom-start">
      {#snippet trigger({ toggle })}
        <button type="button" class="chip" class:unknown={!c && candidates.length > 0} title={c?.hint ?? (candidates.length ? `${v} (not offered now)` : v)} onclick={toggle} {disabled} data-value={v}>
          {#if chosen.length > 1 && !exclusive.includes(v)}<span class="n">{i + 1}</span>{/if}
          <span class="np-ellipsis">{labelOf(v)}</span>
        </button>
      {/snippet}
    </Menu>
  {:else}
    <span class="none">{placeholder}</span>
  {/each}
  {#if !disabled && (max === 0 || chosen.length < max || max === 1)}
    <Menu items={addItems} minWidth={240} placement="bottom-start">
      {#snippet trigger({ toggle })}
        <button type="button" class="add" onclick={toggle} title="Add" aria-label="Add to {label}" data-testid="pick-add"><Icon name="plus" size={12} />{max === 1 && chosen.length ? 'Change' : 'Add'}</button>
      {/snippet}
    </Menu>
    {#if custom}
      <input class="np-input typed" bind:value={typed} placeholder={customPlaceholder} spellcheck="false" autocomplete="off" aria-label="Add a name to {label}" onkeydown={onKey} onblur={() => (add(typed), (typed = ''))} />
    {/if}
  {/if}
</div>

<style>
  .np-picklist {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 4px;
    width: 100%;
    min-width: 0;
  }
  .chip,
  .add {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    max-width: 260px;
    height: 24px;
    padding: 0 8px;
    border: 1px solid var(--border);
    border-radius: 12px;
    background: var(--bg-2);
    color: var(--fg);
    font-size: var(--fs-sm);
    cursor: pointer;
  }
  .chip.unknown {
    border-style: dashed;
    color: var(--fg-muted);
  }
  .chip:hover {
    border-color: var(--accent-line);
  }
  .n {
    color: var(--fg-dim);
    font-size: var(--fs-xs);
    font-variant-numeric: tabular-nums;
  }
  .add {
    background: transparent;
    color: var(--fg-muted);
  }
  .add:hover {
    color: var(--fg);
  }
  .none {
    color: var(--fg-dim);
    font-size: var(--fs-sm);
  }
  .typed {
    flex: 1 1 180px;
    min-width: 160px;
    max-width: 300px;
    height: 24px;
    font-size: var(--fs-sm);
  }
  .disabled {
    opacity: 0.6;
  }
</style>
