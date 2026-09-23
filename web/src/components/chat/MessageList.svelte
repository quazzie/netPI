<script>
  import { onMount, tick } from 'svelte';
  import Icon from '../../lib/kit/Icon.svelte';
  import UserMessage from './UserMessage.svelte';
  import AssistantText from './AssistantText.svelte';
  import StepsGroup from './StepsGroup.svelte';
  import NoticeRow from './NoticeRow.svelte';
  import StatusRow from './StatusRow.svelte';
  import StreamingBlock from './StreamingBlock.svelte';
  import { createItemBuilder } from '../../lib/chatItems.js';
  import { app, isBusy, projectOf } from '../../lib/state/app.svelte.js';
  import { modals } from '../../lib/state/ui.svelte.js';
  import { duration } from '../../lib/format.js';

  let { chat, session } = $props();

  const build = createItemBuilder();
  const items = $derived(build(chat.messages));
  const running = $derived(isBusy(session.id));
  const agent = $derived(app.agents.get(session.id));
  const base = $derived(projectOf(session)?.path ?? null);
  // index of the user message that started the current run (steering input does not start a new one)
  const lastUserIdx = $derived.by(() => {
    for (let i = items.length - 1; i >= 0; i--)
      if (items[i].kind === 'user' && items[i].msg.meta?.kind !== 'steer') return i;
    return -1;
  });
  const liveToolRunning = $derived.by(() => {
    for (const t of chat.live.values()) if (t.status === 'running') return true;
    return false;
  });

  let scroller = $state();
  let content = $state();
  let stick = true; // follow the bottom while new content arrives (plain var: no re-render on scroll)
  let showJump = $state(false);
  let restoring = true;

  function distanceFromBottom() {
    return scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight;
  }

  function onScroll() {
    if (restoring) return;
    const d = distanceFromBottom();
    stick = d < 48;
    const jump = d > 320;
    if (jump !== showJump) showJump = jump;
  }

  export function scrollToBottom(force = false) {
    if (!scroller) return;
    if (force) stick = true;
    scroller.scrollTop = scroller.scrollHeight;
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
      stick = false;
      showJump = distanceFromBottom() > 320;
    } else scrollToBottom(true);
    requestAnimationFrame(() => (restoring = false));

    // keep pinned to the bottom while content grows (streaming, tool output, highlighting…)
    const ro = new ResizeObserver(() => {
      if (stick) scroller.scrollTop = scroller.scrollHeight;
    });
    ro.observe(content);
    ro.observe(scroller);
    return () => {
      ro.disconnect();
      chat.scroll = { top: scroller.scrollTop, atBottom: stick };
    };
  });

  // a completed full load (tab opened / reconnect / jump to latest) starts at the bottom
  let wasLoading = false;
  $effect(() => {
    const l = chat.loading;
    if (wasLoading && !l) tick().then(() => scrollToBottom(true));
    wasLoading = l;
  });

  let now = $state(Date.now());
  $effect(() => {
    if (!running) return;
    now = Date.now();
    const t = setInterval(() => (now = Date.now()), 1000);
    return () => clearInterval(t);
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
      <div class="item" data-key={item.key} data-kind={item.kind}>
        {#if item.kind === 'user'}
          <UserMessage msg={item.msg} onimage={openImage} />
        {:else if item.kind === 'text'}
          <AssistantText {item} />
        {:else if item.kind === 'steps'}
          <StepsGroup {item} {chat} {base} live={running && i > lastUserIdx} />
        {:else if item.kind === 'notice'}
          <NoticeRow msg={item.msg} {chat} />
        {:else if item.kind === 'status'}
          <StatusRow msg={item.msg} />
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
    {:else if chat.stream.active}
      <div class="item"><StreamingBlock stream={chat.stream} /></div>
    {:else if running && !liveToolRunning}
      <div class="item working">
        <span class="np-spinner"></span>
        <span>{agent?.activity || (agent?.status === 'queued' ? 'Waiting for a free lane' : 'Working')}…</span>
        {#if agent?.startedAt && now - Date.parse(agent.startedAt) >= 1000}<span class="np-dim">{duration(now - Date.parse(agent.startedAt))}</span>{/if}
      </div>
    {/if}
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
  .working {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 4px 2px;
    color: var(--fg-muted);
    font-size: var(--fs-sm);
  }
  .tail {
    height: 18px;
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
