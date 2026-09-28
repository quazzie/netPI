<script>
  import Icon from '../../lib/kit/Icon.svelte';
  import ShellView from './tools/ShellView.svelte';
  import DiffView from './tools/DiffView.svelte';
  import ReadView from './tools/ReadView.svelte';
  import SearchView from './tools/SearchView.svelte';
  import AgentView from './tools/AgentView.svelte';
  import GenericView from './tools/GenericView.svelte';
  import WebView from './tools/WebView.svelte';
  import TodoView from './tools/TodoView.svelte';
  import { parseArgs, toolMeta, toolSummary, toolBadge, pathArg, viewName } from '../../lib/tools.js';
  import { openFile } from '../../lib/openFile.js';
  import { asks, approvalFor, answerApproval, opinionText } from '../../lib/state/asks.svelte.js';
  import { toast } from '../../lib/state/ui.svelte.js';
  import { duration } from '../../lib/format.js';
  import { app } from '../../lib/state/app.svelte.js';

  /**
   * step: { kind:'tool', key, call, result, resultMsg, msg } — `live` = the run is still in progress.
   * While the call streams (step.preparing, msg null) the row already sits where the finished call will be.
   */
  let { step, chat, base, live = false } = $props();

  const call = $derived(step.call);
  const result = $derived(step.result);
  const args = $derived(parseArgs(call));
  // a tool with actions (ssh, process, agents) is shown as the tool of its action
  const name = $derived(viewName(call.name, args));
  const meta = $derived(toolMeta(name));
  const lt = $derived(chat.live.get(call.id));
  // opened mid-run: tool.start was missed, but the agent's activity names the tool that is executing
  const runningNow = $derived.by(() => {
    if (!live || result || lt) return false;
    const a = app.agents.get(chat.id);
    return a?.status === 'running' && typeof a.activity === 'string' && /^tool:/.test(a.activity) && a.activity.includes(call.name);
  });
  // calls that never ran (steering skipped them, the run was aborted/stopped, a hook such as a guardrail blocked them)
  // are not failures
  const skipped = $derived(
    !result
      ? null
      : (result.details?.skipped ??
          (/^Skipped:/.test(result.content ?? '')
            ? 'steer'
            : /^(Aborted:|Not executed)/.test(result.content ?? '')
              ? 'aborted'
              : /^Blocked:/.test(result.content ?? '')
                ? 'blocked'
                : null)),
  );
  // a guardrail asks the user before this call runs (plugins/NetPI.Guardrails)
  const approval = $derived(result ? null : approvalFor(chat.id, call.id));
  // an ask rule matched, and the second opinion found it read-only, so it ran without asking
  const cleared = $derived(asks.cleared.get(JSON.stringify([chat.id, call.id])) ?? null);
  let deciding = $state(false);
  async function decide(allow, scope = 'once') {
    deciding = true;
    try {
      await answerApproval(approval.approvalId, allow, scope);
    } catch (e) {
      toast(e.message, 'error');
    } finally {
      deciding = false;
    }
  }
  const status = $derived(
    step.preparing
      ? 'preparing'
      : approval
        ? 'approval'
        : result
        ? skipped
          ? 'skipped'
          : result.isError
            ? 'error'
            : 'ok'
        : lt
          ? lt.status === 'running'
            ? 'running'
            : lt.status === 'error'
              ? 'error'
              : 'ok'
          : runningNow
            ? 'running'
            : live && step.msg?.stopReason !== 'aborted'
              ? 'pending'
              : 'cancelled',
  );
  const summary = $derived(toolSummary(name, args, base));
  // read / write / edit: open the file with the operating system
  const filePath = $derived(['read', 'write', 'edit'].includes(name) ? pathArg(args) : null);
  const badge = $derived.by(() => {
    const b = result ? toolBadge(name, result) : null;
    // agent_spawn records the status at spawn time; prefer the subagent's live status when known
    const d = result?.details;
    if (b && call.name === 'agent_spawn' && d?.sessionId && typeof d.status === 'string') {
      const s = app.agents.get(d.sessionId)?.status;
      if (s && s !== d.status) return { ...b, text: s };
    }
    return b;
  });
  const open = $derived(chat.expanded.get(step.key) ?? false);

  let now = $state(Date.now());
  $effect(() => {
    if (status !== 'running') return;
    now = Date.now();
    const t = setInterval(() => (now = Date.now()), 1000);
    return () => clearInterval(t);
  });
  const dur = $derived(
    result?.durationMs ?? lt?.durationMs ?? (status === 'running' && lt ? Math.max(0, now - lt.startedAt) : null),
  );

  // collapsed running shell command: show the last few output lines under the row, once it has run for a second (a
  // quick command would flash them in and out)
  const tail = $derived.by(() => {
    if (open || status !== 'running' || meta.view !== 'shell' || !lt?.output || now - lt.startedAt < 1000) return '';
    const out = lt.output.replace(/\n+$/, '');
    let idx = out.length;
    for (let k = 0; k < 5 && idx > 0; k++) idx = out.lastIndexOf('\n', idx - 1);
    return out.slice(idx + 1);
  });

  function toggle() {
    chat.expanded.set(step.key, !open);
  }
</script>

<div class="tool" data-status={status} class:open>
  <button class="line" onclick={toggle} aria-expanded={open} disabled={status === 'preparing'}>
    <span class="ic">
      {#if status === 'running' || status === 'preparing'}
        <span class="np-spinner"></span>
      {:else}
        <Icon name={status === 'error' ? 'circle-x' : status === 'cancelled' || status === 'skipped' ? 'ban' : status === 'approval' ? 'alert' : meta.icon} size={14} />
      {/if}
    </span>
    <span class="label">{meta.label}</span>
    {#if summary}<span class="summary np-mono" title={summary}>{summary}</span>{/if}
    <span class="spacer"></span>
    {#if badge}
      {#if badge.tone === 'diff'}
        {@const [a, r] = badge.text.split(' ')}
        <span class="diffbadge np-mono"><span class="add">{a}</span> <span class="del">{r}</span></span>
      {:else}
        <span class="np-badge" data-tone={badge.tone}>{badge.text}</span>
      {/if}
    {/if}
    {#if status === 'preparing'}<span class="state">preparing…</span>{/if}
    {#if status === 'approval'}<span class="state asks">needs your OK</span>{/if}
    {#if cleared && status !== 'approval'}
      {#if cleared.by === 'session'}<span class="state" title="A guardrail asks first ({cleared.rule}); you allowed it for this chat, so it ran without asking">allowed in this chat</span>
      {:else}<span class="state" title="A guardrail asks first ({cleared.rule}); {opinionText(cleared.opinion)}, so it ran without asking">checked</span>{/if}
    {/if}
    {#if status === 'pending'}<span class="state">queued</span>{/if}
    {#if status === 'cancelled'}<span class="state">no result</span>{/if}
    {#if status === 'skipped'}<span class="state">{skipped === 'steer' ? 'skipped · new message' : skipped === 'stopped' ? 'not run' : skipped === 'blocked' ? 'blocked' : 'aborted'}</span>{/if}
    {#if dur != null && status !== 'skipped'}<span class="dur np-mono">{duration(dur)}</span>{/if}
    <span class="chev" class:open><Icon name="chevron-right" size={12} /></span>
  </button>

  {#if approval}
    <div class="approve">
      <span class="why">
        {#if approval.kind === 'path'}It touches <span class="np-mono subj">{approval.subject}</span>: a guardrail asks you first{:else}A
          guardrail asks you first{/if}
        (<span class="np-mono rule">{approval.rule}</span>)
        {#if approval.opinion}<span class="opinion">{opinionText(approval.opinion)}</span>{/if}
      </span>
      <span class="btns">
        <button class="np-btn np-btn-sm" disabled={deciding} onclick={() => decide(false)}>No</button>
        <button
          class="np-btn np-btn-sm"
          disabled={deciding}
          title="Allow it, and don't ask again in this chat for anything the guardrail {approval.rule} matches"
          onclick={() => decide(true, 'session')}>Allow in this chat</button
        >
        <button class="np-btn np-btn-sm np-btn-primary" disabled={deciding} onclick={() => decide(true)}>Allow</button>
      </span>
    </div>
  {/if}

  {#if tail}
    <pre class="tail np-mono">{tail}</pre>
  {/if}

  {#if open}
    <div class="body">
      {#if result?.isError && meta.view !== 'shell'}
        <pre class="error np-mono">{result.content}</pre>
      {/if}
      {#if filePath && !result?.isError}
        <button class="openfile" title="Open {filePath} with its default app" onclick={() => openFile(filePath, chat.id)}>
          <Icon name="external" size={12} /> Open file
        </button>
      {/if}
      {#if meta.view === 'shell'}
        <ShellView name={name} {args} {result} live={lt} />
      {:else if meta.view === 'edit' || meta.view === 'write'}
        <DiffView name={name} {args} {result} />
      {:else if meta.view === 'read'}
        {#if !result?.isError}<ReadView {args} {result} />{/if}
      {:else if meta.view === 'search'}
        {#if !result?.isError}<SearchView name={name} {result} />{/if}
      {:else if meta.view === 'agent'}
        <AgentView name={name} {args} {result} />
      {:else if meta.view === 'web'}
        {#if !result?.isError}<WebView name={name} {result} />{/if}
      {:else if meta.view === 'todo'}
        <TodoView {args} {result} />
      {:else}
        <GenericView {args} {result} />
      {/if}
    </div>
  {/if}
</div>

<style>
  .tool {
    min-width: 0;
  }
  .line {
    display: flex;
    align-items: center;
    gap: 8px;
    width: 100%;
    height: 28px;
    padding: 0 8px 0 4px;
    margin-left: -4px;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--fg-muted);
    font-size: var(--fs-sm);
    text-align: left;
    min-width: 0;
  }
  .line:hover {
    background: var(--bg-2);
  }
  .tool.open > .line {
    background: var(--bg-2);
    border-bottom-left-radius: 0;
    border-bottom-right-radius: 0;
  }
  .ic {
    display: grid;
    place-items: center;
    width: 16px;
    flex: none;
    color: var(--fg-dim);
  }
  .tool[data-status='running'] .ic {
    color: var(--accent);
  }
  .tool[data-status='error'] .ic,
  .tool[data-status='error'] .label {
    color: var(--err);
  }
  .tool[data-status='cancelled'] .label,
  .tool[data-status='skipped'] .label,
  .tool[data-status='skipped'] .ic,
  .tool[data-status='pending'] .label {
    color: var(--fg-dim);
  }
  .label {
    flex: none;
    color: var(--fg);
    font-weight: 500;
  }
  .summary {
    min-width: 0;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
    color: var(--fg-muted);
    font-size: 12px;
  }
  .spacer {
    flex: 1;
  }
  .diffbadge {
    flex: none;
    font-size: 11px;
  }
  .diffbadge .add {
    color: var(--ok);
  }
  .diffbadge .del {
    color: var(--err);
  }
  .state {
    flex: none;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
  }
  .state.asks,
  .tool[data-status='approval'] .ic {
    color: var(--warn);
  }
  /* a guardrail asks the user first: why, and Allow / No, under the row (it wraps on a narrow chat) */
  .approve {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 6px 10px;
    margin: 0 0 6px 20px;
    padding: 6px 10px;
    border-left: 2px solid var(--warn);
    background: var(--warn-soft);
    border-radius: 0 6px 6px 0;
    font-size: var(--fs-sm);
    color: var(--fg-muted);
  }
  .why {
    flex: 1 1 180px;
    min-width: 0;
    overflow-wrap: anywhere;
  }
  /* the second opinion (guardrails.secondOpinion): the decision model's view, on its own line */
  .opinion {
    display: block;
    margin-top: 2px;
    color: var(--fg-dim);
  }
  .rule,
  .subj {
    color: var(--fg);
    font-size: 11.5px;
  }
  .btns {
    display: flex;
    flex: none;
    gap: 6px;
    margin-left: auto;
  }
  .dur {
    flex: none;
    min-width: 34px;
    text-align: right;
    color: var(--fg-dim);
    font-size: 11px;
  }
  .chev {
    flex: none;
    display: inline-grid;
    color: var(--fg-dim);
    opacity: 0;
    transition:
      transform var(--t-fast),
      opacity var(--t-fast);
  }
  .line:hover .chev,
  .chev.open {
    opacity: 1;
  }
  .chev.open {
    transform: rotate(90deg);
  }
  .tail {
    margin: 0 0 4px 20px;
    padding: 4px 10px;
    border-left: 2px solid var(--accent-line);
    color: var(--fg-dim);
    font-size: 11.5px;
    line-height: 1.45;
    white-space: pre-wrap;
    overflow-wrap: anywhere;
    max-height: 7.5em;
    overflow: hidden;
  }
  .body {
    margin: 0 0 8px -4px;
    padding: 8px 10px 10px;
    border: 1px solid var(--bg-2);
    border-top: 0;
    border-radius: 0 0 8px 8px;
    background: color-mix(in srgb, var(--bg-2) 45%, transparent);
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 8px;
  }
  .error {
    margin: 0;
    padding: 8px 10px;
    border-radius: var(--radius-sm);
    background: var(--err-soft);
    color: var(--err);
    white-space: pre-wrap;
    overflow-wrap: anywhere;
    font-size: 12px;
    max-height: 240px;
    overflow: auto;
  }
  .openfile {
    display: inline-flex;
    align-items: center;
    gap: 5px;
    align-self: flex-end;
    float: right;
    margin: 0 0 4px 8px;
    padding: 2px 6px;
    border: 0;
    border-radius: 5px;
    background: transparent;
    color: var(--fg-dim);
    font-size: var(--fs-xs);
  }
  .openfile:hover {
    background: var(--bg-3);
    color: var(--fg);
  }
</style>
