// Tab registry. Core tabs (Sessions, Projects) and plugin tabs (ui.tabs) share one list; a tab's key is
// "<pluginId>/<tabId>" ("core/sessions" for the built-ins). Side panel tabs sit left or right; a "session" tab is a
// view of one chat, shown in the chat's own area (the chat header switches to it, the `ui.open` event opens it).
import { app } from './app.svelte.js';
import { layout, saveLayout } from './ui.svelte.js';

const byOrder = (a, b) => (a.order ?? 100) - (b.order ?? 100) || a.title.localeCompare(b.title);

function normPanel(p) {
  if (p === 0) return 'left';
  if (p === 1) return 'right';
  if (p === 2) return 'session';
  const v = String(p ?? 'right').toLowerCase();
  return v === 'left' || v === 'session' ? v : 'right';
}

class TabRegistry {
  core = $state.raw([]);
  plugin = $derived(
    (app.uiTabs ?? []).map((t) => ({
      key: `${t.pluginId}/${t.id}`,
      title: t.title,
      icon: t.icon,
      panel: normPanel(t.panel),
      order: t.order ?? 100,
      plugin: t,
    })),
  );
  all = $derived([...this.core, ...this.plugin]);
  left = $derived(this.all.filter((t) => t.panel === 'left').sort(byOrder));
  right = $derived(this.all.filter((t) => t.panel === 'right').sort(byOrder));
  session = $derived(this.all.filter((t) => t.panel === 'session').sort(byOrder));
}

export const tabs = new TabRegistry();

/** Register a built-in tab: { key, title, icon, panel, order, component } */
export function registerCoreTab(tab) {
  tabs.core = [...tabs.core.filter((t) => t.key !== tab.key), tab];
}

/** Show a tab by key ("pluginId/tabId"), expanding its panel. */
export function openPanelTab(key) {
  const t = tabs.all.find((x) => x.key === key);
  if (!t) return false;
  const side = layout[t.panel];
  side.active = key;
  side.collapsed = false;
  saveLayout();
  return true;
}
