<script>
  import { onMount } from 'svelte';
  import Modal from './Modal.svelte';
  import Icon from '../../lib/kit/Icon.svelte';
  import { rpc } from '../../lib/rpc.svelte.js';

  /** Browser fallback folder picker (fs.dirs). data: { initial, title, resolve } */
  let { data, onclose } = $props();

  let path = $state('');
  let input = $state('');
  let parent = $state(null);
  let dirs = $state.raw([]);
  let roots = $state.raw([]);
  let loading = $state(false);
  let error = $state('');
  let selected = $state(null);

  async function go(p) {
    loading = true;
    error = '';
    try {
      const res = await rpc('fs.dirs', p ? { path: p } : {});
      path = res.path ?? '';
      input = path;
      parent = res.parent ?? null;
      dirs = res.dirs ?? [];
      roots = res.roots ?? [];
      selected = null;
    } catch (e) {
      error = e.message;
    } finally {
      loading = false;
    }
  }
  onMount(() => go(data.initial));

  function done(v) {
    data.resolve(v);
    onclose();
  }
</script>

<Modal title={data.title ?? 'Choose a folder'} width={560} padded={false} onclose={() => done(null)}>
  <div class="picker">
    <form class="bar" onsubmit={(e) => (e.preventDefault(), go(input))}>
      <button type="button" class="np-icon-btn" title="Parent folder" disabled={!parent} onclick={() => go(parent)}><Icon name="corner-up" size={14} /></button>
      <input class="np-input np-mono" bind:value={input} spellcheck="false" aria-label="Path" />
      <button type="submit" class="np-btn">Go</button>
    </form>
    {#if roots.length > 1}
      <div class="roots">
        {#each roots as r (r)}
          <button class="np-btn np-btn-sm np-btn-ghost" onclick={() => go(r)}><Icon name="drive" size={12} /> {r}</button>
        {/each}
      </div>
    {/if}
    <div class="list np-scroll">
      {#if loading}
        <div class="np-empty"><span class="np-spinner"></span></div>
      {:else if error}
        <div class="np-empty"><Icon name="alert" size={18} />{error}</div>
      {:else}
        {#each dirs as d (d.path)}
          <button
            class="dir"
            class:sel={selected === d.path}
            onclick={() => (selected = d.path)}
            ondblclick={() => go(d.path)}
          >
            <Icon name="folder" size={14} />
            <span class="np-ellipsis">{d.name}</span>
          </button>
        {:else}
          <div class="np-empty">No subfolders</div>
        {/each}
      {/if}
    </div>
  </div>
  {#snippet footer()}
    <span class="np-mono np-dim np-small np-ellipsis chosen">{selected ?? path}</span>
    <button class="np-btn np-btn-ghost" onclick={() => done(null)}>Cancel</button>
    <button class="np-btn np-btn-primary" disabled={!path} onclick={() => done(selected ?? path)}>Select folder</button>
  {/snippet}
</Modal>

<style>
  .picker {
    display: flex;
    flex-direction: column;
    height: 420px;
  }
  .bar {
    display: flex;
    gap: 6px;
    padding: 10px 12px;
    border-bottom: 1px solid var(--border);
  }
  .roots {
    display: flex;
    flex-wrap: wrap;
    gap: 4px;
    padding: 6px 10px;
    border-bottom: 1px solid var(--border);
  }
  .list {
    flex: 1;
    min-height: 0;
    padding: 6px;
  }
  .dir {
    display: flex;
    align-items: center;
    gap: 8px;
    width: 100%;
    height: 30px;
    padding: 0 10px;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--fg);
    text-align: left;
  }
  .dir :global(svg) {
    color: var(--fg-dim);
  }
  .dir:hover {
    background: var(--bg-2);
  }
  .dir.sel {
    background: var(--accent-soft);
  }
  .dir.sel :global(svg) {
    color: var(--accent);
  }
  .chosen {
    flex: 1;
    min-width: 0;
  }
</style>
