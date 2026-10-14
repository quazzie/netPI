<script>
  /**
   * The chat's profile (profiles.apply): its instructions and tools. Free before the first message; in a started chat
   * the next model call re-reads the chat (the system prompt and the tools change). Hidden while there are no profiles.
   */
  import Icon from '../../lib/kit/Icon.svelte';
  import Popover from '../Popover.svelte';
  import { rpc } from '../../lib/rpc.svelte.js';
  import { bus } from '../../lib/bus.js';
  import { toast } from '../../lib/state/ui.svelte.js';
  import { tokens } from '../../lib/format.js';
  import { hasRpc, upsertSessionLocal } from '../../lib/state/app.svelte.js';

  let { session, open = $bindable(false) } = $props();
  let btn = $state();
  let profiles = $state([]);

  // Every settings.changed and plugins.changed loads the list again. A load that fails keeps the list it had: emptying it
  // hid the button and unmounted a picker the person had open. The picker goes when the Profiles plugin does (its
  // methods leave rpc.list), and of loads that overlap only the newest answer counts.
  let loads = 0;
  async function load() {
    const n = ++loads;
    try {
      const list = (await rpc('profiles.list'))?.profiles ?? [];
      if (n === loads) profiles = list;
    } catch {
      /* the list it had stays */
    }
  }
  $effect(() => {
    load();
    const offs = [bus.on('settings.changed', load), bus.on('plugins.changed', load)];
    return () => offs.forEach((off) => off());
  });

  const current = $derived(session.meta?.profile ?? null);
  const currentName = $derived(profiles.find((p) => p.id === current)?.name ?? (current ? current : 'No profile'));
  const started = $derived((session.messageCount ?? 0) > 0);

  async function choose(id) {
    open = false;
    if ((id ?? null) === current) return;
    try {
      // the chat as it is now: one with no messages yet is not announced (no session.updated), so the button follows this
      upsertSessionLocal(await rpc('profiles.apply', { sessionId: session.id, profile: id }));
    } catch (e) {
      toast(e.message, 'error');
    }
  }
</script>

{#if profiles.length && hasRpc('profiles.list') && session.kind !== 'subagent'}
  <button class="pick" disabled={!hasRpc('profiles.apply')} class:none={!current} bind:this={btn} onclick={() => (open = !open)} title="Profile: {currentName}" aria-label="Profile">
    <Icon name="user" size={13} />
    <span class="np-ellipsis">{currentName}</span>
  </button>
  {#if open}
    <Popover anchor={btn} placement="top-start" width={270} onclose={() => (open = false)}>
      <div class="list profile-pop" data-testid="profile-picker">
        <div class="title">Profile</div>
        <div class="help np-dim">
          {#if started}
            Switching changes the system prompt and the tools: the next model call re-reads this chat{session.contextTokens
              ? ` (about ${tokens(session.contextTokens)} tokens)`
              : ''}.
          {:else}
            Its instructions and tools; free to change until the first message.
          {/if}
        </div>
        {#each profiles as p (p.id)}
          <button class="opt" data-testid="profile-option" class:current={current === p.id} title={p.prompt ?? ''} onclick={() => choose(p.id)}>
            <span class="np-ellipsis">{p.name}</span>{#if current === p.id}<Icon name="check" size={13} />{/if}
          </button>
        {/each}
        <button class="opt" class:current={!current} onclick={() => choose(null)}>
          <span>No profile</span>{#if !current}<Icon name="check" size={13} />{/if}
        </button>
      </div>
    </Popover>
  {/if}
{/if}

<style>
  .pick {
    display: flex;
    align-items: center;
    gap: 5px;
    max-width: 140px;
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
  .pick.none span {
    color: var(--fg-dim);
  }
  .list {
    padding: 4px;
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
  .opt {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
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
