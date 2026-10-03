<script>
  import { onMount, untrack } from 'svelte';
  import Modal from './Modal.svelte';
  import Icon from '../../lib/kit/Icon.svelte';
  import { rpc } from '../../lib/rpc.svelte.js';
  import { app } from '../../lib/state/app.svelte.js';
  import { toast } from '../../lib/state/ui.svelte.js';
  import { agentList, ANY } from '../../lib/agents.js';
  import { prepareImage, formatBytes } from '../../lib/images.js';
  import { IDEA_STATUSES, IDEA_PRIORITIES, IDEA_MAX_IMAGES, parseTags } from '../../lib/ideas.js';

  /**
   * One dialog for filing an idea and for editing one (the Ideas tab's "+" and Edit, Ctrl+I, /idea).
   * data: { mode: 'add' | 'edit', idea?: the idea being edited (with its revision), projectId?: where a new one goes
   * ('global' for unbound; default: the active chat's project), refine?: open with the agent task switched on }.
   * Refining saves the idea first and then asks the Ideas plugin (ideas.refine) to start a chat in which an agent
   * researches it and writes it up; the dialog does not wait for that agent.
   */
  let { data, onclose } = $props();

  const editing = untrack(() => data.mode === 'edit');
  const original = untrack(() => data.idea ?? null);
  // The revision the editor opened on, and nothing newer: the idea list refetches on every change, so the card's idea is
  // already the newer one - saving against that would silently overwrite whoever wrote it (idea-c3hihl).
  let revision = $state(untrack(() => original?.revision ?? null));

  let title = $state(untrack(() => original?.title ?? ''));
  let summary = $state(untrack(() => original?.summary ?? ''));
  let priority = $state(untrack(() => original?.priority ?? 'medium'));
  let status = $state(untrack(() => original?.status ?? 'open'));
  let tags = $state(untrack(() => (original?.tags ?? []).join(', ')));
  let project = $state(untrack(() => (original ? (original.project?.id ?? 'global') : (data.projectId ?? app.activeProject?.id ?? 'global'))));
  // Stored references ({ path, name, mediaType, bytes }) plus the data URL for the thumbnail, kept while the dialog is open.
  let images = $state(untrack(() => (original?.images ?? []).map((i) => ({ ...i }))));
  let refine = $state(untrack(() => !!data.refine));
  let agent = $state(ANY);
  let hint = $state('');

  let busy = $state(false);
  let attaching = $state(false);
  let dragOver = $state(false);
  let error = $state('');
  let conflict = $state(false);
  let note = $state('');
  let fileInput = $state(null);
  let committed = false;
  const added = []; // paths stored while the dialog is open: a cancel takes them away again

  const agents = $derived(agentList().filter((a) => a.available && !a.disabled));
  const projects = $derived([...app.projects].sort((a, b) => a.name.localeCompare(b.name)));
  const verb = $derived(editing ? 'Save' : 'Add idea');
  const label = $derived(refine ? (editing ? 'Save and refine' : 'Add and refine') : verb);

  // Thumbnails of the images the idea already has come from the host (the list carries references only).
  function loadThumbs() {
    for (const img of images) {
      if (img.url) continue;
      rpc('ideas.image', { path: img.path })
        .then((r) => {
          if (r?.data) images = images.map((i) => (i.path === img.path ? { ...i, url: `data:${r.mediaType};base64,${r.data}` } : i));
        })
        .catch(() => {});
    }
  }
  onMount(loadThumbs);

  const refs = () => images.map(({ path, name, mediaType, bytes }) => ({ path, name, mediaType, bytes }));

  async function addFiles(files) {
    const room = IDEA_MAX_IMAGES - images.length;
    if (room <= 0) return;
    const picked = [...files].filter((f) => f.type.startsWith('image/')).slice(0, room);
    if (!picked.length) return;
    attaching = true;
    try {
      for (const file of picked) {
        try {
          const { image, note: n } = await prepareImage(file); // shrink to what a message can carry, or refuse with a reason
          if (n) note = n;
          if (!image) continue;
          const r = await rpc('ideas.addImage', { data: image.data, mediaType: image.mediaType, name: image.name });
          added.push(r.path);
          images = [...images, { ...r, url: image.url }];
        } catch (e) {
          // an image that silently does not appear is the worst outcome of the three
          toast(`Could not attach ${file.name || 'the image'}: ${e?.message ?? e}`, 'error');
        }
      }
    } finally {
      attaching = false;
    }
  }
  function onPaste(e) {
    const files = [...(e.clipboardData?.items ?? [])].filter((i) => i.kind === 'file' && i.type.startsWith('image/')).map((i) => i.getAsFile());
    if (!files.length) return;
    e.preventDefault();
    addFiles(files);
  }
  function onDrop(e) {
    e.preventDefault();
    dragOver = false;
    addFiles(e.dataTransfer?.files ?? []);
  }
  async function removeImage(path) {
    images = images.filter((i) => i.path !== path);
    // one stored while this dialog was open is dropped right away; one the idea already had goes with the next save
    // (the host deletes the file once that write has committed)
    const at = added.indexOf(path);
    if (at >= 0) {
      added.splice(at, 1);
      await rpc('ideas.removeImage', { path }).catch(() => {});
    }
  }

  async function save() {
    const tagList = parseTags(tags);
    if (!editing) {
      const idea = { title: title.trim(), priority, tags: tagList, images: refs() };
      if (summary.trim()) idea.summary = summary.trim();
      const params = { idea, prepend: true, sessionId: app.activeId || undefined };
      if (project) params.projectId = project;
      return rpc('ideas.add', params);
    }
    const patch = { title: title.trim(), summary: summary.trim(), priority, status, tags: tagList, images: refs() };
    if (project !== (original.project?.id ?? 'global')) patch.project = project;
    return rpc('ideas.update', { id: original.id, patch, expectedRevision: revision });
  }

  async function submit(e) {
    e?.preventDefault();
    if (!title.trim() || busy) return;
    busy = true;
    error = '';
    conflict = false;
    let saved;
    try {
      saved = await save();
    } catch (err) {
      const message = err?.message ?? String(err);
      if (/conflict|changed since/i.test(message)) {
        conflict = true;
        error = 'This idea changed somewhere else — your text is still here. Load the current version, then apply your change again.';
      } else error = message;
      busy = false;
      return;
    }
    committed = true;
    if (refine && saved?.id) {
      try {
        const r = await rpc('ideas.refine', { id: saved.id, agent: agent === ANY ? undefined : agent, hint: hint.trim() || undefined }, { timeout: 60_000 });
        toast(`Refining “${saved.title}” in the chat “${r.title}”`);
      } catch (err) {
        toast(`Saved, but no agent could be started: ${err?.message ?? err}`, 'error');
      }
    }
    busy = false;
    onclose();
  }

  /** After a conflict: take the idea as it is now into the form (what was typed is replaced, so it can be applied again). */
  async function reload() {
    try {
      const fresh = await rpc('ideas.get', { id: original.id });
      revision = fresh.revision ?? null;
      title = fresh.title ?? '';
      summary = fresh.summary ?? '';
      priority = fresh.priority ?? 'medium';
      status = fresh.status ?? 'open';
      tags = (fresh.tags ?? []).join(', ');
      project = fresh.project?.id ?? 'global';
      images = (fresh.images ?? []).map((i) => ({ ...i }));
      loadThumbs();
      error = '';
      conflict = false;
    } catch (err) {
      error = err?.message ?? String(err);
    }
  }

  function cancel() {
    if (!committed && added.length) for (const path of added) rpc('ideas.removeImage', { path }).catch(() => {});
    onclose();
  }
  function onKey(e) {
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
      e.preventDefault();
      submit();
    }
  }
</script>

<Modal title={editing ? 'Edit idea' : 'New idea'} width={580} class="idea-dialog" onclose={cancel}>
  <form
    id="idea-form"
    class="form"
    class:drop={dragOver}
    onsubmit={submit}
    onkeydown={onKey}
    onpaste={onPaste}
    ondragover={(e) => { e.preventDefault(); dragOver = true; }}
    ondragleave={() => (dragOver = false)}
    ondrop={onDrop}
  >
    {#if editing}<div class="idnote np-dim np-small">{original.id}</div>{/if}
    <input class="np-input i-title" data-autofocus placeholder="Idea title (one line, on its own)" bind:value={title} />
    <textarea class="np-input i-summary" rows="4" placeholder="Summary: what, and why (optional)" bind:value={summary}></textarea>

    <div class="row">
      <div class="np-seg" role="group" aria-label="Priority">
        {#each IDEA_PRIORITIES as p (p)}
          <button type="button" aria-pressed={priority === p} onclick={() => (priority = p)}>{p}</button>
        {/each}
      </div>
      {#if editing}
        <select class="np-input i-status" bind:value={status} title="Status">
          {#each IDEA_STATUSES as s (s)}<option value={s}>{s}</option>{/each}
        </select>
      {/if}
      <select class="np-input i-project" bind:value={project} title="Which project the idea belongs to">
        <option value="global">Global (no project)</option>
        {#each projects as p (p.id)}<option value={p.id}>{p.name}</option>{/each}
      </select>
    </div>
    <input class="np-input i-tags" placeholder="tags, comma separated" bind:value={tags} />

    {#if images.length || dragOver}
      <div class="thumbs">
        {#each images as img (img.path)}
          <figure title="{img.name} · {formatBytes(img.bytes)}">
            {#if img.url}<img src={img.url} alt={img.name} />{:else}<span class="np-spinner"></span>{/if}
            <button type="button" class="rm" aria-label="Remove image" onclick={() => removeImage(img.path)}><Icon name="x" size={11} /></button>
          </figure>
        {/each}
        {#if dragOver}<span class="drop-hint">Drop to attach</span>{/if}
      </div>
    {/if}
    {#if note}<p class="note">{note}</p>{/if}
    <div class="row">
      <input bind:this={fileInput} type="file" accept="image/*" multiple hidden onchange={(e) => { addFiles(e.currentTarget.files ?? []); e.currentTarget.value = ''; }} />
      <button type="button" class="np-btn np-btn-ghost np-btn-sm" disabled={attaching || images.length >= IDEA_MAX_IMAGES} title="Attach an image (or paste one, or drop it here)" onclick={() => fileInput?.click()}>
        <Icon name="image" size={13} />{attaching ? 'Attaching…' : 'Image'}
      </button>
    </div>

    {#if refine}
      <div class="refine">
        <div class="rhead"><Icon name="bot" size={13} /><b>Refine with an agent</b></div>
        <p class="np-dim np-small">Starts a chat in which an agent reads the code, then rewrites this idea: a sharper title and summary, what exists today, a plan and the questions only you can answer. It cannot change files or run commands.</p>
        <label class="lbl" for="refine-agent">Agent</label>
        <select id="refine-agent" class="np-input i-agent" bind:value={agent}>
          <option value={ANY}>Any available agent</option>
          {#each agents as a (a.key)}<option value={a.key}>{a.key}{a.use ? ` — ${a.use}` : ''}</option>{/each}
        </select>
        <label class="lbl" for="refine-hint">Focus (optional)</label>
        <textarea id="refine-hint" class="np-input i-hint" rows="2" placeholder="e.g. check whether the Files plugin already does part of this" bind:value={hint}></textarea>
      </div>
    {/if}

    {#if error}
      <div class="err" role="alert">
        {error}
        {#if conflict}<button type="button" class="np-btn np-btn-sm" onclick={reload}>Load the current version</button>{/if}
      </div>
    {/if}
  </form>
  {#snippet footer()}
    <button type="button" class="np-btn np-btn-ghost refine-toggle" aria-pressed={refine} title="Task an agent to define this idea better" onclick={() => (refine = !refine)}>
      <Icon name="bot" size={13} />Refine with an agent
    </button>
    <span class="grow"></span>
    <button type="button" class="np-btn np-btn-ghost" onclick={cancel}>Cancel</button>
    <button type="submit" form="idea-form" class="np-btn np-btn-primary i-submit" disabled={!title.trim() || busy}>
      {busy ? 'Saving…' : label} <span class="np-kbd">Ctrl+Enter</span>
    </button>
  {/snippet}
</Modal>

<style>
  .form {
    display: flex;
    flex-direction: column;
    gap: 8px;
  }
  .form.drop {
    outline: 1px dashed var(--border-strong, #666);
    outline-offset: 4px;
  }
  .idnote {
    font-family: var(--font-mono, monospace);
  }
  textarea {
    width: 100%;
    height: auto;
    padding: 8px 10px;
    line-height: 1.5;
    resize: vertical;
  }
  .row {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
    align-items: center;
  }
  .row .np-seg button {
    text-transform: capitalize;
  }
  .i-project,
  .i-status {
    flex: 1 1 140px;
    min-width: 0;
  }
  .thumbs {
    display: flex;
    flex-wrap: wrap;
    gap: 6px;
  }
  .thumbs figure {
    position: relative;
    display: flex;
    align-items: center;
    justify-content: center;
    margin: 0;
    width: 72px;
    height: 54px;
    border: 1px solid var(--border);
    border-radius: 4px;
    overflow: hidden;
  }
  .thumbs img {
    width: 100%;
    height: 100%;
    object-fit: cover;
    display: block;
  }
  .rm {
    position: absolute;
    top: 1px;
    right: 1px;
    display: flex;
    padding: 2px;
    border: 0;
    border-radius: 3px;
    background: var(--bg-2);
    color: var(--fg);
    cursor: pointer;
  }
  .drop-hint,
  .note {
    font-size: var(--fs-sm);
    color: var(--fg-muted);
  }
  .note {
    margin: 0;
  }
  .refine {
    display: flex;
    flex-direction: column;
    gap: 6px;
    padding: 10px 12px;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--bg-2);
  }
  .refine p {
    margin: 0;
  }
  .rhead {
    display: flex;
    align-items: center;
    gap: 6px;
  }
  .lbl {
    font-size: var(--fs-sm);
    color: var(--fg-muted);
  }
  .err {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 8px;
    padding: 8px 10px;
    border: 1px solid var(--err, #c55);
    border-radius: var(--radius);
    font-size: var(--fs-sm);
  }
  .grow {
    flex: 1;
  }
  .refine-toggle[aria-pressed='true'] {
    color: var(--accent);
  }
  .refine-toggle :global(svg) {
    margin-right: 5px;
  }
  .np-btn :global(svg) {
    margin-right: 4px;
  }
</style>
