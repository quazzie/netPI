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

/** The dialog's pages: Lanes & budget first among the host settings, then the schema's groups. */
export function pagesOf(schema) {
  const pages = [
    { id: 'lanes', title: 'Lanes & budget', icon: 'layers', sections: [] },
    { id: 'Models', title: 'Models', icon: 'cpu', sections: [] },
    { id: 'Agents', title: 'Agents', icon: 'bot', sections: [] },
    { id: 'Context', title: 'Context', icon: 'file-text', sections: [] },
    { id: 'Tools', title: 'Tools & plugins', icon: 'wrench', sections: [] },
  ];
  const byId = Object.fromEntries(pages.map((p) => [p.id, p]));
  const general = [];
  for (const s of schema ?? []) {
    if (s.id === 'lanes' || s.id === 'budget') byId.lanes.sections.push(s);
    else if (s.group === 'General') general.push(s);
    else (byId[s.group] ?? byId.Tools).sections.push(s);
  }
  return { general, pages };
}

/** Parse a list field ("a, b" or one per line) into strings. */
export function parseList(text) {
  return String(text ?? '')
    .split(/[\n,]/)
    .map((s) => s.trim())
    .filter(Boolean);
}
