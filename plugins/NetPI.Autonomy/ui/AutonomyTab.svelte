<script>
  // The Autonomy tab: what chats nobody watches (schedules, webhooks, subagents) were refused — allow it from now on —,
  // what schedules' and webhooks' chats changed — undo a file change —, and the standing grants. Pulled when looked
  // at; nothing here is pushed. autonomy.changed (unscoped) is the refresh backbone.
  import { useRefresh, Button, IconButton, ConfirmButton, Empty, Segmented, TimeAgo, confirm } from '@netpi/kit';
  import { undoState, refusalState, openRefusals, whoLine, refusalWhy, checkpointFiles } from './model.js';

  let { ctx } = $props();
  let visible = $state(true);
  let view = $state('refused');
  let state = $state.raw(null);
  let refusals = $state.raw([]);
  let journal = $state.raw([]);
  let grants = $state.raw([]);
  let error = $state('');
  let busy = $state('');

  async function load() {
    try {
      const [st, rf, jr, gr] = await Promise.all([
        ctx.rpc('autonomy.state', {}),
        ctx.rpc('autonomy.refusals', { limit: 100 }),
        ctx.rpc('autonomy.journal', { limit: 150 }),
        ctx.rpc('autonomy.grants', {}),
      ]);
      state = st;
      refusals = rf ?? [];
      journal = jr ?? [];
      grants = gr ?? [];
      error = '';
    } catch (e) {
      error = String(e?.message ?? e);
    }
  }

  // svelte-ignore state_referenced_locally
  const tab = useRefresh(ctx, { load, events: ['autonomy.changed'], pollMs: 60_000, visible: () => visible });

  export function setVisible(v) {
    visible = v;
    tab.setVisible?.(v);
  }

  async function act(key, fn) {
    busy = key;
    try {
      await fn();
      error = '';
    } catch (e) {
      error = String(e?.message ?? e);
    } finally {
      busy = '';
      tab.refresh();
    }
  }

  const pause = () => act('pause', () => ctx.rpc('autonomy.pause', { paused: !state?.paused }));
  const grant = (r) => act(r.id, () => ctx.rpc('autonomy.grant', { refusalId: r.id }));
  const revoke = (g) => act(g.id, () => ctx.rpc('autonomy.revoke', { id: g.id }));

  async function undo(row) {
    const files = checkpointFiles(row).map((f) => f.path);
    const ok = await confirm({ title: 'Undo this change?', message: `Put back as it was before:\n${files.join('\n')}`, confirmLabel: 'Undo' });
    if (!ok) return;
    await act(row.id, async () => {
      try {
        await ctx.rpc('autonomy.undo', { id: row.id });
      } catch (e) {
        if (!String(e?.message ?? e).includes('changed again')) throw e;
        const force = await confirm({
          title: 'Changed again since',
          message: `${String(e?.message ?? e)}\n\nPut the old version back anyway? The later changes are lost.`,
          confirmLabel: 'Undo anyway',
          danger: true,
        });
        if (force) await ctx.rpc('autonomy.undo', { id: row.id, force: true });
      }
    });
  }

  const views = $derived([
    { value: 'refused', label: 'Refused', icon: 'ban', count: openRefusals(refusals) || undefined },
    { value: 'changes', label: 'Changes', icon: 'history' },
    { value: 'grants', label: 'Grants', icon: 'check', count: grants.length || undefined },
  ]);
</script>

<div class="autonomy">
  <div class="np-toolbar">
    <span class="np-strong">Autonomy</span>
    {#if state}<span class="np-dim" title="Changes in the last hour by chats of schedules and webhooks">{state.changesLastHour}/h</span>{/if}
    <span class="np-grow"></span>
    {#if state}
      <Button
        size="sm"
        variant={state.paused ? 'danger' : 'default'}
        icon={state.paused ? 'play' : 'pause'}
        disabled={busy === 'pause'}
        title={state.paused ? 'Changes by schedules and webhooks are refused: resume them' : 'Refuse every change by schedules and webhooks (they still read)'}
        data-testid="autonomy-pause"
        onclick={pause}>{state.paused ? 'Resume' : 'Pause'}</Button
      >
    {/if}
    <IconButton icon="refresh" size="sm" title="Refresh" onclick={() => tab.refresh()} />
  </div>
  {#if state?.paused}<div class="banner">Paused: chats of schedules and webhooks may read, nothing they change runs.</div>{/if}
  <div class="seg"><Segmented options={views} bind:value={view} /></div>
  {#if error}<div class="err">{error}</div>{/if}

  <div class="np-list">
    {#if state === null && !error}
      <div class="np-dim np-pad">Loading…</div>
    {:else if view === 'refused'}
      {#if refusals.length === 0}
        <Empty icon="ban">Nothing refused. When a chat nobody watches meets a guardrail's question, it is listed here instead of waiting.</Empty>
      {:else}
        {#each refusals as r (r.id)}
          {@const st = refusalState(r)}
          <div class="row" data-testid="autonomy-refusal">
            <div class="line"><span class="tool">{r.tool}</span> <span class="np-mono subj" title={r.subject}>{r.subject}</span></div>
            <div class="line dim">
              <span class="who" title={whoLine(r)}>{whoLine(r)}</span> · <TimeAgo time={r.at} />
            </div>
            <div class="line dim why" title={refusalWhy(r)}>{refusalWhy(r)}</div>
            <div class="btns">
              {#if st === 'grantable'}
                <Button size="sm" variant="primary" disabled={busy === r.id} title="Let {r.label} run what this rule matches without asking, from now on" onclick={() => grant(r)}
                  >Allow for {r.label}</Button
                >
              {:else if st === 'granted'}<span class="np-dim">allowed now</span>{:else}<span class="np-dim" title="Never allowed after outside text (autonomy.taintedBlocked)">never after outside text</span>{/if}
              {#if r.sessionId}<button class="link" onclick={() => ctx.app.openSession?.(r.sessionId)}>chat</button>{/if}
            </div>
          </div>
        {/each}
      {/if}
    {:else if view === 'changes'}
      {#if journal.length === 0}
        <Empty icon="history">No changes yet. What chats of schedules and webhooks change is listed here, newest first.</Empty>
      {:else}
        {#each journal as j (j.id)}
          {@const u = undoState(j)}
          <div class="row" class:failed={j.error} data-testid="autonomy-change">
            <div class="line"><span class="tool">{j.tool}</span> <span class="np-mono subj" title={j.subject}>{j.subject}</span></div>
            <div class="line dim">
              <span class="who" title={whoLine(j)}>{whoLine(j)}</span> · <TimeAgo time={j.at} />{#if j.error} · <span class="bad">failed</span>{/if}
            </div>
            {#if j.note}<div class="line dim" title={j.note}>{j.note}</div>{/if}
            <div class="btns">
              {#if u === 'ready'}
                <Button size="sm" icon="corner-up" disabled={busy === j.id} title="Put the files back as they were before this change" onclick={() => undo(j)}>Undo</Button>
              {:else if u === 'undone'}<span class="np-dim">undone</span>{/if}
              {#if j.sessionId}<button class="link" onclick={() => ctx.app.openSession?.(j.sessionId)}>chat</button>{/if}
            </div>
          </div>
        {/each}
      {/if}
    {:else if grants.length === 0}
      <Empty icon="check">No grants. "Allow for …" on a refusal, or on a guardrail's question, adds one.</Empty>
    {:else}
      {#each grants as g (g.id)}
        <div class="row" data-testid="autonomy-grant">
          <div class="line"><span class="np-mono subj" title={g.rule}>{g.rule}</span></div>
          <div class="line dim">
            {g.label} · {g.uses ?? 0}× {#if g.lastUsedMs}· last <TimeAgo time={new Date(g.lastUsedMs).toISOString()} />{:else}· not used yet{/if}
          </div>
          <div class="btns">
            <ConfirmButton icon="trash" label="Remove" confirmLabel="Remove?" title="Ask again from now on" disabled={busy === g.id} onconfirm={() => revoke(g)} />
          </div>
        </div>
      {/each}
    {/if}
  </div>
</div>

<style>
  .autonomy {
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
    border-bottom: 1px solid var(--border);
  }
  .seg {
    padding: 6px 10px 2px;
  }
  .banner {
    padding: 6px 10px;
    font-size: var(--fs-sm);
    color: var(--warn);
    border-bottom: 1px solid var(--border);
  }
  .err {
    padding: 4px 10px;
    font-size: var(--fs-sm);
    color: var(--err);
    overflow-wrap: anywhere;
  }
  .np-list {
    overflow-y: auto;
    flex: 1;
  }
  .np-pad {
    padding: 10px;
  }
  .row {
    padding: 6px 10px;
    border-bottom: 1px solid var(--border);
    min-width: 0;
  }
  .row.failed .tool {
    color: var(--err);
  }
  .line {
    display: flex;
    gap: 4px;
    min-width: 0;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    font-size: var(--fs-sm);
    line-height: 18px;
  }
  .line.dim {
    color: var(--fg-dim);
    font-size: var(--fs-xs);
  }
  .tool {
    flex: none;
    font-weight: 600;
  }
  .subj,
  .who,
  .why {
    overflow: hidden;
    text-overflow: ellipsis;
    min-width: 0;
  }
  .who {
    flex: 0 1 auto;
  }
  .bad {
    color: var(--err);
  }
  .btns {
    display: flex;
    align-items: center;
    gap: 6px;
    margin-top: 3px;
    flex-wrap: wrap;
  }
  .link {
    background: none;
    border: 0;
    padding: 0;
    color: var(--accent);
    cursor: pointer;
    font-size: var(--fs-xs);
  }
</style>
