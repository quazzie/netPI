// The settings search: an index of everything the dialog shows (pages, sections, settings, the UI preferences, and the
// items of the dialog's own pages: agents, profiles, providers, plugins) and a ranked match on it. Pure: no Svelte, no
// server, so web/test/settings-model.test.js runs it as it is.

const lower = (s) => String(s ?? '').toLowerCase();

/**
 * The index. `layout` is pagesOf(schema); `prefs` the UI preferences (uiPrefs.js) with their section titles; `items`
 * the custom pages' entries ({ id, page, label, sub, open }).
 * → [{ kind: 'page'|'section'|'setting'|'pref'|'item', id, page, pageTitle, section, sectionTitle, label, key, keywords,
 *      text, setting?, pref?, open? }]
 */
export function buildIndex(layout, { prefs = [], prefSections = [], items = [] } = {}) {
  const out = [];
  const pageTitle = (id) => layout.byId[id]?.title ?? id;
  for (const p of layout.pages) {
    if (p.id === 'other' && !p.sections.length) continue;
    out.push({ kind: 'page', id: `page:${p.id}`, page: p.id, pageTitle: p.title, label: p.title, text: '' });
    for (const s of p.sections) {
      out.push({ kind: 'section', id: `section:${s.id}`, page: p.id, pageTitle: p.title, section: s.id, sectionTitle: s.title, label: s.title, text: lower(s.help) });
      for (const st of s.settings ?? [])
        out.push({
          kind: 'setting',
          id: st.key,
          page: p.id,
          pageTitle: p.title,
          section: s.id,
          sectionTitle: st.subgroup ? `${s.title} › ${st.subgroup}` : s.title,
          label: st.label ?? st.key,
          key: st.key,
          keywords: (st.keywords ?? []).map(lower),
          text: lower([st.help, ...(st.options ?? []), st.placeholder].filter(Boolean).join(' ')),
          setting: st,
        });
    }
  }
  for (const pr of prefs) {
    const sec = prefSections.find((s) => s.id === pr.section);
    out.push({
      kind: 'pref',
      id: `pref:${pr.key}`,
      page: 'general',
      pageTitle: pageTitle('general'),
      section: `pref:${pr.section}`,
      sectionTitle: sec?.title ?? pr.section,
      label: pr.label,
      key: pr.key,
      keywords: (pr.keywords ?? []).map(lower),
      text: lower([pr.help, ...(pr.options ?? []).map((o) => o[1])].filter(Boolean).join(' ')),
      pref: pr,
    });
  }
  for (const it of items)
    out.push({ kind: 'item', pageTitle: pageTitle(it.page), sectionTitle: it.group ?? '', text: lower(it.sub), keywords: [], ...it });
  return out;
}

const words = (s) => lower(s).split(/[^a-z0-9.]+/).filter(Boolean);

/** How well one query word matches an entry (0: not at all). */
function wordScore(e, w) {
  const label = lower(e.label);
  let best = 0;
  if (words(label).some((x) => x.startsWith(w))) best = 10;
  else if (label.includes(w)) best = 6;
  if (e.key && lower(e.key).includes(w)) best = Math.max(best, 5);
  if (e.keywords?.some((k) => k.split(/\s+/).some((x) => x.startsWith(w)))) best = Math.max(best, 6);
  else if (e.keywords?.some((k) => k.includes(w))) best = Math.max(best, 4);
  if (lower(e.sectionTitle).includes(w)) best = Math.max(best, 3);
  if (lower(e.pageTitle).includes(w)) best = Math.max(best, 2);
  if (e.text?.includes(w)) best = Math.max(best, 1);
  return best;
}

const KIND_BONUS = { page: 3, section: 2, item: 1, setting: 0, pref: 0 };

/**
 * The entries that match every word of `query`, best first (ties keep the index order, which is the dialog's order). A
 * query that is a key (`shell.timeoutSeconds`) puts that setting first. `filter(entry)` narrows further (Changed).
 */
export function search(index, query, { filter = null, limit = 200 } = {}) {
  const q = lower(query).trim();
  const ws = words(q);
  const scored = [];
  index.forEach((e, i) => {
    if (filter && !filter(e)) return;
    if (!ws.length) {
      if (filter) scored.push({ e, s: 0, i });
      return;
    }
    let s = 0;
    for (const w of ws) {
      const ws1 = wordScore(e, w);
      if (!ws1) return;
      s += ws1;
    }
    if (e.key && lower(e.key) === q) s += 50;
    else if (e.key && q.includes('.') && lower(e.key).startsWith(q)) s += 20;
    if (lower(e.label) === q) s += 8;
    scored.push({ e, s: s + (KIND_BONUS[e.kind] ?? 0), i });
  });
  scored.sort((a, b) => b.s - a.s || a.i - b.i);
  return scored.slice(0, limit).map((x) => x.e);
}

/**
 * Results for the page: the pages, sections and items they hit as links, and the settings grouped by section in the
 * order of their best entry. → { links: entries, groups: [{ key, page, pageTitle, section, sectionTitle, entries }] }
 */
export function groupResults(results) {
  const links = [];
  const groups = [];
  const byKey = new Map();
  for (const e of results) {
    if (e.kind !== 'setting' && e.kind !== 'pref') {
      links.push(e);
      continue;
    }
    const k = `${e.page}|${e.sectionTitle}`;
    let g = byKey.get(k);
    if (!g) {
      g = { key: k, page: e.page, pageTitle: e.pageTitle, section: e.section, sectionTitle: e.sectionTitle, entries: [] };
      byKey.set(k, g);
      groups.push(g);
    }
    g.entries.push(e);
  }
  return { links, groups };
}
