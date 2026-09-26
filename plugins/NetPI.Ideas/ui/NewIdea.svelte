<script>
  import { Button } from '@netpi/kit';
  import { PRIORITIES, parseTags } from './model.js';

  let { onadd, oncancel, projects = [], activeProjectId = '' } = $props();
  let title = $state('');
  let summary = $state('');
  let priority = $state('medium');
  let tags = $state('');
  let project = $state('');
  let busy = $state(false);

  async function submit(e) {
    e?.preventDefault();
    if (!title.trim() || busy) return;
    busy = true;
    await onadd({ title: title.trim(), summary: summary.trim() || undefined, priority, tags: parseTags(tags) }, project || undefined);
    busy = false;
  }
  function focus(n) {
    n.focus();
  }
</script>

<form class="new np-card" onsubmit={submit}>
  <input class="np-input" placeholder="Idea title" bind:value={title} use:focus onkeydown={(e) => e.key === 'Escape' && oncancel()} />
  <textarea class="np-input" rows="2" placeholder="Summary (optional)" bind:value={summary}></textarea>
  <div class="row">
    <div class="np-seg">
      {#each PRIORITIES as p (p)}
        <button type="button" aria-pressed={priority === p} onclick={() => (priority = p)}>{p}</button>
      {/each}
    </div>
    <select class="np-input project" bind:value={project} title="Which project the idea belongs to">
      <option value="">{activeProjectId ? 'This project' : 'Global'}</option>
      {#if activeProjectId}<option value="global">Global (unbound)</option>{/if}
      {#each projects as p (p.id)}<option value={p.id}>{p.name}</option>{/each}
    </select>
    <input class="np-input tags" placeholder="tags, comma separated" bind:value={tags} />
  </div>
  <div class="btns">
    <Button variant="ghost" size="sm" onclick={oncancel}>Cancel</Button>
    <Button variant="primary" size="sm" type="submit" disabled={!title.trim() || busy}>Add idea</Button>
  </div>
</form>

<style>
  .new {
    display: flex;
    flex-direction: column;
    gap: 6px;
    margin: 4px 10px 4px 12px;
    padding: 8px;
  }
  textarea {
    resize: vertical;
    min-height: 44px;
  }
  .row {
    display: flex;
    flex-wrap: wrap;
    gap: 6px;
    align-items: center;
  }
  .row .np-seg button {
    text-transform: capitalize;
  }
  .tags {
    flex: 1 1 120px;
    min-width: 0;
    height: 26px;
  }
  .project {
    height: 26px;
    min-width: 110px;
    flex: 0 1 auto;
    font-size: var(--fs-sm);
  }
  .btns {
    display: flex;
    justify-content: flex-end;
    gap: 6px;
  }
</style>
