<script>
  import { Icon, IconButton, Menu, confirm, copyText } from '@netpi/kit';
  import { STATUSES } from './model.js';

  let {
    idea,
    api,
    ctx,
    canUp = false,
    canDown = false,
    dragging = false,
    drop = null,
    ondragstart,
    ondragover,
    ondrop,
    ondragend,
  } = $props();

  // The whole idea (fields, sections, evidence) is the host's idea dialog, which captures the revision it opened on
  // (idea-c3hihl).
  const edit = () => ctx.app.openIdea({ idea });
  const refine = () => ctx.app.openIdea({ idea, refine: true });
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
  // A new chat in the idea's project that starts on the idea at once (the pointer "Send to chat" stages, sent).
  const start = () =>
    ctx.app.startChat({
      projectId: idea.project?.id ?? null,
      title: idea.title,
      text: `Work on idea ${idea.id} (${idea.title}) — read it, then tell me what you plan to do.`,
    });
  const moreItems = $derived([
    ...STATUSES.map((s) => ({ label: `Status: ${s}`, checked: idea.status === s, onclick: () => s !== idea.status && api.update(idea.id, { status: s }) })),
    { divider: true },
    { label: 'Start in a new chat', icon: 'play', onclick: start },
    { label: 'Send to chat', icon: 'steer', onclick: () => api.send(idea) },
    { label: 'Edit…', icon: 'pencil', onclick: edit },
    { label: 'Refine with an agent…', icon: 'bot', onclick: refine },
    { label: 'Insert the full text', icon: 'file-text', onclick: () => api.toPrompt(idea.id) },
    { divider: true },
    { label: 'Move up', icon: 'chevron-up', disabled: !canUp, onclick: () => api.move(idea.id, -1) },
    { label: 'Move down', icon: 'chevron-down', disabled: !canDown, onclick: () => api.move(idea.id, 1) },
    { divider: true },
    { label: 'Copy id', icon: 'copy', onclick: () => copyText(idea.id) },
    { label: 'Delete idea…', icon: 'trash', danger: true, onclick: remove },
  ]);
</script>

<div
  class="card"
  class:low={idea.priority === 'low'}
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
  <!-- title and a short summary; the whole idea is in the dialog the click opens -->
  <button class="main" onclick={edit} title={idea.title}>
    <span class="top">
      {#if idea.priority !== 'medium'}
        <span class="pmark" data-p={idea.priority} title="Priority: {idea.priority}">
          <Icon name={idea.priority === 'high' ? 'arrow-up' : 'arrow-down'} size={11} />
        </span>
      {/if}
      <span class="title">{idea.title}</span>
    </span>
    {#if idea.summary}<span class="blurb">{idea.summary}</span>{/if}
  </button>
  <span class="more">
    <IconButton icon="play" title="Start a new chat on this idea" size="sm" onclick={start} />
    <Menu items={moreItems} minWidth={170}>
      {#snippet trigger({ toggle })}<IconButton icon="more" title="Status and actions" size="sm" onclick={toggle} />{/snippet}
    </Menu>
  </span>
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
  .card[data-status='done'],
  .card[data-status='rejected'] {
    opacity: 0.72;
  }
  /* a low-priority idea stays in its group, just quieter */
  .card.low .title {
    opacity: 0.62;
  }
  .card.low:hover .title {
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
    padding: 6px 10px 7px 20px;
    border: 0;
    background: transparent;
    color: var(--fg);
    font: inherit;
    text-align: left;
    cursor: pointer;
  }
  .top {
    display: flex;
    align-items: center;
    gap: 6px;
    min-width: 0;
    /* the play and more buttons sit over this row only; the summary below runs the full width */
    padding-right: 52px;
  }
  .title {
    flex: 1 1 auto;
    min-width: 0;
    font-weight: 600;
    line-height: 1.35;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  /* a limited description: three lines, then cut */
  .blurb {
    color: var(--fg-muted);
    font-size: var(--fs-sm);
    line-height: 1.45;
    overflow-wrap: anywhere;
    display: -webkit-box;
    -webkit-line-clamp: 3;
    line-clamp: 3;
    -webkit-box-orient: vertical;
    overflow: hidden;
  }
  .more {
    display: flex;
    position: absolute;
    top: 3px;
    right: 3px;
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
</style>
