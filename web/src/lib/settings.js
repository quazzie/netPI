// Host settings for the settings dialog: the schema (settings.schema, declared by the host and each plugin), the
// document (settings.get) and single-key writes (settings.set; null removes the key, so the default applies again).
import { rpc } from './rpc.svelte.js';
import { toast } from './state/ui.svelte.js';

/** The value at a dotted path, or undefined. */
export function getAt(doc, key) {
  let o = doc;
  for (const k of key.split('.')) {
    if (o == null || typeof o !== 'object') return undefined;
    o = o[k];
  }
  return o;
}

/** Write one setting; resolves true when saved. */
export async function setSetting(key, value) {
  try {
    await rpc('settings.set', { path: key, value: value === undefined ? null : value });
    return true;
  } catch (e) {
    toast(`Saving ${key} failed: ${e.message}`, 'error');
    return false;
  }
}

/** The dialog's pages: Agents & budget first among the host settings, then the schema's groups. */
export function pagesOf(schema) {
  const pages = [
    { id: 'agents', title: 'Agents & budget', icon: 'bot', sections: [] },
    { id: 'profiles', title: 'Profiles', icon: 'user', sections: [] },
    { id: 'Models', title: 'Models', icon: 'cpu', sections: [] },
    { id: 'Agents', title: 'Runs', icon: 'activity', sections: [] },
    { id: 'Context', title: 'Context', icon: 'file-text', sections: [] },
    { id: 'Tools', title: 'Tools', icon: 'wrench', sections: [] },
    { id: 'Data', title: 'Data & backups', icon: 'file-text', sections: [] },
    { id: 'plugins', title: 'Plugins', icon: 'puzzle', sections: [] },
  ];
  const byId = Object.fromEntries(pages.map((p) => [p.id, p]));
  const general = [];
  for (const s of schema ?? []) {
    if (s.id === 'budget') byId.agents.sections.push(s);
    else if (s.group === 'General') general.push(s);
    else (byId[s.group] ?? byId.Tools).sections.push(s);
  }
  return { general, pages };
}

/** A setting's declared default from the schema (e.g. the built-in text of a prompt setting). */
export function settingDefault(schema, key) {
  for (const s of schema ?? []) for (const st of s.settings ?? []) if (st.key === key) return st.default;
  return undefined;
}

/** For a section's row: how many of its keys are set, and whether its `….enabled` switch is off. */
export function sectionState(section, doc) {
  let changed = 0;
  let off = false;
  for (const st of section.settings ?? []) {
    const v = getAt(doc, st.key);
    if (v === undefined || v === null) continue;
    changed++;
    if (st.type === 'bool' && st.key.endsWith('.enabled') && v === false) off = true;
  }
  return { changed, off };
}

/** Parse a list field ("a, b" or one per line) into strings. */
export function parseList(text) {
  return String(text ?? '')
    .split(/[\n,]/)
    .map((s) => s.trim())
    .filter(Boolean);
}
