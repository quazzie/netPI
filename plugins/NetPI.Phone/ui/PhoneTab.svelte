<script>
  // The Phone tab: whether the listener is up and where, "Pair a phone" (a one-time code, its link and a QR code of it),
  // and the paired phones with a revoke button. The data is the plugin's own RPCs; phone.changed refreshes it.
  import { useRefresh, Button, IconButton, Empty, ConfirmButton, timeAgo } from '@netpi/kit';
  import { qrSvg } from './qr.js';

  let { ctx } = $props();
  let visible = $state(true);
  let status = $state.raw(null);
  let devices = $state.raw(null);
  let pairing = $state.raw(null); // { code, expiresAt, links }
  let error = $state('');
  let copied = $state(false);
  let now = $state(Date.now());

  async function load() {
    try {
      [status, devices] = await Promise.all([ctx.rpc('phone.status', {}), ctx.rpc('phone.devices', {})]);
      error = '';
    } catch (e) {
      error = String(e?.message ?? e);
    }
  }

  // svelte-ignore state_referenced_locally
  const tab = useRefresh(ctx, { load, events: ['phone.changed', 'settings.changed'], pollMs: 30_000, visible: () => visible });
  export function setVisible(v) {
    visible = v;
    tab.setVisible?.(v);
  }

  $effect(() => {
    if (!pairing) return;
    const t = setInterval(() => (now = Date.now()), 1000);
    return () => clearInterval(t);
  });
  const left = $derived(pairing ? Math.max(0, Math.round((new Date(pairing.expiresAt).getTime() - now) / 1000)) : 0);
  $effect(() => {
    if (pairing && left === 0) pairing = null;
  });
  const link = $derived(pairing?.links?.[0] ?? '');
  const qr = $derived(link ? safeQr(link) : '');

  function safeQr(text) {
    try {
      return qrSvg(text, 200);
    } catch {
      return '';
    }
  }

  async function pair() {
    try {
      pairing = await ctx.rpc('phone.pair', {});
      copied = false;
    } catch (e) {
      error = String(e?.message ?? e);
    }
  }
  async function revoke(id) {
    try {
      await ctx.rpc('phone.revoke', { id });
      await load();
    } catch (e) {
      error = String(e?.message ?? e);
    }
  }
  async function copy(text) {
    try {
      await navigator.clipboard.writeText(text);
      copied = true;
    } catch {
      copied = false;
    }
  }
</script>

<div class="phone">
  <div class="np-toolbar">
    <span class="np-strong">Phone</span>
    {#if devices}<span class="np-dim">{devices.length}</span>{/if}
    <span class="np-grow"></span>
    <Button size="sm" variant="primary" icon="plus" disabled={!status?.listening} title="Pair a phone" onclick={pair}>Pair</Button>
    <IconButton icon="refresh" size="sm" title="Refresh" onclick={() => tab.refresh()} />
  </div>

  {#if error}
    <Empty icon="alert">phone unavailable: {error}</Empty>
  {:else if status}
    <div class="state np-small" data-state={status.error ? 'err' : status.listening ? 'ok' : 'off'}>
      {#if status.error}
        The listener could not start: {status.error}
      {:else if status.listening}
        Listening on <span class="np-mono">{status.address}:{status.port}</span>
        {#if status.urls?.length}— phones open <span class="np-mono">{status.urls[0]}</span>{/if}
        {#if status.connections}· {status.connections} live{/if}
      {:else}
        Off. Turn on “Serve the phone page” in Settings → Agents → Phone, and set which addresses may connect (Settings → Phone → “Accept connections from”).
      {/if}
    </div>
  {/if}

  {#if pairing}
    <div class="pairing" role="status">
      <div class="np-strong np-small">Pair a phone — {left}s left</div>
      <div class="code np-mono">{pairing.code}</div>
      {#if qr}<div class="qr">{@html qr}</div>{/if}
      {#each pairing.links ?? [] as l (l)}
        <div class="np-mono np-small link">{l}</div>
      {/each}
      <div class="np-small np-muted">Scan the code with the phone’s camera, or open the link, or type the code on the phone’s pairing screen. It works once.</div>
      <div class="actions">
        {#if link}<Button size="sm" variant="ghost" onclick={() => copy(link)}>{copied ? 'Copied' : 'Copy link'}</Button>{/if}
        <Button size="sm" variant="ghost" onclick={() => (pairing = null)}>Done</Button>
      </div>
    </div>
  {/if}

  {#if devices && devices.length === 0 && status?.listening}
    <Empty icon="plug">No phone is paired. Press Pair, then scan the code with your phone.</Empty>
  {:else if devices}
    <div class="np-list">
      {#each devices as d (d.id)}
        <div class="dev">
          <div class="info">
            <div class="np-strong">{d.name}</div>
            <div class="np-small np-dim">paired {timeAgo(d.createdAt)} ago · seen {timeAgo(d.lastSeenAt)} ago{d.lastFrom ? ` · ${d.lastFrom}` : ''}</div>
          </div>
          <ConfirmButton icon="trash" title="Unpair (its token stops working)" confirmLabel="Unpair?" onconfirm={() => revoke(d.id)} />
        </div>
      {/each}
    </div>
  {/if}

  <div class="np-small np-muted np-pad">
    Anyone who can reach the port with a paired phone’s token can do what the agent can. Keep it on your own network, a VPN, or behind a reverse proxy you trust; behind a proxy, turn on “Behind a reverse proxy” in the settings. Direct connections from another machine also need a firewall rule for the port.
  </div>
</div>

<style>
  .state { padding: 6px 10px; margin: 6px 10px; border-radius: 6px; background: var(--np-surface-2, rgba(128, 128, 128, 0.12)); }
  .state[data-state='err'] { color: var(--np-danger, #e5484d); }
  .pairing { margin: 8px 10px; padding: 10px; border: 1px solid var(--np-border, rgba(128, 128, 128, 0.3)); border-radius: 8px; display: flex; flex-direction: column; gap: 6px; }
  .code { font-size: 1.4em; letter-spacing: 0.12em; }
  .qr { background: #fff; padding: 4px; border-radius: 6px; width: fit-content; line-height: 0; }
  .link { word-break: break-all; }
  .actions { display: flex; gap: 6px; }
  .dev { display: flex; align-items: center; gap: 8px; padding: 8px 10px; }
  .info { flex: 1; min-width: 0; }
</style>
