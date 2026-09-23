<script>
  import Icon from '../../../lib/kit/Icon.svelte';
  import Markdown from '../../../lib/kit/Markdown.svelte';
  import { arg } from '../../../lib/tools.js';
  import { app, openSession } from '../../../lib/state/app.svelte.js';
  import { truncate } from '../../../lib/format.js';

  /** agent_* tools: which agent, its (live) status, the task and the result; link to the subagent session */
  let { name, args, result } = $props();

  const d = $derived(result?.details && typeof result.details === 'object' ? result.details : {});
  const agents = $derived(Array.isArray(d.agents) ? d.agents : null);
  const sessionId = $derived(d.sessionId ?? d.agent?.sessionId ?? null);
  const agentName = $derived(d.name ?? d.agent?.name ?? arg(args, 'name', 'agent', 'to') ?? null);
  const live = $derived(sessionId ? app.agents.get(sessionId) : null);
  const status = $derived(live?.status ?? d.status ?? d.agent?.status ?? null);
  const task = $derived(arg(args, 'task', 'text', 'message') ?? null);
  const model = $derived(arg(args, 'model') ?? d.model ?? null);
  const resultText = $derived(d.result ?? (result && !result.isError ? result.content : null));
  let showResult = $state(false);

  const tone = (s) => (s === 'completed' ? 'ok' : s === 'failed' ? 'error' : s === 'cancelled' ? 'cancelled' : s);
</script>

<div class="agent">
  {#if agentName || sessionId}
    <div class="head">
      <Icon name="bot" size={14} />
      <span class="name">{agentName ?? 'agent'}</span>
      {#if status}<span class="np-dot" data-status={tone(status)}></span><span class="np-dim">{status}</span>{/if}
      {#if live?.activity && status === 'running'}<span class="np-dim np-ellipsis">· {live.activity}</span>{/if}
      {#if model}<span class="np-dim np-mono small">{model}</span>{/if}
      <span class="np-spacer"></span>
      {#if sessionId}
        <button class="np-btn np-btn-sm" onclick={() => openSession(sessionId)}><Icon name="external" size={12} /> Open session</button>
      {/if}
    </div>
  {/if}
  {#if agents}
    <div class="list">
      {#each agents as a (a.id ?? a.sessionId)}
        <div class="arow">
          <span class="np-dot" data-status={tone(a.status)}></span>
          <span class="name">{a.name}</span>
          <span class="np-dim">{a.status}</span>
          <span class="np-spacer"></span>
          {#if a.sessionId}<button class="np-btn np-btn-ghost np-btn-sm" onclick={() => openSession(a.sessionId)}>open</button>{/if}
        </div>
      {/each}
    </div>
  {/if}
  {#if task}
    <div class="task"><Markdown text={truncate(String(task), 4000)} /></div>
  {/if}
  {#if resultText}
    <button class="np-btn np-btn-ghost np-btn-sm toggle" onclick={() => (showResult = !showResult)}>
      <Icon name={showResult ? 'chevron-down' : 'chevron-right'} size={12} /> Result
    </button>
    {#if showResult}<div class="result"><Markdown text={String(resultText)} /></div>{/if}
  {/if}
</div>

<style>
  .agent {
    display: flex;
    flex-direction: column;
    gap: 8px;
    min-width: 0;
  }
  .head,
  .arow {
    display: flex;
    align-items: center;
    gap: 7px;
    font-size: var(--fs-sm);
    min-width: 0;
  }
  .name {
    font-weight: 600;
  }
  .small {
    font-size: 11px;
  }
  .list {
    display: flex;
    flex-direction: column;
    gap: 2px;
  }
  .task {
    padding: 6px 10px;
    border-left: 2px solid var(--border-strong);
    color: var(--fg-muted);
  }
  .task :global(.md),
  .result :global(.md) {
    font-size: 13px;
  }
  .toggle {
    align-self: flex-start;
  }
  .result {
    padding: 8px 10px;
    border-radius: var(--radius-sm);
    background: var(--bg-2);
  }
</style>
