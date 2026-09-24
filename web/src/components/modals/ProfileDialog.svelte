<script>
  /**
   * One profile (settings profiles.<id> = { name, prompt, toolsOff }) in its own dialog: the name, the instructions that
   * open the system prompt (shown as the current opening until they are changed), the tools as checkboxes by category,
   * and whether new chats start with it. Every field saves on its own.
   */
  import Modal from './Modal.svelte';
  import Icon from '../../lib/kit/Icon.svelte';
  import { setSetting } from '../../lib/settings.js';
  import { confirmDialog } from '../../lib/state/ui.svelte.js';

  let { id, profile, categories = [], opening = '', isDefault = false, onclose } = $props();

  const off = $derived(new Set(profile?.toolsOff ?? []));
  const own = $derived(typeof profile?.prompt === 'string' && profile.prompt.trim().length > 0);

  async function setPrompt(text) {
    const t = text.trim();
    await setSetting(`profiles.${id}.prompt`, !t || t === opening.trim() ? null : t);
  }
  async function toggle(name, on) {
    const next = new Set(off);
    if (on) next.delete(name);
    else next.add(name);
    await setSetting(`profiles.${id}.toolsOff`, next.size ? [...next].sort() : null);
  }
  async function setCategory(list, on) {
    const next = new Set(off);
    for (const t of list) {
      if (on) next.delete(t.name);
      else next.add(t.name);
    }
    await setSetting(`profiles.${id}.toolsOff`, next.size ? [...next].sort() : null);
  }
  async function remove() {
    const ok = await confirmDialog({
      title: `Remove the profile "${profile?.name || id}"?`,
      message: 'Chats that have it keep their instructions and tools; new chats no longer get it.',
      confirmLabel: 'Remove',
      danger: true,
    });
    if (!ok) return;
    onclose?.();
    if (isDefault) await setSetting('profiles.defaultProfile', null);
    await setSetting(`profiles.${id}`, null);
  }
</script>

{#snippet foot()}
  <button class="np-btn np-btn-danger" onclick={remove}><Icon name="trash" size={13} /> Remove profile</button>
  <span class="spacer"></span>
  <button class="np-btn np-btn-primary" onclick={() => onclose?.()}>Done</button>
{/snippet}

<Modal title="Profile {profile?.name || id}" width={640} {onclose} footer={foot} class="profile-dialog">
  {#if profile}
    <label class="field">
      <span class="lbl">Name</span>
      <input class="np-input" value={profile.name ?? ''} placeholder={id} aria-label="Name" onchange={(e) => setSetting(`profiles.${id}.name`, e.currentTarget.value.trim() || null)} />
    </label>

    <label class="field">
      <span class="lbl">Instructions <span class="np-dim">{own ? '(this profile\'s own)' : '(the current opening: edit it to make it this profile\'s own)'}</span></span>
      <textarea class="np-input prompt" rows="7" value={own ? profile.prompt : opening} aria-label="Instructions" onchange={(e) => setPrompt(e.currentTarget.value)}></textarea>
      <span class="hint np-dim">They open the system prompt of the profile's chats: who the agent is and how it works.</span>
    </label>

    <label class="np-check default"><input type="checkbox" checked={isDefault} onchange={(e) => setSetting('profiles.defaultProfile', e.currentTarget.checked ? id : null)} /> New chats start with this profile (unless their project has its own)</label>

    <div class="lbl tools-title">Tools</div>
    {#each categories as [cat, list] (cat)}
      {@const allOn = list.every((t) => !off.has(t.name))}
      <div class="cat">
        <span class="np-dim">{cat}</span>
        <button class="all" onclick={() => setCategory(list, !allOn)} title={allOn ? `Switch off every ${cat} tool` : `Switch on every ${cat} tool`}>{allOn ? 'all off' : 'all on'}</button>
      </div>
      <div class="chips">
        {#each list as t (t.name)}
          <label class="chip" class:is-off={off.has(t.name)} title={t.description}>
            <input type="checkbox" checked={!off.has(t.name)} onchange={(e) => toggle(t.name, e.currentTarget.checked)} />
            <span class="np-mono">{t.name}</span>
          </label>
        {/each}
      </div>
    {/each}
  {:else}
    <div class="np-dim">This profile was removed.</div>
  {/if}
</Modal>

<style>
  .field {
    display: flex;
    flex-direction: column;
    gap: 5px;
    margin-bottom: 12px;
  }
  .lbl {
    color: var(--fg-muted);
    font-size: var(--fs-sm);
  }
  .prompt {
    width: 100%;
    height: auto;
    padding: 6px 8px;
    line-height: 1.5;
    resize: vertical;
  }
  .hint {
    font-size: var(--fs-xs);
  }
  .default {
    margin-bottom: 12px;
    font-size: var(--fs-sm);
  }
  .tools-title {
    margin-bottom: 2px;
  }
  .cat {
    display: flex;
    align-items: center;
    gap: 8px;
    margin: 6px 0 3px;
    font-size: var(--fs-xs);
  }
  .all {
    padding: 0 4px;
    border: 0;
    background: transparent;
    color: var(--accent);
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
  .spacer {
    flex: 1;
  }
</style>
