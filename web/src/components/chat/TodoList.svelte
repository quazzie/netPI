<script>
  /** A read-only checklist (todo_write): items [{ text, status: pending | in_progress | done }]. */
  let { items = [] } = $props();
</script>

<ul class="todo">
  {#each items as it, i (i)}
    <li data-status={it.status}>
      <span class="mark" aria-hidden="true">
        {#if it.status === 'done'}<svg viewBox="0 0 12 12"><path d="M2.6 6.3l2.2 2.2 4.6-4.9" /></svg>{/if}
      </span>
      <span class="text">{it.text}</span>
      {#if it.status === 'in_progress'}<span class="sr">(in progress)</span>{/if}
    </li>
  {/each}
</ul>

<style>
  .todo {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 4px;
    min-width: 0;
  }
  li {
    display: flex;
    align-items: flex-start;
    gap: 8px;
    min-width: 0;
    line-height: 1.45;
    color: var(--fg-muted);
  }
  .text {
    min-width: 0;
    overflow-wrap: anywhere;
  }
  .mark {
    flex: none;
    display: grid;
    place-items: center;
    width: 13px;
    height: 13px;
    margin-top: 0.2em;
    border: 1.5px solid var(--border-strong);
    border-radius: 4px;
  }
  li[data-status='done'] .mark {
    border-color: var(--ok);
    background: var(--ok);
  }
  .mark svg {
    width: 10px;
    height: 10px;
    fill: none;
    stroke: var(--bg);
    stroke-width: 2;
    stroke-linecap: round;
    stroke-linejoin: round;
  }
  li[data-status='done'] .text {
    color: var(--fg-dim);
    text-decoration: line-through;
    text-decoration-color: color-mix(in srgb, var(--fg-dim) 55%, transparent);
  }
  li[data-status='in_progress'] .mark {
    border-color: var(--accent);
    background: radial-gradient(circle, var(--accent) 0 3px, transparent 3.5px);
  }
  li[data-status='in_progress'] .text {
    color: var(--fg);
    font-weight: 550;
  }
  .sr {
    position: absolute;
    width: 1px;
    height: 1px;
    overflow: hidden;
    clip-path: inset(50%);
  }
</style>
