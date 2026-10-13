<script>
  // One webhook row: dot + name, a meta line, hover actions, and the expandable detail — where to POST, the template, a
  // "try a payload" panel (preview or send), and the delivery history.
  import { StatusDot, IconButton, ConfirmButton, Button, timeAgo } from '@netpi/kit';
  import DeliveryHistory from './DeliveryHistory.svelte';
  import { rowState, limitsLine, incidentLine, hookLine } from './model.js';

  let { h, ctx, projects, receiver, onedit, onchanged, ontoken } = $props();

  let open = $state(false);
  let busy = $state(false);
  let flash = $state('');
  let tryText = $state('{\n  "host": "nuc",\n  "service": "Kernel errors",\n  "state": "CRIT",\n  "output": "1 in last 30 min"\n}');
  let preview = $state.raw(null);
  let incidents = $state.raw([]);

  async function loadIncidents() {
    try {
      incidents = (await ctx.rpc('webhooks.incidents', { id: h.id })) ?? [];
    } catch {
      incidents = [];
    }
  }
  $effect(() => {
    if (open) {
      void h.lastAt; // reload when a delivery arrives
      loadIncidents();
    }
  });

  const state = $derived(rowState(h));
  const projectName = $derived((projects ?? []).find((p) => p?.id === h.projectId)?.name ?? (h.projectId || 'no project'));
  const meta = $derived(
    [state.key === 'proposed' ? 'proposed by an agent' : null, h.agent || 'default agent', h.profile ? `profile ${h.profile}` : 'automatic profile', projectName, h.pre || h.post ? [h.pre ? 'Before' : null, h.post ? 'After' : null].filter(Boolean).join(' + ') : null, limitsLine(h), `token …${h.tokenHint}`]
      .filter(Boolean)
      .join(' · '),
  );
  const url = $derived(receiver?.url ? receiver.url + (h.slug || h.id) : h.path);

  function say(msg, level = 'info') {
    flash = msg;
    ctx.app.toast?.(msg, level);
    setTimeout(() => (flash = ''), 5000);
  }

  async function call(method, params) {
    if (busy) return null;
    busy = true;
    try {
      return await ctx.rpc(method, params);
    } catch (e) {
      say(String(e?.message ?? e), 'warn');
      return null;
    } finally {
      busy = false;
    }
  }

  async function togglePause() {
    if (await call('webhooks.update', { id: h.id, enabled: !h.enabled })) onchanged?.();
  }

  async function del() {
    if (await call('webhooks.delete', { id: h.id })) onchanged?.();
  }

  async function rotate() {
    const r = await call('webhooks.rotateToken', { id: h.id });
    if (r?.token) {
      ontoken?.(h, r.token);
      onchanged?.();
    }
  }

  function payload() {
    try {
      return JSON.parse(tryText);
    } catch {
      return { body: tryText };
    }
  }

  async function doPreview() {
    const r = await call('webhooks.test', { id: h.id, payload: payload(), run: false });
    if (r) preview = r;
  }

  async function doSend() {
    const r = await call('webhooks.test', { id: h.id, payload: payload(), run: true });
    if (!r) return;
    preview = null;
    if (r.status === 'started' || r.status === 'appended') {
      say(r.status === 'started' ? `Started a chat for “${h.name}”` : `Added to the open chat of “${h.name}”`);
      if (r.sessionId) ctx.app.openSession?.(r.sessionId);
    } else if (r.status === 'held') {
      say(r.reason ?? 'Collecting; the chat starts when the window ends');
    } else {
      say(r.reason ? `${r.status}: ${r.reason}` : r.status, 'warn');
    }
    onchanged?.();
  }
</script>

<div class="hook np-hover-row" data-tone={state.tone}>
  <div
    class="row np-line"
    role="button"
    tabindex="0"
    aria-expanded={open}
    title={state.label}
    onclick={() => (open = !open)}
    onkeydown={(e) => e.key === 'Enter' && (open = !open)}
  >
    <StatusDot status={state.tone} title={state.label} />
    <span class="np-line np-baseline np-grow">
      <span class="name np-ellipsis np-grow" title={h.name}>{h.name}</span>
      <span class="last np-small">{h.lastAt ? timeAgo(h.lastAt) : 'never'}</span>
    </span>
  </div>
  <div class="np-meta" title={meta}>{meta}</div>

  <span class="np-hover-actions">
    <IconButton
      icon={h.enabled ? 'pause' : 'play'}
      size="sm"
      title={h.enabled ? 'Pause' : h.proposedBy ? 'An agent proposed this: check the prompt, then enable it' : 'Resume'}
      disabled={busy}
      onclick={togglePause}
    />
    <IconButton icon="pencil" size="sm" title="Edit" onclick={() => onedit?.()} />
    <IconButton icon="history" size="sm" title="Details and deliveries" pressed={open} onclick={() => (open = !open)} />
    <ConfirmButton icon="refresh" title="New token (the old one stops working)" confirmLabel="New token?" onconfirm={rotate} />
    <ConfirmButton icon="trash" title="Delete" confirmLabel="Delete?" onconfirm={del} />
  </span>

  {#if flash}<div class="np-small np-muted" role="status">{flash}</div>{/if}

  {#if open}
    <div class="detail">
      <div class="np-small np-muted">POST to</div>
      <div class="np-mono np-small url" title={url}>{url}</div>
      <div class="np-small np-muted">with <span class="np-mono">Authorization: Bearer &lt;token&gt;</span></div>

      <div class="np-small np-muted">Prompt</div>
      <div class="prompt np-small" title={h.prompt}>{h.prompt}</div>
      {#if h.pre}
        <div class="np-small np-muted">Before</div>
        <div class="np-mono np-small prompt" title={hookLine(h.pre)}>{hookLine(h.pre)}</div>
      {/if}
      {#if h.post}
        <div class="np-small np-muted">After</div>
        <div class="np-mono np-small prompt" title={hookLine(h.post)}>{hookLine(h.post)}</div>
      {/if}
      {#if incidents.length}
        <div class="np-small np-muted">Conversations</div>
        {#each incidents as i (i.key)}
          <div class="np-small inc" title={incidentLine(i)}>
            <span class="np-ellipsis np-grow">{incidentLine(i)}</span>
            {#if i.sessionId}
              <button class="inc-open np-small" title="Open the chat that handles this conversation" onclick={() => ctx.app.openSession?.(i.sessionId)}>chat</button>
            {/if}
          </div>
        {/each}
      {/if}

      <div class="np-small np-muted">Try a payload</div>
      <textarea class="np-input np-mono" rows="6" bind:value={tryText}></textarea>
      <div class="try-actions">
        <Button size="sm" variant="ghost" disabled={busy} title="Fill in the template and check the filters, limits and agent — start nothing" onclick={doPreview}>Preview</Button>
        <Button size="sm" variant="ghost" disabled={busy} title="Deliver it as if it had arrived: a real chat starts" onclick={doSend}>Send</Button>
      </div>
      {#if preview}
        <div class="preview np-small">
          {#if preview.skip}
            <div class="skip">Would be skipped: {preview.skip}</div>
          {:else}
            <div class="ok">
              {#if preview.action === 'append'}Would be appended to the chat already handling “{preview.key}”.
              {:else if preview.action === 'join'}Would join the alerts being collected for “{preview.key}”.
              {:else if preview.action === 'collect'}Would open the incident “{preview.key}” and collect for a while before the chat starts{preview.agent ? ` on ${preview.agent}` : ''}.
              {:else}Would start a chat{preview.agent ? ` on ${preview.agent}` : ''} at once.{/if}
            </div>
            {#if preview.note}<div class="np-muted">{preview.note}</div>{/if}
          {/if}
          <div class="np-muted">Title: {preview.title}</div>
          <div class="prompt">{preview.prompt}</div>
        </div>
      {/if}

      {#if h.lastSessionId}
        <Button size="sm" variant="ghost" icon="message" title="Open the chat this webhook last started"
          onclick={() => ctx.app.openSession?.(h.lastSessionId)}>Open last chat</Button>
      {/if}
      <DeliveryHistory {ctx} webhookId={h.id} refreshKey={open ? (h.lastAt ?? h.updatedAt) : null} />
    </div>
  {/if}
</div>

<style>
  .hook {
    position: relative;
    padding: 6px 10px;
    border-bottom: 1px solid var(--np-border, #262626);
  }
  .row {
    gap: 8px;
    cursor: pointer;
  }
  .name {
    font-size: 13px;
  }
  .last {
    flex: none;
    opacity: 0.7;
  }
  .np-meta {
    padding: 2px 0 0 14px;
  }
  .detail {
    padding: 8px 0 4px 14px;
    display: flex;
    flex-direction: column;
    gap: 5px;
  }
  .url {
    word-break: break-all;
  }
  .prompt {
    white-space: pre-wrap;
    word-break: break-word;
    max-height: 14em;
    overflow-y: auto;
  }
  .inc {
    display: flex;
    gap: 6px;
    align-items: baseline;
  }
  .inc-open {
    flex: none;
    background: none;
    border: none;
    color: var(--np-accent, #6aa3f0);
    cursor: pointer;
    padding: 0;
    font: inherit;
  }
  .try-actions {
    display: flex;
    gap: 6px;
  }
  .preview {
    border: 1px solid var(--np-border, #2a2a2a);
    border-radius: 4px;
    padding: 6px;
    display: flex;
    flex-direction: column;
    gap: 4px;
  }
  .skip {
    color: var(--np-warn, #d9a441);
  }
  .ok {
    color: var(--np-ok, #4caf7d);
  }
</style>
