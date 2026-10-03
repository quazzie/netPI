<script>
  /**
   * The projects dialog's view of one project: its name, folder and profile, the chats in it, and the instruction
   * files and skills that apply to that folder. What applies to a folder is read here, when the project (or its
   * folder) is what is being shown — so a folder the save moved re-reads them.
   */
  import Icon from '../../lib/kit/Icon.svelte';
  import IconButton from '../../lib/kit/IconButton.svelte';
  import TimeAgo from '../../lib/kit/TimeAgo.svelte';
  import { app } from '../../lib/state/app.svelte.js';
  import { rpc } from '../../lib/rpc.svelte.js';
  import { desktop } from '../../lib/kit/host.js';

  let { project, form = $bindable(), onsubmit, onbrowse, onprofile, onstart, onopen } = $props();

  const MAX_SESSIONS = 6;

  let files = $state(null); // instruction files of this folder; null while loading
  let filesError = $state('');
  let skills = $state(null); // skills.list of this folder; null while loading or without the skills plugin
  let profiles = $state(null); // the profiles plugin: the one new sessions of this project start with

  const sessions = $derived(app.sessions.filter((s) => s.projectId === project.id && !s.archived && !s.parentSessionId));

  function focus(node) {
    node.focus();
  }

  $effect(() => {
    // what applies to a folder: read it for this project, and read it again when the folder it applies to changes
    const id = project.id;
    const path = project.path;
    const current = () => id === project.id && path === project.path;
    files = null;
    filesError = '';
    skills = null;
    rpc('skills.list', { projectId: id })
      .then((r) => current() && (skills = r))
      .catch(() => {});
    rpc('profiles.list')
      .then((r) => (profiles = r))
      .catch(() => (profiles = null));
    rpc('agentsmd.list', { projectId: id })
      .then((res) => {
        if (current()) files = Array.isArray(res) ? res : [];
      })
      .catch((e) => {
        if (current()) filesError = e.message;
      });
  });

  function open(s) {
    onopen(s);
  }
</script>

<form id="project-form" class="fields" onsubmit={onsubmit}>
  <label class="field">
    <span>Name</span>
    <input class="np-input" bind:value={form.name} use:focus />
  </label>
  <label class="field">
    <span>Folder</span>
    <div class="pathrow">
      <input class="np-input np-mono" bind:value={form.path} spellcheck="false" />
      <button type="button" class="np-btn" title="Choose another folder" onclick={onbrowse}><Icon name="folder-open" size={14} /> Browse…</button>
      {#if desktop.available}
        <IconButton icon="external" title="Show in Explorer" onclick={() => desktop.revealPath(project.path)} />
      {/if}
    </div>
    <span class="hint">Sessions in this project follow the folder; their agents are told at their next turn.</span>
  </label>
  {#if profiles?.profiles?.length}
    {@const global = profiles.profiles.find((p) => p.id === profiles.defaultProfile)}
    <label class="field">
      <span>Profile of new sessions</span>
      <select class="np-input" value={project.meta?.profile ?? ''} onchange={(e) => onprofile(e.currentTarget.value)} aria-label="Profile of new sessions">
        <option value="">the default ({global?.name ?? 'no profile'})</option>
        {#each profiles.profiles as p (p.id)}<option value={p.id}>{p.name}</option>{/each}
        <option value="none">no profile</option>
      </select>
      <span class="hint">Its instructions and tools; each chat can still pick another.</span>
    </label>
  {/if}
</form>

<section>
  <div class="np-section-title">
    Sessions <span class="np-section-count">{sessions.length}</span>
    <span class="np-section-actions">
      <button class="np-btn np-btn-sm" onclick={() => onstart(project)}><Icon name="plus" size={12} /> New session</button>
    </span>
  </div>
  {#each sessions.slice(0, MAX_SESSIONS) as s (s.id)}
    <button class="sess" onclick={() => open(s)}>
      <Icon name="sessions" size={13} />
      <span class="np-ellipsis sname">{s.title || 'New session'}</span>
      <TimeAgo time={s.updatedAt} class="np-dim np-small" />
    </button>
  {:else}
    <p class="hint">No sessions yet.</p>
  {/each}
  {#if sessions.length > MAX_SESSIONS}
    <p class="hint">{sessions.length - MAX_SESSIONS} more in the Sessions panel.</p>
  {/if}
</section>

<section>
  <div class="np-section-title">
    Instruction files {#if files}<span class="np-section-count">{files.length}</span>{/if}
  </div>
  {#if filesError}
    <p class="hint">Unavailable: {filesError}</p>
  {:else if !files}
    <p class="hint">Loading…</p>
  {:else if !files.length}
    <p class="hint">No AGENTS.md or CLAUDE.md applies to this folder.</p>
  {:else}
    {#each files as f (f.path)}
      <div class="frow">
        <span class="scope" data-s={f.scope}>{f.scope}</span>
        <span class="fpath np-mono" title={f.path}><bdi>{f.path}</bdi></span>
        {#if desktop.available}
          <IconButton icon="external" size="sm" title="Show in Explorer" onclick={() => desktop.revealPath(f.path)} />
        {/if}
      </div>
    {/each}
    <p class="hint">Agents get these as notices; edits are announced at their next turn.</p>
  {/if}
</section>

{#if skills}
  <section>
    <div class="np-section-title">
      Skills <span class="np-section-count">{skills.skills.length}</span>
    </div>
    {#each skills.skills as s (s.path)}
      <div class="frow" title={s.description}>
        <span class="scope" data-s={s.scope}>{s.scope}</span>
        <span class="skname np-mono" class:off={s.disabled}>{s.name}</span>
        <span class="sdesc">{s.disabled ? 'switched off' : s.userOnly ? '/skill: only' : s.description}</span>
        {#if desktop.available}
          <IconButton icon="external" size="sm" title="Show in Explorer" onclick={() => desktop.revealPath(s.path)} />
        {/if}
      </div>
    {:else}
      <p class="hint">No skills here: put them in .agents/skills (a folder with a SKILL.md each).</p>
    {/each}
    {#each skills.problems as p (p.path + p.message)}
      <div class="problem" data-level={p.level} title={p.path}>
        <Icon name={p.level === 'error' ? 'alert-circle' : 'alert'} size={12} />
        <span class="np-mono pfile">{p.path.split(/[\\/]/).slice(-2).join('/')}</span>
        <span>{p.message}</span>
      </div>
    {/each}
    {#if skills.skills.length}<p class="hint">Agents see them as notices and load one when a task matches; /skill:name loads one for your message.</p>{/if}
  </section>
{/if}

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
  section {
    margin-top: 20px;
  }
  .sess {
    display: flex;
    align-items: center;
    gap: 9px;
    width: calc(100% + 12px);
    height: 30px;
    margin: 0 -6px;
    padding: 0 6px;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--fg-muted);
    text-align: left;
  }
  .sess:hover {
    background: var(--bg-2);
    color: var(--fg);
  }
  .sname {
    flex: 1;
    color: var(--fg);
  }
  .frow {
    display: flex;
    align-items: center;
    gap: 8px;
    min-height: 26px;
  }
  .fpath {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
    direction: rtl;
    text-align: left;
    font-size: 11.5px;
    color: var(--fg-dim);
  }
  .scope {
    flex: none;
    padding: 0 6px;
    border-radius: 8px;
    background: var(--bg-3);
    color: var(--fg-muted);
    font-size: 10px;
    line-height: 16px;
  }
  .scope[data-s='project'] {
    background: var(--accent-soft);
    color: var(--accent);
  }
  .skname {
    flex: none;
    max-width: 45%;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    font-size: 12px;
  }
  .skname.off {
    color: var(--fg-dim);
    text-decoration: line-through;
  }
  .sdesc {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    color: var(--fg-dim);
    font-size: var(--fs-xs);
  }
  .problem {
    display: flex;
    align-items: baseline;
    gap: 6px;
    padding: 2px 0;
    font-size: var(--fs-xs);
    color: var(--warn);
  }
  .problem[data-level='error'] {
    color: var(--err);
  }
  .problem :global(svg) {
    flex: none;
    align-self: center;
  }
  .pfile {
    flex: none;
    color: var(--fg-muted);
  }
</style>
