<script>
  import Icon from '../../lib/kit/Icon.svelte';
  import ModelMenu from '../ModelMenu.svelte';
  import { app, updateSession, sessionModelRef } from '../../lib/state/app.svelte.js';

  /** Model picker grouped by provider (status dot, context window, concurrency). Sets sessions.update { model }. */
  let { session, open = $bindable(false) } = $props();

  let btn = $state();

  const current = $derived(sessionModelRef(session));
  const currentModel = $derived(current ? app.modelsByRef.get(current) : null);
  const label = $derived(currentModel?.displayName || current?.split('/').pop() || 'No model');
  const statusOf = (m) => m.status ?? (m.isLocal ? 'unloaded' : 'available');

  async function choose(ref) {
    open = false;
    if (ref && ref !== session.model) await updateSession(session.id, { model: ref });
  }
</script>

<button class="pick" bind:this={btn} onclick={() => (open = !open)} title="Model: {current ?? 'default'}" aria-haspopup="listbox">
  {#if currentModel}<span class="np-dot" data-status={statusOf(currentModel)}></span>{:else}<Icon name="cpu" size={13} />{/if}
  <span class="np-ellipsis">{label}</span>
  <Icon name="chevron-down" size={11} />
</button>

{#snippet defaultFooter()}default: <span class="np-mono">{app.defaultModel}</span>{/snippet}

{#if open}
  <ModelMenu anchor={btn} {current} placement="top-start" onchoose={choose} onclose={() => (open = false)} footer={app.defaultModel ? defaultFooter : null} />
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
</style>
