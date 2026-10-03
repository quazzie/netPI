<script>
  /**
   * The projects dialog's list: every project with its session count, and new session / edit / remove on each row.
   * The filter, the order and the count are the ones the projects panel shows (lib/projects.js), so the two lists
   * cannot drift apart.
   */
  import Icon from '../../lib/kit/Icon.svelte';
  import IconButton from '../../lib/kit/IconButton.svelte';
  import { app } from '../../lib/state/app.svelte.js';
  import { projectCounts, matchingProjects } from '../../lib/projects.js';

  let { onnew, onedit, onremove, onstart } = $props();

  let q = $state('');
  const counts = $derived(projectCounts(app.sessions));
  const list = $derived(matchingProjects(app.projects, q));

  function focus(node) {
    node.focus();
  }
</script>

<div class="lhead">
  <div class="search">
    <Icon name="search" size={13} />
    <input placeholder="Filter projects" bind:value={q} spellcheck="false" use:focus />
  </div>
  <button class="np-btn np-btn-primary" onclick={onnew}><Icon name="plus" size={14} /> New project</button>
</div>
<div class="plist">
  {#each list as p (p.id)}
    <div class="prow">
      <button class="pmain" title="Edit {p.name}" onclick={() => onedit(p)}>
        <Icon name="folder" size={15} />
        <span class="ptext">
          <span class="pname"
            ><span class="np-ellipsis">{p.name}</span><span class="pcount"
              >{counts.get(p.id) ?? 0} {counts.get(p.id) === 1 ? 'session' : 'sessions'}</span
            ></span
          >
          <span class="ppath np-mono" title={p.path}><bdi>{p.path}</bdi></span>
        </span>
      </button>
      <div class="pacts">
        <IconButton icon="plus" size="sm" title="New session in {p.name}" onclick={() => onstart(p)} />
        <IconButton icon="pencil" size="sm" title="Edit" onclick={() => onedit(p)} />
        <IconButton icon="trash" size="sm" title="Remove" onclick={() => onremove(p)} />
      </div>
    </div>
  {:else}
    <div class="np-empty">
      <Icon name="folder" size={22} />
      {q ? 'No matching projects' : 'No projects yet. A project is a name and a folder the agent works in.'}
    </div>
  {/each}
</div>

<style>
  .lhead {
    display: flex;
    gap: 8px;
    margin-bottom: 10px;
  }
  .search {
    flex: 1;
    min-width: 0;
    display: flex;
    align-items: center;
    gap: 6px;
    height: 28px;
    padding: 0 8px;
    border: 1px solid var(--border-strong);
    border-radius: var(--radius-sm);
    background: var(--bg);
    color: var(--fg-dim);
  }
  .search:focus-within {
    border-color: var(--accent-line);
  }
  .search input {
    flex: 1;
    min-width: 0;
    border: 0;
    outline: none;
    background: transparent;
    color: var(--fg);
  }
  .plist {
    display: flex;
    flex-direction: column;
    gap: 2px;
    margin: 0 -6px;
  }
  .prow {
    display: flex;
    align-items: center;
    gap: 4px;
    padding-right: 4px;
    border-radius: 6px;
  }
  .prow:hover,
  .prow:focus-within {
    background: var(--bg-2);
  }
  .pmain {
    flex: 1;
    min-width: 0;
    display: flex;
    align-items: flex-start;
    gap: 9px;
    padding: 7px 6px;
    border: 0;
    background: transparent;
    color: var(--fg-dim);
    text-align: left;
  }
  .ptext {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 1px;
  }
  .pname {
    display: flex;
    align-items: baseline;
    gap: 8px;
    min-width: 0;
    color: var(--fg);
  }
  .pcount {
    flex: none;
    margin-left: auto;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
    font-variant-numeric: tabular-nums;
  }
  .ppath {
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
    direction: rtl;
    text-align: left;
    font-size: 11.5px;
    color: var(--fg-dim);
  }
  .pacts {
    flex: none;
    display: flex;
    gap: 1px;
  }
</style>
