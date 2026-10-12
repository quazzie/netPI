<script>
  // One webhook row: dot + name, a meta line, hover actions, and the expandable detail — where to POST, the template, a
  // "try a payload" panel (preview or send), and the delivery history.
  import { StatusDot, IconButton, ConfirmButton, Button, timeAgo } from '@netpi/kit';
  import DeliveryHistory from './DeliveryHistory.svelte';
  import { rowState, limitsLine } from './model.js';

  let { h, ctx, projects, receiver, onedit, onchanged, ontoken } = $props();

  let open = $state(false);
  let busy = $state(false);
  let flash = $state('');
  let tryText = $state('{\n  "host": "nuc",\n  "service": "Kernel errors",\n  "state": "CRIT",\n  "output": "1 in last 30 min"\n}');
  let preview = $state.raw(null);

  const state = $derived(rowState(h));
  const projectName = $derived((projects ?? []).find((p) => p?.id === h.projectId)?.name ?? (h.projectId || 'no project'));
  const meta = $derived(
    [state.key === 'proposed' ? 'proposed by an agent' : null, h.agent || 'default agent', projectName, limitsLine(h), `token …${h.tokenHint}`]
      .filter(Boolean)
      .join(' · '),
  );
  const url = $derived(receiver?.url ? receiver.url + h.id : h.path);

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
    if (r.status === 'started') {
      say(`Started a chat for “${h.name}”`);
      if (r.sessionId) ctx.app.openSession?.(r.sessionId);
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
      {#if h.match?.length}
        <div class="np-small np-muted">Only when</div>
        <div class="np-mono np-small prompt">{JSON.stringify(h.match)}</div>
      {/if}
      {#if h.dedupe?.key}
        <div class="np-small np-muted">Dedupe</div>
        <div class="np-mono np-small">{h.dedupe.key} · {h.dedupe.minutes ?? 60} min</div>
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
            <div class="ok">Would start a chat{preview.agent ? ` on ${preview.agent}` : ''}.</div>
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
