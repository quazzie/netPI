<script>
  import Icon from '../../lib/kit/Icon.svelte';
  import ShellView from './tools/ShellView.svelte';
  import DiffView from './tools/DiffView.svelte';
  import ReadView from './tools/ReadView.svelte';
  import SearchView from './tools/SearchView.svelte';
  import AgentView from './tools/AgentView.svelte';
  import GenericView from './tools/GenericView.svelte';
  import { parseArgs, toolMeta, toolSummary, toolBadge } from '../../lib/tools.js';
  import { duration } from '../../lib/format.js';

  /** step: { kind:'tool', key, call, result, resultMsg, msg } — `live` = the run is still in progress */
  let { step, chat, base, live = false } = $props();

  const call = $derived(step.call);
  const result = $derived(step.result);
  const args = $derived(parseArgs(call));
  const meta = $derived(toolMeta(call.name));
  const lt = $derived(chat.live.get(call.id));
  const status = $derived(
    result
      ? result.isError
        ? 'error'
        : 'ok'
      : lt
        ? lt.status === 'running'
          ? 'running'
          : lt.status === 'error'
            ? 'error'
            : 'ok'
        : live && step.msg.stopReason !== 'aborted'
          ? 'pending'
          : 'cancelled',
  );
  const summary = $derived(toolSummary(call.name, args, base));
  const badge = $derived(result ? toolBadge(call.name, result) : null);
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

  // collapsed running shell command: show the last few output lines under the row
  const tail = $derived.by(() => {
    if (open || status !== 'running' || meta.view !== 'shell' || !lt?.output) return '';
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
  <button class="line" onclick={toggle} aria-expanded={open}>
    <span class="ic">
      {#if status === 'running'}
        <span class="np-spinner"></span>
      {:else}
        <Icon name={status === 'error' ? 'circle-x' : status === 'cancelled' ? 'ban' : meta.icon} size={14} />
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
    {#if status === 'pending'}<span class="state">queued</span>{/if}
    {#if status === 'cancelled'}<span class="state">no result</span>{/if}
    {#if dur != null}<span class="dur np-mono">{duration(dur)}</span>{/if}
    <span class="chev" class:open><Icon name="chevron-right" size={12} /></span>
  </button>

  {#if tail}
    <pre class="tail np-mono">{tail}</pre>
  {/if}

  {#if open}
    <div class="body">
      {#if result?.isError && meta.view !== 'shell'}
        <pre class="error np-mono">{result.content}</pre>
      {/if}
      {#if meta.view === 'shell'}
        <ShellView name={call.name} {args} {result} live={lt} />
      {:else if meta.view === 'edit' || meta.view === 'write'}
        <DiffView name={call.name} {args} {result} />
      {:else if meta.view === 'read'}
        {#if !result?.isError}<ReadView {args} {result} />{/if}
      {:else if meta.view === 'search'}
        {#if !result?.isError}<SearchView name={call.name} {result} />{/if}
      {:else if meta.view === 'agent'}
        <AgentView name={call.name} {args} {result} />
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
</style>
