<script>
  import { tokens } from '../../lib/format.js';
  import Icon from '../../lib/kit/Icon.svelte';
  import Popover from '../Popover.svelte';
  import { rpc } from '../../lib/rpc.svelte.js';
  import { openPanelTab } from '../../lib/state/tabs.svelte.js';
  /**
   * Context usage ring: used / window tokens. Hover for the one-line reading, press for the breakdown
   * (what the prompt is made of, from context.preview) and a way into the Diagnostics context view.
   */
  let { used = 0, window: win = 0, sessionId = null } = $props();
  const R = 7;
  const C = 2 * Math.PI * R;
  const frac = $derived(win ? Math.min(1, Math.max(0, used / win)) : 0);
  const tone = $derived(frac >= 0.9 ? 'err' : frac >= 0.7 ? 'warn' : 'ok');
  const tip = $derived(
    win ? `Context ${tokens(used)} / ${tokens(win)} tokens (${Math.round(frac * 100)}%) — click for the breakdown` : `Context ${tokens(used)} tokens`,
  );

  let el = $state();
  let open = $state(false);
  let preview = $state.raw(null); // context.preview: { systemPrompt, frozen, tools, estimatedTokens }
  let previewState = $state('idle'); // idle | loading | ready | error (per open)

  async function show() {
    open = !open;
    if (!open || !sessionId) return;
    // every time it is opened, not once per mount: the breakdown describes the prompt as it is now, and a session that
    // has run a while sends a different one than the ring was mounted with (a reload or a new tool changes it too)
    previewState = 'loading';
    try {
      preview = await rpc('context.preview', { sessionId });
      previewState = 'ready';
    } catch {
      previewState = 'error';
    }
  }

  // the prompt of a session is frozen at its first model call, so what the ring counts and what the breakdown
  // describes can differ; the rows are estimates of a prompt the model already has
  const promptTokens = $derived(preview ? Math.round((preview.systemPrompt?.length ?? 0) / 3.6) : 0);
  const toolChars = $derived((preview?.tools ?? []).reduce((n, t) => n + (t.chars ?? 0) + (t.schemaChars ?? 0), 0));
  const free = $derived(win ? Math.max(0, win - used) : 0);

  function openDiagnostics() {
    open = false;
    let prev = null;
    try {
      prev = localStorage.getItem('netpi.diag.view');
      localStorage.setItem('netpi.diag.view', 'context');
    } catch {}
    if (openPanelTab('netpi.diagnostics/diagnostics')) return;
    try {
      if (prev == null) localStorage.removeItem('netpi.diag.view');
      else localStorage.setItem('netpi.diag.view', prev);
    } catch {}
  }
</script>

<button
  bind:this={el}
  class="ring"
  data-tone={tone}
  title={tip}
  aria-label={tip}
  aria-expanded={open}
  aria-haspopup="dialog"
  onclick={show}
>
  <svg width="18" height="18" viewBox="0 0 18 18" aria-hidden="true">
    <circle cx="9" cy="9" r={R} class="track" />
    <circle
      cx="9"
      cy="9"
      r={R}
      class="bar"
      stroke-dasharray="{C * frac} {C}"
      transform="rotate(-90 9 9)"
    />
  </svg>
  <span class="pct np-mono">{win ? `${Math.round(frac * 100)}%` : tokens(used)}</span>
</button>

{#if open}
  <Popover anchor={el} placement="top-end" width={264} onclose={() => (open = false)}>
    <div class="cx">
      <div class="head np-line">
        <span class="h">Context</span>
        <span class="np-grow"></span>
        <span class="np-mono big" data-tone={tone}>{win ? `${Math.round(frac * 100)}%` : '—'}</span>
      </div>
      <div class="bar" data-tone={tone}><i style:width="{frac * 100}%"></i></div>
      <div class="rows">
        <div class="row"><span>Used</span><b class="np-mono">{tokens(used)}</b></div>
        {#if win}
          <div class="row"><span>Window</span><b class="np-mono">{tokens(win)}</b></div>
          <div class="row"><span>Free</span><b class="np-mono">{tokens(free)}</b></div>
        {:else}
          <div class="row"><span>Window</span><b class="np-dim">unknown</b></div>
        {/if}
      </div>

      <div class="sec">
        <div class="sec-h">The prompt this session is sent</div>
        {#if previewState === 'loading'}
          <div class="np-dim small"><span class="np-spinner"></span> reading…</div>
        {:else if previewState === 'error'}
          <div class="np-dim small">context.preview is unavailable (the Context plugin is off).</div>
        {:else if !preview}
          <div class="np-dim small">No breakdown for this session yet.</div>
        {:else}
          <div class="row"><span>System prompt</span><b class="np-mono">≈{tokens(promptTokens)}</b></div>
          <div class="row"><span>Tools</span><b class="np-mono">{preview.tools?.length ?? 0}</b></div>
          {#if toolChars}
            <div class="row"><span>Tool definitions</span><b class="np-mono">≈{tokens(Math.round(toolChars / 3.6))}</b></div>
          {/if}
          {#if preview.frozen}<div class="frozen np-dim small">Frozen at this session’s first model call.</div>{/if}
        {/if}
      </div>

      <div class="foot">
        <button class="np-chip link" onclick={openDiagnostics} title="Open Diagnostics → Context (prompt, tools, AGENTS.md, skills)">
          <Icon name="bug" size={12} /><span>Breakdown in Diagnostics</span>
        </button>
        <div class="np-dim tiny">The ring is the last prompt the model was sent; the rows above are estimates.</div>
      </div>
    </div>
  </Popover>
{/if}

<style>
  .ring {
    display: inline-flex;
    align-items: center;
    gap: 5px;
    height: 22px;
    padding: 0 6px;
    border: 0;
    border-radius: 11px;
    background: transparent;
    color: var(--fg-dim);
    font: inherit;
    font-size: 11px;
    cursor: default;
  }
  .ring:hover,
  .ring[aria-expanded='true'] {
    background: var(--bg-2);
    color: var(--fg);
  }
  circle {
    fill: none;
    stroke-width: 2.5;
  }
  .track {
    stroke: var(--bg-3);
  }
  .bar {
    stroke: var(--fg-muted);
    stroke-linecap: round;
    transition: stroke-dasharray var(--t);
  }
  .ring[data-tone='warn'] .bar {
    stroke: var(--warn);
  }
  .ring[data-tone='err'] .bar {
    stroke: var(--err);
  }
  .ring[data-tone='warn'] .pct {
    color: var(--warn);
  }
  .ring[data-tone='err'] .pct {
    color: var(--err);
  }

  /* the popout */
  .cx {
    display: flex;
    flex-direction: column;
    padding: 9px 11px 10px;
    font-size: var(--fs-sm);
  }
  .head .h {
    font-weight: 600;
  }
  .big {
    font-size: 15px;
    font-variant-numeric: tabular-nums;
    color: var(--fg-muted);
  }
  .big[data-tone='warn'] {
    color: var(--warn);
  }
  .big[data-tone='err'] {
    color: var(--err);
  }
  .bar {
    height: 4px;
    margin: 7px 0 9px;
    border-radius: 2px;
    background: var(--bg-3);
    overflow: hidden;
  }
  .bar i {
    display: block;
    height: 100%;
    border-radius: 2px;
    background: var(--fg-muted);
  }
  .bar[data-tone='warn'] i {
    background: var(--warn);
  }
  .bar[data-tone='err'] i {
    background: var(--err);
  }
  .rows,
  .sec {
    display: flex;
    flex-direction: column;
    gap: 3px;
  }
  .row {
    display: flex;
    align-items: baseline;
    gap: 8px;
    color: var(--fg-muted);
  }
  .row b {
    margin-left: auto;
    color: var(--fg);
    font-weight: 500;
  }
  .sec {
    margin-top: 9px;
    padding-top: 8px;
    border-top: 1px solid var(--border);
  }
  .sec-h {
    color: var(--fg-dim);
    font-size: var(--fs-xs);
    text-transform: uppercase;
    letter-spacing: 0.05em;
  }
  .small {
    font-size: var(--fs-xs);
    line-height: 1.4;
  }
  .frozen {
    margin-top: 2px;
  }
  .foot {
    display: flex;
    flex-direction: column;
    gap: 5px;
    margin-top: 9px;
    padding-top: 8px;
    border-top: 1px solid var(--border);
  }
  .link {
    display: inline-flex;
    align-items: center;
    gap: 5px;
    align-self: flex-start;
    height: 24px;
    padding: 0 8px;
    font-size: var(--fs-xs);
  }
  .tiny {
    font-size: 10px;
    line-height: 1.4;
  }
</style>
