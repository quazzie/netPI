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
    e.stopPropagation();
    stopping = true;
    try {
      const ok = await ctx.rpc('agent.abort', { sessionId: agent.sessionId });
      if (!ok) ctx.app.toast(`${agent.name} was not running`, 'warn');
    } catch (err) {
      ctx.app.toast(`Abort failed: ${err.message}`, 'error');
    } finally {
      stopping = false;
    }
  }
</script>

<div class="node" class:child={depth > 0}>
  <div
    class="row"
    role="button"
    tabindex="0"
    title={tip}
    onclick={() => ctx.app.openSession(agent.sessionId)}
    onkeydown={(e) => e.key === 'Enter' && ctx.app.openSession(agent.sessionId)}
  >
    <StatusDot status={agentDot(agent.status)} />
    <span class="name">{agent.name}</span>
    {#if agent.isSubagent && depth === 0}<span class="sub">sub</span>{/if}
    <span class="act np-ellipsis">{agent.activity || agent.status}</span>
    <span class="np-spacer"></span>
    {#if agent.startedAt}<Elapsed since={agent.startedAt} class="np-mono el" />{/if}
    <span class="stop"><IconButton icon="stop" title="Stop ({agent.name})" size="sm" disabled={stopping} onclick={abort} /></span>
  </div>
  {#if !agent.isSubagent && title}<div class="stitle np-ellipsis">{title}</div>{/if}
  {#if agent.task && agent.isSubagent}<div class="task np-ellipsis">{agent.task}</div>{/if}
  <div class="meta np-mono np-ellipsis">
    {[
      shortModel(agent.model),
      `${agent.turns ?? 0} turns`,
      `${agent.toolCalls ?? 0} tools`,
      agent.inputTokens || agent.outputTokens ? `${tokens(agent.inputTokens) || 0}↑ ${tokens(agent.outputTokens) || 0}↓` : '',
      agent.pool && shortModel(agent.pool) !== shortModel(agent.model) ? `lane ${agent.pool}` : '',
    ]
      .filter(Boolean)
      .join(' · ')}{#if agent.queuedMessages}<span class="q"> · {agent.queuedMessages} queued</span>{/if}
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
    padding: 2px 0;
  }
  .row {
    display: flex;
    align-items: center;
    gap: 7px;
    min-height: 24px;
    padding: 0 2px 0 3px;
    margin: 0 -2px 0 -3px;
    border-radius: 4px;
    cursor: pointer;
    min-width: 0;
  }
  .row:hover {
    background: var(--bg-2);
  }
  .name {
    flex: none;
    font-weight: 600;
  }
  .sub {
    flex: none;
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
    flex: none;
    font-size: 11px;
    color: var(--fg-dim);
    white-space: nowrap;
  }
  .stop {
    opacity: 0;
    transition: opacity var(--t-fast);
  }
  .row:hover .stop,
  .row:focus-within .stop {
    opacity: 1;
  }
  .meta {
    padding-left: 15px;
    font-size: 10.5px;
    color: var(--fg-dim);
  }
  .stitle {
    padding-left: 15px;
    font-size: var(--fs-xs);
    color: var(--fg-muted);
  }
  .q {
    color: var(--warn);
  }
  .task {
    padding-left: 15px;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
    font-style: italic;
  }
  .kids {
    margin: 2px 0 0 3px;
    padding-left: 10px;
    border-left: 1px solid var(--border);
  }
</style>
