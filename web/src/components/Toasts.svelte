<script>
  import Icon from '../lib/kit/Icon.svelte';
  import { toasts, dismissToast } from '../lib/state/ui.svelte.js';
  const ICON = { info: 'info', warn: 'alert', error: 'alert-circle', ok: 'circle-check' };
</script>

<div class="toasts" aria-live="polite">
  {#each toasts as t (t.id)}
    <div class="toast" data-level={t.level} role={t.level === 'error' ? 'alert' : 'status'}>
      <Icon name={ICON[t.level] ?? 'info'} size={15} />
      <span class="text">{t.text}</span>
      <button class="x" aria-label="Dismiss" onclick={() => dismissToast(t.id)}><Icon name="x" size={12} /></button>
    </div>
  {/each}
</div>

<style>
  .toasts {
    position: fixed;
    right: 16px;
    bottom: 16px;
    z-index: 100;
    display: flex;
    flex-direction: column;
    gap: 8px;
    align-items: flex-end;
    pointer-events: none;
  }
  .toast {
    pointer-events: auto;
    display: flex;
    align-items: flex-start;
    gap: 9px;
    max-width: 420px;
    min-width: 220px;
    padding: 9px 8px 9px 12px;
    border: 1px solid var(--border-strong);
    border-radius: var(--radius);
    background: var(--bg-2);
    color: var(--fg);
    box-shadow: var(--shadow);
    font-size: var(--fs);
    animation: toast-in var(--t) var(--ease);
  }
  .toast :global(svg:first-child) {
    margin-top: 1px;
    color: var(--info);
  }
  .toast[data-level='warn'] :global(svg:first-child) {
    color: var(--warn);
  }
  .toast[data-level='error'] {
    border-color: color-mix(in srgb, var(--err) 45%, var(--border-strong));
  }
  .toast[data-level='error'] :global(svg:first-child) {
    color: var(--err);
  }
  .toast[data-level='ok'] :global(svg:first-child) {
    color: var(--ok);
  }
  .text {
    flex: 1;
    min-width: 0;
    overflow-wrap: anywhere;
    line-height: 1.4;
  }
  .x {
    display: grid;
    place-items: center;
    width: 20px;
    height: 20px;
    padding: 0;
    border: 0;
    border-radius: 4px;
    background: transparent;
    color: var(--fg-dim);
  }
  .x:hover {
    background: var(--bg-3);
    color: var(--fg);
  }
  @keyframes toast-in {
    from {
      opacity: 0;
      transform: translateY(6px) scale(0.98);
    }
  }
</style>
