<script>
  /**
   * The settings of one schema section: under their headings (SettingInfo.subgroup), the advanced ones behind "More
   * settings" unless set or the dialog was opened on one (`reveal`), the others greyed out while the section's
   * `….enabled` switch is off, and Reset section (asks first) when any is set. The Budget and Backups sections lead with
   * their views.
   */
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

  let all = $state(false);
  const layout = $derived(sectionLayout(s, doc, { all, reveal }));
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
  {#each layout.groups as g (g.title)}
    {#if g.title}<div class="sub">{g.title}</div>{/if}
    <div class="fields">
      {#each g.settings.filter((st) => title || st.key !== enabledKey) as st (st.key)}
        <SettingField setting={st} {doc} disabled={off && st.key !== enabledKey} highlight={st.key === reveal} />
      {/each}
    </div>
  {/each}
  <div class="foot">
    {#if layout.hidden}
      <button class="link" onclick={() => (all = true)} data-testid="more-settings">More settings ({layout.hidden})</button>
    {:else if all}
      <button class="link" onclick={() => (all = false)}>Fewer settings</button>
    {/if}
    <span class="np-spacer"></span>
    {#if setKeys.length > 1}<button class="link" onclick={resetAll} data-testid="reset-section">Reset section</button>{/if}
  </div>
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
  .foot {
    display: flex;
    align-items: center;
    gap: 10px;
    min-height: 4px;
  }
  .foot:has(button) {
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
