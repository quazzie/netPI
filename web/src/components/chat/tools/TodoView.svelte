<script>
  /** todo_write: the list as sent (from the result's details, or the arguments while it runs). */
  import TodoList from '../TodoList.svelte';
  import { todoItems } from '../../../lib/tools.js';
  let { args, result } = $props();
  const items = $derived(result?.details?.items ?? todoItems(args));
</script>

{#if items.length}
  <TodoList {items} />
{:else}
  <div class="np-dim np-small">{result ? 'The list was cleared.' : 'waiting for result…'}</div>
{/if}
