<script>
  import { onDestroy } from 'svelte';
  import { IconButton, Icon, copyText, desktop } from '@netpi/kit';

  /**
   * The chat's browser tab, live: frames from the tab's screencast, and the user's mouse, keys and pastes sent back to
   * the page — to watch the agent, or to log in to a site in the agents' browser. Wire: plugins/NetPI.Tools.Web/BrowserView.cs.
   */
  let { ctx } = $props();

  let state = $state({ hasTab: false });
  let frame = $state('');
  let meta = $state(null);
  let connected = $state(false);
  let error = $state('');
  let address = $state('');
  let editing = $state(false);
  let extension = $state(null);
  let visible = true;
  let ws = null;
  let img = $state();
  let stage = $state();
  let retry = 0;
  let closed = false;

  export function setVisible(v) {
    visible = v;
    if (v && !ws) connect();
    if (v) loadExtension();
  }

  function connect() {
    if (closed || !ctx.sessionId) return;
    const proto = location.protocol === 'https:' ? 'wss' : 'ws';
    const socket = new WebSocket(`${proto}://${location.host}/api/p/netpi.tools.web/view?session=${encodeURIComponent(ctx.sessionId)}`);
    ws = socket;
    socket.onopen = () => {
      connected = true;
      error = '';
    };
    socket.onmessage = (e) => {
      let m;
      try {
        m = JSON.parse(e.data);
      } catch {
        return;
      }
      if (m.type === 'frame') {
        frame = `data:image/jpeg;base64,${m.data}`;
        meta = m.meta;
      } else if (m.type === 'state') {
        state = m;
        if (!m.hasTab) frame = '';
        // a state read mid-navigation can carry no URL yet: the next one has it
        if (!editing && (m.url || !m.hasTab)) address = m.url ?? '';
      } else if (m.type === 'error') error = m.message;
    };
    socket.onclose = () => {
      if (ws === socket) ws = null;
      connected = false;
      if (closed || !visible) return;
      // the plugin reloaded, or the server restarted: come back
      setTimeout(connect, Math.min(5000, 300 * ++retry));
    };
  }

  function send(m) {
    if (ws?.readyState === 1) ws.send(JSON.stringify(m));
  }

  async function loadExtension() {
    try {
      extension = await ctx.rpc('browser.extension', {});
    } catch {
      extension = null;
    }
  }

  connect();
  loadExtension();
  onDestroy(() => {
    closed = true;
    ws?.close();
  });

  function open(e) {
    e?.preventDefault();
    const url = address.trim();
    if (!url) return;
    editing = false;
    send({ type: 'open', url });
    // the page gets the keys from here on (the stage takes the focus once it shows)
    document.activeElement?.blur?.();
    stage?.focus();
  }

  // ---------------------------------------------------------------- input to the page

  const mods = (e) => (e.altKey ? 1 : 0) | (e.ctrlKey ? 2 : 0) | (e.metaKey ? 4 : 0) | (e.shiftKey ? 8 : 0);
  const buttonName = (b) => ['left', 'middle', 'right'][b] ?? 'none';

  /** The pointer's place in the page's CSS pixels (the frame is scaled to fit the view). */
  function at(e) {
    if (!img || !meta) return null;
    const r = img.getBoundingClientRect();
    if (!r.width || !r.height) return null;
    return {
      x: ((e.clientX - r.left) / r.width) * meta.deviceWidth,
      y: ((e.clientY - r.top) / r.height) * meta.deviceHeight - (meta.offsetTop ?? 0),
    };
  }

  let lastMove = 0;
  function pointer(e, event) {
    const p = at(e);
    if (!p) return;
    if (event === 'mouseMoved') {
      const now = performance.now();
      if (now - lastMove < 30) return;
      lastMove = now;
    }
    if (event === 'mousePressed') {
      stage?.focus();
      e.currentTarget.setPointerCapture?.(e.pointerId);
    }
    e.preventDefault();
    const button = event === 'mouseMoved' ? (e.buttons & 1 ? 'left' : e.buttons & 2 ? 'right' : 'none') : buttonName(e.button);
    send({ type: 'mouse', event, ...p, button, clickCount: event === 'mouseMoved' ? 0 : Math.max(1, e.detail || 1), modifiers: mods(e) });
  }

  function wheel(e) {
    const p = at(e);
    if (!p) return;
    e.preventDefault();
    send({ type: 'mouse', event: 'mouseWheel', ...p, deltaX: e.deltaX, deltaY: e.deltaY, modifiers: mods(e) });
  }

  function key(e, event) {
    // a paste comes as text (the paste event): Ctrl+V itself is left to the browser here
    if (event === 'keyDown' && (e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'v') return;
    e.preventDefault();
    e.stopPropagation();
    const text = event === 'keyDown' ? (e.key === 'Enter' ? '\r' : e.key.length === 1 && !e.ctrlKey && !e.metaKey ? e.key : undefined) : undefined;
    send({ type: 'key', event, key: e.key, code: e.code, keyCode: e.keyCode, text, modifiers: mods(e) });
  }

  function paste(e) {
    const text = e.clipboardData?.getData('text');
    if (!text) return;
    e.preventDefault();
    send({ type: 'text', text });
  }

  const kindLabel = $derived(
    state.kind === 'own' ? "Agents' browser" : state.shared ? 'Your Chrome (a tab you shared)' : state.kind ? 'Your Chrome' : '',
  );
</script>

<div class="browser">
  <div class="bar">
    <IconButton icon="arrow-left" title="Back" size="sm" disabled={!state.hasTab} onclick={() => send({ type: 'nav', to: 'back' })} />
    <IconButton icon="refresh" title="Reload" size="sm" disabled={!state.hasTab} onclick={() => send({ type: 'nav', to: 'reload' })} />
    <form class="address" onsubmit={open}>
      <input
        class="np-input"
        bind:value={address}
        onfocus={() => (editing = true)}
        onblur={() => (editing = false)}
        placeholder="Open a page (a login, say): the agent uses this browser"
        spellcheck="false"
        aria-label="Address"
      />
    </form>
    {#if state.hasTab && state.kind !== 'own'}
      <button class="np-btn np-btn-sm" onclick={() => send({ type: 'front' })} title="Show this tab in your Chrome">Show in Chrome</button>
    {/if}
    <span class="kind" title={state.title ?? ''}>
      <span class="np-dot" data-status={connected ? 'running' : 'idle'}></span>{kindLabel}
    </span>
  </div>

  {#if error}<div class="error np-small">{error}</div>{/if}

  {#if state.hasTab}
    <!-- svelte-ignore a11y_no_noninteractive_tabindex, a11y_no_noninteractive_element_interactions -->
    <div
      class="stage"
      bind:this={stage}
      tabindex="0"
      role="application"
      aria-label="The browser tab: click and type here"
      onkeydown={(e) => key(e, 'keyDown')}
      onkeyup={(e) => key(e, 'keyUp')}
      onpaste={paste}
    >
      {#if frame}
        <img
          bind:this={img}
          src={frame}
          alt={state.title ?? 'The page'}
          draggable="false"
          onpointerdown={(e) => pointer(e, 'mousePressed')}
          onpointerup={(e) => pointer(e, 'mouseReleased')}
          onpointermove={(e) => pointer(e, 'mouseMoved')}
          onwheel={wheel}
          oncontextmenu={(e) => e.preventDefault()}
        />
      {:else}
        <div class="hint np-small">
          {state.kind === 'own' ? 'Waiting for the page…' : 'This tab is in your Chrome: Chrome draws it only while it is in front there.'}
        </div>
      {/if}
    </div>
  {:else}
    <div class="empty">
      <Icon name="globe" size={28} />
      <div class="np-strong">No browser tab in this chat yet</div>
      <div class="np-small dim">
        Open a page above to log in or look around; the agent's browser keeps the login. The agent opens pages here itself
        with its browser tool, and shows you one with <code>show</code>.
      </div>
      {#if extension}
        <div class="ext np-small">
          <div class="np-strong">
            Your own Chrome {extension.connected ? '— connected' : ''}
          </div>
          {#if extension.connected}
            <div class="dim">Share a tab with a chat from the NetPI button in Chrome's toolbar.</div>
          {:else}
            <div class="dim">
              To share your Chrome tabs with chats (with your logins), load the NetPI extension once: chrome://extensions →
              Developer mode → Load unpacked → this folder:
            </div>
            <div class="path">
              <code>{extension.folder}</code>
              <button class="np-btn np-btn-sm" onclick={() => copyText(extension.folder)}>Copy</button>
              {#if desktop.available}<button class="np-btn np-btn-sm" onclick={() => desktop.revealPath(extension.folder)}>Open</button>{/if}
            </div>
          {/if}
        </div>
      {/if}
    </div>
  {/if}
</div>

<style>
  .browser {
    flex: 1;
    min-height: 0;
    display: flex;
    flex-direction: column;
  }
  .bar {
    flex: none;
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 6px 10px;
    border-bottom: 1px solid var(--border);
  }
  .address {
    flex: 1;
    min-width: 0;
  }
  .address input {
    width: 100%;
    height: 26px;
  }
  .kind {
    display: flex;
    align-items: center;
    gap: 5px;
    color: var(--fg-dim);
    font-size: var(--fs-sm);
    white-space: nowrap;
  }
  .error {
    padding: 4px 12px;
    color: var(--err);
  }
  .stage {
    flex: 1;
    min-height: 0;
    display: flex;
    align-items: flex-start;
    justify-content: center;
    padding: 8px;
    outline: none;
    background: var(--bg-2);
    overflow: hidden;
  }
  .stage:focus-visible {
    box-shadow: inset 0 0 0 2px var(--accent-line);
  }
  .stage img {
    max-width: 100%;
    max-height: 100%;
    object-fit: contain;
    user-select: none;
    touch-action: none;
    box-shadow: 0 1px 6px rgba(0, 0, 0, 0.25);
    cursor: default;
  }
  .hint {
    margin-top: 40px;
    color: var(--fg-dim);
  }
  .empty {
    flex: 1;
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 8px;
    padding: 48px 24px;
    text-align: center;
    color: var(--fg-muted);
  }
  .empty > .np-small {
    max-width: 520px;
  }
  .dim {
    color: var(--fg-dim);
  }
  .ext {
    margin-top: 18px;
    max-width: 560px;
    display: flex;
    flex-direction: column;
    gap: 6px;
    padding: 10px 12px;
    border: 1px solid var(--border);
    border-radius: 6px;
    text-align: left;
  }
  .path {
    display: flex;
    align-items: center;
    gap: 6px;
    min-width: 0;
  }
  .path code {
    flex: 1;
    min-width: 0;
    overflow-wrap: anywhere;
  }
</style>
