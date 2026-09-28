<script>
  import { Icon, IconButton, Menu, Markdown, TimeAgo, Button, confirm, copyText } from '@netpi/kit';
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
  const proj = $derived(idea.project?.id ? (idea.project.name ?? idea.project.id) : null);

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
    const id = idea.id; // the card can be gone (list refetched) by the time the dialog resolves
    const ok = await confirm({
      title: 'Delete idea?',
      message: `“${idea.title}” and its ${idea.sections?.length ?? 0} section(s) will be removed from the ideas file. Consider setting the status to done or rejected instead.`,
      confirmLabel: 'Delete',
      danger: true,
    });
    if (ok) api.remove(id);
  }
  const moreItems = $derived([
    { label: 'Insert the full text', icon: 'file-text', onclick: () => api.toPrompt(idea.id) },
    { divider: true },
    { label: 'Move up', icon: 'chevron-up', disabled: !canUp, onclick: () => api.move(idea.id, -1) },
    { label: 'Move down', icon: 'chevron-down', disabled: !canDown, onclick: () => api.move(idea.id, 1) },
    { divider: true },
    { label: 'Copy id', icon: 'copy', onclick: () => copyText(idea.id) },
    { divider: true },
    { label: 'Delete idea…', icon: 'trash', danger: true, onclick: remove },
  ]);
  async function saveSection(sec) {
    const patch = sec.id ? { updateSections: [sec] } : { addSections: [sec] };
    const r = await api.update(idea.id, patch);
    if (r) secEdit = null;
  }
  async function removeSection(sec) {
    const id = idea.id;
    const ok = await confirm({ title: 'Remove section?', message: `“${sec.title || sec.kind}” will be removed.`, confirmLabel: 'Remove', danger: true });
    if (ok) api.update(id, { removeSectionIds: [sec.id] });
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
  <span class="grip" draggable="true" {ondragstart} {ondragend} title="Drag to reorder" role="button" tabindex="-1">
    <Icon name="grip" size={12} />
  </span>
  <button class="main" onclick={ontoggle} aria-expanded={open} title={idea.title}>
    <span class="title">{idea.title}</span>
    {#if idea.summary && !open}<span class="summary">{idea.summary}</span>{/if}
  </button>
  <div class="meta np-line">
    <Menu items={statusItems} minWidth={150} placement="bottom-start">
      {#snippet trigger({ toggle })}
        <button class="status" data-tone={STATUS_TONE[idea.status]} onclick={toggle} title="Status: {idea.status} (click to change)">
          {idea.status}<Icon name="chevron-down" size={10} />
        </button>
      {/snippet}
    </Menu>
    <span class="proj" title={proj ? `Project ${proj}` : 'Not bound to a project (global)'}>
      <Icon name={proj ? 'folder' : 'globe'} size={11} /><span class="pname">{proj ?? 'Global'}</span>
    </span>
    <span class="prio" data-p={idea.priority} title="Priority: {idea.priority}">
      <Icon name={idea.priority === 'high' ? 'arrow-up' : idea.priority === 'low' ? 'arrow-down' : 'more'} size={11} /><span class="plabel">{idea.priority}</span>
    </span>
    <span class="tags np-grow" title={(idea.tags ?? []).map((t) => `#${t}`).join(' ')}>{#each idea.tags ?? [] as t (t)}<span class="tag">#{t}</span>{/each}</span>
    {#if idea.sections?.length}<span class="dim" title="{idea.sections.length} section(s)"><Icon name="layers" size={11} />{idea.sections.length}</span>{/if}
    {#if agentMade}<span class="dim agent" title="Added by {idea.createdBy}"><Icon name="bot" size={11} /></span>{/if}
    <TimeAgo time={idea.updatedAt ?? idea.createdAt} class="dim when" />
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
            <div class="sec-head np-line">
              <Icon name={KIND_ICON[sec.kind] ?? 'file-text'} size={12} />
              <span class="kind">{sec.kind}</span>
              <span class="stitle np-grow" title={sec.title}>{sec.title ?? ''}</span>
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

      <div class="actions np-line">
        <Button variant="primary" size="sm" icon="steer" onclick={() => api.send(idea)} title="Stage a pointer to this idea in the composer — the agent reads the idea itself">Send<span class="to-chat">to chat</span></Button>
        {#if secEdit !== 'new'}<Button size="sm" icon="plus" onclick={() => (secEdit = 'new')} title="Add section"><span class="wide">Section</span></Button>{/if}
        <span class="np-grow"></span>
        {#if !editing}<IconButton icon="pencil" title="Edit title, summary, priority, tags" size="sm" onclick={startEdit} />{/if}
        <Menu items={moreItems} minWidth={160}>
          {#snippet trigger({ toggle })}<IconButton icon="more" title="More actions" size="sm" onclick={toggle} />{/snippet}
        </Menu>
      </div>
      <div class="info np-mono np-ellipsis" title="{idea.id} · by {idea.createdBy ?? 'user'}">{idea.id} · by {idea.createdBy ?? 'user'}{idea.sessionIds?.length ? ` · ${idea.sessionIds.length} session(s)` : ''}</div>
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
  .grip {
    position: absolute;
    left: 0;
    top: 6px;
    display: grid;
    place-items: center;
    width: 12px;
    height: 20px;
    color: var(--fg-dim);
    opacity: 0;
    cursor: grab;
    transition: opacity var(--t-fast);
  }
  .card:hover .grip {
    opacity: 0.8;
  }
  .main {
    width: 100%;
    display: flex;
    flex-direction: column;
    gap: 2px;
    padding: 7px 10px 0 12px;
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
    display: -webkit-box;
    -webkit-line-clamp: 2;
    line-clamp: 2;
    -webkit-box-orient: vertical;
    overflow: hidden;
  }
  .card.open .title {
    display: block;
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
    gap: 6px;
    padding: 4px 8px 7px 12px;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
  }
  .meta :global(.np-menu-anchor) {
    flex: none;
  }
  .meta :global(.when) {
    flex: none;
  }
  .tags .tag + .tag {
    margin-left: 6px;
  }
  /* narrow: keep status, priority arrow, section count and time; tags are in the filter menu and the tooltip */
  @container (max-width: 279px) {
    .meta .plabel,
    .meta .proj .pname,
    .meta .dim.agent {
      display: none;
    }
    .tags {
      visibility: hidden;
    }
  }
  .meta :global(.dim),
  .dim {
    display: inline-flex;
    align-items: center;
    gap: 3px;
    color: var(--fg-dim);
  }
  .proj {
    display: inline-flex;
    align-items: center;
    gap: 3px;
    color: var(--fg-dim);
    max-width: 110px;
  }
  .proj .pname {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
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
    padding: 0 10px 8px 12px;
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
    gap: 4px;
    padding-top: 2px;
  }
  @container (max-width: 339px) {
    .wide {
      display: none;
    }
  }
  @container (max-width: 279px) {
    .to-chat {
      display: none;
    }
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
    flex-wrap: wrap;
    gap: 6px;
  }
  .erow .np-seg button {
    text-transform: capitalize;
  }
  .erow .np-input {
    flex: 1 1 130px;
    min-width: 0;
    height: 26px;
  }
  .btns {
    display: flex;
    justify-content: flex-end;
    gap: 6px;
  }
</style>
