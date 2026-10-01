<script>
  import { onMount, untrack } from 'svelte';
  import { Section, Empty, IconButton, Badge, TimeAgo, bytes, tokens, copyText, desktop, basename } from '@netpi/kit';

  /** What the model sees for the active session: context.preview + agentsmd.list + skills.list, and how its tools got this way. */
  let { ctx, visible = true } = $props();

  let sid = $state(untrack(() => ctx.app.activeSessionId));
  let preview = $state.raw(null);
  let files = $state.raw(null);
  let skills = $state.raw(null); // null without the skills plugin
  let toolSets = $state.raw(null); // null without the context plugin
  let error = $state('');
  let loading = $state(false);
  let full = $state(false);
  let disposed = false;
  const optional = (method, params) => ctx.hasRpc?.(method) === false ? Promise.resolve(null) : ctx.rpc(method, params);

  async function load() {
    sid = ctx.app.activeSessionId;
    if (!sid) {
      preview = files = skills = toolSets = null;
      return;
    }
    loading = true;
    const id = sid;
    const [p, f, k, ts] = await Promise.allSettled([
      optional('context.preview', { sessionId: id }),
      optional('agentsmd.list', { sessionId: id }),
      optional('skills.list', { sessionId: id }),
      optional('diag.toolsets', { sessionId: id }),
    ]);
    if (disposed || id !== ctx.app.activeSessionId) return;
    preview = p.status === 'fulfilled' ? p.value : null;
    files = f.status === 'fulfilled' ? f.value : null;
    skills = k.status === 'fulfilled' ? k.value : null;
    toolSets = ts.status === 'fulfilled' ? ts.value : null;
    error = p.status === 'rejected' ? p.reason?.message : '';
    loading = false;
  }

  onMount(() => {
    load();
    const off = ctx.app.onChange(() => {
      if (ctx.app.activeSessionId !== sid) load();
    });
    const changes = ctx.on('rpc.changed', () => load());
    return () => { disposed = true; off(); changes(); };
  });

  const session = $derived(sid ? ctx.app.activeSession : null);
  const promptTokens = $derived(preview ? Math.round((preview.systemPrompt?.length ?? 0) / 3.6) : 0);
  const since = $derived(toolSets?.baseline?.tools?.length || 0);
</script>

{#if !sid}
  <Empty icon="sessions">Open a session to see its context</Empty>
{:else}
  <div class="head">
    <div class="title np-ellipsis">{session?.title || 'Session'}</div>
    <IconButton icon="refresh" title="Refresh" size="sm" disabled={loading} onclick={load} />
  </div>
  {#if loading && !preview}
    <Empty><span class="np-spinner"></span></Empty>
  {:else}
    {#if !preview}<div class="np-dim np-small">Context preview unavailable{error ? `: ${error}` : ''}</div>{/if}
    {#if preview}
    <div class="stats">
      <div class="stat" title="Estimated context tokens (system prompt + tools + messages)"><b>≈{tokens(preview.estimatedTokens)}</b><span>context</span></div>
      <div class="stat" title="System prompt tokens (estimate)"><b>{tokens(promptTokens)}</b><span>prompt</span></div>
      <div class="stat" title="Tools sent to the model"><b>{preview.tools?.length ?? 0}</b><span>tools</span></div>
    </div>
    {/if}

    <Section title="AGENTS.md" count={files?.length ?? null} collapsible storageKey="diag.ctx.agentsmd">
      {#each files ?? [] as f (f.path)}
        <div class="file np-line np-hover-row">
          <span class="scope" data-s={f.scope}>{f.scope}</span>
          <span class="path np-mono np-grow" title={f.path}><bdi>{f.path}</bdi></span>
          <span class="size np-dim">{bytes(f.bytes)}</span>
          <span class="np-hover-actions">
            <IconButton icon="copy" title="Copy path" size="sm" onclick={() => copyText(f.path)} />
            {#if desktop.available}<IconButton icon="folder-open" title="Reveal" size="sm" onclick={() => desktop.revealPath(f.path)} />{/if}
          </span>
        </div>
      {:else}
        <div class="np-dim np-small">{files ? 'No instruction files apply to this session.' : 'Instruction discovery unavailable.'}</div>
      {/each}
    </Section>

    {#if skills}
      <Section title="Skills" count={skills.skills.length} collapsible storageKey="diag.ctx.skills">
        {#each skills.skills as s (s.path)}
          <div class="file np-line np-hover-row" title={`${s.description}\n${s.path}`}>
            <span class="scope" data-s={s.scope}>{s.scope}</span>
            <span class="sname np-mono" class:off={s.disabled}>{s.name}</span>
            <span class="np-dim np-small np-grow np-ellipsis">{s.disabled ? 'switched off' : s.userOnly ? '/skill: only' : s.description}</span>
            <span class="np-hover-actions">
              <IconButton icon="copy" title="Copy path" size="sm" onclick={() => copyText(s.path)} />
              {#if desktop.available}<IconButton icon="folder-open" title="Reveal" size="sm" onclick={() => desktop.revealPath(s.path)} />{/if}
            </span>
          </div>
        {:else}
          <div class="np-dim np-small">No skills apply to this session.</div>
        {/each}
        {#each skills.problems as p (p.path + p.message)}
          <div class="problem" data-level={p.level} title={p.path}>
            <span class="np-mono">{basename(p.path.replace(/[\\/]SKILL\.md$/i, ''))}</span> {p.message}
          </div>
        {/each}
      </Section>
    {/if}

    {#if preview}<Section title="System prompt" collapsible storageKey="diag.ctx.prompt">
      {#snippet actions()}
        <IconButton icon="copy" title="Copy system prompt" size="sm" onclick={() => copyText(preview.systemPrompt)} />
        <IconButton icon="expand" title={full ? 'Collapse' : 'Show all'} size="sm" pressed={full} onclick={() => (full = !full)} />
      {/snippet}
      <pre class="prompt np-mono np-scroll" class:full>{preview.systemPrompt}</pre>
    </Section>

    <Section title="Tools" count={preview.tools?.length ?? 0} collapsible open={false} storageKey="diag.ctx.tools">
      {#each preview.tools ?? [] as t (t.name)}
        <div class="t" title={t.description}><span class="np-mono">{t.name}</span><span class="np-dim desc">{t.description}</span></div>
      {/each}
    </Section>

    {/if}
    {#if toolSets}
      <!-- why the tools are what they are: the baseline and every change with its cause, as the model was told -->
      <Section
        title="Tool changes"
        count={toolSets.changes?.length || null}
        collapsible
        open={false}
        storageKey="diag.ctx.toolsets"
      >
        {#each (toolSets.changes ?? []).slice().reverse() as c (c.seq)}
          <div class="change" title={c.text}>
            <Badge tone={c.cause === 'unknown' ? undefined : c.cause === 'plugin-reload' ? 'warn' : 'info'}>{c.cause}</Badge>
            <TimeAgo time={c.time} class="np-dim" />
            {#if c.removed?.length}
              <span class="np-mono gone" title="no longer available">−{c.removed.join(' ')}</span>
            {/if}
            {#if c.added?.length}
              <span class="np-mono new" title="new">+{c.added.join(' ')}</span>
            {/if}
            {#if c.plugins?.length}<span class="np-dim np-small">({c.plugins.join(', ')})</span>{/if}
          </div>
        {:else}
          <div class="np-dim np-small">
            No tool changes{since ? ` since the baseline of ${since} tools` : ''} — what the model has now is what it started with.
          </div>
        {/each}
      </Section>
    {/if}
  {/if}
{/if}

<style>
  .head {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 6px 8px 2px 12px;
  }
  .title {
    flex: 1;
    font-weight: 600;
  }
  .stats {
    display: grid;
    grid-template-columns: repeat(3, 1fr);
    gap: 6px;
    padding: 6px 10px 8px 12px;
  }
  .stat {
    display: flex;
    flex-direction: column;
    min-width: 0;
    padding: 5px 8px;
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--bg-2);
  }
  .stat b {
    font-size: 15px;
    font-variant-numeric: tabular-nums;
  }
  .stat span {
    font-size: var(--fs-xs);
    color: var(--fg-dim);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .file {
    min-height: 24px;
    margin: 0 -4px;
    padding: 0 4px;
    border-radius: var(--radius-sm);
  }
  .file:hover {
    --row-bg: var(--bg-2);
    background: var(--bg-2);
  }
  .scope {
    flex: none;
    padding: 0 6px;
    border-radius: 8px;
    background: var(--bg-3);
    color: var(--fg-muted);
    font-size: 10px;
    line-height: 16px;
  }
  .scope[data-s='project'] {
    background: var(--accent-soft);
    color: var(--accent);
  }
  .path {
    direction: rtl;
    text-align: left;
    font-size: 11px;
  }
  .sname {
    flex: none;
    max-width: 50%;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    font-size: 11.5px;
  }
  .sname.off {
    color: var(--fg-dim);
    text-decoration: line-through;
  }
  .problem {
    padding: 2px 0;
    font-size: var(--fs-xs);
    line-height: 1.4;
    color: var(--warn);
    overflow-wrap: anywhere;
  }
  .problem[data-level='error'] {
    color: var(--err);
  }
  .size {
    flex: none;
    font-size: var(--fs-xs);
  }
  .prompt {
    margin: 0;
    padding: 8px 10px;
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--code-bg);
    color: var(--fg-muted);
    font-size: 11px;
    line-height: 1.5;
    white-space: pre-wrap;
    overflow-wrap: anywhere;
    max-height: 300px;
    overflow: auto;
  }
  .prompt.full {
    max-height: none;
  }
  .t {
    display: flex;
    flex-direction: column;
    padding: 3px 0;
    font-size: var(--fs-xs);
    line-height: 1.4;
  }
  .t .np-mono {
    font-size: 11.5px;
    color: var(--fg);
  }
  .desc {
    display: -webkit-box;
    -webkit-box-orient: vertical;
    -webkit-line-clamp: 2;
    line-clamp: 2;
    overflow: hidden;
  }
  .change {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: 6px;
    min-height: 22px;
    padding: 2px 0;
    font-size: var(--fs-xs);
  }
  .change .np-mono {
    font-size: 11.5px;
    overflow-wrap: anywhere;
  }
  .gone {
    color: var(--err);
  }
  .new {
    color: var(--ok);
  }
</style>
