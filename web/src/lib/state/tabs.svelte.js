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
  // Side-panel tabs as the user arranged them: `layout.tabs` holds each side's keys in order (written by a drag). A
  // listed key overrides the tab's own panel; tabs not listed (new plugins) follow on their own panel, sorted by order.
  // Keys of tabs that are not registered stay in the layout, so a plugin that comes back returns to its place.
  placed = $derived.by(() => {
    const side = this.all.filter((t) => t.panel === 'left' || t.panel === 'right');
    const byKey = new Map(side.map((t) => [t.key, t]));
    const out = { left: [], right: [] };
    const taken = new Set();
    for (const s of ['left', 'right'])
      for (const k of layout.tabs?.[s] ?? []) {
        const t = byKey.get(k);
        if (t && !taken.has(k)) {
          out[s].push(t);
          taken.add(k);
        }
      }
    for (const t of side.filter((x) => !taken.has(x.key)).sort(byOrder)) out[t.panel].push(t);
    return out;
  });
  left = $derived(this.placed.left);
  right = $derived(this.placed.right);
  session = $derived(this.all.filter((t) => t.panel === 'session').sort(byOrder));
}

export const tabs = new TabRegistry();

/** Register a built-in tab: { key, title, icon, panel, order, component } */
export function registerCoreTab(tab) {
  tabs.core = [...tabs.core.filter((t) => t.key !== tab.key), tab];
}

/** The side panel a tab sits in now ('left' | 'right'), or null for a session view or an unknown key. */
export function sideOf(key) {
  if (tabs.left.some((t) => t.key === key)) return 'left';
  if (tabs.right.some((t) => t.key === key)) return 'right';
  return null;
}

/** Show a tab by key ("pluginId/tabId"), expanding its panel. */
export function openPanelTab(key) {
  const s = sideOf(key);
  if (!s) return false;
  layout[s].active = key;
  layout[s].collapsed = false;
  saveLayout();
  return true;
}

/** The tab being dragged between the side panels ({ key } while a drag runs): both panels show a drop target. */
export const tabDrag = $state({ key: null });

/** Move a side-panel tab to `side` before the tab now at `index` of that side's list (its length: at the end), show it
 *  there, and save the whole arrangement. */
export function moveTab(key, side, index) {
  const from = sideOf(key);
  if (!from || (side !== 'left' && side !== 'right')) return;
  const keys = { left: tabs.left.map((t) => t.key), right: tabs.right.map((t) => t.key) };
  const old = keys[from].indexOf(key);
  if (from === side && old < index) index--;
  keys[from].splice(old, 1);
  keys[side].splice(Math.max(0, Math.min(index, keys[side].length)), 0, key);
  // keep unregistered keys (a plugin that is off right now) where they were
  const saved = layout.tabs ?? { left: [], right: [] };
  const live = new Set([...keys.left, ...keys.right]);
  for (const s of ['left', 'right']) keys[s].push(...(saved[s] ?? []).filter((k) => !live.has(k)));
  layout.tabs = keys;
  if (from !== side && layout[from].active === key) layout[from].active = null;
  layout[side].active = key;
  layout[side].collapsed = false;
  saveLayout();
}

/** Put every side-panel tab back on the panel and in the order its owner gave it. */
export function resetTabPlacement() {
  layout.tabs = { left: [], right: [] };
  saveLayout();
}
