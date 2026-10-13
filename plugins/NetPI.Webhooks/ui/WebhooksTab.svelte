<script>
  // The Webhooks tab: the receiver's state, every webhook with what it last did, and the add/edit form. The token of a
  // new or re-keyed webhook is shown once, here, with a ready curl line. The data is the plugin's own RPCs; projects and
  // agents are other plugins' reads, and each degrades on its own when absent. webhooks.changed (unscoped) is the refresh
  // backbone; webhooks.ran is session-scoped to the chat a delivery started, so it is only a hint.
  import { useRefresh, Button, IconButton, Empty } from '@netpi/kit';
  import WebhookRow from './WebhookRow.svelte';
  import WebhookForm from './WebhookForm.svelte';
  import { curlLine } from './model.js';

  let { ctx } = $props();
  let visible = $state(true);
  let hooks = $state.raw(null);
  let receiver = $state.raw(null);
  let projects = $state.raw([]);
  let agents = $state.raw(null);
  let profiles = $state.raw(null);
  let error = $state('');
  let editing = $state(null); // null, or { hook: null } for a new one
  let reveal = $state.raw(null); // { hook, token } right after an add or a re-key
  let copied = $state(false);

  async function load() {
    try {
      const [list, st, pl, al, pf] = await Promise.all([
        ctx.rpc('webhooks.list', {}),
        ctx.rpc('webhooks.status', {}).catch(() => null),
        ctx.rpc('projects.list', {}).catch(() => []),
        ctx.rpc('agents.list', {}).catch(() => null),
        ctx.rpc('profiles.list', {}).catch(() => null),
      ]);
      hooks = list ?? [];
      receiver = st;
      projects = pl ?? [];
      agents = al;
      profiles = pf?.profiles ?? null;
      error = '';
    } catch (e) {
      error = String(e?.message ?? e);
    }
  }

  // svelte-ignore state_referenced_locally
  const tab = useRefresh(ctx, {
    load,
    events: ['webhooks.changed', 'webhooks.ran', 'settings.changed'],
    pollMs: 30_000,
    visible: () => visible,
  });

  export function setVisible(v) {
    visible = v;
    tab.setVisible?.(v);
  }

  function showToken(hook, token) {
    reveal = { hook, token };
    copied = false;
  }

  const base = $derived(receiver?.url ?? 'http://<this-pc>:7432/hook/');
  const command = $derived(reveal ? curlLine(base, reveal.hook.id, reveal.token) : '');

  async function copy(text) {
    try {
      await navigator.clipboard.writeText(text);
      copied = true;
    } catch {
      copied = false;
    }
  }
</script>

<div class="webhooks">
  <div class="np-toolbar">
    <span class="np-strong">Webhooks</span>
    {#if hooks !== null}
      <span class="np-dim">{hooks.length}</span>
    {/if}
    <span class="np-grow"></span>
    {#if !editing}
      <Button size="sm" variant="primary" icon="plus" title="New webhook" onclick={() => (editing = { hook: null })}>New</Button>
      <IconButton icon="refresh" size="sm" title="Refresh" onclick={() => tab.refresh()} />
    {/if}
  </div>

  {#if receiver && !editing}
    <div class="recv np-small" data-state={receiver.error ? 'err' : receiver.listening ? 'ok' : 'off'}>
      {#if receiver.error}
        Receiver could not start: {receiver.error}
      {:else if receiver.listening}
        Receiving on <span class="np-mono">{receiver.url}</span>
      {:else}
        Receiver is off. Turn on “Receive webhooks” in Settings → Agents → Webhooks, set the address it listens on and who may call it.
      {/if}
    </div>
  {/if}

  {#if reveal && !editing}
    <div class="token" role="status">
      <div class="np-strong np-small">Token for “{reveal.hook.name}” — shown once</div>
      <div class="np-mono np-small tok">{reveal.token}</div>
      <div class="np-small np-muted">Example call:</div>
      <div class="np-mono np-small tok">{command}</div>
      <div class="try-actions">
        <Button size="sm" variant="ghost" onclick={() => copy(reveal.token)}>{copied ? 'Copied' : 'Copy token'}</Button>
        <Button size="sm" variant="ghost" onclick={() => copy(command)}>Copy example</Button>
        <Button size="sm" variant="ghost" onclick={() => (reveal = null)}>I have saved it</Button>
      </div>
    </div>
  {/if}

  {#if error}
    <Empty icon="alert">webhooks.list unavailable: {error}</Empty>
  {:else if hooks === null}
    <div class="np-dim np-pad">Loading…</div>
  {:else if hooks.length === 0}
    <Empty icon="plug">No webhooks yet. A webhook lets another system (a monitoring tool, a CI job, a script) start a chat here by POSTing to it.</Empty>
    <div class="np-pad">
      <Button variant="primary" icon="plus" onclick={() => (editing = { hook: null })}>New webhook</Button>
    </div>
  {:else}
    <div class="np-list">
      {#each hooks as h (h.id)}
        <WebhookRow
          {h}
          {ctx}
          {projects}
          {receiver}
          onedit={() => (editing = { hook: h })}
          onchanged={tab.refresh}
          ontoken={showToken}
        />
      {/each}
    </div>
  {/if}

  {#if editing}
    <WebhookForm
      {ctx}
      hook={editing.hook}
      {projects}
      {agents}
      {profiles}
      ondone={(created) => {
        editing = null;
        if (created?.token) showToken(created, created.token);
        tab.refresh();
      }}
      oncancel={() => (editing = null)}
    />
  {/if}
</div>

<style>
  .webhooks {
    display: flex;
    flex-direction: column;
    height: 100%;
    min-width: 0;
  }
  .np-toolbar {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 6px 10px;
    border-bottom: 1px solid var(--np-border, #2a2a2a);
  }
  .np-list {
    overflow-y: auto;
    flex: 1;
  }
  .np-pad {
    padding: 10px;
  }
  .recv {
    overflow-wrap: anywhere;
    padding: 5px 10px;
    border-bottom: 1px solid var(--np-border, #2a2a2a);
    opacity: 0.85;
  }
  .recv[data-state='ok'] {
    color: var(--np-ok, #4caf7d);
  }
  .recv[data-state='err'] {
    color: var(--np-err, #e05555);
  }
  .recv[data-state='off'] {
    color: var(--np-warn, #d9a441);
  }
  .token {
    padding: 8px 10px;
    border-bottom: 1px solid var(--np-border, #2a2a2a);
    display: flex;
    flex-direction: column;
    gap: 4px;
    background: color-mix(in srgb, var(--np-warn, #d9a441) 10%, transparent);
  }
  .tok {
    word-break: break-all;
    user-select: all;
  }
  .try-actions {
    display: flex;
    gap: 6px;
    flex-wrap: wrap;
  }
</style>
