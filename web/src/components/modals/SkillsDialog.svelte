<script>
  /**
   * `skills.disabled` as switches: the skills found for every project and the open chat (skills.list per project, which
   * includes the global ones), by scope. A skill switched off is hidden from agents and refused by the skill tool and
   * /skill:. Names in the setting that no folder has any more are listed so they can be dropped.
   */
  import { untrack } from 'svelte';
  import Modal from '../../lib/kit/Modal.svelte';
  import SearchInput from '../../lib/kit/SearchInput.svelte';
  import { rpc } from '../../lib/rpc.svelte.js';
  import { app } from '../../lib/state/app.svelte.js';
  import { getAt, setSetting } from '../../lib/settings.js';

  let { doc, onclose } = $props();

  let skills = $state(null);
  let error = $state('');
  let off = $state(new Set(untrack(() => getAt(doc, 'skills.disabled') ?? [])));
  let q = $state('');
  let saving = $state(false);

  $effect(() => {
    const asks = [...app.projects.map((p) => ({ projectId: p.id })), ...(app.activeId ? [{ sessionId: app.activeId }] : [])];
    Promise.allSettled(asks.map((a) => rpc('skills.list', a))).then((rs) => {
      const by = new Map();
      for (const r of rs)
        if (r.status === 'fulfilled')
          for (const sk of r.value?.skills ?? []) if (!by.has(sk.name)) by.set(sk.name, { name: sk.name, description: sk.description, scope: sk.scope, path: sk.path });
      if (!by.size && rs.length && rs.every((r) => r.status === 'rejected')) error = rs[0].reason?.message ?? 'skills.list failed';
      skills = [...by.values()].sort((a, b) => a.name.localeCompare(b.name));
    });
  });

  const missing = $derived(skills ? [...off].filter((n) => !skills.some((s) => s.name === n)) : []);
  const shown = $derived.by(() => {
    const query = q.trim().toLowerCase();
    return (skills ?? []).filter((s) => !query || `${s.name} ${s.description ?? ''}`.toLowerCase().includes(query));
  });

  function toggle(name, on) {
    const next = new Set(off);
    if (on) next.delete(name);
    else next.add(name);
    off = next;
  }
  async function save() {
    saving = true;
    const list = [...off].sort();
    if (await setSetting('skills.disabled', list.length ? list : null)) onclose();
    saving = false;
  }
</script>

<Modal title="Skills" width={600} {onclose} class="section-dialog">
  <div class="help np-dim">Skills switched off are hidden from agents; the skill tool and /skill: refuse them.</div>
  {#if error}
    <div class="np-empty">Could not list the skills: {error}</div>
  {:else if !skills}
    <div class="np-empty">Loading…</div>
  {:else}
    <div class="bar"><SearchInput bind:value={q} placeholder="Filter skills" /></div>
    <div class="list" data-testid="skills-dialog">
      {#each shown as sk (sk.name)}
        <label class="row" class:isoff={off.has(sk.name)}>
          <div class="t">
            <span class="name">{sk.name} <span class="np-dim np-small">{sk.scope}</span></span>
            {#if sk.description}<span class="np-dim desc np-ellipsis" title={sk.description}>{sk.description}</span>{/if}
          </div>
          <input type="checkbox" class="np-switch" checked={!off.has(sk.name)} onchange={(e) => toggle(sk.name, e.currentTarget.checked)} aria-label="{sk.name} on" />
        </label>
      {:else}
        <div class="np-empty">No skills found.</div>
      {/each}
      {#each missing as name (name)}
        <label class="row isoff">
          <div class="t"><span class="name">{name} <span class="np-dim np-small">not found</span></span></div>
          <button class="np-btn np-btn-ghost np-btn-sm" onclick={() => toggle(name, true)}>Drop</button>
        </label>
      {/each}
    </div>
  {/if}
  {#snippet footer()}
    <span class="np-dim np-small">{off.size} off</span>
    <span class="np-spacer"></span>
    <button class="np-btn np-btn-ghost np-btn-sm" onclick={onclose}>Cancel</button>
    <button class="np-btn np-btn-primary np-btn-sm" disabled={saving || !skills} onclick={save}>{saving ? 'Saving…' : 'Save'}</button>
  {/snippet}
</Modal>

<style>
  .help {
    margin-bottom: 10px;
    font-size: var(--fs-sm);
  }
  .bar {
    margin-bottom: 8px;
  }
  .row {
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 6px 0;
    border-bottom: 1px solid var(--border);
    cursor: pointer;
  }
  .t {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
  }
  .isoff .name {
    color: var(--fg-dim);
  }
  .desc {
    font-size: var(--fs-xs);
  }
</style>
