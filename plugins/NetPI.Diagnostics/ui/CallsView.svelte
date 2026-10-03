<script>
  import { Segmented, Empty, StatusDot, tokens, duration, timeAgo, useRefresh } from '@netpi/kit';

  /**
   * Model calls (diag.calls), newest first, polled while visible: state, model, time to first token, duration, the
   * agent, tokens and the error. A click shows the call in detail (diag.call): the request's size, the response,
   * retries and notices, and opens its chat.
   */
  let { ctx, visible = true } = $props();

  let calls = $state.raw(null);
  let error = $state('');
  let filter = $state('all');
  let openId = $state(null);
  let detail = $state.raw(null);

  // single-flight: a poll never starts while one is in flight, and the next poll is scheduled after the
  // previous one completes — a slow diag.calls cannot stack requests on top of itself, and an answer that arrives
  // after this view is gone is dropped (the chain, and the view it holds, would live on otherwise)
  async function load() {
    try {
      const answer = await ctx.rpc('diag.calls', { limit: 150 });
      if (!tab.alive) return;
      calls = answer;
      error = '';
    } catch (e) {
      error = e.message;
    }
  }
  // svelte-ignore state_referenced_locally
  const tab = useRefresh(ctx, { load, pollMs: 2000, visible: () => visible });

  async function toggle(c) {
    if (openId === c.id) {
      openId = null;
      return;
    }
    openId = c.id;
    detail = null;
    try {
      detail = await ctx.rpc('diag.call', { id: c.id });
    } catch (e) {
      detail = { error: e.message };
    }
  }

  const shown = $derived((calls ?? []).filter((c) => filter === 'all' || (filter === 'errors' ? c.state === 'error' : c.state === 'running')));
  const counts = $derived({
    running: (calls ?? []).filter((c) => c.state === 'running').length,
    errors: (calls ?? []).filter((c) => c.state === 'error').length,
  });
  const dot = (s) => (s === 'running' ? 'running' : s === 'ok' ? 'ok' : s === 'error' ? 'error' : 'cancelled');
  const short = (m) => m?.split('/').slice(1).join('/') || m;
  const ms = (v) => (v == null ? '–' : v < 1000 ? `${v} ms` : duration(v));
</script>

<div class="lv">
  <Segmented
    bind:value={filter}
    options={[
      { value: 'all', label: 'All', icon: 'list', title: 'Every call' },
      { value: 'running', label: 'Running', icon: 'play', count: counts.running || null, title: 'Calls in progress' },
      { value: 'errors', label: 'Errors', icon: 'circle-x', count: counts.errors || null, tone: 'err', title: 'Failed calls' },
    ]}
  />
</div>
{#if calls == null && !error}
  <Empty><span class="np-spinner"></span></Empty>
{:else if error && !calls}
  <Empty icon="alert">diag.calls unavailable: {error}</Empty>
{:else}
  <div class="list">
    {#each shown as c (c.id)}
      <!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
      <div class="call" data-state={c.state} class:open={openId === c.id} onclick={() => toggle(c)}>
        <div class="row np-line" title="{c.model} · {c.purpose} · call {c.id}">
          <StatusDot status={dot(c.state)} />
          <span class="model np-mono np-grow">{short(c.model)}</span>
          <span class="num np-mono" title="Time to the first token">{ms(c.firstTokenMs)}</span>
          <span class="num np-mono" title="Duration">{ms(c.durationMs)}</span>
        </div>
        <div class="sub np-line">
          <span class="np-grow np-ellipsis">
            {[c.agent, c.purpose !== 'agent' ? c.purpose : null, c.attempts > 1 ? `${c.attempts} attempts` : null].filter(Boolean).join(' · ')}
          </span>
          {#if c.inputTokens != null}<span class="np-mono" title="Input (cached) / output tokens">{tokens(c.inputTokens + (c.cacheReadTokens ?? 0))}↑ {tokens(c.outputTokens ?? 0)}↓</span>{/if}
          <span class="ago">{timeAgo(c.startedAt)}</span>
        </div>
        {#if c.error}<div class="err">{c.error}</div>{/if}
        {#if openId === c.id}
          <div class="detail">
            {#if !detail}
              <span class="np-spinner"></span>
            {:else if detail.error && !detail.id}
              <span class="err">{detail.error}</span>
            {:else}
              <dl>
                <dt>request</dt>
                <dd>{detail.request?.messages} messages · {detail.request?.tools} tools · {tokens(Math.round((detail.request?.inputChars ?? 0) / 4))} tok (est.){detail.reasoningEffort ? ` · effort ${detail.reasoningEffort}` : ''}</dd>
                {#if detail.request?.lastUser}<dt>last user</dt><dd>{detail.request.lastUser}</dd>{/if}
                <dt>response</dt>
                <dd>
                  {detail.response?.textChars} text · {detail.response?.thinkingChars} thinking chars{detail.response?.toolCalls?.length ? ` · ${detail.response.toolCalls.join(', ')}` : ''}{detail.stopReason
                    ? ` · ${detail.stopReason}`
                    : ''}
                </dd>
                {#if detail.resets?.length}<dt>retries</dt><dd>{detail.resets.join(' | ')}</dd>{/if}
                {#if detail.notices?.length}<dt>notices</dt><dd>{detail.notices.join(' | ')}</dd>{/if}
                {#if detail.errorDetail}<dt>error</dt><dd>{detail.errorDetail.type ?? ''}{detail.errorDetail.status ? ` · HTTP ${detail.errorDetail.status}` : ''}{detail.errorDetail.transient ? ' · transient' : ''}{detail.errorDetail.contextOverflow ? ' · context overflow' : ''}</dd>{/if}
              </dl>
              {#if c.sessionId}
                <button class="link" onclick={(e) => (e.stopPropagation(), ctx.app.openSession(c.sessionId))}>Open the chat</button>
              {/if}
            {/if}
          </div>
        {/if}
      </div>
    {:else}
      <Empty>No model calls{filter !== 'all' ? ' match the filter' : ' since NetPI started'}</Empty>
    {/each}
  </div>
{/if}

<style>
  .lv {
    padding: 4px 10px 6px 12px;
  }
  .list {
    padding: 0 10px 10px 12px;
  }
  .call {
    padding: 4px 4px;
    margin: 0 -4px;
    border-radius: var(--radius-sm);
    cursor: pointer;
  }
  .call:hover {
    background: var(--bg-2);
  }
  .row {
    gap: 7px;
  }
  .model {
    font-size: 12px;
    color: var(--fg);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    min-width: 0;
  }
  .num {
    flex: none;
    font-size: 11px;
    color: var(--fg-muted);
  }
  .sub {
    gap: 7px;
    margin: 1px 0 0 14px;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
  }
  .ago {
    flex: none;
  }
  .err {
    margin: 2px 0 0 14px;
    font-size: var(--fs-xs);
    color: var(--err);
    overflow-wrap: anywhere;
  }
  .call:not(.open) .err {
    display: -webkit-box;
    -webkit-box-orient: vertical;
    -webkit-line-clamp: 2;
    line-clamp: 2;
    overflow: hidden;
  }
  .detail {
    margin: 4px 0 2px 14px;
    font-size: var(--fs-xs);
  }
  dl {
    display: grid;
    grid-template-columns: auto minmax(0, 1fr);
    gap: 2px 8px;
    margin: 0 0 4px;
  }
  dt {
    color: var(--fg-dim);
  }
  dd {
    margin: 0;
    color: var(--fg-muted);
    overflow-wrap: anywhere;
  }
  .link {
    padding: 0;
    border: 0;
    background: none;
    color: var(--accent);
    font: inherit;
    cursor: pointer;
  }
</style>
