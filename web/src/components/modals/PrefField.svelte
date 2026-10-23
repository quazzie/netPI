<script>
  /**
   * One UI preference (lib/uiPrefs.js) as a control, laid out like a SettingField: a choice as segments, a bool as a
   * check box, and the zoom (the desktop app's WebView zoom through desktop.zoom; a browser's own zoom otherwise). A
   * preference that differs from its default has the dot and a Reset.
   */
  import { helpParts } from '../../lib/settingsModel.js';
  import { prefChanged } from '../../lib/uiPrefs.js';
  import { prefs, savePrefs, toast } from '../../lib/state/ui.svelte.js';
  import { app } from '../../lib/state/app.svelte.js';
  import { rpc } from '../../lib/rpc.svelte.js';

  let { pref: p, highlight = false } = $props();

  const help = $derived(helpParts(p.help));
  let more = $state(false);
  const changed = $derived(prefChanged(p, prefs));

  function set(v) {
    prefs[p.key] = v;
    savePrefs();
  }

  // desktop app: the WebView zoom (the desktop shell remembers it in window.json)
  let zoom = $state(null);
  $effect(() => {
    if (p.type !== 'zoom' || !app.info?.desktop) return;
    rpc('desktop.zoom', {})
      .then((r) => (zoom = r?.factor ?? 1))
      .catch(() => {});
  });
  async function setZoom(factor) {
    const f = Math.round(Math.min(3, Math.max(0.5, factor)) * 100) / 100;
    try {
      zoom = (await rpc('desktop.zoom', { factor: f }))?.factor ?? f;
    } catch (e) {
      toast(`Zoom failed: ${e.message}`, 'error');
    }
  }
</script>

<div class="field" data-pref={p.key} class:flash={highlight}>
  <div class="lbl">
    <div class="name">{p.label}{#if changed}<span class="dot" title="Changed from the default"></span>{/if}</div>
    {#if p.type === 'zoom'}
      <div class="help">
        {#if app.info?.desktop}Also Ctrl + mouse wheel, Ctrl + / − / 0; NetPI remembers it.{:else}Use the browser's zoom (Ctrl + / −); it is remembered per site.{/if}
      </div>
    {:else if p.help}
      <div class="help">
        {help.first}{#if help.rest && more}{' '}{help.rest}{/if}
        {#if help.rest}<button class="more" onclick={() => (more = !more)} aria-expanded={more}>{more ? 'Less' : 'More'}</button>{/if}
      </div>
    {/if}
  </div>
  <div class="ctl">
    {#if p.type === 'choice'}
      <div class="np-seg">
        {#each p.options as [v, l] (l)}
          <button aria-pressed={prefs[p.key] === v} onclick={() => set(v)}>{l}</button>
        {/each}
      </div>
    {:else if p.type === 'bool'}
      <input type="checkbox" class="check" checked={!!prefs[p.key]} onchange={(e) => set(e.currentTarget.checked)} aria-label={p.label} />
    {:else if p.type === 'zoom' && app.info?.desktop}
      <div class="np-seg zoom">
        <button title="Smaller" onclick={() => setZoom(zoom - 0.1)} disabled={zoom == null}>−</button>
        <button title="Reset to 100%" onclick={() => setZoom(1)} disabled={zoom == null}>{zoom == null ? '…' : `${Math.round(zoom * 100)}%`}</button>
        <button title="Larger" onclick={() => setZoom(zoom + 0.1)} disabled={zoom == null}>+</button>
      </div>
    {/if}
    {#if changed}<button class="reset" title="Back to the default" onclick={() => set(p.default)}>Reset</button>{/if}
  </div>
</div>

<style>
  .field {
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto;
    gap: 4px 16px;
    align-items: center;
    padding: 10px 0;
    border-bottom: 1px solid var(--border);
  }
  .field:last-child {
    border-bottom: 0;
  }
  .name {
    font-size: var(--fs);
  }
  .dot {
    display: inline-block;
    width: 6px;
    height: 6px;
    margin-left: 6px;
    border-radius: 50%;
    background: var(--accent);
    vertical-align: middle;
  }
  .help {
    margin-top: 2px;
    color: var(--fg-dim);
    font-size: var(--fs-sm);
  }
  .more {
    padding: 0;
    border: 0;
    background: transparent;
    color: var(--accent);
    font-size: inherit;
    cursor: pointer;
  }
  .ctl {
    display: flex;
    align-items: center;
    justify-content: flex-end;
    gap: 6px;
  }
  .check {
    accent-color: var(--accent);
    width: 16px;
    height: 16px;
  }
  .zoom button {
    min-width: 34px;
    font-variant-numeric: tabular-nums;
  }
  .reset {
    border: 0;
    background: transparent;
    color: var(--fg-dim);
    font-size: var(--fs-xs);
    text-decoration: underline;
    cursor: pointer;
  }
  .reset:hover {
    color: var(--fg);
  }
  .flash {
    animation: flash 1.8s var(--ease);
  }
  @keyframes flash {
    0%,
    45% {
      background: var(--accent-soft);
    }
  }
  @media (max-width: 640px) {
    .field {
      grid-template-columns: minmax(0, 1fr);
    }
    .ctl {
      justify-content: flex-start;
      flex-wrap: wrap;
    }
  }
</style>
