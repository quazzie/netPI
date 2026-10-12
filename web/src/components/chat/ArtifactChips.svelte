<script>
  /** The files a turn wrote, edited or handed over (artifact tool), as chips at the end of the turn; a click opens one
   * with the operating system. The user pressed it, so there is no question (a path outside the workspace still asks). */
  import Icon from '../../lib/kit/Icon.svelte';
  import { openFile } from '../../lib/openFile.js';

  let { item, sessionId } = $props();
  const verb = { written: 'Written', edited: 'Edited', artifact: 'Artifact' };
</script>

<div class="artifacts" aria-label="Files from this turn">
  {#each item.files as f (f.path)}
    <button class="chip" title="{f.caption ? f.caption + '\n' : ''}{f.path}\nOpen with its default app" onclick={() => openFile(f.path, sessionId)}>
      <Icon name={f.how === 'edited' ? 'pencil' : 'file-text'} size={13} />
      <span class="name">{f.name}</span>
      <span class="how">{f.caption || verb[f.how] || ''}</span>
    </button>
  {/each}
</div>

<style>
  .artifacts {
    display: flex;
    flex-wrap: wrap;
    gap: 6px;
    margin: 4px 0 2px;
    min-width: 0;
  }
  .chip {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    max-width: 100%;
    padding: 4px 10px;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--bg-2);
    color: var(--fg);
    font-size: var(--fs-sm);
    cursor: pointer;
  }
  .chip:hover {
    border-color: var(--border-strong);
  }
  .name {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .how {
    color: var(--fg-dim);
    font-size: var(--fs-xs);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    max-width: 24ch;
  }
</style>
