<script>
  import Icon from '../../lib/kit/Icon.svelte';
  import { app, updateSession, projectOf, modelFor, sessionModelRef, openSession } from '../../lib/state/app.svelte.js';
  import { modals } from '../../lib/state/ui.svelte.js';

  let { session } = $props();

  let editing = $state(false);
  let value = $state('');
  let chipEl = $state();

  const project = $derived(projectOf(session));
  const model = $derived(modelFor(session));
  const modelRef = $derived(sessionModelRef(session));
  const agent = $derived(app.agents.get(session.id));
  const parent = $derived(session.parentSessionId ? app.sessionsById.get(session.parentSessionId) : null);

  function start() {
    value = session.title || '';
    editing = true;
  }
  async function commit() {
    if (!editing) return;
    editing = false;
    const v = value.trim();
    if (v && v !== session.title) await updateSession(session.id, { title: v });
  }
  function focusSelect(node) {
    node.focus();
    node.select();
  }
</script>

<div class="header">
  <div class="inner">
    {#if parent}
      <button class="crumb" title="Open parent session" onclick={() => openSession(parent.id)}>
        <Icon name="branch" size={12} />
        <span class="np-ellipsis">{parent.title || 'Parent'}</span>
        <Icon name="chevron-right" size={11} />
      </button>
    {/if}
    {#if editing}
      <input
        class="title-input"
        bind:value
        use:focusSelect
        onkeydown={(e) => {
          if (e.key === 'Enter') commit();
          if (e.key === 'Escape') editing = false;
        }}
        onblur={commit}
        aria-label="Session title"
      />
    {:else}
      <button class="title" title="Rename" onclick={start}>{session.title || 'New session'}</button>
    {/if}

    <button
      class="chip"
      bind:this={chipEl}
      title={project?.path ?? 'Attach a project'}
      onclick={() => (modals.projectPicker = modals.projectPicker ? null : { sessionId: session.id, anchor: chipEl })}
    >
      <Icon name="folder" size={12} />
      <span class="np-ellipsis">{project?.name ?? 'no project'}</span>
    </button>

    {#if modelRef}
      <span class="model np-mono" title={modelRef}>
        <Icon name="cpu" size={12} />
        {model?.displayName || modelRef.split('/').pop()}
        {#if session.reasoning}<span class="effort">· {session.reasoning}</span>{/if}
      </span>
    {/if}

    <span class="spacer"></span>

    <!-- while the agent works, the status line above the composer says so (RunStatus); here only how a run ended badly -->
    {#if agent && (agent.status === 'failed' || agent.status === 'cancelled')}
      <span class="status" data-status={agent.status}>
        <span class="np-dot" data-status={agent.status === 'failed' ? 'error' : agent.status}></span>
        <span>{agent.status}</span>
      </span>
    {/if}
  </div>
</div>

<style>
  .header {
    flex: none;
    height: 38px;
    border-bottom: 1px solid var(--border);
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 0 16px;
  }
  /* as tall as its content, centered by the header: the title and the model's smaller mono text share a baseline
     (centered, the model sat ~2px high); the boxes (breadcrumb, project chip, status) center */
  .inner {
    width: 100%;
    max-width: calc(var(--chat-max) + 32px);
    display: flex;
    align-items: baseline;
    gap: 10px;
    min-width: 0;
  }
  .crumb,
  .chip,
  .status {
    align-self: center;
  }
  .crumb {
    display: flex;
    align-items: center;
    gap: 4px;
    max-width: 180px;
    padding: 2px 4px;
    border: 0;
    border-radius: 4px;
    background: transparent;
    color: var(--fg-dim);
    font-size: var(--fs-sm);
  }
  .crumb:hover {
    color: var(--fg);
    background: var(--bg-2);
  }
  .title {
    min-width: 0;
    max-width: 46%;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
    padding: 3px 6px;
    margin-left: -6px;
    border: 1px solid transparent;
    border-radius: 5px;
    background: transparent;
    color: var(--fg);
    font-size: 13.5px;
    font-weight: 600;
    text-align: left;
    cursor: text;
  }
  .title:hover {
    border-color: var(--border);
  }
  .title-input {
    min-width: 200px;
    max-width: 46%;
    height: 26px;
    padding: 0 6px;
    margin-left: -6px;
    border: 1px solid var(--accent-line);
    border-radius: 5px;
    background: var(--bg);
    color: var(--fg);
    font-size: 13.5px;
    font-weight: 600;
    outline: none;
  }
  .chip {
    display: flex;
    align-items: center;
    gap: 5px;
    max-width: 180px;
    height: 22px;
    padding: 0 8px;
    border: 1px solid var(--border);
    border-radius: 11px;
    background: transparent;
    color: var(--fg-muted);
    font-size: var(--fs-xs);
  }
  .chip:hover {
    color: var(--fg);
    border-color: var(--border-strong);
  }
  /* its text, not the icon, gives it the baseline it shares with the title */
  .model {
    display: flex;
    align-items: baseline;
    gap: 5px;
    color: var(--fg-dim);
    font-size: 11.5px;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    min-width: 0;
  }
  .model > :global(.np-icon) {
    align-self: center;
  }
  .effort {
    color: var(--fg-dim);
  }
  .spacer {
    flex: 1;
  }
  .status {
    display: flex;
    align-items: center;
    gap: 6px;
    font-size: var(--fs-sm);
    color: var(--fg-muted);
    white-space: nowrap;
  }
  .status[data-status='failed'] {
    color: var(--err);
  }
</style>
