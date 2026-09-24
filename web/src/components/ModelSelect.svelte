<script>
  /**
   * A model field: shows the chosen model (status dot, name, ref) and opens the searchable model list (ModelMenu).
   * onchange(ref | null); `none` labels the empty choice (e.g. "first available"), omit it to require a model.
   */
  import Icon from '../lib/kit/Icon.svelte';
  import ModelMenu from './ModelMenu.svelte';
  import { app } from '../lib/state/app.svelte.js';

  let { value = null, onchange, none = null, label = 'Model', width = 380 } = $props();

  let btn = $state();
  let open = $state(false);
  const model = $derived(value ? app.modelsByRef.get(value) : null);
  const statusOf = (m) => m.status ?? (m.isLocal ? 'unloaded' : 'available');

  function choose(ref) {
    open = false;
    if ((ref ?? null) !== (value ?? null)) onchange?.(ref);
  }
</script>

<button type="button" class="np-input model-select" bind:this={btn} onclick={() => (open = !open)} aria-label={label} aria-haspopup="listbox" title={value ?? none ?? ''}>
  {#if model}
    <span class="np-dot" data-status={statusOf(model)}></span>
    <span class="name np-ellipsis">{model.displayName || model.id}</span>
    <span class="ref np-mono np-ellipsis">{value}</span>
  {:else if value}
    <Icon name="alert" size={12} />
    <span class="name np-ellipsis">{value}</span>
    <span class="ref">not listed</span>
  {:else}
    <span class="name np-ellipsis np-dim">{none ?? 'Choose a model…'}</span>
  {/if}
  <span class="chev"><Icon name="chevron-down" size={11} /></span>
</button>
{#if open}
  <ModelMenu anchor={btn} current={value} {none} {width} onchoose={choose} onclose={() => (open = false)} />
{/if}

<style>
  .model-select {
    display: flex;
    align-items: center;
    gap: 7px;
    width: 100%;
    min-width: 0;
    text-align: left;
    cursor: pointer;
  }
  .name {
    min-width: 0;
  }
  .chev {
    display: grid;
    flex: none;
    margin-left: auto;
    color: var(--fg-dim);
  }
  .ref {
    min-width: 0;
    color: var(--fg-dim);
    font-size: var(--fs-xs);
  }
</style>
