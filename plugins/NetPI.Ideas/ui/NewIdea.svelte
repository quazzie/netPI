<script>
  import { Button, IconButton, formatBytes } from '@netpi/kit';
  import { PRIORITIES, parseTags } from './model.js';

  let { onadd, oncancel, onattach, ondetach, projects = [], activeProjectId = '', maxImages = 6 } = $props();
  let title = $state('');
  let summary = $state('');
  let priority = $state('medium');
  let tags = $state('');
  let project = $state('');
  let busy = $state(false);
  let attaching = $state(false);
  let dragOver = $state(false);
  let fileInput = $state(null);
  // Stored references ({ path, name, mediaType, bytes }) plus the data URL kept only while the form is open, for the
  // thumbnail. The bytes themselves live on disk (ideas.attach); the idea keeps the reference.
  let images = $state([]);
  let note = $state('');

  async function submit(e) {
    e?.preventDefault();
    if (!title.trim() || busy) return;
    busy = true;
    await onadd({ title: title.trim(), summary: summary.trim() || undefined, priority, tags: parseTags(tags), images: refs() }, project || undefined);
    busy = false;
  }
  const refs = () => images.map(({ path, name, mediaType, bytes }) => ({ path, name, mediaType, bytes }));

  // The tab owns the RPCs (it toasts their errors), so attaching is one call out and one stored reference back.
  async function addFiles(files) {
    const room = maxImages - images.length;
    if (room <= 0) return;
    const picked = [...files].filter((f) => f.type.startsWith('image/')).slice(0, room);
    if (!picked.length) return;
    attaching = true;
    try {
      for (const file of picked) {
        const stored = await onattach(file);
        if (stored?.image) images = [...images, stored.image];
        if (stored?.note) note = stored.note;
      }
    } finally {
      // Whatever the attach did to each file (stored, refused, or the tab toasted an error), the button comes back.
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
    await ondetach(path);
  }
  function focus(n) {
    n.focus();
  }
</script>

<form
  class="new np-card"
  class:drop={dragOver}
  onsubmit={submit}
  onpaste={onPaste}
  ondragover={(e) => { e.preventDefault(); dragOver = true; }}
  ondragleave={() => (dragOver = false)}
  ondrop={onDrop}
>
  <input class="np-input" placeholder="Idea title (one line, on its own)" bind:value={title} use:focus onkeydown={(e) => e.key === 'Escape' && oncancel()} />
  <textarea class="np-input" rows="2" placeholder="Summary (optional)" bind:value={summary}></textarea>
  {#if images.length || dragOver}
    <div class="thumbs">
      {#each images as img (img.path)}
        <figure title="{img.name} · {formatBytes(img.bytes)}">
          <img src={img.url} alt={img.name} />
          <IconButton icon="x" size={11} title="Remove image" onclick={() => removeImage(img.path)} />
        </figure>
      {/each}
      {#if dragOver}<span class="drop-hint">Drop to attach</span>{/if}
    </div>
  {/if}
  {#if note}<p class="note">{note}</p>{/if}
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
    <input bind:this={fileInput} type="file" accept="image/*" multiple hidden onchange={(e) => { addFiles(e.currentTarget.files ?? []); e.currentTarget.value = ''; }} />
    <Button variant="ghost" size="sm" title="Attach an image (or paste one, or drop it here)" disabled={attaching || images.length >= maxImages} onclick={() => fileInput?.click()}>
      {attaching ? 'Attaching…' : 'Image'}
    </Button>
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
  .new.drop {
    outline: 1px dashed var(--border-strong, #666);
    outline-offset: -3px;
  }
  textarea {
    resize: vertical;
    min-height: 44px;
  }
  .thumbs {
    display: flex;
    flex-wrap: wrap;
    gap: 6px;
  }
  .thumbs figure {
    position: relative;
    margin: 0;
    width: 64px;
    height: 48px;
    border: 1px solid var(--border, #555);
    border-radius: 4px;
    overflow: hidden;
  }
  .thumbs img {
    width: 100%;
    height: 100%;
    object-fit: cover;
    display: block;
  }
  .thumbs :global(button) {
    position: absolute;
    top: 1px;
    right: 1px;
  }
  .drop-hint {
    display: flex;
    align-items: center;
    padding: 0 8px;
    font-size: var(--fs-sm);
    color: var(--fg-muted);
  }
  .note {
    margin: 0;
    font-size: var(--fs-xs);
    color: var(--fg-muted);
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
