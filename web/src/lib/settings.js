// Host settings for the settings dialog: the schema (settings.schema, declared by the host and each plugin), the
// document (settings.get) and single-key writes (settings.set; null removes the key, so the default applies again).
// The pure part (pages, layout, targets, unknown keys) is settingsModel.js.
import { rpc } from './rpc.svelte.js';
import { toast } from './state/ui.svelte.js';

export { getAt, pagesOf, settingDefault, sectionState, parseList, parseNumber } from './settingsModel.js';

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

/** Remove several keys (Reset section): one settings.set each, in order; resolves true when all were saved. */
export async function resetSettings(keys) {
  let ok = true;
  for (const k of keys) ok = (await setSetting(k, null)) && ok;
  return ok;
}
