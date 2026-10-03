// What the host UI knows about the ideas backlog (plugins/NetPI.Ideas): the vocabulary the idea dialog offers, and the one
// helper that turns the tags box into tags. The Ideas tab keeps its own copy of these in its bundle (a plugin tab does not
// import host modules); both follow plugins/NetPI.Ideas/IdeaOps.cs.
export const IDEA_STATUSES = ['open', 'planned', 'in-progress', 'parked', 'done', 'rejected'];
export const IDEA_PRIORITIES = ['high', 'medium', 'low'];
/** The most images one idea carries (IdeaImages.MaxPerIdea). */
export const IDEA_MAX_IMAGES = 6;

/** "ui, #docs  ui" → ['ui', 'docs'] (comma or space separated, a leading # dropped, no duplicates). */
export function parseTags(s) {
  const seen = new Set();
  const out = [];
  for (const raw of String(s ?? '').split(/[,\s]+/)) {
    const t = raw.replace(/^#/, '').trim();
    if (t && !seen.has(t.toLowerCase())) {
      seen.add(t.toLowerCase());
      out.push(t);
    }
  }
  return out;
}
