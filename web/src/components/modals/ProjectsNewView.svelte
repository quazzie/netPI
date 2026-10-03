<script>
  /**
   * The projects dialog's create form: a folder and a name (the folder can be created with it). Opened from the
   * picker it is then attached to a session, or with `select` a new session starts in it — the dialog says which.
   */
  import Icon from '../../lib/kit/Icon.svelte';
  import { basename } from '../../lib/format.js';

  let { form = $bindable(), hint = '', onsubmit, onbrowse } = $props();

  function focus(node) {
    node.focus();
  }
</script>

<form id="project-form" class="fields" onsubmit={onsubmit}>
  <label class="field">
    <span>Folder</span>
    <div class="pathrow">
      <input class="np-input np-mono" placeholder="C:\src\my-project" bind:value={form.path} spellcheck="false" use:focus />
      <button type="button" class="np-btn" onclick={onbrowse}><Icon name="folder-open" size={14} /> Browse…</button>
    </div>
  </label>
  <label class="field">
    <span>Name</span>
    <input class="np-input" placeholder={form.path.trim() ? basename(form.path.trim()) : 'my-project'} bind:value={form.name} />
  </label>
  <label class="np-check np-small np-muted"><input type="checkbox" bind:checked={form.create} /> Create the folder if it doesn’t exist</label>
  {#if hint}<p class="hint">{hint}</p>{/if}
</form>

<style>
  .fields {
    display: flex;
    flex-direction: column;
    gap: 12px;
  }
  .field {
    display: flex;
    flex-direction: column;
    gap: 4px;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
  }
  .pathrow {
    display: flex;
    gap: 6px;
  }
  .pathrow .np-btn {
    flex: none;
  }
  .hint {
    margin: 0;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
    line-height: 1.45;
  }
</style>
