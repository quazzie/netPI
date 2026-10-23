<script>
  /**
   * Choose an agent, or several in order: a button that names the choice and opens a menu of the special choices
   * (`options`: Default, Any, Any free…) and the agents, each with its model, free or paid, and whether it can take work
   * now. Shared by the host UI and plugin tabs, so it takes the agents as data: `agents.list`'s slots
   * ({ key, model, available, unavailable, disabled, free, configured? }); null means the list is unavailable, and a
   * text field takes keys instead (comma-separated when `multiple`).
   *   single:   value is '' / an option's value / an agent key; onchange(value)
   *   multiple: value is an array in the order chosen (options first-class too, e.g. 'free'); onchange(array)
   */
  import Icon from './Icon.svelte';
  import Menu from './Menu.svelte';

  let {
    agents = [],
    value = $bindable(),
    multiple = false,
    options = [],
    placeholder = 'Choose…',
    disabled = false,
    label = 'Agent',
    onchange = undefined,
  } = $props();

  const list = $derived((agents ?? []).filter((a) => a && a.key && a.configured !== false));
  const chosen = $derived(multiple ? (Array.isArray(value) ? value : []) : value ?? '');
  const short = (model) => String(model ?? '').split('/').pop();
  function hint(a) {
    const state = a.disabled ? 'off' : a.available === false ? (a.unavailable ? 'unavailable' : 'not active') : '';
    return [short(a.model), a.free ? 'free' : 'paid', state].filter(Boolean).join(' · ');
  }
  const nameOf = (v) => options.find((o) => o.value === v)?.label ?? v;
  const summary = $derived.by(() => {
    if (multiple) return chosen.length ? chosen.map(nameOf).join(', ') : placeholder;
    if (chosen === '' && !options.some((o) => o.value === '')) return placeholder;
    return nameOf(chosen);
  });
  const missing = $derived((multiple ? chosen : chosen ? [chosen] : []).filter((v) => !options.some((o) => o.value === v) && !list.some((a) => a.key === v)));

  function pick(v) {
    let next;
    if (multiple) next = chosen.includes(v) ? chosen.filter((x) => x !== v) : [...chosen, v];
    else next = v;
    value = next;
    onchange?.(next);
  }
  const on = (v) => (multiple ? chosen.includes(v) : chosen === v);
  const order = (v) => (multiple && chosen.length > 1 && chosen.includes(v) ? ` #${chosen.indexOf(v) + 1}` : '');

  const items = $derived([
    ...options.map((o) => ({ label: o.label + order(o.value), hint: o.hint, checked: on(o.value), keepOpen: multiple, onclick: () => pick(o.value) })),
    ...(options.length && list.length ? [{ divider: true }] : []),
    ...(list.length ? [{ header: multiple ? 'Agents (in the order chosen)' : 'Agents' }] : []),
    ...list.map((a) => ({ label: a.key + order(a.key), hint: hint(a), checked: on(a.key), keepOpen: multiple, onclick: () => pick(a.key) })),
    ...missing.map((k) => ({ label: `${k} (no such agent)`, hint: 'remove', checked: true, keepOpen: multiple, onclick: () => pick(k) })),
    ...(!list.length ? [{ header: 'No agents set up yet' }] : []),
  ]);

  function typed(e) {
    const t = e.currentTarget.value;
    const next = multiple ? t.split(',').map((s) => s.trim()).filter(Boolean) : t.trim();
    value = next;
    onchange?.(next);
  }
</script>

{#if agents === null}
  <input class="np-input np-agent-select-text" value={multiple ? chosen.join(', ') : chosen} {placeholder} {disabled} spellcheck="false" aria-label={label} onchange={typed} />
{:else}
  <Menu {items} minWidth={260} placement="bottom-start">
    {#snippet trigger({ toggle, open })}
      <button type="button" class="np-input np-agent-select" class:open class:empty={summary === placeholder} {disabled} aria-label={label} aria-haspopup="menu" title={summary} onclick={toggle} data-testid="agent-select">
        <Icon name="bot" size={13} />
        <span class="np-ellipsis">{summary}</span>
        <Icon name="chevron-down" size={12} />
      </button>
    {/snippet}
  </Menu>
{/if}

<style>
  .np-agent-select {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    width: 100%;
    min-width: min(220px, 100%);
    text-align: left;
    cursor: pointer;
  }
  .np-agent-select > :global(span) {
    flex: 1;
    min-width: 0;
  }
  .np-agent-select.empty > :global(span) {
    color: var(--fg-dim);
  }
  .np-agent-select:disabled {
    cursor: default;
    opacity: 0.6;
  }
</style>
