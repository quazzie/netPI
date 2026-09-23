<script>
  import { Icon, IconButton, Menu, Markdown, TimeAgo, Button, confirm } from '@netpi/kit';
  import SectionEditor from './SectionEditor.svelte';
  import { STATUSES, PRIORITIES, STATUS_TONE, KIND_ICON, parseTags } from './model.js';

  let {
    idea,
    api,
    open = false,
    ontoggle,
    canUp = false,
    canDown = false,
    dragging = false,
    drop = null,
    ondragstart,
    ondragover,
    ondrop,
    ondragend,
  } = $props();

  let editing = $state(false);
  let form = $state({ title: '', summary: '', priority: 'medium', tags: '' });
  let secEdit = $state(null); // section id | 'new'
  let busy = $state(false);

  const statusItems = $derived(
    STATUSES.map((s) => ({ label: s, checked: idea.status === s, onclick: () => s !== idea.status && api.update(idea.id, { status: s }) })),
  );
  const agentMade = $derived(String(idea.createdBy ?? '').startsWith('agent'));

  function startEdit(e) {
    e?.stopPropagation();
    form = { title: idea.title, summary: idea.summary ?? '', priority: idea.priority ?? 'medium', tags: (idea.tags ?? []).join(', ') };
    editing = true;
    if (!open) ontoggle();
  }
  async function saveEdit(e) {
    e?.preventDefault();
    if (!form.title.trim()) return;
    busy = true;
    const r = await api.update(idea.id, {
      title: form.title.trim(),
      summary: form.summary.trim(),
      priority: form.priority,
      tags: parseTags(form.tags),
    });
    busy = false;
    if (r) editing = false;
  }
  async function remove() {
    const ok = await confirm({
      title: 'Delete idea?',
      message: `“${idea.title}” and its ${idea.sections?.length ?? 0} section(s) will be removed from the ideas file. Consider setting the status to done or rejected instead.`,
      confirmLabel: 'Delete',
      danger: true,
    });
    if (ok) api.remove(idea.id);
  }
  async function saveSection(sec) {
    const patch = sec.id ? { updateSections: [sec] } : { addSections: [sec] };
    const r = await api.update(idea.id, patch);
    if (r) secEdit = null;
  }
  async function removeSection(sec) {
    const ok = await confirm({ title: 'Remove section?', message: `“${sec.title || sec.kind}” will be removed.`, confirmLabel: 'Remove', danger: true });
    if (ok) api.update(idea.id, { removeSectionIds: [sec.id] });
  }
</script>

<div
  class="card"
  class:open
  class:dragging
  class:drop-before={drop === 'before'}
  class:drop-after={drop === 'after'}
  data-status={idea.status}
  {ondragover}
  {ondrop}
  role="listitem"
>
  <div class="head">
    <span class="grip" draggable="true" {ondragstart} {ondragend} title="Drag to reorder" role="button" tabindex="-1">
      <Icon name="grip" size={13} />
    </span>
    <button class="main" onclick={ontoggle} aria-expanded={open}>
      <span class="title">{idea.title}</span>
      {#if idea.summary && !open}<span class="summary">{idea.summary}</span>{/if}
    </button>
    <Menu items={statusItems} minWidth={150}>
      {#snippet trigger({ toggle })}
        <button class="status" data-tone={STATUS_TONE[idea.status]} onclick={toggle} title="Change status">
          {idea.status}<Icon name="chevron-down" size={10} />
        </button>
      {/snippet}
    </Menu>
  </div>
  <div class="meta">
    <span class="prio" data-p={idea.priority}>
      <Icon name={idea.priority === 'high' ? 'arrow-up' : idea.priority === 'low' ? 'arrow-down' : 'more'} size={11} />{idea.priority}
    </span>
    {#each idea.tags ?? [] as t (t)}<span class="tag">#{t}</span>{/each}
    {#if idea.sections?.length}<span class="dim"><Icon name="layers" size={11} />{idea.sections.length}</span>{/if}
    {#if agentMade}<span class="dim" title={idea.createdBy}><Icon name="bot" size={11} /></span>{/if}
    <span class="np-spacer"></span>
    <TimeAgo time={idea.updatedAt ?? idea.createdAt} class="dim" />
  </div>

  {#if open}
    <div class="body">
      {#if editing}
        <form class="edit" onsubmit={saveEdit}>
          <input class="np-input" bind:value={form.title} placeholder="Title" />
          <textarea class="np-input" rows="3" bind:value={form.summary} placeholder="Summary"></textarea>
          <div class="erow">
            <div class="np-seg">
              {#each PRIORITIES as p (p)}
                <button type="button" aria-pressed={form.priority === p} onclick={() => (form.priority = p)}>{p}</button>
              {/each}
            </div>
            <input class="np-input" bind:value={form.tags} placeholder="tags, comma separated" />
          </div>
          <div class="btns">
            <Button variant="ghost" size="sm" onclick={() => (editing = false)}>Cancel</Button>
            <Button variant="primary" size="sm" type="submit" disabled={busy || !form.title.trim()}>Save</Button>
          </div>
        </form>
      {:else if idea.summary}
        <div class="full-summary">{idea.summary}</div>
      {/if}

      {#each idea.sections ?? [] as sec (sec.id)}
        {#if secEdit === sec.id}
          <SectionEditor section={sec} onsave={saveSection} oncancel={() => (secEdit = null)} />
        {:else}
          <div class="sec">
            <div class="sec-head">
              <Icon name={KIND_ICON[sec.kind] ?? 'file-text'} size={12} />
              <span class="kind">{sec.kind}</span>
              {#if sec.title}<span class="stitle">{sec.title}</span>{/if}
              <span class="np-spacer"></span>
              <span class="sec-acts">
                <IconButton icon="pencil" title="Edit section" size="sm" onclick={() => (secEdit = sec.id)} />
                <IconButton icon="trash" title="Remove section" size="sm" onclick={() => removeSection(sec)} />
              </span>
            </div>
            <Markdown text={sec.content || '_empty_'} class="sec-md" />
          </div>
        {/if}
      {/each}
      {#if secEdit === 'new'}
        <SectionEditor section={null} onsave={saveSection} oncancel={() => (secEdit = null)} />
      {/if}

      <div class="actions">
        <Button variant="primary" size="sm" icon="steer" onclick={() => api.toPrompt(idea.id)} title="Insert a prompt for this idea into the composer">Send to chat</Button>
        {#if secEdit !== 'new'}<Button size="sm" icon="plus" onclick={() => (secEdit = 'new')}>Section</Button>{/if}
        <span class="np-spacer"></span>
        {#if !editing}<IconButton icon="pencil" title="Edit title, summary, priority, tags" size="sm" onclick={startEdit} />{/if}
        <IconButton icon="chevron-up" title="Move up" size="sm" disabled={!canUp} onclick={() => api.move(idea.id, -1)} />
        <IconButton icon="chevron-down" title="Move down" size="sm" disabled={!canDown} onclick={() => api.move(idea.id, 1)} />
        <IconButton icon="trash" title="Delete idea" size="sm" onclick={remove} />
      </div>
      <div class="info np-mono">{idea.id} · by {idea.createdBy ?? 'user'}{idea.sessionIds?.length ? ` · ${idea.sessionIds.length} session(s)` : ''}</div>
    </div>
  {/if}
</div>

<style>
  .card {
    position: relative;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--bg-2);
    transition: border-color var(--t-fast);
  }
  .card:hover {
    border-color: var(--border-strong);
  }
  .card.open {
    border-color: var(--border-strong);
    background: var(--bg-1);
  }
  .card[data-status='done'],
  .card[data-status='rejected'] {
    opacity: 0.72;
  }
  .card.dragging {
    opacity: 0.4;
  }
  .card.drop-before::before,
  .card.drop-after::after {
    content: '';
    position: absolute;
    left: 6px;
    right: 6px;
    height: 2px;
    border-radius: 2px;
    background: var(--accent);
  }
  .card.drop-before::before {
    top: -5px;
  }
  .card.drop-after::after {
    bottom: -5px;
  }
  .head {
    display: flex;
    align-items: flex-start;
    gap: 4px;
    padding: 6px 6px 0 2px;
  }
  .grip {
    display: grid;
    place-items: center;
    width: 16px;
    height: 20px;
    color: var(--fg-dim);
    opacity: 0.35;
    cursor: grab;
    flex: none;
  }
  .card:hover .grip {
    opacity: 1;
  }
  .main {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 2px;
    padding: 1px 0;
    border: 0;
    background: transparent;
    color: var(--fg);
    font: inherit;
    text-align: left;
    cursor: pointer;
  }
  .title {
    font-weight: 600;
    line-height: 1.35;
    overflow-wrap: anywhere;
  }
  .summary {
    color: var(--fg-muted);
    font-size: var(--fs-sm);
    line-height: 1.4;
    display: -webkit-box;
    -webkit-line-clamp: 2;
    line-clamp: 2;
    -webkit-box-orient: vertical;
    overflow: hidden;
  }
  .status {
    display: inline-flex;
    align-items: center;
    gap: 3px;
    height: 20px;
    padding: 0 6px 0 8px;
    border: 0;
    border-radius: 10px;
    background: var(--bg-3);
    color: var(--fg-muted);
    font: inherit;
    font-size: 11px;
    font-weight: 600;
    white-space: nowrap;
    cursor: pointer;
    flex: none;
  }
  .status[data-tone='info'] {
    background: var(--info-soft);
    color: var(--info);
  }
  .status[data-tone='accent'] {
    background: var(--accent-soft);
    color: var(--accent);
  }
  .status[data-tone='warn'] {
    background: var(--warn-soft);
    color: var(--warn);
  }
  .status[data-tone='ok'] {
    background: var(--ok-soft);
    color: var(--ok);
  }
  .status[data-tone='err'] {
    background: var(--err-soft);
    color: var(--err);
  }
  .meta {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 3px 8px;
    padding: 3px 8px 6px 20px;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
  }
  .meta :global(.dim),
  .dim {
    display: inline-flex;
    align-items: center;
    gap: 3px;
    color: var(--fg-dim);
  }
  .prio {
    display: inline-flex;
    align-items: center;
    gap: 2px;
    text-transform: capitalize;
  }
  .prio[data-p='high'] {
    color: var(--err);
  }
  .prio[data-p='low'] {
    opacity: 0.8;
  }
  .tag {
    color: var(--accent);
  }
  .body {
    display: flex;
    flex-direction: column;
    gap: 8px;
    padding: 2px 10px 8px 20px;
  }
  .full-summary {
    color: var(--fg-muted);
    font-size: var(--fs-sm);
    line-height: 1.5;
    white-space: pre-wrap;
  }
  .sec {
    border-left: 2px solid var(--border-strong);
    padding: 0 0 0 10px;
  }
  .sec-head {
    display: flex;
    align-items: center;
    gap: 6px;
    min-height: 22px;
    color: var(--fg-dim);
    font-size: var(--fs-xs);
  }
  .kind {
    text-transform: uppercase;
    letter-spacing: 0.05em;
    font-weight: 600;
  }
  .stitle {
    color: var(--fg);
    font-size: var(--fs-sm);
    font-weight: 600;
  }
  .sec-acts {
    display: flex;
    opacity: 0;
    transition: opacity var(--t-fast);
  }
  .sec:hover .sec-acts {
    opacity: 1;
  }
  .sec :global(.sec-md) {
    font-size: 12.5px;
    line-height: 1.55;
  }
  .sec :global(.sec-md p) {
    margin-bottom: 0.45em;
  }
  .actions {
    display: flex;
    align-items: center;
    gap: 4px;
    padding-top: 2px;
  }
  .info {
    font-size: 10px;
    color: var(--fg-dim);
    opacity: 0.8;
  }
  .edit {
    display: flex;
    flex-direction: column;
    gap: 6px;
  }
  .erow {
    display: flex;
    gap: 6px;
  }
  .erow .np-seg button {
    text-transform: capitalize;
  }
  .erow .np-input {
    flex: 1;
    height: 26px;
  }
  .btns {
    display: flex;
    justify-content: flex-end;
    gap: 6px;
  }
</style>
