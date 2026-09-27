<script>
  /**
   * Recall on the first message (docs/plans/2026-09-27-ideas-follow-the-session.md): while the first message of a
   * chat is typed, `ideas.recall` looks for the open idea it continues (after a pause in typing, again when the text
   * changed a lot), and once more on the sent text. A match shows one line above the composer: the idea's title,
   * Add (`ideas.attach`: the idea joins the chat as a notice) and dismiss. Never holds up the send.
   */
  import Icon from '../../lib/kit/Icon.svelte';
  import Button from '../../lib/kit/Button.svelte';
  import IconButton from '../../lib/kit/IconButton.svelte';
  import { rpc } from '../../lib/rpc.svelte.js';
  import { toast } from '../../lib/state/ui.svelte.js';
  import { recall } from './ideaRecall.svelte.js';

  let { chat, session } = $props();

  const PAUSE_MS = 1000;
  const MIN_CHARS = 12;
  const REASK_CHARS = 20; // a new check while typing once the text changed by this much

  const rs = $derived(recall.get(session.id));
  const userMessages = $derived(chat.messages.filter((m) => m.role === 'user'));
  const sentText = $derived(
    chat.pendingUser?.text ?? userMessages[0]?.parts?.filter((p) => p.type === 'text').map((p) => p.text).join('\n') ?? null,
  );
  const empty = $derived(userMessages.length === 0 && !chat.pendingUser);

  let timer = 0;
  let busy = $state(false);

  async function ask(s, text, final) {
    if (!recall.enabled || s.done) return;
    const seq = ++s.seq;
    s.lastText = text;
    if (final) s.finalAsked = true;
    try {
      const res = await rpc('ideas.recall', { sessionId: session.id, text });
      if (seq !== s.seq || s.done) return; // a newer check answered first, or the user already chose
      if (res?.reason === 'off') recall.enabled = false;
      if (res?.match) s.match = res.match;
      else if (!final) s.match = null; // while typing the chip follows the text; after the send a match stays
    } catch (e) {
      // No Ideas plugin (or an older one): stop asking in this window.
      if (/unknown method/i.test(e.message ?? '')) recall.enabled = false;
    }
  }

  // While typing the first message: ask after a pause, and again when the text changed a lot.
  $effect(() => {
    const s = rs;
    const text = chat.draft.trim();
    if (!empty || !recall.enabled || s.done) return;
    s.sawEmpty = true;
    if (text.length < MIN_CHARS) return;
    if (s.lastText != null && Math.abs(text.length - s.lastText.length) < REASK_CHARS && text.startsWith(s.lastText.slice(0, 40))) return;
    const t = setTimeout(() => ask(s, text, false), PAUSE_MS);
    timer = t;
    return () => clearTimeout(t);
  });

  // The first message was sent (seen empty in this window): once more on the sent text.
  $effect(() => {
    const s = rs;
    if (empty || !s.sawEmpty || s.finalAsked || userMessages.length > 1 || !sentText?.trim()) return;
    clearTimeout(timer);
    ask(s, sentText.trim(), true);
  });

  async function add() {
    const s = rs;
    if (!s.match || busy) return;
    busy = true;
    try {
      await rpc('ideas.attach', { sessionId: session.id, id: s.match.id });
      toast(`Added the idea to this chat: ${s.match.title}`, 'info');
      s.done = 'added';
    } catch (e) {
      toast(`Could not add the idea: ${e.message}`, 'error');
    } finally {
      busy = false;
    }
  }
</script>

{#if rs.match && !rs.done && userMessages.length <= 1}
  <div class="chip" role="status" aria-label="Matching idea">
    <span class="ic"><Icon name="idea" size={13} /></span>
    <span class="np-ellipsis title" title="{rs.match.title} ({rs.match.id})">{rs.match.title}</span>
    <Button size="sm" variant="primary" disabled={busy} onclick={add} title="Add this idea's notes to the chat">Add</Button>
    <IconButton icon="x" size="sm" title="Not this idea" onclick={() => (rs.done = 'dismissed')} />
  </div>
{/if}

<style>
  .chip {
    display: flex;
    align-items: center;
    gap: 8px;
    margin: 0 0 8px;
    padding: 0 4px 0 10px;
    height: 32px;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--bg-1);
    font-size: var(--fs-sm);
  }
  .ic {
    flex: none;
    display: grid;
    color: var(--accent);
  }
  .title {
    flex: 1;
    min-width: 0;
    color: var(--fg);
  }
</style>
