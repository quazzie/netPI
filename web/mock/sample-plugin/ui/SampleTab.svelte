<script>
  import { onMount, untrack } from 'svelte';
  import { Section, Badge, StatusDot, TimeAgo, Empty, Button, IconButton, Collapsible, Markdown, tokens } from '@netpi/kit';

  /** ctx: the host plugin API (see docs/PROTOCOL.md → Plugin UI tabs) */
  let { ctx } = $props();

  let visible = $state(true);
  let shownCount = $state(1);
  let session = $state(untrack(() => ctx.app.activeSession));
  let project = $state(untrack(() => ctx.app.activeProject));
  let agents = $state([]);
  let lanes = $state([]);
  let aboutOpen = $state(false);
  const mountedAt = new Date().toISOString();

  /** Called by main.js from onShow/onHide. */
  export function setVisible(v) {
    visible = v;
    if (v) {
      shownCount++;
      refresh();
    }
  }

  async function refresh() {
    try {
      agents = (await ctx.rpc('runs.list', { includeFinished: true })) ?? [];
      lanes = (await ctx.rpc('agents.list')) ?? [];
    } catch (e) {
      ctx.app.toast(`Sample: ${e.message}`, 'error');
    }
  }

  onMount(() => {
    refresh();
    const offChange = ctx.app.onChange(() => {
      session = ctx.app.activeSession;
      project = ctx.app.activeProject;
    });
    const offStatus = ctx.on('agent.status', (d) => {
      const i = agents.findIndex((a) => a.id === d.agent.id);
      if (i >= 0) agents[i] = d.agent;
      else agents = [d.agent, ...agents];
    });
    const offLanes = ctx.on('agents.changed', (d) => (lanes = d.agents ?? lanes));
    return () => {
      offChange();
      offStatus();
      offLanes();
    };
  });

  const tone = (s) => (s === 'completed' ? 'ok' : s === 'failed' ? 'error' : s);
  const busy = $derived(agents.filter((a) => a.status === 'running' || a.status === 'queued'));
</script>

<div class="sample">
  <Section title="Active session">
    {#snippet actions()}
      <IconButton icon="refresh" title="Refresh" size="sm" onclick={refresh} />
    {/snippet}
    {#if session}
      <div class="np-stack-sm">
        <div class="np-strong np-ellipsis">{session.title || 'New session'}</div>
        <dl class="np-kv">
          <dt>Project</dt><dd>{project?.name ?? '—'}</dd>
          <dt>Model</dt><dd class="np-mono">{session.model ?? 'default'}</dd>
          <dt>Messages</dt><dd>{session.messageCount}</dd>
          <dt>Context</dt><dd>{tokens(session.contextTokens)} tokens</dd>
        </dl>
        <div class="np-hstack" style="flex-wrap:wrap;margin-top:6px">
          <Button size="sm" icon="pencil" onclick={() => ctx.app.insertText('Summarize this session in three bullet points.')}>Insert text</Button>
          <Button size="sm" icon="plus" onclick={() => ctx.app.newSession({ projectId: session.projectId ?? undefined })}>New here</Button>
          <Button size="sm" variant="ghost" icon="info" onclick={() => ctx.app.toast('Hello from the sample plugin 👋')}>Toast</Button>
        </div>
      </div>
    {:else}
      <Empty icon="sessions">No active session</Empty>
    {/if}
  </Section>

  <Section title="Agents">
    {#snippet actions()}
      {#if busy.length}<Badge tone="accent">{busy.length} busy</Badge>{/if}
    {/snippet}
    {#each agents as a (a.id)}
      <div class="np-row" role="button" tabindex="0" onclick={() => ctx.app.openSession(a.sessionId)} onkeydown={(e) => e.key === 'Enter' && ctx.app.openSession(a.sessionId)}>
        <StatusDot status={tone(a.status)} />
        <span class="np-row-title">{a.name}{a.isSubagent ? ' (sub)' : ''}</span>
        <span class="np-row-sub">{a.activity ?? a.status}</span>
        {#if a.startedAt}<TimeAgo time={a.startedAt} class="np-dim np-small" />{/if}
      </div>
    {:else}
      <Empty icon="bot">No agents</Empty>
    {/each}
  </Section>

  <Section title="Lanes">
    {#each lanes as l (l.key)}
      <div class="np-stack-sm lane">
        <div class="np-hstack"><span class="np-mono">{l.key}</span><span class="np-spacer"></span><span class="np-dim np-small">{l.busy}/{l.capacity} busy · {l.queued} queued</span></div>
        <div class="np-progress" style="--value: {l.capacity ? l.busy / l.capacity : 0}" data-tone={l.busy >= l.capacity ? 'warn' : undefined}></div>
      </div>
    {:else}
      <Empty>No lane pools</Empty>
    {/each}
  </Section>

  <Section>
    <Collapsible title="About this plugin" bind:open={aboutOpen}>
      <Markdown
        text={`This tab is built from \`web/mock/sample-plugin/ui\` with **@netpi/kit** and bundled into one ES module.\n\n- mounted <code>${new Date(mountedAt).toLocaleTimeString()}</code>, shown ${shownCount}×, currently ${visible ? 'visible' : 'hidden'}\n- plugin \`${ctx.pluginId}\`, tab \`${ctx.tabId}\`\n\n\`\`\`js\nexport function mount(el, ctx) { /* … */ }\n\`\`\``}
      />
    </Collapsible>
  </Section>
</div>

<style>
  .sample {
    display: flex;
    flex-direction: column;
  }
  .lane + .lane {
    margin-top: 10px;
  }
</style>
