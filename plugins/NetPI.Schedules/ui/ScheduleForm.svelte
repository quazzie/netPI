<script>
  // The add/edit form: an inline panel that replaces the list (not a modal), stacked fields for 220px.
  // Edit mode always sends `enabled` explicitly (a cadence change alone would silently resume a paused
  // schedule), and a cadence only when it changed (a new cadence counts the next run from now).
  import { Button, Segmented } from '@netpi/kit';
  import { cadenceToForm, formToCadence, cadenceChanged } from './model.js';

  let { ctx, schedule = null, projects = [], agents = null, ondone, oncancel } = $props();

  const editing = $derived(!!schedule);

  // The initial value, computed once when the form mounts (the form remounts on every open): a function
  // passed to $state would be stored as the value itself, and `f.name` would read the function's own name.
  function initForm() {
    if (schedule) {
      const c = cadenceToForm(schedule.cadence);
      return {
        name: schedule.name ?? '',
        prompt: schedule.prompt ?? '',
        kind: c.kind,
        atLocal: c.atLocal,
        minutes: c.minutes,
        time: c.time,
        tz: c.tz,
        projectId: schedule.projectId ?? '',
        agent: schedule.agent ?? '',
        priority: schedule.priority ?? 'normal',
        enabled: schedule.enabled !== false,
      };
    }
    return {
      name: '',
      prompt: '',
      kind: 'daily',
      atLocal: '',
      minutes: 60,
      time: '09:00',
      tz: Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC',
      projectId: ctx.app.activeProject?.id ?? '',
      agent: '',
      priority: 'normal',
      enabled: true,
    };
  }
  const f = $state(initForm());

  let busy = $state(false);
  let error = $state('');

  const zones = $derived(
    ['UTC', ...new Set([f.tz, ...(typeof Intl.supportedValuesOf === 'function' ? Intl.supportedValuesOf('timeZone') : [])])]
      .filter((z, i, a) => a.indexOf(z) === i),
  );
  const agentOptions = $derived(
    (agents ?? []).map((a) => ({
      value: a.key,
      label: a.key + (a.disabled ? ' (disabled)' : a.available === false ? ` (unavailable${a.unavailable ? ': ' + a.unavailable : ''})` : ''),
    })),
  );

  function minNow() {
    const p = (n) => String(n).padStart(2, '0');
    const d = new Date();
    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`;
  }

  async function save() {
    if (busy) return;
    error = '';
    if (!f.prompt.trim()) {
      error = 'A prompt is required.';
      return;
    }
    if (f.kind === 'once' && !f.atLocal) {
      error = 'Pick when the one-time run starts.';
      return;
    }
    const params = {
      prompt: f.prompt,
      cadence: formToCadence(f),
      name: f.name.trim() || undefined,
      enabled: f.enabled,
    };
    if (f.projectId) params.projectId = f.projectId;
    if (f.agent) params.agent = f.agent;
    if (f.priority !== 'normal') params.priority = f.priority;

    busy = true;
    try {
      if (editing) {
        const p = { id: schedule.id, enabled: f.enabled };
        if (f.name.trim() !== (schedule.name ?? '')) p.name = f.name.trim();
        if (f.prompt !== (schedule.prompt ?? '')) p.prompt = f.prompt;
        if (f.projectId !== (schedule.projectId ?? '')) p.projectId = f.projectId;
        if (f.agent !== (schedule.agent ?? '')) p.agent = f.agent;
        if (f.priority !== (schedule.priority ?? 'normal')) p.priority = f.priority;
        if (cadenceChanged(formToCadence(f), cadenceToForm(schedule.cadence))) p.cadence = formToCadence(f);
        // a cadence change alone would resume a paused schedule; `enabled` is what the user sees, so it wins
        await ctx.rpc('schedules.update', p);
      } else {
        await ctx.rpc('schedules.add', params);
      }
      ondone?.();
    } catch (e) {
      error = String(e?.message ?? e);
    } finally {
      busy = false;
    }
  }
</script>

<div class="form">
  <div class="f-head">
    <div class="f-title np-strong">{editing ? 'Edit schedule' : 'New schedule'}</div>
    <label class="np-check f-enabled"
      title={schedule?.finishedAt ? 'This one-time schedule has run; it stays off until you give it a new time'
        : schedule?.proposedBy && !schedule.enabled ? 'An agent proposed this schedule: enabling it lets it run on its own' : ''}>
      <input type="checkbox" bind:checked={f.enabled} />
      <span>Enabled</span>
    </label>
  </div>

  <label class="fld">
    <span class="f-label">Name</span>
    <input class="np-input" placeholder="Defaults to the prompt's start" bind:value={f.name} />
  </label>

  <label class="fld">
    <span class="f-label">Prompt</span>
    <textarea class="np-input" rows="6" placeholder="What the fresh chat is told" bind:value={f.prompt}></textarea>
  </label>

  <div class="fld">
    <span class="f-label">Repeat</span>
    <Segmented
      value={f.kind}
      fit={false}
      options={[
        { value: 'once', label: 'Once' },
        { value: 'every', label: 'Every' },
        { value: 'daily', label: 'Daily' },
      ]}
    />
  </div>

  {#if f.kind === 'once'}
    <label class="fld">
      <span class="f-label">When</span>
      <input class="np-input" type="datetime-local" min={minNow()} bind:value={f.atLocal} />
    </label>
  {/if}
  {#if f.kind === 'every'}
    <label class="fld">
      <span class="f-label">Every (minutes)</span>
      <input class="np-input" type="number" min="5" step="1" bind:value={f.minutes} />
    </label>
  {/if}
  {#if f.kind === 'daily'}
    <div class="fld f-pair">
      <label>
        <span class="f-label">Time</span>
        <input class="np-input" type="time" bind:value={f.time} />
      </label>
      <label>
        <span class="f-label">Zone</span>
        <input class="np-input" list="np-tz-zones" bind:value={f.tz} />
      </label>
    </div>
    <datalist id="np-tz-zones">
      {#each zones as z (z)}<option value={z}></option>{/each}
    </datalist>
  {/if}

  <label class="fld">
    <span class="f-label">Project</span>
    <select class="np-input" bind:value={f.projectId}>
      <option value="">No project (default workspace)</option>
      {#each projects as p (p.id)}
        <option value={p.id}>{p.name}</option>
      {/each}
    </select>
  </label>

  <label class="fld">
    <span class="f-label">Agent</span>
    {#if agents === null}
      <input class="np-input" placeholder="agent key (agents.list unavailable)" bind:value={f.agent} />
    {:else}
      <select class="np-input" bind:value={f.agent}>
        <option value="">Default</option>
        <option value="any">Any available</option>
        {#each agentOptions as a (a.value)}
          <option value={a.value}>{a.label}</option>
        {/each}
      </select>
    {/if}
  </label>

  <label class="fld">
    <span class="f-label">Priority</span>
    <select class="np-input" bind:value={f.priority} title="The order its chats queue in while their agent is busy">
      <option value="low">Low: after other queued work</option>
      <option value="normal">Normal</option>
      <option value="high">High: first in the queue</option>
      <option value="critical">Critical: takes an instance from lower work</option>
    </select>
  </label>

  {#if editing}
    <div class="np-small np-dim">Changing the schedule counts the next run from now.</div>
  {/if}
  {#if error}
    <div class="f-err" role="alert">{error}</div>
  {/if}

  <div class="f-actions">
    <Button variant="primary" size="sm" disabled={busy} onclick={save}>{busy ? 'Saving…' : 'Save'}</Button>
    <Button variant="ghost" size="sm" disabled={busy} onclick={() => oncancel?.()}>Cancel</Button>
  </div>
</div>

<style>
  .form {
    display: flex;
    flex-direction: column;
    gap: 8px;
    padding: 10px;
  }
  .f-title {
    font-size: 13px;
  }
  .f-head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
  }
  .f-enabled {
    font-size: 12px;
  }
  .fld {
    display: flex;
    flex-direction: column;
    gap: 3px;
  }
  .f-pair {
    flex-direction: row;
    gap: 8px;
  }
  .f-pair > label {
    flex: 1;
    display: flex;
    flex-direction: column;
    gap: 3px;
    min-width: 0;
  }
  .f-label {
    font-size: 11px;
    opacity: 0.65;
  }
  .f-err {
    font-size: 12px;
    color: var(--np-err, #e05555);
  }
  .f-actions {
    display: flex;
    gap: 6px;
  }
</style>
