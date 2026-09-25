<script>
  import { onMount, tick } from 'svelte';
  import Icon from '../../lib/kit/Icon.svelte';
  import UserMessage from './UserMessage.svelte';
  import AssistantText from './AssistantText.svelte';
  import StepsGroup from './StepsGroup.svelte';
  import NoticeRow from './NoticeRow.svelte';
  import PromptRow from './PromptRow.svelte';
  import StatusRow from './StatusRow.svelte';
  import ShownImage from './ShownImage.svelte';
  import AskCard from './AskCard.svelte';
  import { createItemBuilder, withStream } from '../../lib/chatItems.js';
  import { isBusy, projectOf, forkSession } from '../../lib/state/app.svelte.js';
  import { modals } from '../../lib/state/ui.svelte.js';

  let { chat, session } = $props();

  const build = createItemBuilder();
  // the streaming answer is laid out like the finished one (thinking and tool calls join the open steps group), so the
  // chat does not jump when message.added replaces it
  // with the system prompts the chat was sent (the first above the first message when the window starts there)
  const items = $derived.by(() => {
    const built = build(chat.messages, chat.prompts, !chat.hasMore);
    return chat.hasNewer ? built : withStream(built, chat.stream);
  });
  const running = $derived(isBusy(session.id));
  // fork: a user message forks before it (its text goes to the new chat's message box), an answer forks after it
  const forkable = $derived(session.kind !== 'subagent');
  const textOf = (m) => (m.parts ?? []).filter((p) => p.type === 'text').map((p) => p.text).join('\n');
  const forkBefore = (m) => forkSession(session.id, m.seq - 1, textOf(m));
  const forkAfter = (m) => forkSession(session.id, m.seq);
  const base = $derived(projectOf(session)?.path ?? null);
  // index of the user message that started the current run (steering input does not start a new one)
  const lastUserIdx = $derived.by(() => {
    for (let i = items.length - 1; i >= 0; i--)
      if (items[i].kind === 'user' && items[i].msg.meta?.kind !== 'steer' && items[i].msg.meta?.delivery !== 'steer') return i;
    return -1;
  });

  let scroller = $state();
  let content = $state();
  let stick = true; // follow the bottom while new content arrives (plain var: no re-render on scroll)
  let lastTop = 0; // scrollTop at the last scroll event or programmatic scroll
  let showJump = $state(false);
  let restoring = true;

  function distanceFromBottom() {
    return scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight;
  }

  function pin() {
    scroller.scrollTop = scroller.scrollHeight;
    lastTop = scroller.scrollTop;
  }

  function onScroll() {
    if (restoring || !scroller) return;
    const top = scroller.scrollTop;
    const d = distanceFromBottom();
    // Only the user scrolling up unpins. Content that grew, or a view that shrank (the plan strip or queue chips
    // appearing above the composer), can put the bottom out of sight before the resize observer re-pins: that
    // must not count as leaving the bottom.
    if (d < 48) stick = true;
    else if (top < lastTop - 1) stick = false;
    lastTop = top;
    const jump = d > 320;
    if (jump !== showJump) showJump = jump;
  }

  export function scrollToBottom(force = false) {
    if (!scroller) return;
    if (force) stick = true;
    pin();
    showJump = false;
  }

  async function loadEarlier() {
    const first = content.querySelector('[data-key]');
    const key = first?.dataset.key;
    const before = first?.offsetTop ?? 0;
    stick = false;
    const changed = await chat.loadEarlier();
    if (!changed) return;
    await tick();
    const el = key ? content.querySelector(`[data-key="${CSS.escape(key)}"]`) : null;
    if (el) scroller.scrollTop += el.offsetTop - before;
  }

  async function jumpToLatest() {
    if (chat.hasNewer) {
      await chat.jumpToLatest();
      await tick();
    }
    scrollToBottom(true);
  }

  onMount(() => {
    // restore the scroll position remembered for this session (or start at the bottom)
    const saved = chat.scroll;
    if (saved && !saved.atBottom) {
      scroller.scrollTop = saved.top;
      lastTop = scroller.scrollTop;
      stick = false;
      showJump = distanceFromBottom() > 320;
    } else scrollToBottom(true);
    requestAnimationFrame(() => (restoring = false));

    // keep pinned to the bottom while content grows (streaming, tool output, highlighting…) and while the view
    // shrinks (the composer dock grows)
    const ro = new ResizeObserver(() => {
      if (stick) pin();
    });
    ro.observe(content);
    ro.observe(scroller);
    return () => {
      ro.disconnect();
      // the element may already be gone here: keep the position the scroll handler last saw
      chat.scroll = { top: lastTop, atBottom: stick };
    };
  });

  // a completed full load (tab opened / reconnect / jump to latest) starts at the bottom
  let wasLoading = false;
  $effect(() => {
    const l = chat.loading;
    if (wasLoading && !l) tick().then(() => scrollToBottom(true));
    wasLoading = l;
  });

  function openImage(src) {
    modals.lightbox = { src };
  }
</script>

<div class="wrap">
<div class="scroller np-scroll" bind:this={scroller} onscroll={onScroll}>
  <div class="content" bind:this={content}>
    {#if chat.hasMore}
      <div class="earlier">
        <button class="np-btn np-btn-sm" onclick={loadEarlier} disabled={chat.loadingEarlier}>
          {#if chat.loadingEarlier}<span class="np-spinner"></span>{:else}<Icon name="history" size={13} />{/if}
          Load earlier
        </button>
      </div>
    {/if}

    {#if chat.loading && !chat.messages.length}
      <div class="np-empty"><span class="np-spinner"></span> Loading messages…</div>
    {:else if chat.error && !chat.messages.length}
      <div class="np-empty">
        <Icon name="alert" size={20} />
        Couldn’t load messages: {chat.error}
        <button class="np-btn np-btn-sm" onclick={() => chat.load()}>Retry</button>
      </div>
    {:else if !chat.messages.length && !chat.pendingUser && !chat.stream.active}
      <div class="intro">
        <div class="intro-title">{session.title || 'New session'}</div>
        <div class="np-dim">
          {#if projectOf(session)}Working in <span class="np-mono">{projectOf(session).path}</span>{:else}No project attached — the agent works in the default workspace.{/if}
        </div>
        <div class="hints np-dim np-small">
          <span><span class="np-kbd">/</span> commands</span>
          <span><span class="np-kbd">@</span> mention files</span>
          <span><span class="np-kbd">Ctrl</span>+<span class="np-kbd">V</span> paste images</span>
        </div>
      </div>
    {/if}

    {#each items as item, i (item.key)}
      <div class="item" data-key={item.key} data-kind={item.kind} data-stream={item.stream || item.key.startsWith('g.stream') ? '' : undefined}>
        {#if item.kind === 'user'}
          <UserMessage msg={item.msg} onimage={openImage} onfork={forkable && item.msg.seq ? () => forkBefore(item.msg) : null} />
        {:else if item.kind === 'text'}
          <AssistantText {item} onfork={forkable && item.msg?.seq ? () => forkAfter(item.msg) : null} />
        {:else if item.kind === 'steps'}
          <StepsGroup {item} {chat} {base} live={running && i > lastUserIdx} active={running && i === items.length - 1} />
        {:else if item.kind === 'notice'}
          <NoticeRow msg={item.msg} {chat} />
        {:else if item.kind === 'prompt'}
          <PromptRow prompt={item.prompt} {chat} />
        {:else if item.kind === 'status'}
          <StatusRow msg={item.msg} />
        {:else if item.kind === 'shown'}
          <ShownImage {item} onimage={openImage} />
        {:else if item.kind === 'ask'}
          <AskCard {item} />
        {:else if item.kind === 'images'}
          <div class="images">
            {#each item.images as img, j (j)}
              {@const src = `data:${img.mediaType};base64,${img.data}`}
              <button class="thumb" onclick={() => openImage(src)}><img {src} alt="" /></button>
            {/each}
          </div>
        {/if}
      </div>
    {/each}

    {#if chat.pendingUser && !chat.hasNewer}
      <div class="item pending"><UserMessage msg={chat.pendingUser} pending onimage={openImage} /></div>
    {/if}

    {#if chat.hasNewer}
      <div class="earlier">
        <button class="np-btn np-btn-sm" onclick={jumpToLatest}>
          <Icon name="arrow-down" size={13} /> Newer messages{chat.newerCount ? ` (${chat.newerCount} new)` : ''} — jump to latest
        </button>
      </div>
    {/if}
    <!-- room below the last row: the run's status line is above the composer (RunStatus) -->
    <div class="tail"></div>
  </div>
</div>

{#if showJump && !chat.hasNewer}
  <button class="jump" onclick={jumpToLatest} title="Jump to latest">
    <Icon name="arrow-down" size={14} /> Latest
  </button>
{/if}
</div>

<style>
  .wrap {
    flex: 1;
    min-height: 0;
    display: flex;
    flex-direction: column;
    position: relative;
  }
  .scroller {
    flex: 1;
    min-height: 0;
    overflow-y: auto;
    overflow-anchor: none;
    padding: 0 16px;
  }
  .content {
    max-width: var(--chat-max);
    margin: 0 auto;
    padding: 18px 0 12px;
    display: flex;
    flex-direction: column;
  }
  .item {
    min-width: 0;
  }
  .item + .item {
    margin-top: 10px;
  }
  .item[data-kind='user'] {
    margin-top: 26px;
  }
  .item[data-kind='user']:first-child,
  .earlier + .item[data-kind='user'] {
    margin-top: 0;
  }
  .item[data-kind='steps'] + .item[data-kind='steps'] {
    margin-top: 2px;
  }
  .item.pending {
    margin-top: 22px;
    opacity: 0.75;
  }
  .earlier {
    display: flex;
    justify-content: center;
    padding: 4px 0 14px;
  }
  .intro {
    padding: 12vh 8px 24px;
    display: flex;
    flex-direction: column;
    gap: 8px;
    align-items: center;
    text-align: center;
  }
  .intro-title {
    font-size: 18px;
    font-weight: 600;
  }
  .hints {
    display: flex;
    gap: 16px;
    margin-top: 12px;
  }
  .images {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
  }
  .thumb {
    padding: 0;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    overflow: hidden;
    background: var(--bg-2);
    cursor: zoom-in;
  }
  .thumb img {
    display: block;
    max-width: 320px;
    max-height: 240px;
  }
  .tail {
    height: 24px;
  }
  .jump {
    position: absolute;
    left: 50%;
    transform: translateX(-50%);
    bottom: 12px;
    z-index: 5;
    display: flex;
    align-items: center;
    gap: 5px;
    height: 28px;
    padding: 0 12px;
    border: 1px solid var(--border-strong);
    border-radius: 14px;
    background: var(--bg-2);
    color: var(--fg-muted);
    font-size: var(--fs-sm);
    box-shadow: var(--shadow-sm);
  }
  .jump:hover {
    color: var(--fg);
  }
</style>
