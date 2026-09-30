<script>
  import { onMount } from 'svelte';
  import TopBar from './components/TopBar.svelte';
  import SidePanel from './components/panels/SidePanel.svelte';
  import SessionsTab from './components/panels/SessionsTab.svelte';
  import ProjectsTab from './components/panels/ProjectsTab.svelte';
  import ChatView from './components/chat/ChatView.svelte';
  import Welcome from './components/Welcome.svelte';
  import Toasts from './components/Toasts.svelte';
  import Modals from './components/modals/Modals.svelte';
  import { app, startApp, activate, closeTab, cycleTab, newSession, abortAgent, isBusy, goneSessions } from './lib/state/app.svelte.js';
  import { getChat } from './lib/state/chat.svelte.js';
  import { modals, togglePanel, applyTheme } from './lib/state/ui.svelte.js';
  import { registerCoreTab } from './lib/state/tabs.svelte.js';
  import { notifyAppChange } from './lib/pluginCtx.js';

  registerCoreTab({ key: 'core/sessions', title: 'Sessions', icon: 'sessions', panel: 'left', order: 0, component: SessionsTab });
  registerCoreTab({ key: 'core/projects', title: 'Projects', icon: 'folder', panel: 'left', order: 10, component: ProjectsTab });

  // The store of the active chat — but only while that session's tab is still open and the session is not gone:
  // when a chat is deleted or its tab closed, activeId flips to another tab (or null) in the same flush, and
  // during that window a Svelte teardown can still answer reactive reads with the just-closed id (from its
  // rollback map). The goneSessions check is a plain, non-reactive read, so it is the only one the teardown
  // cannot answer stale: materializing a store for the gone session would leave a zombie in the LRU (evicting
  // a real tab), and a tab that is gone is never rendered, so nothing is lost by not materializing it.
  const chat = $derived.by(() => {
    const id = app.activeId;
    return id && !goneSessions.has(id) && app.openTabs.includes(id) ? getChat(id) : null;
  });

  // Plugin ctx.app.onChange(): fire when the active session / its project changes.
  $effect(() => {
    app.activeId;
    app.activeSession;
    app.activeProject;
    notifyAppChange();
  });

  onMount(() => {
    applyTheme();
    startApp();
  });

  function anyModalOpen() {
    return (
      modals.settings || modals.palette || modals.help || modals.folder || modals.confirm || modals.prompt || modals.lightbox
    );
  }

  function onKeydown(e) {
    const mod = e.ctrlKey || e.metaKey;
    if (e.key === 'Escape' && !anyModalOpen() && app.activeId && isBusy(app.activeId) && !e.defaultPrevented) {
      // Esc anywhere (outside popups) stops the running agent — but not Esc that ends typing in another field (a title
      // rename, the session search): the composer handles its own Esc
      const t = e.target;
      const otherField = t.closest?.('input, textarea, select, [contenteditable="true"]') && !t.closest?.('.composer');
      if (!t.closest?.('.popover') && !otherField) {
        e.preventDefault();
        abortAgent(app.activeId);
      }
      return;
    }
    if (!mod) return;
    const k = e.key.toLowerCase();
    if (k === 't' && !e.shiftKey && !e.altKey) {
      e.preventDefault();
      newSession();
    } else if (k === 'w' && !e.shiftKey && !e.altKey) {
      if (app.activeId) {
        e.preventDefault();
        closeTab(app.activeId);
      }
    } else if (e.key === 'Tab') {
      e.preventDefault();
      cycleTab(e.shiftKey ? -1 : 1);
    } else if (e.key === 'PageDown' || e.key === 'PageUp') {
      e.preventDefault();
      cycleTab(e.key === 'PageDown' ? 1 : -1);
    } else if (/^[1-9]$/.test(e.key) && !e.altKey) {
      e.preventDefault();
      const n = +e.key;
      const tabs = app.openTabs;
      const id = n === 9 ? tabs[tabs.length - 1] : tabs[n - 1];
      if (id) activate(id);
    } else if (k === 'b' && !e.shiftKey) {
      e.preventDefault();
      togglePanel(e.altKey ? 'right' : 'left');
    } else if (k === 'k' && !e.shiftKey && !e.altKey) {
      e.preventDefault();
      modals.palette = !modals.palette;
    } else if (e.key === ',') {
      e.preventDefault();
      modals.settings = true;
    } else if (e.key === '/' && !e.altKey) {
      e.preventDefault();
      modals.help = !modals.help;
    }
  }
</script>

<svelte:window onkeydown={onKeydown} />

<div class="app">
  <TopBar />
  <div class="main">
    <SidePanel side="left" />
    <main class="center">
      {#if chat && app.activeId}
        {#key app.activeId}
          <ChatView {chat} sessionId={app.activeId} />
        {/key}
      {:else}
        <Welcome />
      {/if}
    </main>
    <SidePanel side="right" />
  </div>
  <Toasts />
  <Modals />
</div>

<style>
  .app {
    display: flex;
    flex-direction: column;
    height: 100%;
    min-height: 0;
  }
  .main {
    flex: 1;
    display: flex;
    min-height: 0;
    min-width: 0;
  }
  .center {
    flex: 1;
    min-width: 320px;
    display: flex;
    flex-direction: column;
    background: var(--bg);
    position: relative;
  }
</style>
