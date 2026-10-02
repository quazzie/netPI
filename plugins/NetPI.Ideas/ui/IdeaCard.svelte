<script>
  import { Icon, IconButton, Menu, Markdown, TimeAgo, Button, confirm, copyText } from '@netpi/kit';
  import SectionEditor from './SectionEditor.svelte';
  import { STATUSES, PRIORITIES, STATUS_TONE, KIND_ICON, parseTags } from './model.js';

  let {
    idea,
    api,
    ctx,
    images = {},
    loadimage,
    ondetachimage,
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
  // What an open editor was opened on: the revision it had when it opened, and nothing else. The list refetches on
  // every ideas.changed, so the card's own idea is already the newer one — an editor that sent that would silently
  // overwrite whoever wrote it (idea-c3hihl).
  let editRev = $state(null);
  let secRev = $state(null);
  let busy = $state(false);

  const statusItems = $derived(
    STATUSES.map((s) => ({ label: s, checked: idea.status === s, onclick: () => s !== idea.status && api.update(idea.id, { status: s }) })),
  );
  const agentMade = $derived(String(idea.createdBy ?? '').startsWith('agent'));
  const proj = $derived(idea.project?.id ? (idea.project.name ?? idea.project.id) : null);
  const shots = $derived(idea.images ?? []);

  // Thumbnails arrive when the card opens: one fetch per image, kept by the tab, so a list of ideas stays cheap.
  $effect(() => {
    if (!open) return;
    for (const img of shots) loadimage?.(img.path);
  });

  // Removing an image replaces the image set in one patch. The server deletes the file the dropped reference named
  // only once that write has committed — an update refused as stale leaves the file and the reference exactly where
  // they were (idea-qpaghc). The idea keeps the rest.
  async function removeImage(path) {
    const kept = shots.filter((i) => i.path !== path).map(({ path: p, name, mediaType, bytes }) => ({ path: p, name, mediaType, bytes }));
    await api.update(idea.id, { images: kept });
  }

  function startEdit(e) {
    e?.stopPropagation();
    form = { title: idea.title, summary: idea.summary ?? '', priority: idea.priority ?? 'medium', tags: (idea.tags ?? []).join(', ') };
    editRev = idea.revision ?? null;
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
    }, editRev);
    busy = false;
    if (r) editing = false;
  }
  async function remove() {
    const id = idea.id; // the card can be gone (list refetched) by the time the dialog resolves
    const ok = await confirm({
      title: 'Delete idea?',
      message: `“${idea.title}” and its ${idea.sections?.length ?? 0} section(s) will be removed from the backlog. Consider setting the status to done or rejected instead.`,
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
  function openSec(sec) {
    secEdit = sec?.id ?? 'new';
    secRev = idea.revision ?? null;
  }
  function closeSec() {
    secEdit = null;
    secRev = null;
  }
  async function saveSection(sec) {
    const patch = sec.id ? { updateSections: [sec] } : { addSections: [sec] };
    const r = await api.update(idea.id, patch, secRev);
    if (r) closeSec();
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
  class:low={idea.priority === 'low' && !open}
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
  <!-- one line when closed: a priority mark, the title, the section count. The status is the group it sits in, so
       repeating it here would be noise, and the meta row lives in the open body (idea-43oruq). -->
  <button class="main" class:open onclick={ontoggle} aria-expanded={open} title={idea.title}>
    {#if idea.priority !== 'medium'}
      <span class="pmark" data-p={idea.priority} title="Priority: {idea.priority}">
        <Icon name={idea.priority === 'high' ? 'arrow-up' : 'arrow-down'} size={11} />
      </span>
    {/if}
    <span class="title">{idea.title}</span>
    {#if idea.sections?.length}<span class="dim secs" title="{idea.sections.length} section(s)"><Icon name="layers" size={11} />{idea.sections.length}</span>{/if}
  </button>

  {#if open}
    <div class="body">
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
    {#if idea.commits?.length}<span class="dim" title="{idea.commits.length} commit(s) recorded on this idea"><Icon name="branch" size={11} />{idea.commits.length}</span>{/if}
    {#if idea.sessions?.length}<span class="dim" title="{idea.sessions.length} chat(s) attached to this idea"><Icon name="message" size={11} />{idea.sessions.length}</span>{/if}
    {#if shots.length}<span class="dim" title="{shots.length} image(s) attached"><Icon name="image" size={11} />{shots.length}</span>{/if}
        {#if agentMade}<span class="dim agent" title="Added by {idea.createdBy}"><Icon name="bot" size={11} /></span>{/if}
        <TimeAgo time={idea.updatedAt ?? idea.createdAt} class="dim when" />
      </div>
      {#if shots.length}
        <!-- The screenshots are the report: they sit above the text, big enough to read, removable one by one. -->
        <div class="shots">
          {#each shots as img (img.path)}
            <figure>
              {#if images[img.path]}
                <img src={images[img.path]} alt={img.name} />
              {:else}
                <div class="pending" title={img.name}><span class="np-spinner"></span></div>
              {/if}
              <figcaption>
                <span class="np-ellipsis" title={img.name}>{img.name}</span>
                <IconButton icon="x" size={11} title="Remove image" onclick={() => removeImage(img.path)} />
              </figcaption>
            </figure>
          {/each}
        </div>
      {/if}
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
          <SectionEditor section={sec} onsave={saveSection} oncancel={closeSec} />
        {:else}
          <div class="sec">
            <div class="sec-head np-line">
              <Icon name={KIND_ICON[sec.kind] ?? 'file-text'} size={12} />
              <span class="kind">{sec.kind}</span>
              <span class="stitle np-grow" title={sec.title}>{sec.title ?? ''}</span>
              <span class="sec-acts">
                <IconButton icon="pencil" title="Edit section" size="sm" onclick={() => openSec(sec)} />
                <IconButton icon="trash" title="Remove section" size="sm" onclick={() => removeSection(sec)} />
              </span>
            </div>
            <Markdown text={sec.content || '_empty_'} class="sec-md" />
          </div>
        {/if}
      {/each}
      {#if secEdit === 'new'}
        <SectionEditor section={null} onsave={saveSection} oncancel={closeSec} />
      {/if}

      <!-- The evidence: which chats worked on this idea and which commits were recorded for it, as entries the user
           can open, not as counts. A commit entry is plain text (there is no commit view to link into); a chat opens. -->
      {#if (idea.sessions ?? []).length || (idea.commits ?? []).length}
        <div class="evidence">
          {#if (idea.sessions ?? []).length}
            <div class="ev-head"><Icon name="message" size={11} />Chats</div>
            {#each idea.sessions as s (s.sessionId + (s.at ?? ''))}
              <div class="ev-row np-line">
                {#if s.seen === undefined || s.seen === false}
                  <span class="unseen" title="You have not opened this idea since this chat worked on it"></span>
                {/if}
                <button class="ev-link" onclick={(e) => (e.stopPropagation(), ctx.app.openSession(s.sessionId))} title="Open this chat">
                  {s.title || s.sessionId}
                </button>
                <span class="dim when"><TimeAgo time={s.at} /></span>
              </div>
            {/each}
          {/if}
          {#if (idea.commits ?? []).length}
            <div class="ev-head"><Icon name="branch" size={11} />Commits</div>
            {#each idea.commits as c (c.hash)}
              <div class="ev-row np-line">
                <code class="hash">{c.short}</code><span class="np-grow np-ellipsis" title={c.subject}>{c.subject}</span>
                <span class="dim when"><TimeAgo time={c.at} /></span>
              </div>
            {/each}
          {/if}
        </div>
      {/if}

      <div class="actions np-line">
        <Button variant="primary" size="sm" icon="steer" onclick={() => api.send(idea)} title="Stage a pointer to this idea in the composer — the agent reads the idea itself">Send<span class="to-chat">to chat</span></Button>
        {#if secEdit !== 'new'}<Button size="sm" icon="plus" onclick={() => openSec(null)} title="Add section"><span class="wide">Section</span></Button>{/if}
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
  .evidence {
    display: flex;
    flex-direction: column;
    gap: 3px;
    margin: 6px 0 2px;
    padding: 6px 8px;
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--bg-1);
  }
  .ev-head {
    display: flex;
    align-items: center;
    gap: 4px;
    font-size: 10px;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    color: var(--text-dim);
  }
  .ev-row { gap: 6px; font-size: 11px; }
  .ev-link {
    background: none;
    border: none;
    padding: 0;
    font: inherit;
    color: var(--accent);
    cursor: pointer;
    text-align: left;
  }
  .ev-link:hover { text-decoration: underline; }
  .hash { font-family: var(--mono); color: var(--text-dim); }
  .unseen { width: 6px; height: 6px; border-radius: 50%; background: var(--accent); flex: none; }
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
  /* a low-priority idea stays in its group, just quieter — folding it under the high and medium ones hides it */
  .card.low .title {
    opacity: 0.62;
  }
  .card.low:hover .title,
  .card.low.open .title {
    opacity: 1;
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
  /* closed: one line, title only, ellipsised — the list has to read as a list of titles */
  .main {
    flex-direction: row;
    align-items: center;
    gap: 6px;
    padding: 6px 10px 6px 20px;
  }
  .main.open {
    flex-direction: column;
    align-items: stretch;
    gap: 2px;
    padding: 7px 10px 0 12px;
  }
  .title {
    flex: 1 1 auto;
    min-width: 0;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .main.open .title {
    white-space: normal;
    overflow: visible;
  }
  .pmark {
    display: inline-flex;
    align-items: center;
    flex: none;
  }
  .pmark[data-p='high'] {
    color: var(--err);
  }
  .pmark[data-p='low'] {
    color: var(--fg-dim);
  }
  .dim.secs {
    flex: none;
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
    padding: 0 2px;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
  }
  .meta :global(.np-menu-anchor) {
    flex: none;
  }
  .meta :global(.when) {
    flex: none;
  }
  .shots {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
    margin: 8px 0 2px;
  }
  .shots figure {
    margin: 0;
    width: 168px;
    border: 1px solid var(--border, #555);
    border-radius: 5px;
    overflow: hidden;
    background: var(--bg-2, #252525);
  }
  .shots img,
  .shots .pending {
    display: block;
    width: 100%;
    height: 104px;
    object-fit: cover;
  }
  .shots .pending {
    display: flex;
    align-items: center;
    justify-content: center;
    opacity: 0.6;
  }
  .shots figcaption {
    display: flex;
    align-items: center;
    gap: 4px;
    padding: 2px 4px 2px 6px;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
  }
  .shots figcaption :global(.np-ellipsis) {
    flex: 1 1 auto;
    min-width: 0;
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
