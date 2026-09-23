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
</script>

<Modal title={data.title} width={440} onclose={() => done(null)}>
  <form onsubmit={(e) => (e.preventDefault(), done(value))}>
    {#if data.label}<label class="lbl" for="prompt-input">{data.label}</label>{/if}
    <input id="prompt-input" class="np-input" bind:value placeholder={data.placeholder} use:sel />
  </form>
  {#snippet footer()}
    <button class="np-btn np-btn-ghost" onclick={() => done(null)}>Cancel</button>
    <button class="np-btn np-btn-primary" onclick={() => done(value)}>OK</button>
  {/snippet}
</Modal>

<style>
  .lbl {
    display: block;
    margin-bottom: 6px;
    font-size: var(--fs-sm);
    color: var(--fg-muted);
  }
</style>
