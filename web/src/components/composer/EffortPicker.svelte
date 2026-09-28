<script>
  import Icon from '../../lib/kit/Icon.svelte';
  import Popover from '../Popover.svelte';
  import { updateSession } from '../../lib/state/app.svelte.js';

  /** Reasoning effort (model.reasoning.efforts + "default"). Sets sessions.update { reasoning } ('' = default). */
  let { session, model, open = $bindable(false) } = $props();
  let btn = $state();

  const efforts = $derived(model?.reasoning?.supported ? (model.reasoning.efforts ?? []) : []);
  const def = $derived(model?.reasoning?.default ?? null);
  const current = $derived(session.reasoning || null);

  async function choose(v) {
    open = false;
    if ((v || null) !== current) await updateSession(session.id, { reasoning: v ?? '' });
  }
</script>

{#if efforts.length}
  <button
    class="pick"
    class:auto={!current}
    bind:this={btn}
    aria-label="Reasoning effort"
    onclick={() => (open = !open)}
    title={current ? `Reasoning effort: ${current}` : `Reasoning effort: model default${def ? ` (${def})` : ''}`}
  >
    <Icon name="brain" size={13} />
    <span>{current ?? def ?? 'default'}</span>
  </button>
  {#if open}
    <Popover anchor={btn} placement="top-start" width={200} onclose={() => (open = false)}>
      <div class="list">
        <div class="title">Reasoning effort</div>
        <button class="opt" class:current={!current} onclick={() => choose(null)}>
          <span>default{def ? ` (${def})` : ''}</span>{#if !current}<Icon name="check" size={13} />{/if}
        </button>
        {#each efforts as e (e)}
          <button class="opt" class:current={current === e} onclick={() => choose(e)}>
            <span>{e}</span>{#if current === e}<Icon name="check" size={13} />{/if}
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
  .pick:hover {
    background: var(--bg-3);
    color: var(--fg);
  }
  .pick.auto span {
    color: var(--fg-dim);
  }
  .list {
    padding: 4px;
  }
  .title {
    padding: 6px 8px 4px;
    font-size: var(--fs-xs);
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.05em;
    color: var(--fg-dim);
  }
  .opt {
    display: flex;
    align-items: center;
    justify-content: space-between;
    width: 100%;
    height: 28px;
    padding: 0 8px;
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
</style>
