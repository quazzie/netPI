<script>
  /**
   * A system prompt the session was sent (context.prompts), as a divider like the notices: the first one above the first
   * message, a later one (after a profile switch) where it was rendered again. It opens to the prompt exactly as sent,
   * then the tool definitions that went with it (each opens to its JSON). Every request starts with these; later tool
   * changes arrive as "tools" notices.
   */
  import Icon from '../../lib/kit/Icon.svelte';
  import SentBlock from './SentBlock.svelte';
  import { stamp, firstLine, truncate } from '../../lib/format.js';

  let { prompt, chat } = $props();

  const key = $derived(`p${prompt.version}`);
  const open = $derived(chat.expanded.get(key) ?? false);
  const tools = $derived(Array.isArray(prompt.tools) ? prompt.tools : []);
  const toolChars = $derived(tools.reduce((n, t) => n + JSON.stringify(t).length, 0));
  const tokens = $derived(Math.round((prompt.systemPrompt.length + toolChars) / 4));
  let openTool = $state(null);
</script>

<div class="notice" data-tone="info" class:open>
  <div class="rule"></div>
  <button class="pill" onclick={() => chat.expanded.set(key, !open)} title={prompt.createdAt ? stamp(prompt.createdAt) : ''} aria-expanded={open}>
    <Icon name="sliders" size={13} />
    <span class="label">{prompt.version > 1 ? 'System prompt, rendered again' : 'System prompt'}</span>
    <span class="text">≈{tokens.toLocaleString()} tokens · {tools.length} tools</span>
    <span class="chev" class:open><Icon name="chevron-right" size={11} /></span>
  </button>
  <div class="rule"></div>
</div>
{#if open}
  <SentBlock text={prompt.systemPrompt} note="The system prompt: the start of every request in this chat">
    <div class="tools">
      <div class="tools-title">Tools sent with it ({tools.length}), sorted by name</div>
      {#each tools as t (t.name)}
        <button class="tool" class:on={openTool === t.name} onclick={() => (openTool = openTool === t.name ? null : t.name)}>
          <span class="tname np-mono">{t.name}</span>
          <span class="tdesc np-ellipsis">{truncate(firstLine(t.description ?? ''), 140)}</span>
        </button>
        {#if openTool === t.name}
          <pre class="tjson np-mono">{JSON.stringify(t, null, 2)}</pre>
        {/if}
      {/each}
    </div>
  </SentBlock>
{/if}

<style>
  .notice {
    display: flex;
    align-items: center;
    gap: 8px;
    min-height: 26px;
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
    cursor: pointer;
  }
  .pill:hover {
    border-color: var(--border-strong);
    color: var(--fg);
  }
  .pill :global(svg) {
    color: var(--info);
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
  .tools {
    border-top: 1px solid var(--border);
    padding: 6px 0;
  }
  .tools-title {
    padding: 2px 12px 4px;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
  }
  .tool {
    display: flex;
    align-items: baseline;
    gap: 10px;
    width: 100%;
    min-width: 0;
    padding: 3px 12px;
    border: 0;
    background: transparent;
    color: var(--fg-muted);
    font-size: var(--fs-xs);
    text-align: left;
  }
  .tool:hover,
  .tool.on {
    background: var(--bg-2);
  }
  .tname {
    flex: none;
    color: var(--fg);
    font-size: 11.5px;
  }
  .tdesc {
    flex: 1;
    min-width: 0;
  }
  .tjson {
    margin: 2px 12px 6px;
    padding: 8px 10px;
    max-height: 320px;
    overflow: auto;
    border-radius: var(--radius-sm);
    background: var(--code-bg);
    font-size: 11px;
    line-height: 1.45;
    white-space: pre-wrap;
    overflow-wrap: anywhere;
  }
</style>
