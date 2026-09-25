<script>
  import Icon from '../../lib/kit/Icon.svelte';
  import SentBlock from './SentBlock.svelte';
  import { openSession } from '../../lib/state/app.svelte.js';
  import { confirmDialog, toast } from '../../lib/state/ui.svelte.js';
  import { rpc } from '../../lib/rpc.svelte.js';
  import { stamp, firstLine, truncate } from '../../lib/format.js';

  /**
   * Harness notices (role notice) and compaction summaries (role summary) as slim dividers. Every one opens to exactly
   * what the model got: a notice wrapped in <system-notice kind="…"> and a summary in <conversation-summary>, as the
   * host's ModelMessages.Normalize sends them.
   */
  let { msg, chat } = $props();

  const KINDS = {
    project: { icon: 'folder', tone: 'info' },
    instructions: { icon: 'file-text', tone: 'info', label: 'Instructions', expand: true },
    skills: { icon: 'sparkle', tone: 'info', label: 'Skills', expand: true },
    skill: { icon: 'sparkle', tone: 'accent', expand: true },
    tools: { icon: 'wrench', tone: 'info', label: 'Tools changed', expand: true },
    todo: { icon: 'list', tone: 'info', label: 'Todo list', expand: true },
    goal: { icon: 'target', tone: 'accent', label: 'Goal' },
    budget: { icon: 'dollar', tone: 'warn' },
    profile: { icon: 'user', tone: 'accent' },
    nudge: { icon: 'zap', tone: 'warn', label: 'Nudge' },
    'agent-message': { icon: 'message-circle', tone: 'accent', expand: true },
    'agent-result': { icon: 'bot', tone: 'ok', expand: true },
    compaction: { icon: 'layers', tone: 'info', label: 'Context compacted', expand: true },
    summary: { icon: 'layers', tone: 'info', label: 'Summary of earlier conversation', expand: true },
    retry: { icon: 'refresh', tone: 'warn' },
    error: { icon: 'alert', tone: 'err' },
    info: { icon: 'info', tone: 'muted' },
  };

  const kind = $derived(msg.role === 'summary' ? 'summary' : (msg.meta?.kind ?? 'info'));
  const k = $derived(KINDS[kind] ?? KINDS.info);
  const text = $derived(msg.parts.filter((p) => p.type === 'text').map((p) => p.text).join('\n'));
  const who = $derived(msg.meta?.agentName ?? msg.meta?.from ?? msg.meta?.name ?? null);
  const label = $derived(
    k.label ??
      (kind === 'agent-message'
        ? `Message from ${who ?? 'agent'}`
        : kind === 'agent-result'
          ? `${who ?? 'Subagent'} finished`
          : kind === 'skill'
            ? msg.meta?.missing
              ? `No skill “${msg.meta?.skill ?? ''}”`
              : `Skill: ${msg.meta?.skill ?? ''}`
            : null),
  );
  const linkSession = $derived(msg.meta?.sessionId && msg.meta.sessionId !== msg.sessionId ? msg.meta.sessionId : null);
  // what the model got for this message (ModelMessages.WrapNotice, or the summary wrapper)
  const sent = $derived(
    msg.role === 'summary'
      ? `<conversation-summary>\nThe earlier part of this conversation was compacted. Summary:\n\n${text}\n</conversation-summary>`
      : `<system-notice${msg.meta?.kind ? ` kind="${msg.meta.kind}"` : ''}>\n${text.trim()}\n</system-notice>`,
  );
  // written to be read: opens rendered, with a toggle to see it as sent
  const readable = $derived(kind === 'agent-result' || kind === 'agent-message' || kind === 'summary');
  const long = true; // every notice opens to what was sent
  const key = $derived(`n${msg.id}`);
  const open = $derived(chat.expanded.get(key) ?? false);
  const oneLine = $derived(truncate(firstLine(text).replace(/[*_`#>]/g, ''), 160));

  // the budget stopped this chat and budget.onLimit is "ask": the latest such notice offers to let the chat go over
  let allowed = $state(false);
  const canAllow = $derived(
    kind === 'budget' && msg.meta?.canOverride === true && !allowed && chat.messages.findLast((m) => m.meta?.kind === 'budget')?.id === msg.id,
  );
  async function allow() {
    const ok = await confirmDialog({
      title: 'Let this chat go over the budget?',
      message: 'It may keep using paid models until the budget period ends, and it continues now. Other chats stay stopped.',
      confirmLabel: 'Go over',
    });
    if (!ok) return;
    try {
      await rpc('budget.allow', { sessionId: msg.sessionId });
      allowed = true;
    } catch (e) {
      toast(e.message, 'error');
    }
  }
</script>

<div class="notice" data-tone={k.tone} class:open>
  <div class="rule"></div>
  <button
    class="pill"
    class:clickable={long}
    onclick={() => long && chat.expanded.set(key, !open)}
    title={stamp(msg.createdAt)}
    aria-expanded={long ? open : undefined}
  >
    <Icon name={k.icon} size={13} />
    {#if label}<span class="label">{label}</span>{/if}
    {#if !open && (!label || !k.expand) && oneLine}<span class="text">{oneLine}</span>{/if}
    {#if long}<span class="chev" class:open><Icon name="chevron-right" size={11} /></span>{/if}
  </button>
  {#if canAllow}
    <button class="link" onclick={allow}><Icon name="dollar" size={11} /> Let this chat go over</button>
  {/if}
  {#if linkSession}
    <button class="link" onclick={() => openSession(linkSession)}><Icon name="external" size={11} /> open</button>
  {/if}
  <div class="rule"></div>
</div>
{#if open}
  <SentBlock text={sent} markdown={readable ? text : null} />
{/if}

<style>
  .notice {
    display: flex;
    align-items: center;
    gap: 8px;
    min-height: 26px;
    --tone: var(--fg-dim);
  }
  .notice[data-tone='info'] {
    --tone: var(--info);
  }
  .notice[data-tone='warn'] {
    --tone: var(--warn);
  }
  .notice[data-tone='err'] {
    --tone: var(--err);
  }
  .notice[data-tone='ok'] {
    --tone: var(--ok);
  }
  .notice[data-tone='accent'] {
    --tone: var(--accent);
  }
  .rule {
    flex: 1;
    min-width: 16px;
    height: 1px;
    background: var(--border);
  }
  .pill {
    display: flex;
    align-items: center;
    gap: 7px;
    max-width: 78%;
    min-width: 0;
    height: 24px;
    padding: 0 10px;
    border: 1px solid var(--border);
    border-radius: 12px;
    background: var(--bg);
    color: var(--fg-muted);
    font-size: var(--fs-sm);
    cursor: default;
  }
  .pill.clickable {
    cursor: pointer;
  }
  .pill.clickable:hover {
    border-color: var(--border-strong);
    color: var(--fg);
  }
  .pill :global(svg) {
    color: var(--tone);
  }
  .notice[data-tone='err'] .pill {
    color: var(--err);
    border-color: color-mix(in srgb, var(--err) 35%, var(--border));
    background: color-mix(in srgb, var(--err) 6%, var(--bg));
  }
  .label {
    flex: none;
    font-weight: 550;
    color: var(--fg);
  }
  .text {
    min-width: 0;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
  }
  .chev {
    display: inline-grid;
    color: var(--fg-dim);
    transition: transform var(--t-fast);
  }
  .chev.open {
    transform: rotate(90deg);
  }
  .link {
    display: inline-flex;
    align-items: center;
    gap: 3px;
    border: 0;
    padding: 2px 4px;
    border-radius: 4px;
    background: transparent;
    color: var(--accent);
    font-size: var(--fs-xs);
  }
  .link:hover {
    background: var(--accent-soft);
  }
</style>
