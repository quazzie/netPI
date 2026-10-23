// The settings dialog's model, without Svelte or the server: its pages and where each section goes, how a section is
// laid out, the dialog's targets (a page, a section, a key, a search), and the keys a settings document holds that no
// section declares. settings.js adds the writes; settingsSearch.js the search.

/** The value at a dotted path, or undefined. */
export function getAt(doc, key) {
  let o = doc;
  for (const k of key.split('.')) {
    if (o == null || typeof o !== 'object') return undefined;
    o = o[k];
  }
  return o;
}

/**
 * The dialog's pages in nav order. `group` is the schema's SettingsSection.Group a page lists (the plugins choose it);
 * a page without one is built by the dialog itself. `nav` is the heading it is listed under.
 */
export const PAGES = [
  { id: 'general', title: 'General', icon: 'sliders', nav: '', group: 'General' },
  { id: 'models', title: 'Models', icon: 'cpu', nav: 'AI', group: 'Models' },
  { id: 'agents', title: 'Agents & budget', icon: 'bot', nav: 'AI', group: 'Agents' },
  { id: 'profiles', title: 'Profiles', icon: 'user', nav: 'AI' },
  { id: 'runs', title: 'Runs', icon: 'activity', nav: 'AI', group: 'Runs' },
  { id: 'context', title: 'Context', icon: 'file-text', nav: 'AI', group: 'Context' },
  { id: 'tools', title: 'Tools', icon: 'wrench', nav: 'Capabilities', group: 'Tools' },
  { id: 'knowledge', title: 'Knowledge', icon: 'brain', nav: 'Capabilities', group: 'Knowledge' },
  { id: 'automation', title: 'Automation', icon: 'zap', nav: 'Capabilities', group: 'Automation' },
  { id: 'safety', title: 'Safety', icon: 'shield', nav: 'Capabilities', group: 'Safety' },
  { id: 'data', title: 'Data & backups', icon: 'drive', nav: 'System', group: 'Data' },
  { id: 'plugins', title: 'Plugins', icon: 'puzzle', nav: 'System' },
  { id: 'advanced', title: 'Advanced', icon: 'settings', nav: 'System', group: 'Advanced' },
  { id: 'other', title: 'Other', icon: 'layers', nav: 'System' },
  { id: 'json', title: 'settings.json', icon: 'file', nav: 'System' },
  { id: 'about', title: 'About', icon: 'info', nav: 'System' },
];

/** Pages the dialog fills itself: listed even without a schema section. */
const OWN = new Set(['general', 'models', 'agents', 'profiles', 'tools', 'plugins', 'advanced', 'json', 'about']);

/**
 * The pages with their sections from the schema (in schema order: the host sorts by page, then order). A provider kind
 * (providerType) is no section: Models offers it under Add provider. A group no page lists goes to Other.
 * → { pages: [{ ...page, sections }], byId, sections: Map(id → { section, page }) }
 */
export function pagesOf(schema) {
  const pages = PAGES.map((p) => ({ ...p, sections: [] }));
  const byId = Object.fromEntries(pages.map((p) => [p.id, p]));
  const byGroup = Object.fromEntries(pages.filter((p) => p.group).map((p) => [p.group, p]));
  const sections = new Map();
  for (const s of schema ?? []) {
    if (s.providerType) continue;
    const page = byGroup[s.group] ?? byId.other;
    page.sections.push(s);
    sections.set(s.id, { section: s, page });
  }
  return { pages: pages.filter((p) => OWN.has(p.id) || p.sections.length), byId, sections };
}

/** The nav: pages under their headings, in order → [{ heading, pages }]. */
export function navOf(pages) {
  const out = [];
  for (const p of pages) {
    if (out.at(-1)?.heading !== p.nav) out.push({ heading: p.nav, pages: [] });
    out.at(-1).pages.push(p);
  }
  return out;
}

/** A setting's declared default from the schema (e.g. the built-in text of a prompt setting). */
export function settingDefault(schema, key) {
  for (const s of schema ?? []) for (const st of s.settings ?? []) if (st.key === key) return st.default;
  return undefined;
}

export const isSet = (doc, key) => {
  const v = getAt(doc, key);
  return v !== undefined && v !== null;
};

/**
 * The section's on/off switch: its bool setting `<name>.enabled` (one level: `webhooks.enabled`), or null. A deeper one
 * (`ideas.autoRefine.enabled`) switches a part of the section, not the section.
 */
export function enabledSettingOf(section) {
  return (section?.settings ?? []).find((st) => st.type === 'bool' && /^[^.]+\.enabled$/.test(st.key)) ?? null;
}

/** Whether the section's switch is off (set false, or false by default). */
export function sectionOff(section, doc) {
  const st = enabledSettingOf(section);
  if (!st) return false;
  const v = getAt(doc, st.key);
  return (v ?? st.default) === false;
}

/** For a section's row: how many of its keys are set, and whether its switch is off. */
export function sectionState(section, doc) {
  let changed = 0;
  for (const st of section.settings ?? []) if (isSet(doc, st.key)) changed++;
  return { changed, off: sectionOff(section, doc) };
}

/**
 * How a section is shown: its settings under their headings (subgroup; null: no heading; in the order they first appear,
 * the ones without a heading first), and the advanced ones apart, in a block of their own at the end, grouped the same
 * way. `set`: how many of the advanced ones are set (the block then starts open, and its line says so).
 * → { groups: [{ title, settings }], advanced: { groups, count, set } }
 */
export function sectionLayout(section, doc) {
  const group = (settings) => {
    const groups = [];
    for (const st of settings) {
      const title = st.subgroup ?? null;
      let g = groups.find((x) => x.title === title);
      if (!g) groups.push((g = { title, settings: [] }));
      g.settings.push(st);
    }
    return groups.sort((a, b) => (a.title === null ? -1 : 0) - (b.title === null ? -1 : 0));
  };
  const all = section.settings ?? [];
  const adv = all.filter((st) => st.advanced);
  return {
    groups: group(all.filter((st) => !st.advanced)),
    advanced: { groups: group(adv), count: adv.length, set: adv.filter((st) => isSet(doc, st.key)).length },
  };
}

/**
 * Whether a section is shown in its page (small: a few plain settings) or as a row that opens it. A text area counts
 * twice; a section with headings or with more than three visible settings is a row.
 */
export function sectionInline(section) {
  const visible = (section.settings ?? []).filter((st) => !st.advanced);
  if (visible.some((st) => st.subgroup)) return false;
  const weight = visible.reduce((n, st) => n + (st.type === 'text' ? 2 : 1), 0);
  return weight <= 3;
}

/** The first sentence of a help text, and whether there is more. */
export function helpParts(help) {
  const text = String(help ?? '').trim();
  const m = /^(.+?[.!?])(\s+)(?=[A-Z0-9(])/s.exec(text);
  if (!m || m[1].length < 24 || text.length - m[1].length < 24) return { first: text, rest: '' };
  return { first: m[1], rest: text.slice(m[1].length + m[2].length) };
}

/**
 * Where the dialog opens: `true` (where it was), a page id, or { page?, section?, key?, q? }. A key names its section
 * and page, a section its page. → { page, section, key, q } (missing parts null)
 */
export function resolveTarget(target, layout, prefKeys = []) {
  const t = typeof target === 'string' ? { page: target } : target && typeof target === 'object' ? target : {};
  let { page = null, section = null, key = null, q = null } = t;
  if (key && !section) {
    if (prefKeys.includes(key)) page ??= 'general';
    else
      for (const [id, { section: s }] of layout.sections)
        if ((s.settings ?? []).some((st) => st.key === key)) {
          section = id;
          break;
        }
  }
  if (section && !page) page = layout.sections.get(section)?.page.id ?? null;
  if (page && !layout.pages.some((p) => p.id === page)) page = null;
  if (section && !layout.sections.has(section)) section = null;
  return { page, section, key, q: q ? String(q) : null };
}

/**
 * Roots of settings that are maps of the owner's own entries, or are edited elsewhere (the agents, profiles, providers,
 * MCP servers, the UI state, the plugin and tool lists, the storage): not declared key by key, never "unknown".
 */
export const DYNAMIC_ROOTS = ['profiles', 'providers', 'mcp.servers', 'ui', 'plugins', 'tools', 'storage'];
/** Roots whose object entries are the owner's (an agent is `agents.<id>: { model, … }`); a plain value there is a setting. */
export const ENTRY_ROOTS = ['agents'];

/**
 * The keys of `doc` that no section declares and no dynamic root covers: a typo, a leftover of a removed setting, or the
 * setting of a plugin that is switched off. Leaves are values that are not objects. → [{ key, value }]
 */
export function unknownKeys(doc, schema) {
  const declared = new Set();
  for (const s of schema ?? []) if (!s.providerType) for (const st of s.settings ?? []) declared.add(st.key);
  const covered = (path) => {
    if (DYNAMIC_ROOTS.some((r) => path === r || path.startsWith(r + '.'))) return true;
    for (let p = path; p; p = p.includes('.') ? p.slice(0, p.lastIndexOf('.')) : '') if (declared.has(p)) return true;
    return false;
  };
  const out = [];
  const walk = (o, prefix) => {
    for (const [k, v] of Object.entries(o ?? {})) {
      const path = prefix ? `${prefix}.${k}` : k;
      if (covered(path)) continue;
      if (ENTRY_ROOTS.includes(prefix) && v && typeof v === 'object' && !Array.isArray(v)) continue;
      if (v && typeof v === 'object' && !Array.isArray(v) && Object.keys(v).length) walk(v, path);
      else out.push({ key: path, value: v });
    }
  };
  walk(doc, '');
  return out;
}

/** Parse a list field ("a, b" or one per line) into strings. */
export function parseList(text) {
  return String(text ?? '')
    .split(/[\n,]/)
    .map((s) => s.trim())
    .filter(Boolean);
}

/** An IPv6 address as text: hex groups of 1-4 digits, eight of them, or fewer with one `::` for the rest (an IPv4 tail is not taken). */
function isIPv6(addr) {
  if (!/^[0-9a-f:]+$/i.test(addr) || !addr.includes(':')) return false;
  const halves = addr.split('::');
  if (halves.length > 2) return false;
  const groups = (h) => (h === '' ? [] : h.split(':'));
  const all = halves.flatMap(groups);
  if (!all.every((g) => /^[0-9a-f]{1,4}$/i.test(g))) return false;
  return halves.length === 2 ? all.length < 8 : all.length === 8;
}

/** An address or a CIDR range (IPv4 or IPv6) as `webhooks.allowFrom` / `phone.allowFrom` take it; null when valid. */
export function cidrProblem(text) {
  const t = String(text ?? '').trim();
  const [addr, bits, extra] = t.split('/');
  if (extra !== undefined || !addr) return 'an address, or an address/bits range';
  const v4 = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(addr);
  const v6 = !v4 && isIPv6(addr);
  if (v4 && v4.slice(1).some((n) => +n > 255)) return 'each part of an IPv4 address is 0–255';
  if (!v4 && !v6) return 'not an IPv4 or IPv6 address';
  if (bits !== undefined && !(/^\d+$/.test(bits) && +bits <= (v4 ? 32 : 128))) return `bits are 0–${v4 ? 32 : 128}`;
  return null;
}

/**
 * The tools.deferred list that defers exactly `deferred` among `names`: every tool by default, the others as
 * exceptions, so a tool that comes later is deferred as with the built-in default.
 */
export function deferredList(names, deferred) {
  const keep = names.filter((n) => !deferred.has(n)).sort();
  return ['*', ...keep.map((n) => `!${n}`)];
}

/**
 * What a picker setting offers (types `pick`: an array; `pickText`: a comma-separated text, as decide.model reads it). Its
 * `options` name the candidates: `@models` (the local models the model list has now), `@tools` (NetPI's tools), `@mcpTools`
 * (the MCP servers' own tool names), `@plugins` (`plugin:<id>`), `rpc:<method>` (a read-only method that answers names),
 * `=value` (a value that stands alone: default, none), or a plain value (a fixed candidate). `max` 1: one name.
 * → { sources: string[], fixed: string[], exclusive: string[], max }
 */
export function pickSpec(setting) {
  const sources = [];
  const fixed = [];
  const exclusive = [];
  for (const o of setting?.options ?? []) {
    if (o.startsWith('@') || o.startsWith('rpc:')) sources.push(o);
    else if (o.startsWith('=')) {
      exclusive.push(o.slice(1));
      fixed.push(o.slice(1));
    } else fixed.push(o);
  }
  return { sources, fixed, exclusive, max: setting?.max ?? 0 };
}

/** A picker setting's value as names: an array as it is, a text split at commas. */
export function pickValue(setting, value) {
  if (Array.isArray(value)) return value.map(String);
  if (typeof value === 'string') return value.split(',').map((s) => s.trim()).filter(Boolean);
  return [];
}

/** Names back to the value the setting stores: null when none (the default applies), a text for `pickText`. */
export function pickStore(setting, names) {
  if (!names.length) return null;
  return setting.type === 'pickText' ? names.join(', ') : names;
}
