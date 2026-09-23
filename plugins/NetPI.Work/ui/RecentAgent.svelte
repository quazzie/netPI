<script>
  import { Icon, TimeAgo } from '@netpi/kit';
  import { firstLine } from './util.js';
  let { agent, titles, ctx } = $props();
  const ICON = { completed: 'circle-check', failed: 'circle-x', cancelled: 'ban' };
  const detail = $derived(agent.status === 'failed' ? agent.error : agent.status === 'completed' ? agent.result : agent.error);
</script>

<button class="recent" data-status={agent.status} title={[titles?.get(agent.sessionId), detail].filter(Boolean).join('\n')} onclick={() => ctx.app.openSession(agent.sessionId)}>
  <Icon name={ICON[agent.status] ?? 'info'} size={13} />
  <span class="name">{agent.name}</span>
  <span class="detail np-ellipsis">{firstLine(detail) || agent.status}</span>
  <span class="np-spacer"></span>
  {#if agent.finishedAt}<TimeAgo time={agent.finishedAt} class="when" />{/if}
</button>

<style>
  .recent {
    display: flex;
    align-items: center;
    gap: 7px;
    width: 100%;
    min-height: 24px;
    padding: 0 3px;
    margin: 0 -3px;
    border: 0;
    border-radius: 4px;
    background: transparent;
    color: var(--fg);
    font: inherit;
    font-size: var(--fs-sm);
    text-align: left;
    cursor: pointer;
    min-width: 0;
  }
  .recent:hover {
    background: var(--bg-2);
  }
  .recent :global(svg) {
    color: var(--fg-dim);
  }
  .recent[data-status='completed'] :global(svg) {
    color: var(--ok);
  }
  .recent[data-status='failed'] :global(svg) {
    color: var(--err);
  }
  .name {
    flex: none;
    font-weight: 500;
  }
  .detail {
    color: var(--fg-dim);
    font-size: var(--fs-xs);
  }
  .recent[data-status='failed'] .detail {
    color: var(--err);
    opacity: 0.85;
  }
  .recent :global(.when) {
    flex: none;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
  }
</style>
