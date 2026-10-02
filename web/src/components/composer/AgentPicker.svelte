<script>
  /**
   * The chat's agent: chats run on agents (a model with instances, active while its model is loaded). The menu lists the
   * agents with their state (ready, busy, not loaded, switched off) and filters as you type; "New agent…" picks a model,
   * sets up an agent on it and runs this chat on it; "Manage agents" opens Settings → Agents & budget. agents.use sets
   * the chat's agent and its model.
   */
  import { untrack } from 'svelte';
  import Icon from '../../lib/kit/Icon.svelte';
  import Popover from '../Popover.svelte';
  import ModelMenu from '../ModelMenu.svelte';
  import { app, sessionModelRef } from '../../lib/state/app.svelte.js';
  import { modals, toast } from '../../lib/state/ui.svelte.js';
  import { ANY, agentList, agentOf, agentState, createAgent, useAgent } from '../../lib/agents.js';
  import { usd } from '../../lib/format.js';

  let { session, open = $bindable(false) } = $props();
  let btn = $state();
  let picking = $state(false);
  let q = $state('');
  let index = $state(0);

  const agents = $derived(agentList());
  const mine = $derived(agentOf(session));
  const current = $derived(mine.agent);
  const isAny = $derived(mine.any === true);
  const currentState = $derived(agentState(current));
  const modelRef = $derived(sessionModelRef(session));
  const label = $derived(
    isAny ? 'Any available' : current?.key ?? (agents.length ? 'Choose an agent' : app.modelsByRef.get(modelRef)?.displayName || modelRef?.split('/').pop() || 'No agent'),
  );
  const title = $derived(
    isAny
      ? 'Any available agent: each run goes to the first agent with a free instance, and this chat’s model follows it'
      : current
      ? `Agent ${current.key} · ${current.model}${currentState.text ? ` · ${currentState.text}` : ''}`
      : agents.length
        ? `No agent runs ${modelRef ?? 'this chat’s model'}: choose one`
        : 'No agents set up yet',
  );
  const filtered = $derived.by(() => {
    const query = q.trim().toLowerCase();
    return agents.filter((p) => !query || p.key.toLowerCase().includes(query) || (p.model ?? '').toLowerCase().includes(query));
  });

  $effect(() => {
    if (!open) return;
    untrack(() => {
      q = '';
      index = Math.max(0, agents.findIndex((p) => p.key === current?.key));
    });
  });

  const nameOf = (p) => app.modelsByRef.get(p.model)?.displayName || p.model;
  const priceOf = (p) => (p.free ? 'free' : p.priceInput != null ? `${usd(p.priceInput)}/${usd(p.priceOutput)}` : '');

  async function choose(p) {
    open = false;
    if (p && (p.key !== session.meta?.agent || mine.implicit)) await useAgent(session.id, p.key);
  }
  async function chooseAny() {
    open = false;
    if (!isAny) await useAgent(session.id, ANY);
  }
  function newAgent() {
    open = false;
    picking = true;
  }
  async function created(ref) {
    picking = false;
    if (!ref) return;
    const id = await createAgent(ref);
    if (!id) return;
    await useAgent(session.id, id);
    toast(`Agent “${id}” set up on ${ref}. Settings → Agents to rename it or give it more instances.`);
  }
  function manage() {
    open = false;
    modals.settings = 'agents';
  }

  function onKey(e) {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      index = Math.min(filtered.length - 1, index + 1);
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      index = Math.max(0, index - 1);
    } else if (e.key === 'Enter') {
      e.preventDefault();
      if (filtered[index]) choose(filtered[index]);
    }
  }
  function focus(node) {
    node.focus();
  }
</script>

{#if session.kind !== 'subagent' || current || isAny}
  <button class="pick" class:none={!current && !isAny} bind:this={btn} onclick={() => (open = !open)} {title} aria-label="Agent" aria-haspopup="listbox">
    {#if current && !isAny}<span class="np-dot" data-status={currentState.dot}></span>{:else}<Icon name="bot" size={13} />{/if}
    <span class="np-ellipsis">{label}</span>
    <Icon name="chevron-down" size={11} />
  </button>
{/if}

{#if open}
  <Popover anchor={btn} placement="top-start" width={320} onclose={() => (open = false)}>
    <div class="agent-pop">
      {#if agents.length > 3}
        <div class="head">
          <Icon name="search" size={13} />
          <input class="filter" placeholder="Filter agents" bind:value={q} oninput={() => (index = 0)} onkeydown={onKey} use:focus spellcheck="false" aria-label="Filter agents" />
        </div>
      {/if}
      <div class="list np-scroll" role="listbox" tabindex="-1" onkeydown={onKey}>
        {#if agents.length}
          <button
            class="opt any"
            class:current={isAny}
            role="option"
            aria-selected={isAny}
            data-agent={ANY}
            title="Each run goes to whichever agent has a free instance first, whatever its model; the chat’s model follows the agent it gets"
            onclick={chooseAny}
          >
            <Icon name="bot" size={13} />
            <span class="name">
              <span class="np-ellipsis">Any available</span>
              <span class="sub np-ellipsis">the first free agent, whatever its model</span>
            </span>
            {#if isAny}<Icon name="check" size={13} />{/if}
          </button>
        {/if}
        {#each filtered as p, i (p.key)}
          {@const st = agentState(p)}
          <button
            class="opt"
            class:active={i === index}
            class:current={p.key === current?.key}
            class:inactive={!p.available}
            role="option"
            aria-selected={p.key === current?.key}
            data-agent={p.key}
            title={p.use || ''}
            onclick={() => choose(p)}
            onmouseenter={() => (index = i)}
          >
            <span class="np-dot" data-status={st.dot}></span>
            <span class="name">
              <span class="np-ellipsis">{p.key}</span>
              <span class="sub np-ellipsis">{nameOf(p)}{!p.available ? ` · ${st.text}` : p.queued ? ` · ${p.queued} waiting` : ''}</span>
            </span>
            <span class="meta np-mono">
              {#if p.available}<span title="Busy / instances">{p.busy}/{p.capacity}</span>{/if}
              {#if priceOf(p)}<span class:free={p.free}>{priceOf(p)}</span>{/if}
            </span>
            {#if p.key === current?.key}<Icon name="check" size={13} />{/if}
          </button>
        {:else}
          <div class="empty np-dim">
            {#if agents.length}No agents match “{q}”.{:else}No agents yet. An agent is a model with a number of instances;
              chats and subagents run on agents.{/if}
          </div>
        {/each}
      </div>
      <div class="foot">
        <button class="np-btn np-btn-sm np-btn-ghost" onclick={newAgent}><Icon name="plus" size={12} /> New agent…</button>
        <span class="np-spacer"></span>
        <button class="np-btn np-btn-sm np-btn-ghost" onclick={manage}><Icon name="settings" size={12} /> Manage agents</button>
      </div>
    </div>
  </Popover>
{/if}

{#if picking}
  <ModelMenu anchor={btn} placement="top-start" current={null} onchoose={created} onclose={() => (picking = false)} />
{/if}

<style>
  .pick {
    display: flex;
    align-items: center;
    gap: 6px;
    max-width: 200px;
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
    color: var(--warn);
  }
  .head {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 6px 10px;
    border-bottom: 1px solid var(--border);
    color: var(--fg-dim);
  }
  .filter {
    flex: 1;
    border: 0;
    outline: none;
    background: transparent;
    color: var(--fg);
    height: 24px;
  }
  .list {
    padding: 4px;
    max-height: 340px;
    outline: none;
  }
  .opt.any {
    margin-bottom: 4px;
    border-bottom: 1px dashed var(--border);
    border-radius: 6px 6px 0 0;
  }
  .opt {
    display: flex;
    align-items: center;
    gap: 9px;
    width: 100%;
    min-height: 36px;
    padding: 4px 8px;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--fg);
    text-align: left;
  }
  .opt.active {
    background: var(--bg-3);
  }
  .opt.current {
    color: var(--accent);
  }
  .opt.inactive .name > span:first-child {
    color: var(--fg-muted);
  }
  .name {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    line-height: 1.25;
  }
  .sub {
    font-size: 10.5px;
    color: var(--fg-dim);
  }
  .meta {
    display: flex;
    align-items: center;
    gap: 8px;
    flex: none;
    color: var(--fg-dim);
    font-size: 11px;
  }
  .free {
    color: var(--ok);
  }
  .empty {
    padding: 10px;
    font-size: var(--fs-sm);
    line-height: 1.45;
  }
  .foot {
    display: flex;
    align-items: center;
    gap: 4px;
    padding: 4px 6px;
    border-top: 1px solid var(--border);
  }
</style>
