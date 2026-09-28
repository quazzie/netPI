<script>
  /**
   * The cards a closed chat leaves behind (plugins/NetPI.Ideas, phase 2 of
   * docs/plans/2026-09-27-ideas-follow-the-session.md). One card per plan the save check found unsaved, above the
   * composer of whatever chat is open: the chat it came from is closed, and the event is unscoped. An offer, never an
   * action — nothing reaches the backlog without a click, and Discard is final for that card.
   */
  import Icon from '../../lib/kit/Icon.svelte';
  import Button from '../../lib/kit/Button.svelte';
  import { toast } from '../../lib/state/ui.svelte.js';
  import { suggestions } from './ideaSuggestions.svelte.js';

  let editing = $state(null); // the card being edited, with the draft fields
  let draftTitle = $state('');
  let draftSummary = $state('');

  const cards = $derived([...suggestions.items.values()].sort((a, b) => (a.at ?? '').localeCompare(b.at ?? '')));

  $effect(() => {
    suggestions.load();
  });

  function startEdit(s) {
    editing = s.id;
    draftTitle = s.title ?? '';
    draftSummary = s.summary ?? '';
  }

  async function save(s, edit) {
    try {
      const idea = await suggestions.resolve(s.id, 'save', edit);
      if (idea) toast(`Saved to the ideas backlog: ${idea.title}`, 'info');
      editing = null;
    } catch (e) {
      toast(e.message, 'error');
    }
  }

  async function discard(s) {
    try {
      await suggestions.resolve(s.id, 'discard');
      toast('Discarded. The chat is not asked about again.', 'info');
    } catch (e) {
      toast(e.message, 'error');
    }
  }
</script>

{#each cards as s (s.id)}
  <div class="card" role="group" aria-label="Unsaved plan from a closed chat">
    <div class="head">
      <span class="ic"><Icon name="idea" size={13} /></span>
      <span class="from np-ellipsis" title="From the chat “{s.sessionTitle}”">from “{s.sessionTitle || 'a closed chat'}”</span>
    </div>

    {#if editing === s.id}
      <input class="np-input title" bind:value={draftTitle} placeholder="Title" aria-label="Idea title" />
      <textarea class="np-input summary" bind:value={draftSummary} rows="2" placeholder="One line about it" aria-label="Idea summary"></textarea>
    {:else}
      <div class="np-ellipsis title" title={s.title}>{s.title}</div>
      {#if s.summary}<div class="summary">{s.summary}</div>{/if}
    {/if}

    <div class="btns">
      {#if editing === s.id}
        <Button size="sm" variant="primary" disabled={!draftTitle.trim() || suggestions.busy === s.id} onclick={() => save(s, { title: draftTitle.trim(), summary: draftSummary.trim() })}>Save</Button>
        <Button size="sm" disabled={suggestions.busy === s.id} onclick={() => (editing = null)}>Cancel</Button>
      {:else}
        <Button size="sm" variant="primary" disabled={suggestions.busy === s.id} onclick={() => save(s)} title="Add this to the ideas backlog">Save</Button>
        <Button size="sm" disabled={suggestions.busy === s.id} onclick={() => startEdit(s)}>Edit</Button>
        <Button size="sm" disabled={suggestions.busy === s.id} onclick={() => discard(s)} title="Drop it; this chat is not asked about again">Discard</Button>
      {/if}
    </div>
  </div>
{/each}

<style>
  .card {
    margin: 0 0 8px;
    padding: 8px 10px;
    border: 1px solid var(--border);
    border-left: 2px solid var(--accent);
    border-radius: var(--radius);
    background: var(--bg-1);
    font-size: var(--fs-sm);
  }
  .head {
    display: flex;
    align-items: center;
    gap: 6px;
    min-width: 0;
    color: var(--fg-dim);
    font-size: var(--fs-xs);
  }
  .ic {
    flex: none;
    display: grid;
    color: var(--accent);
  }
  .from {
    flex: 1;
    min-width: 0;
  }
  .title {
    margin: 2px 0 0;
    color: var(--fg);
    font-weight: 600;
  }
  input.title {
    display: block;
    width: 100%;
    margin: 4px 0 2px;
    font-weight: 600;
  }
  .summary {
    margin-top: 2px;
    color: var(--fg-muted);
    line-height: 1.45;
  }
  textarea.summary {
    display: block;
    width: 100%;
    margin: 2px 0 0;
    resize: vertical;
  }
  .btns {
    display: flex;
    gap: 6px;
    margin-top: 8px;
  }
</style>
