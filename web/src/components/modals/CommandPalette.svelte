<script>
  import { onMount } from 'svelte';
  import Icon from '../../lib/kit/Icon.svelte';
  import { app, openSession, newSession, projectOf } from '../../lib/state/app.svelte.js';
  import { modals, prefs, savePrefs, togglePanel, composer } from '../../lib/state/ui.svelte.js';
  import { tabs, openPanelTab } from '../../lib/state/tabs.svelte.js';
  import { allCommands } from '../../lib/commands.js';

  let { onclose } = $props();
  let q = $state('');
  let index = $state(0);
  let inputEl = $state();

  function entries() {
    const out = [];
    const act = (label, icon, run, hint = '') => out.push({ group: 'Actions', label, icon, run, hint });
    act('New session', 'plus', () => newSession(), 'Ctrl+T');
    act('Settings', 'settings', () => (modals.settings = true), 'Ctrl+,');
    act('Toggle left panel', 'panel-left', () => togglePanel('left'), 'Ctrl+B');
    act('Toggle right panel', 'panel-right', () => togglePanel('right'), 'Ctrl+Alt+B');
    act(`Switch to ${prefs.theme === 'light' ? 'dark' : 'light'} theme`, prefs.theme === 'light' ? 'moon' : 'sun', () => {
      prefs.theme = prefs.theme === 'light' ? 'dark' : 'light';
      savePrefs();
    });
    act('Keyboard shortcuts', 'keyboard', () => (modals.help = true), 'Ctrl+/');
    for (const t of tabs.all) out.push({ group: 'Panels', label: `Open ${t.title}`, icon: t.icon ?? 'puzzle', run: () => openPanelTab(t.key) });
    const seen = new Set();
    for (const id of app.openTabs) {
      const s = app.sessionsById.get(id);
      if (!s) continue;
      seen.add(id);
      out.push({ group: 'Sessions', label: s.title || 'New session', icon: 'sessions', run: () => openSession(id), hint: 'open' });
    }
    for (const s of app.sessions) {
      if (seen.has(s.id) || s.archived || s.parentSessionId) continue;
      out.push({ group: 'Sessions', label: s.title || 'New session', icon: 'sessions', run: () => openSession(s.id), hint: projectOf(s)?.name ?? '' });
    }
    for (const p of app.projects)
      out.push({ group: 'Projects', label: `New session in ${p.name}`, icon: 'folder', run: () => newSession({ projectId: p.id }), hint: p.path });
    for (const c of allCommands())
      out.push({
        group: 'Commands',
        label: `/${c.name}`,
        icon: 'slash',
        hint: c.description,
        run: () => {
          if (c.argsHint) composer.setText?.(`/${c.name} `), composer.focus?.();
          else c.run('', { sessionId: app.activeId });
        },
      });
    return out;
  }

  function score(label, query) {
    const l = label.toLowerCase();
    if (!query) return 1;
    if (l.startsWith(query)) return 3;
    if (l.includes(query)) return 2;
    let i = 0;
    for (const ch of l) if (ch === query[i]) i++;
    return i === query.length ? 1 : 0;
  }

  const results = $derived.by(() => {
    const query = q.trim().toLowerCase();
    const all = entries();
    if (!query) return all.filter((e) => e.group !== 'Sessions' || e.hint === 'open' || all.indexOf(e) < 40).slice(0, 60);
    return all
      .map((e) => ({ e, s: score(e.label, query) }))
      .filter((x) => x.s > 0)
      .sort((a, b) => b.s - a.s)
      .slice(0, 50)
      .map((x) => x.e);
  });

  $effect(() => {
    q;
    index = 0;
  });

  function run(e) {
    onclose();
    queueMicrotask(() => e.run());
  }

  function onKey(e) {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      index = Math.min(results.length - 1, index + 1);
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      index = Math.max(0, index - 1);
    } else if (e.key === 'Enter') {
      e.preventDefault();
      if (results[index]) run(results[index]);
    }
  }

  onMount(() => {
    inputEl.focus();
    const k = (e) => {
      if (e.key === 'Escape') {
        e.preventDefault();
        e.stopPropagation();
        onclose();
      }
    };
    window.addEventListener('keydown', k, true);
    return () => window.removeEventListener('keydown', k, true);
  });

  function scrollIntoView(node, active) {
    if (active) node.scrollIntoView({ block: 'nearest' });
    return { update: (a) => a && node.scrollIntoView({ block: 'nearest' }) };
  }
</script>

<div class="overlay" role="presentation" onpointerdown={(e) => e.target === e.currentTarget && onclose()}>
  <div class="palette" role="dialog" aria-label="Command palette">
    <div class="search">
      <Icon name="command" size={15} />
      <input bind:this={inputEl} bind:value={q} placeholder="Type a command, session or project…" onkeydown={onKey} spellcheck="false" />
      <span class="np-kbd">Esc</span>
    </div>
    <div class="list np-scroll">
      {#each results as r, i (r.group + r.label + i)}
        {#if i === 0 || results[i - 1].group !== r.group}<div class="group">{r.group}</div>{/if}
        <button class="item" class:active={i === index} onclick={() => run(r)} onmouseenter={() => (index = i)} use:scrollIntoView={i === index}>
          <Icon name={r.icon} size={14} />
          <span class="label np-ellipsis">{r.label}</span>
          {#if r.hint}<span class="hint np-ellipsis">{r.hint}</span>{/if}
        </button>
      {:else}
        <div class="np-empty">Nothing matches “{q}”</div>
      {/each}
    </div>
  </div>
</div>

<style>
  .overlay {
    position: fixed;
    inset: 0;
    z-index: 85;
    display: flex;
    justify-content: center;
    padding-top: 12vh;
    background: var(--overlay);
  }
  .palette {
    width: min(620px, calc(100vw - 32px));
    max-height: 60vh;
    display: flex;
    flex-direction: column;
    border: 1px solid var(--border-strong);
    border-radius: var(--radius-lg);
    background: var(--bg-1);
    box-shadow: var(--shadow);
    overflow: hidden;
    align-self: flex-start;
    animation: rise var(--t) var(--ease);
  }
  .search {
    display: flex;
    align-items: center;
    gap: 10px;
    height: 48px;
    padding: 0 14px;
    border-bottom: 1px solid var(--border);
    color: var(--fg-dim);
  }
  .search input {
    flex: 1;
    border: 0;
    outline: none;
    background: transparent;
    color: var(--fg);
    font-size: 14.5px;
  }
  .list {
    padding: 6px;
  }
  .group {
    padding: 8px 10px 3px;
    font-size: var(--fs-xs);
    font-weight: 600;
    color: var(--fg-dim);
    text-transform: uppercase;
    letter-spacing: 0.05em;
  }
  .item {
    display: flex;
    align-items: center;
    gap: 10px;
    width: 100%;
    height: 32px;
    padding: 0 10px;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--fg-muted);
    text-align: left;
  }
  .item.active {
    background: var(--bg-3);
    color: var(--fg);
  }
  .label {
    flex: 0 1 auto;
    color: var(--fg);
  }
  .hint {
    flex: 1;
    text-align: right;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
  }
  @keyframes rise {
    from {
      opacity: 0;
      transform: translateY(6px);
    }
  }
</style>
