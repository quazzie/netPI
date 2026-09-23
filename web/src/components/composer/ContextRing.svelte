<script>
  import { tokens } from '../../lib/format.js';
  /** Context usage ring: used / window tokens. */
  let { used = 0, window: win = 0 } = $props();
  const R = 7;
  const C = 2 * Math.PI * R;
  const frac = $derived(win ? Math.min(1, Math.max(0, used / win)) : 0);
  const tone = $derived(frac >= 0.9 ? 'err' : frac >= 0.7 ? 'warn' : 'ok');
  const tip = $derived(
    win ? `Context ${tokens(used)} / ${tokens(win)} tokens (${Math.round(frac * 100)}%)` : `Context ${tokens(used)} tokens`,
  );
</script>

<span class="ring" data-tone={tone} title={tip} role="img" aria-label={tip}>
  <svg width="18" height="18" viewBox="0 0 18 18">
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
</span>

<style>
  .ring {
    display: inline-flex;
    align-items: center;
    gap: 5px;
    color: var(--fg-dim);
    font-size: 11px;
    cursor: default;
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
</style>
