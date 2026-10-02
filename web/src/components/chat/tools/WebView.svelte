<script>
  /** web_fetch (title, address, the text), web_search (the result list), screenshot (the image, console errors). */
  let { name, result } = $props();
  const d = $derived(result?.details ?? {});
  // web_fetch: the page text follows the header lines, after the first blank line
  const text = $derived.by(() => {
    const c = result?.content ?? '';
    const i = c.indexOf('\n\n');
    return i >= 0 ? c.slice(i + 2) : c;
  });
  let all = $state(false);

  // A search result or a fetched page is remote text: only an http(s) address goes in an href, so what a result says
  // cannot turn a link into a file:// or javascript: one.
  const webHref = (url) => (/^https?:\/\//i.test(String(url ?? '').trim()) ? url : undefined);
</script>

{#if !result}
  <div class="np-dim np-small">waiting for result…</div>
{:else if name === 'web_search'}
  <div class="hits">
    {#each d.results ?? [] as r, i (i)}
      <div class="hit">
        <a class="t" href={webHref(r.url)} target="_blank" rel="noopener noreferrer">{r.title || r.url}</a>
        <div class="u np-mono">{r.url}{#if r.age}<span class="age">&nbsp;· {r.age}</span>{/if}</div>
        {#if r.snippet}<div class="s">{r.snippet}</div>{/if}
      </div>
    {:else}
      <div class="np-dim np-small">No results.</div>
    {/each}
    {#if d.provider}
      <div class="np-dim np-small">via {d.provider}{#if d.fallback?.length} (after {d.fallback.join('; ')}){/if}</div>
    {/if}
  </div>
{:else if name === 'screenshot'}
  {#each result.images ?? [] as img, i (i)}
    <img class="shot" src="data:{img.mediaType};base64,{img.data}" alt="Screenshot" />
  {/each}
  {#if d.consoleErrors?.length}
    <div class="lbl">console errors</div>
    <pre class="box np-mono np-scroll">{d.consoleErrors.join('\n')}</pre>
  {/if}
{:else}
  <div class="page">
    {#if d.title}<div class="title">{d.title}</div>{/if}
    {#if d.finalUrl}<a class="u np-mono" href={webHref(d.finalUrl)} target="_blank" rel="noopener noreferrer">{d.finalUrl}</a>{/if}
    {#if result.images?.length}
      {#each result.images as img, i (i)}<img class="shot" src="data:{img.mediaType};base64,{img.data}" alt="" />{/each}
    {:else}
      <pre class="box np-mono np-scroll" class:all>{text}</pre>
      {#if text.length > 2000}
        <button class="np-btn np-btn-ghost np-btn-sm" onclick={() => (all = !all)}>{all ? 'Show less' : 'Show all'}</button>
      {/if}
    {/if}
  </div>
{/if}

<style>
  .hits,
  .page {
    display: flex;
    flex-direction: column;
    gap: 8px;
    min-width: 0;
  }
  .page {
    gap: 4px;
    align-items: flex-start;
  }
  .hit {
    min-width: 0;
    line-height: 1.4;
  }
  .t {
    color: var(--accent);
    text-decoration: none;
    font-weight: 550;
    overflow-wrap: anywhere;
  }
  .t:hover {
    text-decoration: underline;
  }
  .u {
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
    max-width: 100%;
    font-size: 11.5px;
    color: var(--fg-dim);
    text-decoration: none;
  }
  a.u:hover {
    color: var(--fg-muted);
  }
  .age {
    font-family: var(--font-ui);
  }
  .s {
    margin-top: 2px;
    color: var(--fg-muted);
    font-size: var(--fs-sm);
  }
  .title {
    font-weight: 600;
    color: var(--fg);
  }
  .lbl {
    margin-top: 6px;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
    text-transform: uppercase;
    letter-spacing: 0.05em;
  }
  .box {
    align-self: stretch;
    margin: 4px 0 0;
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
  .box.all {
    max-height: none;
  }
  .shot {
    display: block;
    max-width: 100%;
    max-height: 480px;
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
  }
</style>
