<script>
  /** The agent's checklist (session meta "todo", written by todo_write) above the composer while items are open. */
  import Icon from '../../lib/kit/Icon.svelte';
  import TodoList from '../chat/TodoList.svelte';
  let { session } = $props();

  const items = $derived(Array.isArray(session?.meta?.todo) ? session.meta.todo : []);
  const done = $derived(items.filter((i) => i.status === 'done').length);
  const current = $derived(items.find((i) => i.status === 'in_progress') ?? items.find((i) => i.status !== 'done'));
  let open = $state(false);
</script>

{#if items.length && done < items.length}
  <div class="strip" class:open>
    <button class="bar" onclick={() => (open = !open)} aria-expanded={open} title={open ? 'Hide the plan' : 'Show the plan'}>
      <Icon name="list" size={13} />
      <span class="count">{done}/{items.length}</span>
      <span class="np-ellipsis cur">{current?.text ?? ''}</span>
      <span class="chev"><Icon name="chevron-up" size={12} /></span>
    </button>
    {#if open}
      <div class="list np-scroll"><TodoList {items} /></div>
    {/if}
  </div>
{/if}

<style>
  .strip {
    margin: 0 0 8px;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--bg-1);
    overflow: hidden;
  }
  .bar {
    display: flex;
    align-items: center;
    gap: 8px;
    width: 100%;
    height: 30px;
    padding: 0 10px;
    border: 0;
    background: transparent;
    color: var(--fg-muted);
    font-size: var(--fs-sm);
    text-align: left;
  }
  .bar:hover {
    background: var(--bg-2);
  }
  .count {
    flex: none;
    font-variant-numeric: tabular-nums;
    color: var(--fg-dim);
  }
  .cur {
    flex: 1;
    color: var(--fg);
  }
  .chev {
    flex: none;
    display: grid;
    transform: rotate(180deg);
    transition: transform var(--t-fast);
    color: var(--fg-dim);
  }
  .open .chev {
    transform: none;
  }
  .list {
    max-height: 240px;
    padding: 6px 12px 10px;
    border-top: 1px solid var(--border);
    font-size: var(--fs-sm);
  }
</style>
