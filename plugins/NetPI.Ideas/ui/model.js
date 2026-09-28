export const STATUSES = ['open', 'planned', 'in-progress', 'parked', 'done', 'rejected'];
export const ACTIVE = new Set(['open', 'planned', 'in-progress']);
export const PRIORITIES = ['high', 'medium', 'low'];
export const KINDS = ['note', 'research', 'plan', 'requirements', 'design', 'decision', 'blocker', 'links', 'todo'];

export const STATUS_TONE = {
  open: 'info',
  planned: 'accent',
  'in-progress': 'warn',
  parked: undefined,
  done: 'ok',
  rejected: 'err',
};

// The list reads top to bottom as: what is moving, what is next, what is still open — then the closed ones
// behind a count line. Grouping beats sorting for this: the status is where an idea belongs, and a sorted
// backlog still shows it on every card.
export const GROUP_ORDER = ['in-progress', 'planned', 'open'];
export const CLOSED_ORDER = ['parked', 'done', 'rejected'];

export const KIND_ICON = {
  note: 'file-text',
  research: 'search',
  plan: 'list',
  requirements: 'check',
  design: 'layers',
  decision: 'circle-check',
  blocker: 'alert',
  links: 'link',
  todo: 'list-tree',
};

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

/** Does the idea match every word of the query (title, summary, tags, sections)? */
export function matches(idea, q) {
  if (!q) return true;
  const hay = [idea.title, idea.summary, ...(idea.tags ?? []), ...(idea.sections ?? []).flatMap((s) => [s.title, s.content])]
    .filter(Boolean)
    .join('\n')
    .toLowerCase();
  return q
    .toLowerCase()
    .split(/\s+/)
    .filter(Boolean)
    .every((w) => hay.includes(w));
}
