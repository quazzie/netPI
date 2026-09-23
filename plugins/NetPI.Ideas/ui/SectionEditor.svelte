<script>
  import { untrack } from 'svelte';
  import { Button } from '@netpi/kit';
  import { KINDS } from './model.js';

  /** Add (section = null) or edit a section: kind + title + markdown content. */
  let { section = null, onsave, oncancel } = $props();
  const init = untrack(() => section);
  let kind = $state(init?.kind ?? 'note');
  let title = $state(init?.title ?? '');
  let content = $state(init?.content ?? '');
  let busy = $state(false);

  async function save(e) {
    e?.preventDefault();
    if (!content.trim() || busy) return;
    busy = true;
    await onsave({ ...(init?.id ? { id: init.id } : {}), kind, title: title.trim() || undefined, content });
    busy = false;
  }
  function onKey(e) {
    if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') save(e);
    if (e.key === 'Escape') {
      e.stopPropagation();
      oncancel();
    }
  }
  function focus(n) {
    n.focus();
  }
</script>

<form class="sed" onsubmit={save}>
  <div class="row">
    <select class="np-input kind" bind:value={kind}>
      {#each KINDS as k (k)}<option value={k}>{k}</option>{/each}
    </select>
    <input class="np-input" placeholder="Section title (optional)" bind:value={title} />
  </div>
  <textarea class="np-input np-mono" rows="6" placeholder="Markdown…" bind:value={content} onkeydown={onKey} use:focus></textarea>
  <div class="btns">
    <span class="np-dim hint">Ctrl+Enter to save</span>
    <Button variant="ghost" size="sm" onclick={oncancel}>Cancel</Button>
    <Button variant="primary" size="sm" type="submit" disabled={!content.trim() || busy}>{init ? 'Save section' : 'Add section'}</Button>
  </div>
</form>

<style>
  .sed {
    display: flex;
    flex-direction: column;
    gap: 6px;
    padding: 8px;
    border: 1px solid var(--accent-line);
    border-radius: var(--radius-sm);
    background: var(--bg);
  }
  .row {
    display: flex;
    gap: 6px;
  }
  .kind {
    width: 130px;
    flex: none;
    height: 26px;
  }
  .row input {
    height: 26px;
  }
  textarea {
    font-size: 12px;
    resize: vertical;
  }
  .btns {
    display: flex;
    align-items: center;
    justify-content: flex-end;
    gap: 6px;
  }
  .hint {
    margin-right: auto;
    font-size: var(--fs-xs);
  }
</style>
