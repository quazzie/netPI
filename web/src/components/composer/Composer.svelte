<script>
  import { onMount, tick } from 'svelte';
  import Icon from '../../lib/kit/Icon.svelte';
  import ModelPicker from './ModelPicker.svelte';
  import EffortPicker from './EffortPicker.svelte';
  import ProfilePicker from './ProfilePicker.svelte';
  import ToolsPicker from './ToolsPicker.svelte';
  import ChatCost from './ChatCost.svelte';
  import ContextRing from './ContextRing.svelte';
  import QueueChips from './QueueChips.svelte';
  import TodoStrip from './TodoStrip.svelte';
  import GoalStrip from './GoalStrip.svelte';
  import { app, isBusy, modelFor, sendMessage, abortAgent } from '../../lib/state/app.svelte.js';
  import { composer, modals, prefs, toast } from '../../lib/state/ui.svelte.js';
  import { allCommands, parseCommand } from '../../lib/commands.js';
  import { rpc } from '../../lib/rpc.svelte.js';

  let { chat, session, onsent } = $props();

  let ta = $state();
  let root = $state();
  let fileInput = $state();
  let dragOver = $state(false);
  let modelOpen = $state(false);
  let effortOpen = $state(false);
  let popup = $state(null); // { type: 'command'|'mention', items, index, start, query, loading }

  const running = $derived(isBusy(session.id));
  const sendKeys = $derived(prefs.enterSends ? 'Enter' : 'Ctrl+Enter'); // for the hints
  const model = $derived(modelFor(session));
  const ctxInfo = $derived(app.context.get(session.id));
  const used = $derived(ctxInfo?.used ?? session.contextTokens ?? 0);
  const win = $derived(ctxInfo?.window ?? model?.contextWindow ?? 0);
  const canSend = $derived(!!chat.draft.trim() || chat.images.length > 0);
  const acceptsImages = $derived(!model || (model.inputModalities ?? ['text']).includes('image'));

  // ------------------------------------------------------------------ autosize
  function autosize() {
    if (!ta) return;
    ta.style.height = 'auto';
    const max = Math.max(160, Math.round(window.innerHeight * 0.4));
    ta.style.height = `${Math.min(ta.scrollHeight, max)}px`;
    ta.style.overflowY = ta.scrollHeight > max ? 'auto' : 'hidden';
  }

  // ------------------------------------------------------------------ bridge for plugins / commands
  function insertText(text) {
    const el = ta;
    if (!el) return;
    const s = el.selectionStart ?? chat.draft.length;
    const e = el.selectionEnd ?? s;
    const before = chat.draft.slice(0, s);
    const pad = before && !/\s$/.test(before) && !/^\s/.test(text) ? ' ' : '';
    chat.draft = before + pad + text + chat.draft.slice(e);
    tick().then(() => {
      const pos = s + pad.length + text.length;
      el.focus();
      el.setSelectionRange(pos, pos);
      autosize();
    });
  }

  onMount(() => {
    composer.insertText = insertText;
    composer.focus = () => ta?.focus();
    composer.setText = (t) => {
      chat.draft = t;
      tick().then(autosize);
    };
    autosize();
    if (!document.activeElement || document.activeElement === document.body) ta.focus({ preventScroll: true });
    return () => {
      if (composer.insertText === insertText) {
        composer.insertText = null;
        composer.focus = null;
        composer.setText = null;
      }
      chat.saveDraft();
    };
  });

  let draftTimer = 0;
  function onInput() {
    autosize();
    updatePopup();
    clearTimeout(draftTimer);
    draftTimer = setTimeout(() => chat.saveDraft(), 400);
  }

  // ------------------------------------------------------------------ popups (/ commands, @ mentions)
  let searchTimer = 0;
  let searchSeq = 0;

  function updatePopup() {
    if (!ta) return;
    const pos = ta.selectionStart;
    const before = chat.draft.slice(0, pos);
    const cm = /^\/([\w:.-]*)$/.exec(before);
    if (cm) {
      const q = cm[1].toLowerCase();
      const items = allCommands()
        .filter((c) => c.name.toLowerCase().startsWith(q) || (q.length > 1 && c.name.toLowerCase().includes(q)))
        .slice(0, 12);
      popup = items.length ? { type: 'command', items, index: 0, start: 0, query: q } : null;
      return;
    }
    const mm = /(^|\s)@([^\s@]*)$/.exec(before);
    if (mm) {
      const query = mm[2];
      const start = pos - query.length - 1;
      const prev = popup?.type === 'mention' ? popup : null;
      popup = { type: 'mention', items: prev?.items ?? [], index: 0, start, query, loading: true };
      clearTimeout(searchTimer);
      const seq = ++searchSeq;
      searchTimer = setTimeout(async () => {
        try {
          const res = await rpc('files.search', { sessionId: session.id, query, limit: 30 }, { timeout: 8000 });
          if (seq !== searchSeq || popup?.type !== 'mention') return;
          popup = { ...popup, items: res ?? [], index: 0, loading: false };
        } catch {
          if (seq === searchSeq && popup?.type === 'mention') popup = { ...popup, items: [], loading: false };
        }
      }, 70);
      return;
    }
    popup = null;
  }

  function accept(i = popup?.index ?? 0) {
    if (!popup) return;
    const item = popup.items[i];
    if (!item) return;
    if (popup.type === 'command') {
      popup = null;
      if (item.argsHint) {
        chat.draft = `/${item.name} `;
        tick().then(() => {
          ta.setSelectionRange(chat.draft.length, chat.draft.length);
          autosize();
        });
      } else {
        chat.draft = '';
        runCmd(item, '');
      }
    } else {
      const pos = ta.selectionStart;
      const rel = item.rel.replace(/\\/g, '/') + (item.isDir && !item.rel.endsWith('/') ? '/' : '');
      const insert = `@${rel.includes(' ') ? `"${rel}"` : rel}${item.isDir ? '' : ' '}`;
      chat.draft = chat.draft.slice(0, popup.start) + insert + chat.draft.slice(pos);
      const caret = popup.start + insert.length;
      popup = null;
      tick().then(() => {
        ta.focus();
        ta.setSelectionRange(caret, caret);
        autosize();
        if (item.isDir) updatePopup();
      });
    }
  }

  function runCmd(cmd, args) {
    chat.saveDraft();
    tick().then(autosize);
    return cmd.run(args, {
      sessionId: session.id,
      ui: {
        openModelPicker: () => (modelOpen = true),
        openProjectPicker: () => (modals.projectPicker = { sessionId: session.id, anchor: root }),
      },
    });
  }

  // ------------------------------------------------------------------ send
  async function submit(mode) {
    const text = chat.draft.trim();
    const images = chat.images;
    if (!text && !images.length) return;
    if (text.startsWith('/') && !images.length) {
      const parsed = parseCommand(text);
      if (parsed?.cmd) {
        chat.draft = '';
        popup = null;
        await runCmd(parsed.cmd, parsed.args);
        return;
      }
    }
    const sendMode = running ? (mode === 'queue' ? 'queue' : 'steer') : 'auto';
    chat.draft = '';
    chat.images = [];
    popup = null;
    chat.saveDraft();
    tick().then(autosize);
    onsent?.();
    const ok = await sendMessage(session.id, text, images, sendMode);
    if (!ok && !chat.draft) {
      chat.draft = text;
      chat.images = images;
      tick().then(autosize);
    }
  }

  function onKeydown(e) {
    if (popup) {
      const n = popup.items.length;
      if (e.key === 'ArrowDown' && n) {
        e.preventDefault();
        popup.index = (popup.index + 1) % n;
        return;
      }
      if (e.key === 'ArrowUp' && n) {
        e.preventDefault();
        popup.index = (popup.index - 1 + n) % n;
        return;
      }
      if ((e.key === 'Enter' || e.key === 'Tab') && n && !e.shiftKey) {
        e.preventDefault();
        accept();
        return;
      }
      if (e.key === 'Escape') {
        e.preventDefault();
        popup = null;
        return;
      }
    }
    if (e.key === 'Escape') {
      if (running) {
        e.preventDefault();
        abortAgent(session.id);
      }
      return;
    }
    if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
      const sendKey = prefs.enterSends ? !e.ctrlKey && !e.metaKey : e.ctrlKey || e.metaKey;
      const altSend = e.altKey; // Alt+Enter = queue while running
      if (sendKey || altSend || (prefs.enterSends && (e.ctrlKey || e.metaKey))) {
        e.preventDefault();
        submit(altSend ? 'queue' : 'steer');
      }
    }
  }

  // ------------------------------------------------------------------ images
  const MAX_IMAGE = 20 * 1024 * 1024;
  function readImage(file) {
    return new Promise((resolve) => {
      if (!file.type.startsWith('image/')) return resolve(null);
      if (file.size > MAX_IMAGE) {
        toast(`${file.name || 'Image'} is larger than 20 MB`, 'warn');
        return resolve(null);
      }
      const r = new FileReader();
      r.onload = () => {
        const url = String(r.result);
        const i = url.indexOf(',');
        resolve({ mediaType: file.type, data: url.slice(i + 1), url, name: file.name || 'pasted image' });
      };
      r.onerror = () => resolve(null);
      r.readAsDataURL(file);
    });
  }
  async function addFiles(files) {
    const imgs = (await Promise.all([...files].map(readImage))).filter(Boolean);
    if (!imgs.length) return;
    if (!acceptsImages) toast(`${model?.displayName || model?.id || 'This model'} may not accept images`, 'warn');
    chat.images = [...chat.images, ...imgs];
    ta?.focus();
  }
  function onPaste(e) {
    const files = [...(e.clipboardData?.items ?? [])]
      .filter((i) => i.kind === 'file' && i.type.startsWith('image/'))
      .map((i) => i.getAsFile())
      .filter(Boolean);
    if (files.length) {
      e.preventDefault();
      addFiles(files);
    }
  }
  function onDrop(e) {
    dragOver = false;
    if (!e.dataTransfer?.files?.length) return;
    e.preventDefault();
    addFiles(e.dataTransfer.files);
  }
  function removeImage(i) {
    chat.images = chat.images.filter((_, j) => j !== i);
  }

  function triggerPopup(ch) {
    ta.focus();
    const pos = ta.selectionStart;
    if (ch === '/') {
      if (chat.draft.trim()) return toast('Commands go at the start of an empty message', 'info');
      chat.draft = '/';
    } else {
      insertText('@');
      return tick().then(updatePopup);
    }
    tick().then(() => {
      ta.setSelectionRange(pos + 1, pos + 1);
      updatePopup();
    });
  }

  function scrollIntoView(node, active) {
    if (active) node.scrollIntoView({ block: 'nearest' });
    return { update: (a) => a && node.scrollIntoView({ block: 'nearest' }) };
  }
</script>

<div class="dock">
  <div class="dock-inner">
    {#if chat.notice}
      <div class="banner" data-level={chat.notice.level} role="status">
        <Icon name={chat.notice.level === 'error' ? 'alert' : chat.notice.level === 'warn' ? 'refresh' : 'info'} size={14} />
        <span class="banner-text">{chat.notice.text}</span>
        <button class="banner-x" aria-label="Dismiss" onclick={() => (chat.notice = null)}><Icon name="x" size={12} /></button>
      </div>
    {/if}
    {#if chat.queue.length}<QueueChips {chat} />{/if}
    <GoalStrip {session} />
    <TodoStrip {session} />

    <div
      class="composer"
      class:drag={dragOver}
      class:running
      bind:this={root}
      data-composer
      role="group"
      aria-label="Message composer"
      ondragover={(e) => {
        if (e.dataTransfer?.types?.includes('Files')) {
          e.preventDefault();
          dragOver = true;
        }
      }}
      ondragleave={(e) => {
        if (!e.currentTarget.contains(e.relatedTarget)) dragOver = false;
      }}
      ondrop={onDrop}
    >
      {#if popup && (popup.items.length || popup.loading)}
        <div class="popup" role="listbox">
          {#if popup.type === 'command'}
            <div class="popup-title">Commands</div>
            {#each popup.items as c, i (c.name)}
              <button
                class="opt"
                class:active={i === popup.index}
                role="option"
                aria-selected={i === popup.index}
                onmousedown={(e) => e.preventDefault()}
                onclick={() => accept(i)}
                onmouseenter={() => (popup.index = i)}
                use:scrollIntoView={i === popup.index}
              >
                <span class="cmd np-mono">/{c.name}</span>
                {#if c.argsHint}<span class="hint np-mono">{c.argsHint}</span>{/if}
                <span class="desc">{c.description}</span>
                {#if c.source !== 'builtin'}<span class="src">{c.source}</span>{/if}
              </button>
            {/each}
          {:else}
            <div class="popup-title">Files {#if popup.loading}<span class="np-spinner" style="width:10px;height:10px"></span>{/if}</div>
            {#each popup.items as f, i (f.path ?? f.rel)}
              <button
                class="opt"
                class:active={i === popup.index}
                role="option"
                aria-selected={i === popup.index}
                onmousedown={(e) => e.preventDefault()}
                onclick={() => accept(i)}
                onmouseenter={() => (popup.index = i)}
                use:scrollIntoView={i === popup.index}
              >
                <Icon name={f.isDir ? 'folder' : 'file'} size={13} />
                <span class="file np-mono">{f.rel}</span>
              </button>
            {:else}
              {#if !popup.loading}<div class="np-empty">No files match “{popup.query}”</div>{/if}
            {/each}
          {/if}
        </div>
      {/if}

      {#if chat.images.length}
        <div class="thumbs">
          {#each chat.images as img, i (i)}
            <div class="thumb" title={img.name}>
              <img src={img.url} alt={img.name} />
              <button class="thumb-x" aria-label="Remove image" onclick={() => removeImage(i)}><Icon name="x" size={11} stroke={2.2} /></button>
            </div>
          {/each}
        </div>
      {/if}

      <textarea
        bind:this={ta}
        bind:value={chat.draft}
        rows="1"
        placeholder={running ? `Steer the agent — ${sendKeys} to steer, Alt+Enter to queue` : 'Message — / commands, @ files'}
        spellcheck={prefs.spellcheck ? 'true' : 'false'}
        oninput={onInput}
        onkeydown={onKeydown}
        onclick={updatePopup}
        onkeyup={(e) => (e.key === 'ArrowLeft' || e.key === 'ArrowRight' || e.key === 'Home' || e.key === 'End') && updatePopup()}
        onpaste={onPaste}
        onblur={() => setTimeout(() => document.activeElement !== ta && (popup = null), 120)}
        aria-label="Message"
      ></textarea>

      <div class="bar">
        <button class="tb" title="Attach image" onclick={() => fileInput.click()}><Icon name="image" size={15} /></button>
        <button class="tb" title="Commands (/)" onclick={() => triggerPopup('/')}><Icon name="slash" size={15} /></button>
        <button class="tb" title="Mention a file (@)" onclick={() => triggerPopup('@')}><Icon name="at" size={15} /></button>
        <span class="sep"></span>
        <ProfilePicker {session} />
        <ModelPicker {session} bind:open={modelOpen} />
        <EffortPicker {session} {model} bind:open={effortOpen} />
        <ToolsPicker {session} />
        <span class="spacer"></span>
        {#if running}
          <span class="keys np-dim"
            ><span class="np-kbd">{sendKeys}</span> steer <span class="np-kbd">Alt+Enter</span> queue <span class="np-kbd">Esc</span> stop</span
          >
        {/if}
        <ChatCost sessionId={session.id} />
        {#if used || win}<ContextRing {used} window={win} />{/if}
        {#if running && !canSend}
          <button class="send stop" title="Stop (Esc)" aria-label="Stop" onclick={() => abortAgent(session.id)}>
            <Icon name="stop" size={14} />
          </button>
        {:else}
          <button
            class="send"
            class:steer={running}
            title={running ? `Steer (${sendKeys}) — Alt+Enter to queue` : `Send (${sendKeys})`}
            aria-label="Send"
            disabled={!canSend}
            onclick={() => submit('steer')}
          >
            <Icon name={running ? 'steer' : 'arrow-up'} size={15} stroke={2.2} />
          </button>
        {/if}
      </div>
      <input
        bind:this={fileInput}
        type="file"
        accept="image/*"
        multiple
        hidden
        onchange={(e) => {
          addFiles(e.currentTarget.files);
          e.currentTarget.value = '';
        }}
      />
    </div>
  </div>
</div>

<style>
  .dock {
    flex: none;
    display: flex;
    justify-content: center;
    padding: 0 16px 14px;
  }
  .dock-inner {
    width: 100%;
    max-width: var(--chat-max);
  }
  .banner {
    display: flex;
    align-items: center;
    gap: 8px;
    margin: 0 0 8px;
    padding: 6px 8px 6px 12px;
    border-radius: var(--radius);
    border: 1px solid var(--border);
    background: var(--info-soft);
    color: var(--info);
    font-size: var(--fs-sm);
    animation: slide-in var(--t) var(--ease);
  }
  .banner[data-level='warn'] {
    background: var(--warn-soft);
    color: var(--warn);
    border-color: color-mix(in srgb, var(--warn) 30%, transparent);
  }
  .banner[data-level='error'] {
    background: var(--err-soft);
    color: var(--err);
    border-color: color-mix(in srgb, var(--err) 30%, transparent);
  }
  .banner-text {
    flex: 1;
    min-width: 0;
  }
  .banner-x {
    display: grid;
    place-items: center;
    width: 20px;
    height: 20px;
    padding: 0;
    border: 0;
    border-radius: 4px;
    background: transparent;
    color: inherit;
    opacity: 0.7;
  }
  .banner-x:hover {
    opacity: 1;
  }
  @keyframes slide-in {
    from {
      opacity: 0;
      transform: translateY(4px);
    }
  }
  .composer {
    position: relative;
    display: flex;
    flex-direction: column;
    border: 1px solid var(--border-strong);
    border-radius: 12px;
    background: var(--bg-1);
    box-shadow: 0 1px 0 rgba(255, 255, 255, 0.02) inset, var(--shadow-sm);
    transition: border-color var(--t-fast);
  }
  .composer:focus-within {
    border-color: color-mix(in srgb, var(--accent) 45%, var(--border-strong));
  }
  .composer.running {
    border-color: color-mix(in srgb, var(--accent) 30%, var(--border-strong));
  }
  .composer.drag {
    border-color: var(--accent);
    border-style: dashed;
    background: var(--accent-soft);
  }
  textarea {
    display: block;
    width: 100%;
    min-height: 46px;
    padding: 12px 14px 4px;
    border: 0;
    outline: none;
    resize: none;
    background: transparent;
    color: var(--fg);
    font-family: var(--font-ui);
    font-size: var(--fs-chat);
    line-height: 1.5;
    overflow-y: hidden;
    scrollbar-width: thin;
  }
  textarea::placeholder {
    color: var(--fg-dim);
  }
  .bar {
    display: flex;
    align-items: center;
    gap: 2px;
    padding: 4px 6px 6px 8px;
    min-width: 0;
  }
  .tb {
    display: grid;
    place-items: center;
    width: 28px;
    height: 28px;
    padding: 0;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--fg-dim);
  }
  .tb:hover {
    background: var(--bg-3);
    color: var(--fg);
  }
  .sep {
    width: 1px;
    height: 16px;
    margin: 0 4px;
    background: var(--border);
  }
  .spacer {
    flex: 1;
  }
  .keys {
    display: flex;
    align-items: center;
    gap: 4px;
    margin-right: 10px;
    font-size: var(--fs-xs);
    white-space: nowrap;
  }
  .keys .np-kbd {
    margin-left: 4px;
  }
  .send {
    display: grid;
    place-items: center;
    width: 32px;
    height: 32px;
    margin-left: 8px;
    padding: 0;
    border: 0;
    border-radius: 9px;
    background: var(--accent);
    color: var(--accent-fg);
    transition:
      filter var(--t-fast),
      opacity var(--t-fast);
  }
  .send:hover {
    filter: brightness(1.1);
  }
  .send:disabled {
    background: var(--bg-3);
    color: var(--fg-dim);
    cursor: default;
    filter: none;
  }
  .send.stop {
    background: var(--fg);
    color: var(--bg);
  }
  .thumbs {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
    padding: 10px 12px 0;
  }
  .thumb {
    position: relative;
    width: 64px;
    height: 64px;
    border-radius: 8px;
    overflow: hidden;
    border: 1px solid var(--border-strong);
  }
  .thumb img {
    width: 100%;
    height: 100%;
    object-fit: cover;
  }
  .thumb-x {
    position: absolute;
    top: 3px;
    right: 3px;
    display: grid;
    place-items: center;
    width: 18px;
    height: 18px;
    padding: 0;
    border: 0;
    border-radius: 50%;
    background: rgba(0, 0, 0, 0.65);
    color: #fff;
  }
  .popup {
    position: absolute;
    left: 0;
    right: 0;
    bottom: calc(100% + 6px);
    z-index: 20;
    max-height: 320px;
    overflow-y: auto;
    padding: 4px;
    border: 1px solid var(--border-strong);
    border-radius: var(--radius);
    background: var(--bg-1);
    box-shadow: var(--shadow);
    scrollbar-width: thin;
  }
  .popup-title {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 5px 8px 3px;
    font-size: var(--fs-xs);
    font-weight: 600;
    letter-spacing: 0.05em;
    text-transform: uppercase;
    color: var(--fg-dim);
  }
  .opt {
    display: flex;
    align-items: center;
    gap: 10px;
    width: 100%;
    min-height: 30px;
    padding: 3px 8px;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--fg-muted);
    text-align: left;
  }
  .opt.active {
    background: var(--bg-3);
    color: var(--fg);
  }
  .cmd {
    color: var(--fg);
    min-width: 88px;
  }
  .hint {
    color: var(--fg-dim);
    font-size: 11.5px;
  }
  .desc {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
    font-size: var(--fs-sm);
  }
  .src {
    font-size: var(--fs-xs);
    color: var(--fg-dim);
  }
  .file {
    min-width: 0;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
    color: var(--fg);
  }
</style>
