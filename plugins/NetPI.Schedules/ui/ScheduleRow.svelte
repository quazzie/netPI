<script>
  // One schedule row: dot + name + next, a meta line, hover actions, and the expandable detail with its run history.
  import { onDestroy } from 'svelte';
  import { StatusDot, IconButton, ConfirmButton, Button, timeAgo } from '@netpi/kit';
  import RunHistory from './RunHistory.svelte';
  import { rowState, describeNext, summarise } from './model.js';

  let { s, ctx, now, running, agents, projects, onedit, onchanged } = $props();

  let open = $state(false);
  let busy = $state(false);
  let flash = $state('');
  let flashTimer = 0;

  // `now` is the kit's 1 s clock getter: calling it inside a $derived is what ties the row to the tick.
  const state = $derived(rowState(s, running, now()));
  const next = $derived(describeNext(s, now()));
  const projectName = $derived(
    (projects ?? []).find((p) => p?.id === s.projectId)?.name ?? (s.projectId || 'no project'),
  );
  const agentLabel = $derived(s.agent || 'default');
  const lastSummary = $derived(state.key === 'ran' ? state.label : state.key === 'idle' ? 'not run yet' : state.label);
  const finishedOnce = $derived(!!s.finishedAt);

  function say(msg, level = 'info') {
    flash = msg;
    ctx.app.toast?.(msg, level);
    clearTimeout(flashTimer);
    flashTimer = setTimeout(() => (flash = ''), 4000);
  }
  onDestroy(() => clearTimeout(flashTimer));

  async function runNow() {
    if (busy) return;
    busy = true;
    try {
      const r = await ctx.rpc('schedules.run', { id: s.id });
      if (r?.status === 'ran') {
        say(`Started “${s.name || s.id}”`);
        if (r.sessionId) ctx.app.openSession?.(r.sessionId);
      } else {
        say(r?.reason ? `Skipped: ${r.reason}` : 'Run did not start', 'warn');
      }
      onchanged?.();
    } catch (e) {
      say(String(e?.message ?? e), 'warn');
    } finally {
      busy = false;
    }
  }

  async function togglePause() {
    if (busy) return;
    busy = true;
    try {
      await ctx.rpc('schedules.update', { id: s.id, enabled: !s.enabled });
      onchanged?.();
    } catch (e) {
      say(String(e?.message ?? e), 'warn');
    } finally {
      busy = false;
    }
  }

  async function del() {
    if (busy) return;
    busy = true;
    try {
      await ctx.rpc('schedules.delete', { id: s.id });
      onchanged?.();
    } catch (e) {
      say(String(e?.message ?? e), 'warn');
    } finally {
      busy = false;
    }
  }

  const meta = $derived(
    [state.key === 'proposed' ? 'proposed by an agent' : null, s.when, projectName, agentLabel, s.priority && s.priority !== 'normal' ? `${s.priority} priority` : null, state.key === 'proposed' ? null : lastSummary]
      .filter(Boolean)
      .join(' · '),
  );
</script>

<div class="sch np-hover-row" class:open data-tone={state.tone}>
  <div
    class="row np-line"
    role="button"
    tabindex="0"
    aria-expanded={open}
    title={s.name || s.id}
    onclick={() => (open = !open)}
    onkeydown={(e) => e.key === 'Enter' && (open = !open)}
  >
    <StatusDot status={state.tone} title={state.label} />
    <span class="np-line np-baseline np-grow">
      <span class="name np-ellipsis np-grow" title={s.name || s.id}>{s.name || s.id}</span>
      <span class="next np-mono" class:overdue={next === 'overdue'}>{next}</span>
    </span>
  </div>
  <div class="np-meta" title={meta}>{meta}</div>

  <span class="np-hover-actions">
    <IconButton icon="play" size="sm" title="Run now" disabled={busy || finishedOnce} onclick={runNow} />
    <IconButton
      icon={s.enabled ? 'pause' : 'play'}
      size="sm"
      title={s.enabled ? 'Pause' : finishedOnce ? 'This one-time schedule has run; edit it to give a new time'
        : s.proposedBy ? 'An agent proposed this: check the prompt, then enable it to let it run' : 'Resume'}
      disabled={busy || (finishedOnce && !s.enabled)}
      onclick={togglePause}
    />
    <IconButton icon="pencil" size="sm" title="Edit" onclick={() => onedit?.()} />
    <IconButton icon="history" size="sm" title="History" pressed={open} onclick={() => (open = !open)} />
    <ConfirmButton icon="trash" title="Delete" confirmLabel="Delete?" onconfirm={del} />
  </span>

  {#if flash}<div class="np-small np-muted" role="status">{flash}</div>{/if}

  {#if open}
    <div class="detail">
      <div class="prompt np-muted np-small" title={s.prompt}>{summarise(s)}</div>
      <div class="np-kv">
        <dt>id</dt><dd class="np-mono np-small">{s.id}</dd>
        <dt>created</dt><dd class="np-small">{timeAgo(s.createdAt)}</dd>
      </div>
      {#if s.lastSessionId}
        <Button size="sm" variant="ghost" icon="message" title="Open the chat this schedule last started"
          onclick={() => ctx.app.openSession?.(s.lastSessionId)}>Open last chat</Button>
      {/if}
      <RunHistory ctx={ctx} scheduleId={s.id} refreshKey={open ? s.updatedAt ?? s.lastRunAt : null} />
    </div>
  {/if}
</div>

<style>
  .sch {
    position: relative;
    padding: 6px 10px;
    border-bottom: 1px solid var(--np-border, #262626);
  }
  .row {
    gap: 8px;
    cursor: pointer;
  }
  .name {
    font-size: 13px;
  }
  .next {
    flex: none;
    font-size: 11px;
    opacity: 0.75;
  }
  .next.overdue {
    color: var(--np-warn, #d9a441);
    opacity: 1;
  }
  .np-meta {
    padding: 2px 0 0 14px;
  }
  .detail {
    padding: 8px 0 4px 14px;
    display: flex;
    flex-direction: column;
    gap: 6px;
  }
  .prompt {
    white-space: pre-wrap;
    word-break: break-word;
  }
  .np-kv {
    display: grid;
    grid-template-columns: auto 1fr;
    gap: 2px 8px;
    margin: 0;
  }
  .np-kv dt {
    opacity: 0.6;
  }
  .np-kv dd {
    margin: 0;
    overflow: hidden;
    text-overflow: ellipsis;
  }
</style>
