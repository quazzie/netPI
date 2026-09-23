<script>
  import ChatHeader from './ChatHeader.svelte';
  import MessageList from './MessageList.svelte';
  import Composer from '../composer/Composer.svelte';
  import { app } from '../../lib/state/app.svelte.js';

  /** One session's chat. Keyed on the session id by App, so only the active chat is in the DOM. */
  let { chat, sessionId } = $props();
  const session = $derived(app.sessionsById.get(sessionId) ?? null);
  let list = $state();
</script>

<div class="chat">
  {#if session}
    <ChatHeader {session} />
    <MessageList bind:this={list} {chat} {session} />
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
</style>
