// Pure helpers for the Autonomy tab. No Svelte, so Node can test them (web/test/autonomy-model.test.js).

/** The files a journal row kept copies of: one checkpoint, or several under `files`. */
export function checkpointFiles(row) {
  const cp = row?.checkpoint;
  if (!cp) return [];
  return Array.isArray(cp.files) ? cp.files : [cp];
}

/** What the row's undo button is: 'none' (no copy kept), 'undone', or 'ready'. */
export function undoState(row) {
  const files = checkpointFiles(row);
  if (files.length === 0) return 'none';
  return files.some((f) => f?.undoneAt) ? 'undone' : 'ready';
}

/** A refusal's state: 'granted' (a grant answers it now), 'grantable', or 'never' (what outside text may never do). */
export function refusalState(r) {
  if (r?.grantedMs != null) return 'granted';
  return r?.grantable ? 'grantable' : 'never';
}

/** The refusals the owner can still act on: grantable and not granted yet. */
export function openRefusals(list) {
  return (list ?? []).filter((r) => refusalState(r) === 'grantable').length;
}

/** The second line of a row: who it worked for, and the chat's title when it is not just the origin again. */
export function whoLine(row) {
  const label = row?.label ?? row?.origin ?? '';
  const title = (row?.title ?? '').trim();
  const name = label.replace(/^(schedule|webhook) /, '');
  return title && !title.includes(name) ? `${label} · ${title}` : label;
}

/** Why a refusal happened, in a few words. */
export function refusalWhy(r) {
  if (r?.why === 'tainted') return `read outside text (${r.rule ?? 'autonomy.taintedBlocked'})`;
  return r?.rule ? `asks first: ${r.rule}` : 'asks first';
}
