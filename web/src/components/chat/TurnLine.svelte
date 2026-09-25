<script>
  import { turnStats, turnTitle, cachedText, speedText } from '../../lib/turn.js';
  import { duration, tokens } from '../../lib/format.js';

  /**
   * One model turn's numbers, under its last step: time to the first token, how much of the prompt the backend reused
   * from its cache, the output speed, and the tokens in and out. On a narrow chat the items at the end are dropped
   * whole (np-fit); the tooltip has the exact numbers.
   */
  let { msg } = $props();
  const s = $derived(turnStats(msg));
</script>

{#if s}
  <div class="turn np-fit np-mono" title={turnTitle(s, msg.durationMs)}>
    {#if s.ttft != null}<span>first token {duration(s.ttft)}</span>{/if}
    {#if s.prompt}<span>{cachedText(s)} cached</span>{/if}
    {#if s.tps}<span>{speedText(s.tps)} tok/s</span>{/if}
    {#if s.prompt}<span>{tokens(s.prompt)} in</span>{/if}
    {#if s.out != null}<span>{tokens(s.out)} out</span>{/if}
  </div>
{/if}

<style>
  /* the text starts where the step labels do (after the 16px icon and its 8px gap) */
  .turn {
    height: 18px;
    padding-left: 24px;
    color: var(--fg-dim);
    font-size: 10.5px;
    opacity: 0.85;
  }
  .turn > span {
    line-height: 18px;
  }
</style>
