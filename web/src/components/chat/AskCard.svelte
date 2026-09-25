<script>
  import Icon from '../../lib/kit/Icon.svelte';
  import { asks, pick, answerAsk } from '../../lib/state/asks.svelte.js';
  import { toast } from '../../lib/state/ui.svelte.js';
  import { parseArgs } from '../../lib/tools.js';
  import { firstLine } from '../../lib/format.js';
  import { prefs } from '../../lib/state/ui.svelte.js';
  import TurnLine from './TurnLine.svelte';

  /**
   * The agent's questions (ask_user) as an item of their own in the chat, below the message that gives their context;
   * nothing covers the chat. While they wait (asks.pending) the card is open: a click on an option answers a single
   * question; with several questions, or several picks allowed, Send sends the picks, and whatever the user types in
   * the message box goes with them (Composer). Once they stopped waiting the card is one line, question → answer, that
   * opens to every question and its answer; to read the context the user scrolls up.
   */
  let { item } = $props();

  // the raw arguments, when neither the pending question nor the result has the questions as the plugin read them
  function fromArgs(a) {
    const list = Array.isArray(a?.questions) ? a.questions : a?.question ? [a] : [];
    return list.map((q) =>
      typeof q === 'string'
        ? { question: q, options: [] }
        : {
            question: String(q?.question ?? q?.text ?? ''),
            options: (Array.isArray(q?.options) ? q.options : []).map((o) => ({ label: typeof o === 'string' ? o : String(o?.label ?? '') })),
            multiple: !!q?.multiple,
          },
    );
  }

  const id = $derived(item.call.id);
  const pending = $derived(asks.pending.get(id) ?? null);
  const closed = $derived(asks.closed.get(id) ?? null);
  const r = $derived(item.result);
  const d = $derived(r && !r.isError ? r.details : null);
  const questions = $derived(pending?.questions ?? d?.questions ?? fromArgs(parseArgs(item.call)));
  // preparing | waiting | answered | steered | withdrawn | cancelled | error | unanswered (no result: NetPI restarted).
  // An error result is the run's abort (or a steer skipping the call) unless the tool refused to ask.
  const failed = (res) => (/^Skipped:/.test(res.content ?? '') ? 'steered' : /^(Aborted:|Not executed)/.test(res.content ?? '') ? 'cancelled' : 'error');
  const status = $derived(
    item.preparing ? 'preparing' : pending ? 'waiting' : (d?.status ?? closed?.status ?? (r?.isError ? failed(r) : r ? 'answered' : 'unanswered')),
  );
  const answers = $derived(d?.answers ?? closed?.answers ?? []);
  const text = $derived(d?.text ?? closed?.text ?? null);
  const draft = $derived(asks.drafts.get(id) ?? []);
  const several = $derived(questions.length > 1 || questions.some((q) => q.multiple));
  const picked = $derived(draft.some((x) => x?.length));

  let open = $state(false);
  let sending = $state(false);

  async function send() {
    if (sending) return;
    sending = true;
    try {
      await answerAsk(id);
    } catch (e) {
      toast(e.message, 'error');
    } finally {
      sending = false;
    }
  }
  function choose(i, label, multiple) {
    pick(id, i, label, multiple);
    if (!several) send();
  }

  // the turn's numbers (TurnLine) go under the card when asking was the last thing its message did
  const endsTurn = $derived(item.msg?.stopReason === 'tool_use' && item.msg.parts.findLast((p) => p.type === 'tool_call')?.id === id);

  const answerOf = (i) => (answers[i]?.length ? answers[i].join(', ') : '');
  const summary = $derived.by(() => {
    switch (status) {
      case 'answered':
        return answerOf(0) || text || (questions.length > 1 ? 'nothing picked' : '');
      case 'steered':
        return 'not answered: you wrote a new message instead';
      case 'withdrawn':
        return 'withdrawn';
      case 'cancelled':
        return 'not answered: the run stopped';
      case 'error':
        return `not asked: ${firstLine(r?.content ?? '')}`;
      default:
        return 'not answered';
    }
  });
</script>

{#if status === 'preparing'}
  <div class="line">
    <span class="ic"><span class="np-spinner"></span></span>
    <span class="np-line np-baseline np-grow tx"><span class="lbl">Question</span><span class="dim">preparing…</span></span>
  </div>
{:else if status === 'waiting'}
  <div class="card" role="group" aria-label="The agent's question">
    <div class="who"><Icon name="message-circle" size={13} /><span>{questions.length > 1 ? `${questions.length} questions for you` : 'A question for you'}</span></div>
    {#each questions as q, i (i)}
      <div class="q">
        <div class="qtext">{q.question}</div>
        {#if q.options?.length}
          <div class="opts">
            {#each q.options as o (o.label)}
              {@const on = !!draft[i]?.includes(o.label)}
              <button class="opt" class:on role={q.multiple ? 'checkbox' : 'radio'} aria-checked={on} disabled={sending} onclick={() => choose(i, o.label, q.multiple)}>
                <span class="mark" class:multi={q.multiple}></span>
                <span class="otext">
                  <span class="olbl">{o.label}</span>
                  {#if o.description}<span class="odesc">{o.description}</span>{/if}
                </span>
              </button>
            {/each}
          </div>
        {/if}
      </div>
    {/each}
    <div class="foot">
      <span class="hint">{questions.some((q) => q.options?.length) ? 'Or answer in your own words in the message box' : 'Answer in the message box'}</span>
      {#if several}<button class="np-btn np-btn-sm np-btn-primary" disabled={!picked || sending} onclick={send}>Send</button>{/if}
    </div>
  </div>
{:else}
  <button class="line done" class:open aria-expanded={open} onclick={() => (open = !open)}>
    <span class="ic"><Icon name="message-circle" size={14} /></span>
    <span class="np-line np-baseline np-grow tx">
      <span class="lbl">Question</span>
      <span class="qt np-grow">{questions[0]?.question ?? ''}</span>
      <span class="arrow">→</span>
      <span class="a" class:none={status !== 'answered'}>{summary}</span>
      {#if questions.length > 1}<span class="more">+{questions.length - 1}</span>{/if}
    </span>
    <span class="chev" class:open><Icon name="chevron-right" size={12} /></span>
  </button>
  {#if open}
    <div class="qa">
      {#each questions as q, i (i)}
        <div class="qa-q">{q.question}</div>
        <div class="qa-a" class:none={status !== 'answered'}>
          {status === 'answered' ? answerOf(i) || (questions.length === 1 && text) || 'nothing picked' : summary}
        </div>
      {/each}
      {#if status === 'answered' && text && (questions.length > 1 || answerOf(0))}
        <div class="qa-q">In your own words</div>
        <div class="qa-a">{text}</div>
      {/if}
    </div>
  {/if}
  {#if prefs.turnDetails && endsTurn}<TurnLine msg={item.msg} />{/if}
{/if}

<style>
  /* one line, like a step (ToolRow): icon, label, then question → answer */
  .line {
    display: flex;
    align-items: center;
    gap: 8px;
    width: 100%;
    height: 28px;
    padding: 0 8px 0 4px;
    margin-left: -4px;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--fg-muted);
    font: inherit;
    font-size: var(--fs-sm);
    text-align: left;
    min-width: 0;
  }
  .done {
    cursor: pointer;
  }
  .done:hover,
  .done.open {
    background: var(--bg-2);
  }
  .ic {
    display: grid;
    place-items: center;
    width: 16px;
    flex: none;
    color: var(--accent);
  }
  .tx {
    height: 1lh;
  }
  .lbl {
    color: var(--fg);
    font-weight: 500;
  }
  /* on a narrow chat the question gives way first, then the answer */
  .tx .qt {
    flex: 1 1 0;
    min-width: 3em;
    color: var(--fg-muted);
  }
  .arrow,
  .more,
  .dim {
    color: var(--fg-dim);
  }
  .a {
    flex: 0 1 auto;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    color: var(--fg);
  }
  .a.none,
  .qa-a.none {
    color: var(--fg-dim);
  }
  .chev {
    display: inline-grid;
    flex: none;
    color: var(--fg-dim);
    transition: transform var(--t-fast);
  }
  .chev.open {
    transform: rotate(90deg);
  }
  .qa {
    display: grid;
    grid-template-columns: 1fr;
    gap: 2px;
    margin: 2px 0 6px 20px;
    padding: 8px 10px;
    border-radius: 6px;
    background: var(--bg-1);
    font-size: var(--fs-sm);
  }
  .qa-q {
    color: var(--fg-muted);
    white-space: pre-wrap;
    overflow-wrap: anywhere;
  }
  .qa-a {
    margin-bottom: 6px;
    color: var(--fg);
    white-space: pre-wrap;
    overflow-wrap: anywhere;
  }
  .qa-a:last-child {
    margin-bottom: 0;
  }

  /* waiting: the questions and their options, open */
  .card {
    display: flex;
    flex-direction: column;
    gap: 12px;
    padding: 10px 12px 12px;
    border: 1px solid var(--accent-line);
    border-radius: 8px;
    background: var(--bg-1);
  }
  .who {
    display: flex;
    align-items: center;
    gap: 6px;
    color: var(--accent);
    font-size: var(--fs-xs);
    font-weight: 600;
  }
  .qtext {
    color: var(--fg);
    white-space: pre-wrap;
    overflow-wrap: anywhere;
  }
  .opts {
    display: flex;
    flex-direction: column;
    gap: 4px;
    margin-top: 8px;
  }
  .opt {
    display: flex;
    align-items: flex-start;
    gap: 9px;
    width: 100%;
    padding: 6px 10px;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--bg);
    color: var(--fg);
    font: inherit;
    text-align: left;
    cursor: pointer;
  }
  .opt:hover:not(:disabled) {
    background: var(--bg-2);
  }
  .opt.on {
    border-color: var(--accent-line);
    background: var(--accent-soft);
  }
  .opt:disabled {
    cursor: default;
    opacity: 0.7;
  }
  /* a radio or a checkbox mark, on the first line of the label */
  .mark {
    flex: none;
    width: 12px;
    height: 12px;
    margin-top: calc((1lh - 12px) / 2);
    border: 1.5px solid var(--fg-dim);
    border-radius: 50%;
  }
  .mark.multi {
    border-radius: 3px;
  }
  .on .mark {
    border-color: var(--accent);
    background: radial-gradient(circle, var(--accent) 0 3px, transparent 3.5px);
  }
  .on .mark.multi {
    background: var(--accent);
  }
  .on .mark.multi::after {
    content: '';
    display: block;
    width: 3px;
    height: 6px;
    margin: 0.5px 0 0 3px;
    border: solid var(--bg);
    border-width: 0 1.5px 1.5px 0;
    transform: rotate(45deg);
  }
  .otext {
    display: flex;
    flex-direction: column;
    min-width: 0;
  }
  .olbl {
    overflow-wrap: anywhere;
  }
  .odesc {
    color: var(--fg-muted);
    font-size: var(--fs-sm);
    overflow-wrap: anywhere;
  }
  .foot {
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .hint {
    flex: 1;
    min-width: 0;
    color: var(--fg-dim);
    font-size: var(--fs-xs);
  }
</style>
