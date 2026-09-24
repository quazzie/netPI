<script>
  import { onDestroy, tick } from 'svelte';
  import { Icon, Elapsed, TimeAgo, ConfirmButton, bytes, duration, copyText } from '@netpi/kit';

  let { proc, ctx } = $props();

  const MAX = 64 * 1024;
  let open = $state(false);
  let output = $state('');
  let loadingOut = $state(false);
  let pre = $state();
  let off = null;
  let poll = 0;
  let lastEvent = 0;

  const running = $derived(proc.status === 'running');
  const tone = $derived(
    running ? 'run' : proc.status === 'killed' || proc.status === 'timeout' ? 'err' : proc.exitCode ? 'warn' : 'ok',
  );
  const icon = $derived(running ? null : tone === 'ok' ? 'circle-check' : tone === 'warn' ? 'alert-circle' : 'circle-x');
  const dur = $derived(proc.endedAt && proc.startedAt ? Date.parse(proc.endedAt) - Date.parse(proc.startedAt) : null);

  function append(chunk) {
    output = (output + chunk).slice(-MAX);
    stick();
  }
  async function stick() {
    await tick();
    if (pre && pre.scrollHeight - pre.scrollTop - pre.clientHeight < 60) pre.scrollTop = pre.scrollHeight;
  }

  async function fetchTail() {
    try {
      const text = await ctx.rpc('processes.output', { id: proc.id, tail: 300 });
      output = String(text ?? '').slice(-MAX);
      await tick();
      if (pre) pre.scrollTop = pre.scrollHeight;
    } catch (e) {
      output = `(${e.message})`;
    }
  }

  async function toggle() {
    open = !open;
    if (!open) return stopLive();
    loadingOut = true;
    await fetchTail();
    loadingOut = false;
    // background processes stream process.output events; foreground ones are polled while running
    off = ctx.on('process.output', (d) => {
      if (d?.id !== proc.id) return;
      lastEvent = Date.now();
      append(d.chunk ?? '');
    });
    poll = setInterval(() => {
      if (proc.status === 'running' && Date.now() - lastEvent > 3000) fetchTail();
    }, 2000);
  }

  function stopLive() {
    off?.();
    off = null;
    clearInterval(poll);
  }
  $effect(() => {
    // one last fetch when the process exits while expanded
    if (open && !running) {
      stopLive();
      fetchTail();
    }
  });
  onDestroy(stopLive);

  async function kill() {
    const id = proc.id; // the row moves to "Recent" (another instance) once the process exits
    try {
      const ok = await ctx.rpc('processes.kill', { id });
      ctx.app.toast(ok ? `Killed ${id}` : `${id} was not running`, ok ? 'info' : 'warn');
    } catch (e) {
      ctx.app.toast(`Kill failed: ${e.message}`, 'error');
    }
  }
</script>

<div class="proc np-hover-row" data-tone={tone} class:open>
  <div class="row np-line" role="button" tabindex="0" onclick={toggle} onkeydown={(e) => e.key === 'Enter' && toggle()} aria-expanded={open} title={proc.command}>
    <span class="ic">{#if running}<span class="np-spinner"></span>{:else}<Icon name={icon} size={13} />{/if}</span>
    <span class="cmd np-mono np-grow">{proc.command}</span>
    {#if running}
      <Elapsed since={proc.startedAt} class="np-mono el" />
    {:else}
      <span class="res np-mono">{proc.status === 'exited' ? `exit ${proc.exitCode ?? '?'}` : proc.status}</span>
    {/if}
  </div>
  <div class="line2 np-line">
    <span class="np-meta np-grow" title="{proc.shell} · pid {proc.pid}{proc.background ? ' · background' : ''} · {proc.cwd}">
      {#if proc.shell && proc.shell !== 'bash'}<span>{proc.shell}</span>{/if}<span>pid {proc.pid}</span>{#if proc.background}<span class="bg">bg</span>{/if}{#if !running && dur != null}<span>{duration(dur)}</span>{/if}{#if proc.outputBytes}<span>{bytes(proc.outputBytes)}</span>{/if}<span class="cwd">{proc.cwd}</span>
    </span>
    {#if !running && proc.endedAt}<TimeAgo time={proc.endedAt} class="when" />{/if}
  </div>
  {#if running}
    <span class="np-hover-actions"><ConfirmButton icon="kill" title="Kill process tree" confirmLabel="Kill?" onconfirm={kill} /></span>
  {/if}
  {#if open}
    <div class="out-wrap">
      {#if loadingOut && !output}
        <div class="np-dim np-small">loading output…</div>
      {:else}
        <pre class="out np-mono np-scroll" bind:this={pre}>{output || '(no output)'}</pre>
        <div class="out-foot np-line">
          {#if running}<span class="live"><span class="np-dot" data-status="running"></span>live</span>{/if}
          <span class="np-grow"></span>
          <button class="lnk" onclick={() => copyText(proc.command)}>copy command</button>
          <button class="lnk" onclick={fetchTail}>reload</button>
        </div>
      {/if}
    </div>
  {/if}
</div>

<style>
  .proc {
    padding: 3px 4px 4px;
    margin: 0 -4px;
    border-radius: var(--radius-sm);
  }
  .proc:hover {
    --row-bg: var(--bg-2);
    background: var(--bg-2);
  }
  .proc.open {
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
  .ic {
    display: grid;
    place-items: center;
    width: 14px;
    flex: none;
    color: var(--fg-dim);
  }
  [data-tone='run'] .ic {
    color: var(--accent);
  }
  [data-tone='ok'] .ic {
    color: var(--ok);
  }
  [data-tone='warn'] .ic,
  [data-tone='warn'] .res {
    color: var(--warn);
  }
  [data-tone='err'] .ic,
  [data-tone='err'] .res {
    color: var(--err);
  }
  .cmd {
    font-size: 12px;
    color: var(--fg);
  }
  .res {
    flex: none;
    font-size: 11px;
    color: var(--fg-dim);
  }
  .row :global(.el) {
    flex: none;
    font-size: 11px;
    color: var(--fg-dim);
    white-space: nowrap;
  }
  .line2 {
    padding-left: 21px;
    gap: 8px;
  }
  .line2 .np-meta {
    font-size: 10.5px;
  }
  .line2 :global(.when) {
    font-size: 10.5px;
    color: var(--fg-dim);
  }
  .bg {
    color: var(--info);
  }
  .cwd {
    font-family: var(--font-mono);
  }
  .out-wrap {
    margin: 5px 0 1px;
  }
  .out {
    margin: 0;
    padding: 6px 8px;
    max-height: 220px;
    overflow: auto;
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--code-bg);
    color: var(--fg-muted);
    font-size: 11px;
    line-height: 1.45;
    white-space: pre-wrap;
    overflow-wrap: anywhere;
  }
  .out-foot {
    gap: 10px;
    padding-top: 3px;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
  }
  .live {
    display: inline-flex;
    align-items: center;
    gap: 5px;
    color: var(--accent);
  }
  .lnk {
    border: 0;
    padding: 0;
    background: transparent;
    color: var(--fg-dim);
    font: inherit;
    cursor: pointer;
  }
  .lnk:hover {
    color: var(--fg);
  }
</style>
