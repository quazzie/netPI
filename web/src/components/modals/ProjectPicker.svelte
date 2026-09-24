<script>
  import Icon from '../../lib/kit/Icon.svelte';
  import Popover from '../Popover.svelte';
  import { app, setSessionProject } from '../../lib/state/app.svelte.js';
  import { openPanelTab } from '../../lib/state/tabs.svelte.js';
  import { toast } from '../../lib/state/ui.svelte.js';

  /** Attach a project to a session (sessions.setProject). data: { sessionId, anchor } */
  let { data, onclose } = $props();
  let q = $state('');
  let index = $state(0);

  const session = $derived(data ? app.sessionsById.get(data.sessionId) : null);
  const items = $derived.by(() => {
    const query = q.trim().toLowerCase();
    const list = app.projects.filter((p) => !query || p.name.toLowerCase().includes(query) || p.path.toLowerCase().includes(query));
    return [{ id: null, name: 'No project', path: 'default workspace' }, ...list];
  });

  async function choose(p) {
    // read everything from props BEFORE closing: onclose() unmounts this popover and `data` becomes null
    const sessionId = data?.sessionId;
    const current = session?.projectId ?? null;
    onclose();
    if (!sessionId || current === p.id) return;
    const s = await setSessionProject(sessionId, p.id);
    if (s) toast(p.id ? `Project: ${p.name}` : 'Project detached');
  }

  function onKey(e) {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      index = Math.min(items.length - 1, index + 1);
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      index = Math.max(0, index - 1);
    } else if (e.key === 'Enter') {
      e.preventDefault();
      if (items[index]) choose(items[index]);
    }
  }
  function focus(n) {
    n.focus();
  }
</script>

<Popover anchor={data?.anchor} placement={data?.anchor?.closest?.('[data-composer]') ? 'top-start' : 'bottom-end'} width={320} {onclose}>
  <div class="head">
    <Icon name="search" size={13} />
    <input placeholder="Attach project…" bind:value={q} onkeydown={onKey} use:focus spellcheck="false" />
  </div>
  <div class="list np-scroll">
    {#each items as p, i (p.id ?? '__none')}
      <button class="item" class:active={i === index} class:current={(session?.projectId ?? null) === p.id} onclick={() => choose(p)} onmouseenter={() => (index = i)}>
        <Icon name={p.id ? 'folder' : 'x'} size={14} />
        <span class="main">
          <span class="np-ellipsis name">{p.name}</span>
          <span class="np-ellipsis path np-mono">{p.path}</span>
        </span>
        {#if (session?.projectId ?? null) === p.id}<Icon name="check" size={13} />{/if}
      </button>
    {/each}
  </div>
  <button class="manage" onclick={() => (onclose(), openPanelTab('core/projects'))}>
    <Icon name="settings" size={13} /> Manage projects…
  </button>
</Popover>

<style>
  .head {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 8px 10px;
    border-bottom: 1px solid var(--border);
    color: var(--fg-dim);
  }
  .head input {
    flex: 1;
    border: 0;
    outline: none;
    background: transparent;
    color: var(--fg);
  }
  .list {
    padding: 4px;
    max-height: 320px;
  }
  .item {
    display: flex;
    align-items: center;
    gap: 9px;
    width: 100%;
    padding: 5px 8px;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--fg-muted);
    text-align: left;
  }
  .item.active {
    background: var(--bg-3);
  }
  .item.current {
    color: var(--accent);
  }
  .main {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    line-height: 1.3;
  }
  .name {
    color: var(--fg);
  }
  .path {
    font-size: 10.5px;
    color: var(--fg-dim);
  }
  .manage {
    display: flex;
    align-items: center;
    gap: 7px;
    height: 34px;
    padding: 0 12px;
    border: 0;
    border-top: 1px solid var(--border);
    background: transparent;
    color: var(--fg-muted);
    font-size: var(--fs-sm);
    text-align: left;
  }
  .manage:hover {
    color: var(--fg);
    background: var(--bg-2);
  }
</style>
