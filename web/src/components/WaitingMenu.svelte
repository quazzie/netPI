<script>
  /**
   * The chats that wait for the user, beside the connection dot: an orange dot with the count and a chevron, hidden when
   * nothing waits. The menu lists them oldest first (question, tool OK, plan, plan-mode offer, schedule, budget stop);
   * a row opens its chat, where the card is.
   */
  import Icon from '../lib/kit/Icon.svelte';
  import TimeAgo from '../lib/kit/TimeAgo.svelte';
  import Popover from './Popover.svelte';
  import { openSession } from '../lib/state/app.svelte.js';
  import { waitingItems, KINDS } from '../lib/state/waiting.svelte.js';

  let btn = $state();
  let open = $state(false);
  const items = $derived(waitingItems());

  $effect(() => {
    if (!items.length) open = false;
  });

  function go(id) {
    open = false;
    openSession(id);
  }
</script>

{#if items.length}
  <button
    class="waiting"
    bind:this={btn}
    aria-haspopup="dialog"
    aria-expanded={open}
    title="{items.length} chat{items.length === 1 ? '' : 's'} waiting for you"
    onclick={() => (open = !open)}
  >
    <span class="np-dot" data-status="asking"></span>
    <span class="count">{items.length}</span>
    <Icon name="chevron-down" size={12} />
  </button>
  {#if open}
    <Popover anchor={btn} placement="bottom-end" width={340} onclose={() => (open = false)}>
      <div class="head">Waiting for you</div>
      <div class="list np-scroll">
        {#each items as it (it.key)}
          <button class="row" onclick={() => go(it.sessionId)}>
            <span class="kind" title={KINDS[it.kind].label}><Icon name={KINDS[it.kind].icon} size={14} /></span>
            <span class="body">
              <span class="t np-ellipsis">{it.title}{#if it.project}<span class="proj"> · {it.project}</span>{/if}</span>
              <span class="s np-ellipsis">{it.summary}</span>
            </span>
            {#if it.since}<TimeAgo time={it.since} class="ago" />{/if}
          </button>
        {/each}
      </div>
    </Popover>
  {/if}
{/if}

<style>
  .waiting {
    display: flex;
    align-items: center;
    gap: 5px;
    height: 26px;
    padding: 0 6px 0 8px;
    border: 0;
    border-radius: 13px;
    background: var(--warn-soft);
    color: var(--warn);
    font-size: var(--fs-sm);
  }
  .waiting:hover,
  .waiting[aria-expanded='true'] {
    filter: brightness(1.15);
  }
  .waiting .np-dot {
    animation: wait-pulse 2.4s ease-in-out infinite;
  }
  @keyframes wait-pulse {
    50% {
      opacity: 0.45;
    }
  }
  @media (prefers-reduced-motion: reduce) {
    .waiting .np-dot {
      animation: none;
    }
  }
  .count {
    font-variant-numeric: tabular-nums;
    font-weight: 600;
  }
  .head {
    padding: 8px 12px 6px;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
    text-transform: uppercase;
    letter-spacing: 0.04em;
  }
  .list {
    overflow-y: auto;
    padding: 0 4px 4px;
  }
  .row {
    display: flex;
    align-items: center;
    gap: 9px;
    width: 100%;
    padding: 6px 8px;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--fg);
    text-align: left;
  }
  .row:hover,
  .row:focus-visible {
    background: var(--bg-2);
  }
  .kind {
    color: var(--warn);
    flex: none;
    display: grid;
  }
  .body {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 1px;
  }
  .t {
    font-size: var(--fs);
  }
  .proj {
    color: var(--fg-dim);
  }
  .s {
    font-size: var(--fs-sm);
    color: var(--fg-muted);
  }
  .row :global(.ago) {
    flex: none;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
  }
</style>
