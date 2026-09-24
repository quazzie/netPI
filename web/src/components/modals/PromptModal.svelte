<script>
  import { untrack } from 'svelte';
  import Modal from './Modal.svelte';
  let { data, onclose } = $props();
  let value = $state(untrack(() => data.value ?? ''));
  function done(v) {
    data.resolve(v);
    onclose();
  }
  function sel(node) {
    node.focus();
    node.select();
  }
  function onKey(e) {
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
      e.preventDefault();
      done(value);
    }
  }
</script>

<Modal title={data.title} width={data.multiline ? 560 : 440} onclose={() => done(null)}>
  <form onsubmit={(e) => (e.preventDefault(), done(value))}>
    {#if data.label}<label class="lbl" for="prompt-input">{data.label}</label>{/if}
    {#if data.multiline}
      <textarea id="prompt-input" class="np-input area" bind:value placeholder={data.placeholder} rows="6" onkeydown={onKey} use:sel></textarea>
    {:else}
      <input id="prompt-input" class="np-input" bind:value placeholder={data.placeholder} use:sel />
    {/if}
    {#if data.hint}<div class="hint np-dim np-small">{data.hint}</div>{/if}
  </form>
  {#snippet footer()}
    <button class="np-btn np-btn-ghost" onclick={() => done(null)}>Cancel</button>
    <button class="np-btn np-btn-primary" onclick={() => done(value)}>
      {data.confirmLabel || 'OK'}{#if data.multiline} <span class="np-kbd">Ctrl+Enter</span>{/if}
    </button>
  {/snippet}
</Modal>

<style>
  .lbl {
    display: block;
    margin-bottom: 6px;
    font-size: var(--fs-sm);
    color: var(--fg-muted);
  }
  .area {
    width: 100%;
    height: auto;
    min-height: 120px;
    padding: 8px 10px;
    line-height: 1.5;
    resize: vertical;
  }
  .hint {
    margin-top: 8px;
  }
</style>
