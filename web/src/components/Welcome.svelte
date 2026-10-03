<script>
  import Icon from '../lib/kit/Icon.svelte';
  import TimeAgo from '../lib/kit/TimeAgo.svelte';
  import { app, newSession, openSession, projectOf } from '../lib/state/app.svelte.js';
  import { modals, toast } from '../lib/state/ui.svelte.js';
  import { welcomeIdeas } from '../lib/state/welcomeIdeas.svelte.js';
  import { openPanelTab } from '../lib/state/tabs.svelte.js';
  import { conn, rpc } from '../lib/rpc.svelte.js';

  const recent = $derived(app.sessions.filter((s) => !s.archived && !s.parentSessionId).slice(0, 6));
  // the project last worked in: shown as a hint on the project button, which opens the picker — picking a
  // project starts a session in it (the + button and Ctrl+T always start without one)
  const target = $derived(app.lastProjectId ? (app.projectsById.get(app.lastProjectId) ?? null) : null);
  let targetEl = $state();
  // Ideas to work on, above the recent sessions (idea-ky14bu): the plugin ranks them, this screen only shows them.
  const picks = $derived(welcomeIdeas.picks);
  let busy = $state(null); // the row being started

  function pickTarget() {
    modals.projectPicker = modals.projectPicker ? null : { select: true, anchor: targetEl };
  }

  const projectName = (p) => (p.projectId ? (app.projectsById.get(p.projectId)?.name ?? p.projectName ?? p.projectId) : '');

  // A pick starts the work: a session in the idea's own project with the idea in it, the same attach the composer
  // chip does. The session is created first, so a click never loses the idea to a failure later on; if the attach
  // cannot happen the tab opens instead rather than dropping the click.
  async function start(pick) {
    if (busy) return;
    busy = pick.id;
    try {
      const s = await newSession({ projectId: pick.projectId ?? null, title: pick.title });
      if (!s) return;
      try {
        await rpc('ideas.attach', { sessionId: s.id, id: pick.id });
      } catch (e) {
        openPanelTab('netpi.ideas/ideas');
        toast(`Could not add the idea to the chat: ${e.message}`, 'warn');
      }
    } finally {
      busy = null;
    }
  }

  // One read per window, for the project the screen targets; ideas.changed moves it (welcomeIdeas.changed).
  $effect(() => {
    if (app.ready) welcomeIdeas.load(target?.id ?? undefined);
  });
</script>

<div class="welcome np-scroll">
  <div class="inner">
    <div class="hero">
      <svg width="40" height="40" viewBox="0 0 32 32" aria-hidden="true">
        <rect width="32" height="32" rx="8" fill="var(--accent-soft)" />
        <path d="M9 23V9h3.2l7.6 9.6V9H23v14h-3.2L12.2 13.4V23z" fill="var(--accent)" />
      </svg>
      <h1>net<b>PI</b></h1>
      <p class="np-muted">A fast, minimal agent harness.</p>
    </div>

    {#if !app.ready}
      <div class="np-empty">
        <span class="np-spinner"></span>
        {conn.status === 'open' ? 'Loading…' : 'Connecting to the NetPI host…'}
      </div>
    {:else}
      <div class="start">
        <div class="actions">
          <button class="np-btn np-btn-primary big" onclick={() => newSession()}>
            <Icon name="plus" size={15} /> New session <span class="np-kbd">Ctrl+T</span>
          </button>
          <span class="in">or</span>
          <button
            class="np-btn big target"
            bind:this={targetEl}
            onclick={pickTarget}
            aria-haspopup="dialog"
            title="Start a session in the project picked here"
          >
            <Icon name="folder" size={14} />
            <span class="np-ellipsis">{target?.name ?? 'No project'}</span>
            <Icon name="chevron-down" size={12} />
          </button>
        </div>
        <div class="where np-mono" title={target?.path ?? app.info?.defaultWorkspace ?? ''}>
          <bdi>{target?.path ?? app.info?.defaultWorkspace ?? 'the default workspace'}</bdi>
        </div>
      </div>

      {#if picks.length}
        <div class="picks">
          <div class="np-section-title">Ideas to work on</div>
          {#each picks as p (p.id)}
            <button
              class="prow"
              title={`Start a chat on this idea${projectName(p) ? ` (${projectName(p)})` : ''}`}
              disabled={busy !== null}
              onclick={() => start(p)}
            >
              <Icon name="idea" size={14} />
              <span class="lines">
                <span class="np-ellipsis title">{p.title}</span>
                {#if p.summary}<span class="np-ellipsis np-dim np-small sum">{p.summary}</span>{/if}
              </span>
              {#if projectName(p) && projectName(p) !== target?.name}<span class="np-dim np-small">{projectName(p)}</span>{/if}
              <TimeAgo time={p.updatedAt} class="np-dim np-small" />
            </button>
          {/each}
        </div>
      {/if}

      {#if recent.length}
        <div class="recent">
          <div class="np-section-title">Recent sessions</div>
          {#each recent as s (s.id)}
            <button class="rrow" onclick={() => openSession(s.id)}>
              <Icon name="sessions" size={14} />
              <span class="np-ellipsis title">{s.title || 'New session'}</span>
              {#if projectOf(s)}<span class="np-dim np-small">{projectOf(s).name}</span>{/if}
              <TimeAgo time={s.updatedAt} class="np-dim np-small" />
            </button>
          {/each}
        </div>
      {/if}

      <div class="keys np-dim np-small">
        <span><span class="np-kbd">Ctrl+K</span> palette</span>
        <span><span class="np-kbd">Ctrl+B</span> sessions panel</span>
        <span><span class="np-kbd">Ctrl+Alt+B</span> right panel</span>
        <span><span class="np-kbd">Ctrl+/</span> shortcuts</span>
      </div>
    {/if}
  </div>
</div>

<style>
  .welcome {
    flex: 1;
    min-height: 0;
    display: flex;
    justify-content: center;
    padding: 0 24px;
  }
  .inner {
    width: 100%;
    max-width: 560px;
    padding: 14vh 0 40px;
    display: flex;
    flex-direction: column;
    gap: 28px;
  }
  .hero {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 6px;
  }
  h1 {
    margin: 6px 0 0;
    font-size: 26px;
    font-weight: 600;
    letter-spacing: -0.02em;
    color: var(--fg-muted);
  }
  h1 b {
    color: var(--fg);
  }
  p {
    margin: 0;
  }
  .start {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 6px;
  }
  .actions {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    justify-content: center;
    gap: 8px;
  }
  .in {
    color: var(--fg-dim);
  }
  .target {
    max-width: 260px;
  }
  .where {
    max-width: 100%;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
    direction: rtl;
    font-size: 11.5px;
    color: var(--fg-dim);
  }
  .big {
    height: 34px;
    padding: 0 14px;
  }
  .big .np-kbd {
    margin-left: 4px;
    background: transparent;
    border-color: color-mix(in srgb, var(--accent-fg) 30%, transparent);
    color: inherit;
    opacity: 0.8;
  }
  .picks,
  .recent {
    display: flex;
    flex-direction: column;
    gap: 1px;
  }
  .prow {
    display: flex;
    align-items: center;
    gap: 10px;
    min-height: 34px;
    padding: 5px 10px;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--fg-muted);
    text-align: left;
  }
  .prow:hover {
    background: var(--bg-2);
    color: var(--fg);
  }
  .prow[disabled] {
    opacity: 0.55;
  }
  .lines {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 1px;
  }
  .title {
    color: var(--fg);
  }
  .sum {
    line-height: 1.25;
  }
  .rrow {
    display: flex;
    align-items: center;
    gap: 10px;
    height: 34px;
    padding: 0 10px;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--fg-muted);
    text-align: left;
  }
  .rrow:hover {
    background: var(--bg-2);
    color: var(--fg);
  }
  .title {
    flex: 1;
    color: var(--fg);
  }
  .keys {
    display: flex;
    flex-wrap: wrap;
    justify-content: center;
    gap: 16px;
  }
</style>
