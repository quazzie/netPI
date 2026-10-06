<script>
  // The Schedules tab: every schedule, what it last did, and the add/edit form (idea-1y1174's UI half).
  // The data is the plugin's own RPCs; runs/projects/agents are other plugins' reads, and each degrades on its
  // own when absent. schedules.changed (unscoped) is the refresh backbone; schedules.ran is a hint only, because
  // it is session-scoped to the chat a run started.
  import { useRefresh, secondNow, Button, IconButton, Empty } from '@netpi/kit';
  import ScheduleRow from './ScheduleRow.svelte';
  import ScheduleForm from './ScheduleForm.svelte';

  let { ctx } = $props();
  let visible = $state(true);
  let schedules = $state.raw(null);
  let runs = $state.raw([]);
  let projects = $state.raw([]);
  let agents = $state.raw(null);
  let error = $state('');
  let editing = $state(null); // null, or { schedule: null } for a new one

  const now = secondNow();
  const running = $derived(
    new Set(
      (runs ?? [])
        .filter((r) => ['queued', 'running', 'yielded'].includes(r?.status))
        .map((r) => r?.sessionId)
        .filter(Boolean),
    ),
  );

  async function load() {
    try {
      const [list, rl, pl, al] = await Promise.all([
        ctx.rpc('schedules.list', {}),
        ctx.rpc('runs.list', {}).catch(() => []),
        ctx.rpc('projects.list', {}).catch(() => []),
        ctx.rpc('agents.list', {}).catch(() => null),
      ]);
      schedules = list ?? [];
      runs = rl ?? [];
      projects = pl ?? [];
      agents = al;
      error = '';
    } catch (e) {
      error = String(e?.message ?? e);
    }
  }

  // svelte-ignore state_referenced_locally
  const tab = useRefresh(ctx, {
    load,
    events: ['schedules.changed', 'schedules.ran'],
    pollMs: 30_000,
    visible: () => visible,
  });

  // A run's agent ending is the dot going green without the 30 s poll: agent.status carries the agent, whose
  // sessionId is the run's chat. (svelte-ignore: ctx is the frozen tab context; the initial value is the value.)
  // svelte-ignore state_referenced_locally
  ctx.on('agent.status', (d) => {
    const sid = d?.agent?.sessionId;
    if (sid && running.has(sid)) tab.schedule();
  });

  export function setVisible(v) {
    visible = v;
    tab.setVisible?.(v);
  }
</script>

<div class="schedules">
  <div class="np-toolbar">
    <span class="np-strong">Schedules</span>
    {#if schedules !== null}
      <span class="np-dim">{schedules.length}</span>
    {/if}
    <span class="np-grow"></span>
    {#if !editing}
      <Button size="sm" variant="primary" icon="plus" title="New schedule" onclick={() => (editing = { schedule: null })}>New</Button>
      <IconButton icon="refresh" size="sm" title="Refresh" onclick={() => tab.refresh()} />
    {/if}
  </div>

  {#if editing}
    <ScheduleForm
      ctx={ctx}
      schedule={editing.schedule}
      projects={projects}
      agents={agents}
      ondone={() => {
        editing = null;
        tab.refresh();
      }}
      oncancel={() => (editing = null)}
    />
  {:else if error}
    <Empty icon="alert">schedules.list unavailable: {error}</Empty>
  {:else if schedules === null}
    <div class="np-dim np-pad">Loading…</div>
  {:else if schedules.length === 0}
    <Empty icon="clock">No schedules yet. A schedule starts a prompt as a fresh chat on its own.</Empty>
    <div class="np-pad">
      <Button variant="primary" icon="plus" onclick={() => (editing = { schedule: null })}>New schedule</Button>
    </div>
  {:else}
    <div class="np-list">
      {#each schedules as s (s.id)}
        <ScheduleRow
          s={s}
          ctx={ctx}
          {now}
          {running}
          {agents}
          {projects}
          onedit={() => (editing = { schedule: s })}
          onchanged={tab.refresh}
        />
      {/each}
    </div>
  {/if}
</div>

<style>
  .schedules {
    display: flex;
    flex-direction: column;
    height: 100%;
    min-width: 0;
  }
  .np-toolbar {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 6px 10px;
    border-bottom: 1px solid var(--np-border, #2a2a2a);
  }
  .np-list {
    overflow-y: auto;
    flex: 1;
  }
  .np-pad {
    padding: 10px;
  }
</style>
