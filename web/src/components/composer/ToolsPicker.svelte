<script>
  /**
   * The tools of this chat (agent.tools / agent.setTools; the session's meta.toolsOff). Switched-off tools are not sent to
   * the agent nor its subagents. Before the first message a change is free; later it applies from the next model call,
   * which re-reads the conversation once (tool definitions lead the request).
   */
  import Icon from '../../lib/kit/Icon.svelte';
  import Popover from '../Popover.svelte';
  import { rpc } from '../../lib/rpc.svelte.js';
  import { toast } from '../../lib/state/ui.svelte.js';
  import { tokens } from '../../lib/format.js';

  let { session, open = $bindable(false) } = $props();
  let btn = $state();
  let info = $state(null);

  const offCount = $derived(Array.isArray(session.meta?.toolsOff) ? session.meta.toolsOff.length : 0);
  const categories = $derived.by(() => {
    const byCat = new Map();
    for (const t of info?.tools ?? []) {
      const c = t.category || 'general';
      if (!byCat.has(c)) byCat.set(c, []);
      byCat.get(c).push(t);
    }
    return [...byCat].sort(([a], [b]) => a.localeCompare(b));
  });
  const offListed = $derived((info?.tools ?? []).filter((t) => !t.on).map((t) => t.name));

  async function load() {
    try {
      info = await rpc('agent.tools', { sessionId: session.id });
    } catch (e) {
      info = null;
      toast(e.message, 'error');
    }
  }
  // loaded on every open (the chat may have started since); nothing stale in between
  $effect(() => {
    if (open) load();
    else info = null;
  });

  async function set(change) {
    try {
      info = await rpc('agent.setTools', { sessionId: session.id, ...change });
    } catch (e) {
      toast(e.message, 'error');
    }
  }
</script>

<button
  class="pick"
  class:off={offCount > 0}
  bind:this={btn}
  onclick={() => (open = !open)}
  title={offCount ? `Tools for this chat: ${offCount} switched off` : 'Tools for this chat: all on'}
  aria-label="Tools for this chat"
>
  <Icon name="wrench" size={13} />
  {#if offCount}<span>{offCount} off</span>{/if}
</button>
{#if open}
  <Popover anchor={btn} placement="top-start" width={320} onclose={() => (open = false)}>
    <div class="tools-pop">
      <div class="title">Tools for this chat</div>
      <div class="help np-dim">
        {#if info?.started}
          A change applies from the next model call; the model then re-reads this chat once{info.contextTokens
            ? ` (about ${tokens(info.contextTokens)} tokens)`
            : ''}.
        {:else if info}
          Switched-off tools are not sent to the agent nor its subagents.
        {/if}
      </div>
      <div class="list">
        {#each categories as [cat, list] (cat)}
          <div class="cat np-dim">{cat}</div>
          <div class="chips">
            {#each list as t (t.name)}
              <label class="chip" class:is-off={!t.on} title={t.description}>
                <input type="checkbox" checked={t.on} onchange={(e) => set(e.currentTarget.checked ? { on: [t.name] } : { off: [t.name] })} />
                <span class="np-mono">{t.name}</span>
              </label>
            {/each}
          </div>
        {/each}
        {#if info && !info.tools?.length}<div class="np-dim empty">No tools.</div>{/if}
      </div>
      {#if offListed.length}
        <div class="foot">
          <span class="np-dim">{offListed.length} off</span>
          <button class="np-btn np-btn-sm" onclick={() => set({ on: offListed })}>All on</button>
        </div>
      {/if}
    </div>
  </Popover>
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
    color: var(--fg-dim);
    font-size: var(--fs-sm);
  }
  .pick:hover {
    background: var(--bg-3);
    color: var(--fg);
  }
  .pick.off {
    color: var(--warn);
  }
  .tools-pop {
    display: flex;
    flex-direction: column;
    max-height: min(460px, 70vh);
    padding: 4px 4px 6px;
  }
  .title {
    padding: 6px 8px 2px;
    font-size: var(--fs-xs);
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.05em;
    color: var(--fg-dim);
  }
  .help {
    padding: 0 8px 6px;
    font-size: var(--fs-xs);
    line-height: 1.45;
  }
  .list {
    flex: 1;
    min-height: 0;
    overflow-y: auto;
    padding: 0 8px;
  }
  .cat {
    margin: 6px 0 3px;
    font-size: var(--fs-xs);
  }
  .chips {
    display: flex;
    flex-wrap: wrap;
    gap: 4px;
  }
  .chip {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    height: 22px;
    padding: 0 7px 0 5px;
    border: 1px solid var(--border);
    border-radius: 11px;
    font-size: var(--fs-xs);
    cursor: pointer;
  }
  .chip.is-off {
    opacity: 0.55;
    text-decoration: line-through;
  }
  .chip input {
    margin: 0;
    accent-color: var(--accent);
  }
  .empty {
    padding: 6px 0;
    font-size: var(--fs-sm);
  }
  .foot {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    padding: 8px 8px 0;
    border-top: 1px solid var(--border);
    margin-top: 8px;
    font-size: var(--fs-xs);
  }
</style>
