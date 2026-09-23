<script>
  import Icon from '../lib/kit/Icon.svelte';
  import TimeAgo from '../lib/kit/TimeAgo.svelte';
  import { app, newSession, openSession, projectOf } from '../lib/state/app.svelte.js';
  import { conn } from '../lib/rpc.svelte.js';

  const recent = $derived(app.sessions.filter((s) => !s.archived && !s.parentSessionId).slice(0, 6));
  const projects = $derived(
    app.projects
      .slice()
      .sort((a, b) => (Date.parse(b.lastUsedAt ?? b.updatedAt) || 0) - (Date.parse(a.lastUsedAt ?? a.updatedAt) || 0))
      .slice(0, 4),
  );
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
      <div class="actions">
        <button class="np-btn np-btn-primary big" onclick={() => newSession({ projectId: null })}>
          <Icon name="plus" size={15} /> New session <span class="np-kbd">Ctrl+T</span>
        </button>
        {#each projects as p (p.id)}
          <button class="np-btn big" onclick={() => newSession({ projectId: p.id })} title={p.path}>
            <Icon name="folder" size={14} /> {p.name}
          </button>
        {/each}
      </div>

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
  .actions {
    display: flex;
    flex-wrap: wrap;
    justify-content: center;
    gap: 8px;
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
  .recent {
    display: flex;
    flex-direction: column;
    gap: 1px;
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
