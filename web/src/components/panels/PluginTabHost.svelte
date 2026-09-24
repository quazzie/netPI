<script>
  import { onDestroy, untrack } from 'svelte';
  import Icon from '../../lib/kit/Icon.svelte';
  import { createPluginCtx } from '../../lib/pluginCtx.js';

  /**
   * Hosts one plugin tab: lazy-imports /plugins/{pluginId}/{module}?v={version}, calls mount(el, ctx)
   * (or module[export]), forwards onShow/onHide, and remounts when the tab's version changes (hot reload).
   */
  let { tab, visible = true } = $props();

  let host = $state();
  let phase = $state('loading'); // loading | ready | error
  let error = $state('');
  let handle = null;
  let dispose = null;
  let token = 0;
  let shown = null;

  function cleanup() {
    try {
      handle?.unmount?.();
    } catch (e) {
      console.error(`[plugin ${tab.pluginId}] unmount failed`, e);
    }
    handle = null;
    dispose?.();
    dispose = null;
    shown = null;
    host?.replaceChildren();
  }

  // failed dynamic imports are cached per URL by the browser, so every retry uses a fresh URL
  let attempt = 0;
  function moduleUrl(t) {
    const mod = String(t.module || 'ui.js').replace(/^\/+/, '');
    return `/plugins/${encodeURIComponent(t.pluginId)}/${mod}?v=${encodeURIComponent(t.version ?? '0')}${attempt ? `&r=${attempt}` : ''}`;
  }
  function retry() {
    attempt++;
    mountTab();
  }

  async function mountTab() {
    const my = ++token;
    cleanup();
    phase = 'loading';
    error = '';
    try {
      const mod = await import(/* @vite-ignore */ moduleUrl(tab));
      if (my !== token) return;
      const fn = tab.export ? mod[tab.export] : (mod.mount ?? mod.default?.mount ?? mod.default);
      if (typeof fn !== 'function') throw new Error(`module has no ${tab.export ?? 'mount'}() export`);
      const target = document.createElement('div');
      target.className = 'plugin-root';
      host.replaceChildren(target);
      const c = createPluginCtx(tab);
      dispose = c.dispose;
      const h = await fn(target, c.ctx);
      if (my !== token) {
        try {
          h?.unmount?.();
        } catch {}
        return;
      }
      handle = h ?? {};
      phase = 'ready';
      syncVisibility();
    } catch (e) {
      if (my !== token) return;
      console.error(`[plugin ${tab.pluginId}] failed to load tab ${tab.id}`, e);
      cleanup();
      phase = 'error';
      error = e?.message ?? String(e);
    }
  }

  function syncVisibility() {
    if (phase !== 'ready' || !handle || shown === visible) return;
    shown = visible;
    try {
      if (visible) handle.onShow?.();
      else handle.onHide?.();
    } catch (e) {
      console.error(e);
    }
  }

  // (re)mount whenever the module identity changes (ui.tabs refetches recreate the tab object)
  let mountedSig = null;
  $effect(() => {
    const sig = `${tab.pluginId}|${tab.module}|${tab.export ?? ''}|${tab.version ?? ''}`;
    if (sig === mountedSig) return;
    mountedSig = sig;
    untrack(mountTab);
  });

  $effect(() => {
    visible;
    untrack(syncVisibility);
  });

  onDestroy(() => {
    token++;
    cleanup();
  });
</script>

<div class="plugin-tab">
  {#if phase === 'loading'}
    <div class="state"><span class="np-spinner"></span> Loading {tab.title}…</div>
  {:else if phase === 'error'}
    <div class="state error">
      <Icon name="alert" size={20} />
      <div class="np-strong">{tab.title} failed to load</div>
      <div class="np-mono np-small msg">{error}</div>
      <button class="np-btn np-btn-sm" onclick={retry}><Icon name="refresh" size={13} /> Retry</button>
    </div>
  {/if}
  <div class="mount np-scroll" bind:this={host} hidden={phase !== 'ready'}></div>
</div>

<style>
  .plugin-tab {
    flex: 1;
    min-height: 0;
    display: flex;
    flex-direction: column;
  }
  .mount {
    flex: 1;
    min-height: 0;
    overflow-x: hidden; /* side panels never scroll sideways; tabs ellipsize instead */
  }
  .mount :global(.plugin-root) {
    min-height: 100%;
    display: flex;
    flex-direction: column;
    /* tabs style wider variants with @container (min-width: …) */
    container-type: inline-size;
  }
  .state {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 8px;
    padding: 32px 16px;
    color: var(--fg-dim);
    font-size: var(--fs-sm);
    text-align: center;
  }
  .state.error {
    color: var(--fg-muted);
  }
  .state.error :global(svg) {
    color: var(--err);
  }
  .msg {
    color: var(--err);
    max-width: 100%;
    overflow-wrap: anywhere;
  }
</style>
