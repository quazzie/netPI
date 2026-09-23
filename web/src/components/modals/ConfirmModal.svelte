<script>
  import Modal from './Modal.svelte';
  let { data, onclose } = $props();
  function done(v) {
    data.resolve(v);
    onclose();
  }
</script>

<Modal title={data.title} width={420} onclose={() => done(false)}>
  {#if data.message}<p class="msg">{data.message}</p>{/if}
  {#snippet footer()}
    <button class="np-btn np-btn-ghost" onclick={() => done(false)}>Cancel</button>
    <button class="np-btn {data.danger ? 'np-btn-danger' : 'np-btn-primary'}" data-autofocus onclick={() => done(true)}>{data.confirmLabel}</button>
  {/snippet}
</Modal>

<style>
  .msg {
    margin: 0;
    color: var(--fg-muted);
    line-height: 1.55;
    overflow-wrap: anywhere;
  }
</style>
