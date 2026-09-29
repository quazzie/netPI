<script>
  import Icon from '../../../lib/kit/Icon.svelte';
  import { arg } from '../../../lib/tools.js';
  import { copyText } from '../../../lib/markdown.js';

  /** bash / pwsh: command, live output (tool.output) or final result, exit code and process info */
  let { name, args, result, live } = $props();

  const cmd = $derived(String(arg(args, 'command', 'cmd', 'script') ?? ''));
  const d = $derived(result?.details ?? {});
  const running = $derived(!result && live?.status === 'running');
  // live output is the complete stream (capped at 200KB); the result is the model-facing (tail) version
  const output = $derived(live?.output || result?.content || '');
  const lineCount = $derived.by(() => {
    let n = 1;
    for (let i = 0; i < output.length && n <= 40; i++) if (output.charCodeAt(i) === 10) n++;
    return n;
  });

  let full = $state(false);
  let pre = $state();
  let lastScroll = 0;
  $effect(() => {
    output;
    if (running && pre && !full) {
      // setting scrollTop forces a layout of this whole <pre> (up to 200 KB of text); at stream speed that is a
      // forced synchronous layout ~60x/s. The position is indistinguishable at 4x/s, and the text keeps being
      // appended every frame either way.
      const t = Date.now();
      if (t - lastScroll >= 250) {
        lastScroll = t;
        pre.scrollTop = pre.scrollHeight;
      }
    }
  });

  let copied = $state(false);
  async function copyCmd() {
    if (await copyText(cmd)) {
      copied = true;
      setTimeout(() => (copied = false), 1200);
    }
  }
</script>

<div class="shell">
  <div class="cmd np-mono">
    <span class="prompt">{name === 'pwsh' ? 'PS>' : name === 'ssh_run' ? `${arg(args, 'host') ?? 'ssh'}$` : '$'}</span>
    <code>{cmd}</code>
    <button class="copy" title="Copy command" onclick={copyCmd}><Icon name={copied ? 'check' : 'copy'} size={12} /></button>
  </div>
  {#if output}
    <pre class="out np-mono np-scroll" class:full bind:this={pre}>{output}</pre>
  {:else}
    <div class="out empty np-mono">{running ? 'waiting for output…' : '(no output)'}</div>
  {/if}
  <div class="foot">
    {#if running}
      <span class="np-badge" data-tone="accent"><span class="np-spinner" style="width:9px;height:9px"></span> running</span>
    {:else if d.timedOut}
      <span class="np-badge" data-tone="err">timed out</span>
    {:else if d.aborted}
      <span class="np-badge" data-tone="warn">aborted</span>
    {:else if d.exitCode != null}
      <span class="np-badge" data-tone={d.exitCode === 0 ? 'ok' : 'warn'}>exit {d.exitCode}</span>
    {:else if result?.isError}
      <span class="np-badge" data-tone="err">failed</span>
    {/if}
    {#if d.background}<span class="np-badge" data-tone="info">background · {d.processId}</span>{/if}
    {#if d.pid}<span class="np-dim">pid {d.pid}</span>{/if}
    {#if d.cwd}<span class="np-dim np-ellipsis cwd" title={d.cwd}>{d.cwd}</span>{/if}
    {#if live?.truncated || d.truncated}<span class="np-dim">output truncated{d.fullOutputPath ? ` · full log ${d.fullOutputPath}` : ''}</span>{/if}
    <span class="np-spacer"></span>
    {#if lineCount > 20}
      <button class="np-btn np-btn-ghost np-btn-sm" onclick={() => (full = !full)}>{full ? 'Collapse' : 'Show all'}</button>
    {/if}
  </div>
</div>

<style>
  .shell {
    display: flex;
    flex-direction: column;
    gap: 6px;
    min-width: 0;
  }
  .cmd {
    display: flex;
    align-items: flex-start;
    gap: 8px;
    font-size: 12.5px;
    color: var(--fg);
  }
  .prompt {
    color: var(--accent);
    user-select: none;
    flex: none;
  }
  .cmd code {
    flex: 1;
    white-space: pre-wrap;
    overflow-wrap: anywhere;
  }
  .copy {
    display: grid;
    place-items: center;
    width: 20px;
    height: 20px;
    padding: 0;
    border: 0;
    border-radius: 4px;
    background: transparent;
    color: var(--fg-dim);
    flex: none;
  }
  .copy:hover {
    background: var(--bg-3);
    color: var(--fg);
  }
  .out {
    margin: 0;
    padding: 8px 10px;
    border-radius: var(--radius-sm);
    background: var(--code-bg);
    border: 1px solid var(--border);
    color: var(--fg-muted);
    font-size: 12px;
    line-height: 1.5;
    white-space: pre-wrap;
    overflow-wrap: anywhere;
    max-height: 320px;
    overflow: auto;
  }
  .out.full {
    max-height: none;
  }
  .out.empty {
    color: var(--fg-dim);
    font-style: italic;
  }
  .foot {
    display: flex;
    align-items: center;
    gap: 8px;
    min-height: 22px;
    font-size: var(--fs-xs);
    min-width: 0;
  }
  .cwd {
    max-width: 40%;
    font-family: var(--font-mono);
  }
</style>
