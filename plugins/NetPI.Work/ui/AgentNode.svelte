<script>
  import { StatusDot, Elapsed, IconButton, tokens } from '@netpi/kit';
  import AgentNode from './AgentNode.svelte';
  import { agentDot, shortModel } from './util.js';

  let { agent, childrenOf, titles, ctx, depth = 0 } = $props();
  const title = $derived(titles?.get(agent.sessionId) ?? '');
  const tip = $derived([title, agent.task].filter(Boolean).join('\n') || 'Open session');
  const kids = $derived(childrenOf.get(agent.id) ?? []);
  let stopping = $state(false);

  async function abort(e) {
    e?.stopPropagation();
    stopping = true;
    const { sessionId, name } = agent; // the node can unmount (agent finished) before the call returns
    try {
      const ok = await ctx.rpc('agent.abort', { sessionId });
      if (!ok) ctx.app.toast(`${name} was not running`, 'warn');
    } catch (err) {
      ctx.app.toast(`Abort failed: ${err.message}`, 'error');
    } finally {
      stopping = false;
    }
  }
</script>

<div class="node" class:child={depth > 0}>
  <div class="card np-hover-row">
    <div
      class="row np-line"
      role="button"
      tabindex="0"
      title={tip}
      onclick={() => ctx.app.openSession(agent.sessionId)}
      onkeydown={(e) => e.key === 'Enter' && ctx.app.openSession(agent.sessionId)}
    >
      <StatusDot status={agentDot(agent.status)} />
      <span class="name">{agent.name}</span>
      {#if agent.isSubagent && depth === 0}<span class="sub">sub</span>{/if}
      <span class="act np-grow">{agent.activity || agent.status}</span>
      {#if agent.startedAt}<Elapsed since={agent.startedAt} class="np-mono el" />{/if}
    </div>
    {#if !agent.isSubagent && title}<div class="line2 np-ellipsis" title={title}>{title}</div>{/if}
    {#if agent.task && agent.isSubagent}<div class="line2 task np-ellipsis" title={agent.task}>{agent.task}</div>{/if}
    <div class="np-meta meta">
      <span>{shortModel(agent.model)}</span><span>{agent.turns ?? 0} turns</span><span>{agent.toolCalls ?? 0} tools</span
      >{#if agent.inputTokens || agent.outputTokens}<span>{tokens(agent.inputTokens) || 0}↑ {tokens(agent.outputTokens) || 0}↓</span>{/if}{#if agent.queuedMessages}<span
          class="q">{agent.queuedMessages} queued</span
        >{/if}{#if agent.agent && shortModel(agent.agent) !== shortModel(agent.model)}<span>agent {agent.agent}</span>{/if}
    </div>
    <span class="np-hover-actions"><IconButton icon="stop" title="Stop ({agent.name})" size="sm" disabled={stopping} onclick={abort} /></span>
  </div>
  {#if kids.length}
    <div class="kids">
      {#each kids as k (k.id)}
        <AgentNode agent={k} {childrenOf} {titles} {ctx} depth={depth + 1} />
      {/each}
    </div>
  {/if}
</div>

<style>
  .node {
    padding: 1px 0;
  }
  .card {
    padding: 2px 4px 3px;
    margin: 0 -4px;
    border-radius: var(--radius-sm);
  }
  .card:hover,
  .card:focus-within {
    --row-bg: var(--bg-2);
    background: var(--bg-2);
  }
  .row {
    gap: 7px;
    min-height: 22px;
    cursor: pointer;
    outline: none;
  }
  .row:focus-visible {
    box-shadow: 0 0 0 2px var(--accent-line);
    border-radius: 4px;
  }
  .row > .name {
    flex: none;
    max-width: 55%;
    overflow: hidden;
    text-overflow: ellipsis;
    font-weight: 600;
  }
  .sub {
    font-size: 10px;
    padding: 0 4px;
    border-radius: 3px;
    background: var(--bg-3);
    color: var(--fg-dim);
  }
  .act {
    color: var(--fg-muted);
    font-size: var(--fs-sm);
  }
  .row :global(.el) {
    font-size: 11px;
    color: var(--fg-dim);
  }
  .line2,
  .meta {
    padding-left: 13px;
  }
  .line2 {
    font-size: var(--fs-xs);
    color: var(--fg-muted);
  }
  .task {
    color: var(--fg-dim);
    font-style: italic;
  }
  .meta {
    font-family: var(--font-mono);
    font-size: 10.5px;
  }
  .q {
    color: var(--warn);
  }
  .kids {
    margin: 1px 0 0 3px;
    padding-left: 9px;
    border-left: 1px solid var(--border);
  }
</style>
