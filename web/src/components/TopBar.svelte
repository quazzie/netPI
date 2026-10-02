<script>
  import { tick } from 'svelte';
  import Icon from '../lib/kit/Icon.svelte';
  import IconButton from '../lib/kit/IconButton.svelte';
  import Menu from '../lib/kit/Menu.svelte';
  import BudgetPill from './BudgetPill.svelte';
  import { app, activate, closeTab, moveTab, newSession, sessionStatus, projectOf } from '../lib/state/app.svelte.js';
  import { modals, prefs } from '../lib/state/ui.svelte.js';
  import { conn, reconnectNow } from '../lib/rpc.svelte.js';

  let tabsEl = $state();
  let dragId = $state(null);
  let retryIn = $state(0);

  // keep the active tab visible
  $effect(() => {
    const id = app.activeId;
    if (!id || !tabsEl) return;
    tick().then(() => {
      tabsEl?.querySelector(`[data-tab="${CSS.escape(id)}"]`)?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    });
  });

  // reconnect countdown
  $effect(() => {
    if (conn.status === 'open') return;
    const t = setInterval(() => (retryIn = Math.max(0, Math.ceil((conn.retryAt - Date.now()) / 1000))), 250);
    return () => clearInterval(t);
  });

  function onWheel(e) {
    if (Math.abs(e.deltaY) > Math.abs(e.deltaX)) {
      tabsEl.scrollLeft += e.deltaY;
      e.preventDefault();
    }
  }

  function onAux(e, id) {
    if (e.button === 1) {
      e.preventDefault();
      closeTab(id);
    }
  }

  function onDragStart(e, id) {
    dragId = id;
    e.dataTransfer.effectAllowed = 'move';
    e.dataTransfer.setData('text/plain', id);
  }
  function onDragOver(e, id) {
    if (!dragId || dragId === id) return;
    e.preventDefault();
    const r = e.currentTarget.getBoundingClientRect();
    const after = e.clientX > r.left + r.width / 2;
    let to = app.openTabs.indexOf(id);
    const from = app.openTabs.indexOf(dragId);
    if (after && from > to) to++;
    else if (!after && from < to) to--;
    moveTab(dragId, to);
  }

  // favorite projects (Projects tab ★) that still exist: the chevron menu is the way to start a session
  // in a project (the + button beside it always starts without one)
  const favorites = $derived(prefs.favorites.map((id) => app.projectsById.get(id)).filter(Boolean));
  const newSessionItems = $derived([
    { header: 'Favorites' },
    ...favorites.map((p) => ({
      label: p.name,
      icon: 'folder',
      hint: p.path,
      onclick: () => newSession({ projectId: p.id }),
    })),
  ]);

  function title(id) {
    return app.sessionsById.get(id)?.title || 'New session';
  }
  function tip(id) {
    const s = app.sessionsById.get(id);
    if (!s) return '';
    const p = projectOf(s);
    return [s.title || 'New session', p ? `project: ${p.name}` : null, s.model ? `model: ${s.model}` : null]
      .filter(Boolean)
      .join('\n');
  }

</script>

<header class="topbar">
  <div class="brand" title="netPI">
    <svg width="18" height="18" viewBox="0 0 32 32" aria-hidden="true">
      <rect width="32" height="32" rx="8" fill="var(--accent-soft)" />
      <path d="M9 23V9h3.2l7.6 9.6V9H23v14h-3.2L12.2 13.4V23z" fill="var(--accent)" />
    </svg>
    <span class="word">net<b>PI</b></span>
  </div>

  <div class="tabs np-scroll" bind:this={tabsEl} onwheel={onWheel} role="tablist" aria-label="Open sessions">
    {#each app.openTabs as id (id)}
      {@const status = sessionStatus(id)}
      <div
        class="tab"
        class:active={id === app.activeId}
        class:dragging={dragId === id}
        data-tab={id}
        role="tab"
        tabindex="0"
        aria-selected={id === app.activeId}
        title={tip(id)}
        draggable="true"
        onclick={() => activate(id)}
        onkeydown={(e) => (e.key === 'Enter' || e.key === ' ') && activate(id)}
        onauxclick={(e) => onAux(e, id)}
        onmousedown={(e) => e.button === 1 && e.preventDefault()}
        ondragstart={(e) => onDragStart(e, id)}
        ondragover={(e) => onDragOver(e, id)}
        ondragend={() => (dragId = null)}
      >
        <span class="np-dot" data-status={status}></span>
        <span class="tab-title">{title(id)}</span>
        <button
          class="tab-close"
          title="Close (Ctrl+W)"
          aria-label="Close tab"
          onclick={(e) => {
            e.stopPropagation();
            closeTab(id);
          }}><Icon name="x" size={12} stroke={2} /></button
        >
      </div>
    {/each}
  </div>
  <div class="quick">
    <button class="new-tab" title="New session (Ctrl+T)" aria-label="New session" onclick={() => newSession()}>
      <Icon name="plus" size={15} />
    </button>
    {#if favorites.length}
      <Menu items={newSessionItems} minWidth={220}>
        {#snippet trigger({ toggle, open })}
          <button class="quick-more" aria-expanded={open} aria-haspopup="menu" title="Favorites — new session in a starred project" onclick={toggle}>
            <Icon name="chevron-down" size={13} />
          </button>
        {/snippet}
      </Menu>
    {/if}
  </div>
  <span class="gap"></span>

  <div class="right">
    <BudgetPill />
    <button
      class="conn"
      data-status={conn.status}
      title={conn.status === 'open' ? `Connected${conn.version ? ` · v${conn.version}` : ''}` : 'Click to retry now'}
      onclick={() => conn.status !== 'open' && reconnectNow()}
    >
      <span class="np-dot" data-status={conn.status === 'open' ? 'ok' : conn.status === 'connecting' ? 'queued' : 'error'}
      ></span>
      {#if conn.status !== 'open'}
        <span class="conn-text"
          >{conn.status === 'connecting' ? 'Connecting…' : `Reconnecting${retryIn ? ` in ${retryIn}s` : '…'}`}</span
        >
      {/if}
    </button>
    <IconButton icon="settings" title="Settings (Ctrl+,)" onclick={() => (modals.settings = true)} />
  </div>
</header>

<style>
  .topbar {
    display: flex;
    align-items: center;
    gap: 8px;
    height: var(--topbar-h);
    padding: 0 8px 0 10px;
    background: var(--bg-1);
    border-bottom: 1px solid var(--border);
    flex: none;
    user-select: none;
  }
  .brand {
    display: flex;
    align-items: center;
    gap: 7px;
    padding-right: 6px;
    flex: none;
  }
  .word {
    font-weight: 600;
    font-size: 13.5px;
    letter-spacing: -0.01em;
    color: var(--fg-muted);
  }
  .word b {
    color: var(--fg);
    font-weight: 700;
  }
  .tabs {
    flex: 0 1 auto;
    min-width: 0;
    display: flex;
    align-items: center;
    gap: 2px;
    height: 100%;
    overflow-x: auto;
    overflow-y: hidden;
    scrollbar-width: none;
  }
  .tabs::-webkit-scrollbar {
    display: none;
  }
  .tab {
    position: relative;
    display: flex;
    align-items: center;
    gap: 7px;
    flex: 0 1 auto;
    min-width: 96px;
    max-width: 210px;
    height: 28px;
    padding: 0 4px 0 10px;
    border-radius: 6px;
    border: 1px solid transparent;
    color: var(--fg-muted);
    cursor: default;
    transition:
      background var(--t-fast),
      color var(--t-fast);
  }
  .tab:hover {
    background: var(--bg-2);
    color: var(--fg);
  }
  .tab.active {
    background: var(--bg-3);
    color: var(--fg);
    border-color: var(--border-strong);
  }
  .tab.dragging {
    opacity: 0.5;
  }
  .tab-title {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
    font-size: var(--fs);
  }
  .tab-close {
    display: grid;
    place-items: center;
    width: 18px;
    height: 18px;
    border: 0;
    border-radius: 4px;
    padding: 0;
    background: transparent;
    color: var(--fg-dim);
    opacity: 0;
    flex: none;
  }
  .tab:hover .tab-close,
  .tab.active .tab-close {
    opacity: 1;
  }
  .tab-close:hover {
    background: var(--bg-3);
    color: var(--fg);
  }
  .tab.active .tab-close:hover {
    background: var(--border-strong);
  }
  .new-tab {
    display: grid;
    place-items: center;
    width: 28px;
    height: 28px;
    flex: none;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--fg-dim);
  }
  .new-tab:hover {
    background: var(--bg-2);
    color: var(--fg);
  }
  .quick {
    display: flex;
    align-items: center;
    gap: 2px;
    flex: 0 1 auto;
    min-width: 28px;
    overflow: hidden;
  }
  .quick-more {
    display: grid;
    place-items: center;
    width: 18px;
    height: 28px;
    flex: none;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--fg-dim);
  }
  .quick-more:hover,
  .quick-more[aria-expanded='true'] {
    background: var(--bg-2);
    color: var(--fg);
  }
  .gap {
    flex: 1;
  }
  .right {
    display: flex;
    align-items: center;
    gap: 4px;
    flex: none;
  }
  .conn {
    display: flex;
    align-items: center;
    gap: 6px;
    height: 26px;
    padding: 0 8px;
    border: 0;
    border-radius: 13px;
    background: transparent;
    color: var(--fg-muted);
    font-size: var(--fs-sm);
    cursor: default;
  }
  .conn:not([data-status='open']) {
    background: var(--err-soft);
    color: var(--err);
    cursor: pointer;
  }
  .conn[data-status='connecting'] {
    background: var(--warn-soft);
    color: var(--warn);
  }
</style>
