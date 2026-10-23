<script>
  /**
   * The settings of one schema section: under their headings (SettingInfo.subgroup), greyed out while the section's
   * `….enabled` switch is off, and Reset section (asks first) when more than one is set. The advanced ones are a block of
   * their own at the end: one line names them ("Advanced settings (3): Query prefix · Timeout · Texts per request") and
   * opens them right below it; it starts open when one of them is set or the dialog was opened on one (`reveal`). The
   * Budget and Backups sections lead with their views.
   */
  import { untrack } from 'svelte';
  import Icon from '../../lib/kit/Icon.svelte';
  import SettingField from './SettingField.svelte';
  import BudgetView from './BudgetView.svelte';
  import BackupView from './BackupView.svelte';
  import { sectionLayout, sectionOff, enabledSettingOf, isSet } from '../../lib/settingsModel.js';
  import { resetSettings } from '../../lib/settings.js';
  import { confirmDialog } from '../../lib/state/ui.svelte.js';

  /**
   * title: show the section's title and help (inline on a page); a sub-page has them, and the on/off switch, in its
   * header, so there the switch's own field is left out.
   */
  let { section: s, doc, reveal = null, title = true } = $props();

  const layout = $derived(sectionLayout(s, doc));
  const advancedKeys = $derived(layout.advanced.groups.flatMap((g) => g.settings.map((st) => st.key)));
  let advancedOpen = $state(untrack(() => layout.advanced.set > 0));
  // opened on an advanced setting (a search result's place, a deep link): show it
  $effect(() => {
    if (reveal && advancedKeys.includes(reveal)) untrack(() => (advancedOpen = true));
  });
  const advancedNames = $derived(layout.advanced.groups.flatMap((g) => g.settings.map((st) => st.label ?? st.key)).join(' · '));
  const off = $derived(sectionOff(s, doc));
  const enabledKey = $derived(enabledSettingOf(s)?.key ?? null);
  const setKeys = $derived((s.settings ?? []).filter((st) => isSet(doc, st.key)).map((st) => st.key));

  async function resetAll() {
    const ok = await confirmDialog({
      title: `Reset ${s.title}?`,
      message: `${setKeys.length} setting${setKeys.length === 1 ? '' : 's'} go back to the default: ${setKeys.join(', ')}.`,
      confirmLabel: 'Reset',
      danger: true,
    });
    if (ok) await resetSettings(setKeys);
  }
</script>

<section class="sec" data-section={s.id}>
  {#if title}
    <div class="sec-title">{s.title}</div>
    {#if s.help}<div class="sec-help np-dim">{s.help}</div>{/if}
  {/if}
  {#if s.id === 'budget'}<BudgetView />{/if}
  {#if s.id === 'backup'}<BackupView />{/if}
  {#if off}<div class="off np-dim">Switched off: the settings below apply once it is on again.</div>{/if}
  {#snippet fields(groups)}
    {#each groups as g (g.title)}
      {#if g.title}<div class="sub">{g.title}</div>{/if}
      <div class="fields">
        {#each g.settings.filter((st) => title || st.key !== enabledKey) as st (st.key)}
          <SettingField setting={st} {doc} disabled={off && st.key !== enabledKey} highlight={st.key === reveal} />
        {/each}
      </div>
    {/each}
  {/snippet}
  {@render fields(layout.groups)}
  {#if layout.advanced.count}
    <div class="adv" class:open={advancedOpen}>
      <button class="adv-toggle" aria-expanded={advancedOpen} onclick={() => (advancedOpen = !advancedOpen)} data-testid="advanced-settings">
        <Icon name={advancedOpen ? 'chevron-down' : 'chevron-right'} size={13} />
        <span class="adv-title">Advanced settings ({layout.advanced.count})</span>
        {#if layout.advanced.set}<span class="adv-set">{layout.advanced.set} changed</span>{/if}
        {#if !advancedOpen}<span class="adv-names np-ellipsis">{advancedNames}</span>{/if}
      </button>
      {#if advancedOpen}<div class="adv-body">{@render fields(layout.advanced.groups)}</div>{/if}
    </div>
  {/if}
  {#if setKeys.length > 1}
    <div class="foot"><span class="np-spacer"></span><button class="link" onclick={resetAll} data-testid="reset-section">Reset section</button></div>
  {/if}
</section>

<style>
  .sec {
    margin-bottom: 18px;
  }
  .sec-title {
    margin: 10px 0 2px;
    font-size: 13.5px;
    font-weight: 600;
  }
  .sec-help {
    margin-bottom: 6px;
    font-size: var(--fs-sm);
  }
  .sub {
    margin: 14px 0 0;
    color: var(--fg-muted);
    font-size: var(--fs-xs);
    font-weight: 600;
    letter-spacing: 0.04em;
    text-transform: uppercase;
  }
  .off {
    margin: 4px 0;
    padding: 6px 8px;
    border-radius: var(--radius-sm);
    background: var(--bg-2);
    font-size: var(--fs-sm);
  }
  .adv {
    margin-top: 8px;
    border: 1px solid var(--border);
    border-radius: var(--radius);
  }
  .adv-toggle {
    display: flex;
    align-items: center;
    gap: 8px;
    width: 100%;
    min-width: 0;
    padding: 7px 10px;
    border: 0;
    border-radius: var(--radius);
    background: transparent;
    color: var(--fg-muted);
    font-size: var(--fs-sm);
    text-align: left;
    cursor: pointer;
  }
  .adv-toggle:hover {
    background: var(--bg-2);
    color: var(--fg);
  }
  .adv-title {
    flex: none;
    color: var(--fg);
    font-weight: 500;
  }
  .adv-set {
    flex: none;
    padding: 0 6px;
    border-radius: 8px;
    background: var(--accent-soft);
    color: var(--accent);
    font-size: var(--fs-xs);
  }
  .adv-names {
    flex: 1;
    min-width: 0;
    color: var(--fg-dim);
  }
  .adv.open .adv-toggle {
    border-bottom: 1px solid var(--border);
    border-radius: var(--radius) var(--radius) 0 0;
  }
  .adv-body {
    padding: 0 12px 4px;
  }
  .foot {
    display: flex;
    align-items: center;
    gap: 10px;
    padding-top: 6px;
  }
  .link {
    padding: 0;
    border: 0;
    background: transparent;
    color: var(--accent);
    font-size: var(--fs-sm);
    cursor: pointer;
  }
  .link:hover {
    text-decoration: underline;
  }
</style>
