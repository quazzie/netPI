<script>
  /** One row of a settings list that opens its own dialog: a title, a line under it, badges and a chevron. */
  import Icon from '../../lib/kit/Icon.svelte';

  let { title, subtitle = '', badges = [], icon = null, mono = false, onclick, ...rest } = $props();
</script>

<button type="button" class="setting-row" {onclick} {...rest}>
  {#if icon}<span class="ic"><Icon name={icon} size={14} /></span>{/if}
  <span class="main">
    <span class="t np-ellipsis" class:np-mono={mono}>{title}</span>
    {#if subtitle}<span class="sub np-ellipsis">{subtitle}</span>{/if}
  </span>
  {#each badges.filter(Boolean) as b, i (i)}
    <span class="badge" data-tone={b.tone ?? 'muted'}>{b.text}</span>
  {/each}
  <span class="chev"><Icon name="chevron-right" size={13} /></span>
</button>

<style>
  .setting-row {
    display: flex;
    align-items: center;
    gap: 10px;
    width: 100%;
    min-height: 46px;
    padding: 7px 10px;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--bg-1);
    color: var(--fg);
    text-align: left;
  }
  .setting-row:hover {
    border-color: var(--border-strong);
    background: var(--bg-2);
  }
  .ic {
    display: grid;
    flex: none;
    color: var(--fg-dim);
  }
  .main {
    display: flex;
    flex-direction: column;
    flex: 1;
    min-width: 0;
    line-height: 1.3;
  }
  .t {
    font-weight: 600;
  }
  .sub {
    color: var(--fg-dim);
    font-size: var(--fs-sm);
  }
  .badge {
    flex: none;
    padding: 1px 7px;
    border-radius: 9px;
    background: var(--bg-2);
    color: var(--fg-muted);
    font-size: var(--fs-xs);
    white-space: nowrap;
  }
  .badge[data-tone='ok'] {
    color: var(--ok);
  }
  .badge[data-tone='warn'] {
    color: var(--warn);
  }
  .badge[data-tone='accent'] {
    background: var(--accent-soft);
    color: var(--accent);
  }
  .chev {
    display: grid;
    flex: none;
    color: var(--fg-dim);
  }
</style>
