<script>
  import Icon from '../../lib/kit/Icon.svelte';
  import Popover from '../Popover.svelte';
  import { updateSession } from '../../lib/state/app.svelte.js';

  let { session, open = $bindable(false) } = $props();
  let btn = $state();
  let busy = $state(false);

  const levels = [
    { value: 'background', label: 'Background', detail: 'Yields its slot to any waiting higher priority run, even one you started.' },
    { value: 'low', label: 'Low', detail: 'Runs after normal and higher priority work.' },
    { value: 'normal', label: 'Normal', detail: 'The standard queue position.' },
    { value: 'high', label: 'High', detail: 'Runs before normal and low priority work.' },
    { value: 'critical', label: 'Critical', detail: 'May take a slot from eligible lower priority work.' },
  ];
  const current = $derived(levels.find((p) => p.value === session.meta?.priority)?.value ?? 'normal');
  const label = $derived(levels.find((p) => p.value === current)?.label ?? 'Normal');

  async function choose(value) {
    open = false;
    if (busy || value === current) return;
    busy = true;
    try {
      await updateSession(session.id, { meta: { priority: value, prioritySource: 'manual' } });
    } finally {
      busy = false;
    }
  }
</script>

{#if session.kind !== 'subagent'}
  <button
    class="pick"
    class:high={current === 'high'}
    class:critical={current === 'critical'}
    bind:this={btn}
    disabled={busy}
    aria-label="Priority"
    aria-haspopup="dialog"
    aria-expanded={open}
    title={`Priority: ${label}. Runs queue by priority${current === 'critical' ? '; critical runs can take a slot from eligible lower priority work' : ''}.`}
    onclick={() => (open = !open)}
  >
    <Icon name="arrow-up" size={13} />
    <span class="priority-label">{label}</span>
  </button>
  {#if open}
    <Popover anchor={btn} placement="top-start" width={252} onclose={() => (open = false)}>
      <div class="list" role="listbox" aria-label="Session priority">
        <div class="title">Session priority</div>
        {#each levels as level (level.value)}
          <button
            class="opt"
            class:current={current === level.value}
            data-priority={level.value}
            role="option"
            aria-selected={current === level.value}
            onclick={() => choose(level.value)}
          >
            <span class="copy">
              <span>{level.label}</span>
              <span class="detail">{level.detail}{level.value === 'critical' ? ' Your own chats remain protected.' : ''}</span>
            </span>
            {#if current === level.value}<Icon name="check" size={13} />{/if}
          </button>
        {/each}
      </div>
    </Popover>
  {/if}
{/if}

<style>
  .pick {
    display: flex;
    align-items: center;
    gap: 5px;
    height: 26px;
    padding: 0 8px;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--fg-muted);
    font-size: var(--fs-sm);
  }
  .pick:hover:not(:disabled) {
    background: var(--bg-3);
    color: var(--fg);
  }
  .pick.high {
    color: var(--warn);
  }
  .pick.critical {
    color: var(--err);
  }
  .list {
    padding: 4px;
  }
  .title {
    padding: 6px 8px 4px;
    color: var(--fg-dim);
    font-size: var(--fs-xs);
    font-weight: 600;
    letter-spacing: 0.05em;
    text-transform: uppercase;
  }
  .opt {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    width: 100%;
    min-height: 40px;
    padding: 5px 8px;
    border: 0;
    border-radius: 5px;
    background: transparent;
    color: var(--fg);
    text-align: left;
  }
  .opt:hover {
    background: var(--bg-3);
  }
  .opt.current {
    color: var(--accent);
  }
  .copy {
    display: flex;
    min-width: 0;
    flex-direction: column;
    gap: 2px;
  }
  .detail {
    color: var(--fg-dim);
    font-size: var(--fs-xs);
    line-height: 1.35;
  }
</style>
