<script>
  /**
   * The one line that says the agent is busy, above the composer: what it does in a word (Thinking, Writing, Waiting for …;
   * otherwise a playful verb chosen per run) and how long the run has taken. The steps group shows the tool that runs and
   * the composer's hints say how to stop, so this line repeats neither.
   */
  import { app, isBusy } from '../../lib/state/app.svelte.js';
  import { duration } from '../../lib/format.js';

  let { session } = $props();

  const VERBS = ['Working', 'Tinkering', 'Pondering', 'Crunching', 'Brewing', 'Wrangling', 'Noodling', 'Cooking', 'Conjuring',
    'Untangling', 'Percolating', 'Churning', 'Mulling', 'Whirring', 'Puzzling', 'Scheming'];
  const GLYPHS = ['·', '✢', '✳', '✶', '✻', '✽', '✻', '✶', '✳', '✢'];

  const agent = $derived(app.agents.get(session.id));
  const running = $derived(isBusy(session.id));
  const cap = (s) => s.charAt(0).toUpperCase() + s.slice(1);
  // the same verb for the whole run
  const verb = $derived.by(() => {
    const seed = agent?.startedAt ?? session.id;
    let h = 0;
    for (const c of seed) h = (h * 31 + c.charCodeAt(0)) | 0;
    return VERBS[Math.abs(h) % VERBS.length];
  });
  const word = $derived.by(() => {
    const a = agent?.activity ?? '';
    if (a === 'thinking') return 'Thinking';
    if (a === 'writing') return 'Writing';
    if (a.startsWith('waiting for') && a !== 'waiting for model') return cap(a);
    if (agent?.status === 'queued') return 'Waiting for a free instance';
    return verb;
  });

  let now = $state(Date.now());
  let frame = $state(0);
  $effect(() => {
    if (!running) return;
    now = Date.now();
    const clock = setInterval(() => (now = Date.now()), 1000);
    const spin = setInterval(() => (frame = (frame + 1) % GLYPHS.length), 120);
    return () => {
      clearInterval(clock);
      clearInterval(spin);
    };
  });
  const elapsed = $derived(agent?.startedAt ? Math.max(0, now - Date.parse(agent.startedAt)) : 0);
</script>

{#if running}
  <div class="run-status" role="status" aria-live="polite">
    <span class="glyph" aria-hidden="true">{GLYPHS[frame]}</span>
    <span class="word np-ellipsis">{word}…</span>
    {#if elapsed >= 1000}<span class="meta">{duration(elapsed)}</span>{/if}
  </div>
{/if}

<style>
  /* over the chat's bottom room (MessageList .tail), so the chat never moves when it comes or goes */
  .run-status {
    position: absolute;
    left: 0;
    right: 0;
    bottom: 100%;
    z-index: 2;
    display: flex;
    align-items: center;
    gap: 7px;
    height: 24px;
    padding: 0 6px;
    min-width: 0;
    background: linear-gradient(to bottom, transparent, var(--bg) 45%);
    font-size: var(--fs-sm);
    color: var(--fg-muted);
    pointer-events: none;
  }
  .glyph {
    flex: none;
    width: 12px;
    text-align: center;
    color: var(--accent);
  }
  .word {
    min-width: 0;
    color: var(--accent);
    font-weight: 550;
  }
  .meta {
    flex: none;
    color: var(--fg-dim);
    font-size: var(--fs-xs);
    font-variant-numeric: tabular-nums;
  }
</style>
