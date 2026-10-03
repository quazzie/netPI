<script>
  /**
   * The cards a closed chat leaves behind, and the ones a commit may have finished (plugins/NetPI.Ideas, phases 2 and 3
   * of docs/archive/2026-09-27-ideas-follow-the-session.md). One card per offer, above the composer of whatever chat is
   * open: the chat a plan came from is closed, and the events are unscoped. An offer, never an action — nothing reaches
   * the backlog, and nothing is marked done, without a click.
   */
  import Icon from '../../lib/kit/Icon.svelte';
  import Button from '../../lib/kit/Button.svelte';
  import { toast } from '../../lib/state/ui.svelte.js';
  import { onOpen } from '../../lib/rpc.svelte.js';
  import { suggestions } from './ideaSuggestions.svelte.js';

  let editing = $state(null); // the card being edited, with the draft fields
  let draftTitle = $state('');
  let draftSummary = $state('');

  const cards = $derived([...suggestions.items.values()].sort((a, b) => (a.at ?? '').localeCompare(b.at ?? '')));

  $effect(() => {
    suggestions.load();
  });

  // A card answered in another window (or finished after a restart) arrives by event; a window that was hidden or
  // reconnecting catches up from the file when it comes back, so no unanswerable card is left on screen.
  $effect(() => onOpen(({ reconnect }) => { if (reconnect) suggestions.refresh(); }));
  $effect(() => {
    if (typeof document === 'undefined') return;
    const onVisible = () => { if (document.visibilityState === 'visible') suggestions.refresh(); };
    document.addEventListener('visibilitychange', onVisible);
    return () => document.removeEventListener('visibilitychange', onVisible);
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
      gone(s, e);
    }
  }

  /**
   * A card that another window answered is not an error here: it is simply no longer there. Re-read the file and
   * drop it, so nothing stays on screen that cannot be answered.
   */
  function gone(s, e) {
    if (/not_found|no longer|gone|conflict/i.test(e?.message ?? '')) {
      suggestions.items.delete(s.id);
      editing = null;
      suggestions.refresh();
      toast('That card was answered somewhere else.', 'info');
      return;
    }
    toast(e.message, 'error');
  }

  /** The commit check's card: the idea stays where it is, only its status changes. */
  async function markDone(s) {
    try {
      const idea = await suggestions.resolve(s.id, 'done');
      if (idea) toast(`Marked done: ${idea.title}`, 'info');
    } catch (e) {
      gone(s, e);
    }
  }

  async function discard(s) {
    try {
      await suggestions.resolve(s.id, 'discard');
      toast(s.kind === 'done' ? 'Left open. A later commit can ask again.' : 'Discarded. The chat is not asked about again.', 'info');
    } catch (e) {
      gone(s, e);
    }
  }
</script>

{#each cards as s (s.id)}
  {#if s.kind === 'done'}
    <div class="card done" role="group" aria-label="An idea a commit may have finished">
      <div class="head">
        <span class="ic"><Icon name="check" size={13} /></span>
        <span class="from np-ellipsis" title="A commit may have finished this idea">may be done</span>
      </div>
      <div class="np-ellipsis title" title={s.title}>{s.title}</div>
      {#if s.commits?.length}
        <div class="commits">
          {#each s.commits.slice(0, 3) as c (c)}<div class="np-ellipsis commit" title={c}>{c}</div>{/each}
          {#if s.commits.length > 3}<div class="commit dim">+{s.commits.length - 3} more</div>{/if}
        </div>
      {/if}
      <div class="btns">
        <Button size="sm" variant="primary" disabled={suggestions.busy === s.id} onclick={() => markDone(s)} title="Mark this idea done">Mark done</Button>
        <Button size="sm" disabled={suggestions.busy === s.id} onclick={() => discard(s)} title="Leave it open">Dismiss</Button>
      </div>
    </div>
  {:else}
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
  {/if}
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
  /* the commit check's card: an idea that is already in the backlog, only its status is in question */
  .card.done {
    border-left-color: var(--ok);
  }
  .card.done .ic {
    color: var(--ok);
  }
  .commits {
    margin-top: 3px;
  }
  .commit {
    color: var(--fg-dim);
    font-family: var(--font-mono);
    font-size: 10.5px;
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
