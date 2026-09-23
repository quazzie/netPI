<script>
  /** Unknown tools: pretty arguments + raw result */
  let { args, result } = $props();
  const argsText = $derived(args?.__raw ?? JSON.stringify(args ?? {}, null, 2));
  const details = $derived(result?.details != null ? JSON.stringify(result.details, null, 2) : null);
  let showDetails = $state(false);
</script>

<div class="generic">
  {#if argsText && argsText !== '{}'}
    <div class="lbl">arguments</div>
    <pre class="box np-mono np-scroll">{argsText}</pre>
  {/if}
  {#if result && !result.isError}
    <div class="lbl">result</div>
    <pre class="box np-mono np-scroll">{result.content || '(empty)'}</pre>
    {#if result.images?.length}
      <div class="imgs">{#each result.images as img, i (i)}<img src="data:{img.mediaType};base64,{img.data}" alt="" />{/each}</div>
    {/if}
    {#if details}
      <button class="np-btn np-btn-ghost np-btn-sm" onclick={() => (showDetails = !showDetails)}>{showDetails ? 'Hide' : 'Show'} details</button>
      {#if showDetails}<pre class="box np-mono np-scroll">{details}</pre>{/if}
    {/if}
  {:else if !result}
    <div class="np-dim np-small">waiting for result…</div>
  {/if}
</div>

<style>
  .generic {
    display: flex;
    flex-direction: column;
    gap: 4px;
    min-width: 0;
    align-items: flex-start;
  }
  .lbl {
    font-size: var(--fs-xs);
    color: var(--fg-dim);
    text-transform: uppercase;
    letter-spacing: 0.05em;
  }
  .box {
    align-self: stretch;
    margin: 0 0 4px;
    padding: 8px 10px;
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--code-bg);
    color: var(--fg-muted);
    font-size: 12px;
    line-height: 1.5;
    white-space: pre-wrap;
    overflow-wrap: anywhere;
    max-height: 320px;
    overflow: auto;
  }
  .imgs img {
    max-width: 100%;
    max-height: 280px;
    border-radius: var(--radius-sm);
  }
</style>
