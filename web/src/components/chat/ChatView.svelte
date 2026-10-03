<script>
  import ChatHeader from './ChatHeader.svelte';
  import MessageList from './MessageList.svelte';
  import Composer from '../composer/Composer.svelte';
  import PluginTabHost from '../panels/PluginTabHost.svelte';
  import { app } from '../../lib/state/app.svelte.js';
  import { tabs } from '../../lib/state/tabs.svelte.js';
  import { sessionViews } from '../../lib/state/ui.svelte.js';

  /** One session's chat. Keyed on the session id by App, so only the active chat is in the DOM. */
  let { chat, sessionId } = $props();
  const session = $derived(app.sessionsById.get(sessionId) ?? null);
  let list = $state();

  // A session view (a plugin tab with panel "session") shown instead of the messages; the composer stays, so the user
  // can talk to the agent while looking at it. Views opened once stay mounted (hidden) until the chat is left.
  const viewKey = $derived(sessionViews[sessionId] ?? null);
  const view = $derived(viewKey ? (tabs.session.find((t) => t.key === viewKey) ?? null) : null);
  let mounted = $state([]);
  $effect(() => {
    if (view && !mounted.includes(view.key)) mounted = [...mounted, view.key];
  });
  const mountedViews = $derived(mounted.map((k) => tabs.session.find((t) => t.key === k)).filter(Boolean));
</script>

<div class="chat">
  {#if session}
    <ChatHeader {session} />
    <div class="messages" class:hidden={!!view}>
      <MessageList bind:this={list} {chat} {session} />
    </div>
    {#each mountedViews as v (v.key)}
      <div class="view" class:hidden={view?.key !== v.key} data-view={v.key}>
        <PluginTabHost tab={v.plugin} {sessionId} visible={view?.key === v.key} />
      </div>
    {/each}
    <Composer {chat} {session} onsent={() => list?.scrollToBottom(true)} />
  {:else}
    <div class="np-empty">Loading session…</div>
  {/if}
</div>

<style>
  .chat {
    flex: 1;
    min-height: 0;
    display: flex;
    flex-direction: column;
  }
  .messages,
  .view {
    flex: 1;
    min-height: 0;
    display: flex;
    flex-direction: column;
  }
  .hidden {
    display: none;
  }
</style>
