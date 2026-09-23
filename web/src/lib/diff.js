// Unified diff parsing for the edit/write tool views.

/**
 * Parse a unified diff into display rows:
 *   { t: 'file'|'hunk'|'meta'|'add'|'del'|'ctx', text, o?: oldLine, n?: newLine }
 * Also returns added/removed counts.
 */
export function parseDiff(text) {
  const rows = [];
  let added = 0;
  let removed = 0;
  if (!text) return { rows, added, removed };
  const lines = text.replace(/\r\n/g, '\n').split('\n');
  if (lines.length && lines[lines.length - 1] === '') lines.pop();
  let oldLn = 0;
  let newLn = 0;
  let remOld = 0;
  let remNew = 0;
  for (const raw of lines) {
    const inHunk = remOld > 0 || remNew > 0;
    if (!inHunk) {
      if (raw.startsWith('@@')) {
        const m = /^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@(.*)$/.exec(raw);
        if (m) {
          oldLn = +m[1];
          newLn = +m[3];
          remOld = m[2] == null ? 1 : +m[2];
          remNew = m[4] == null ? 1 : +m[4];
        }
        rows.push({ t: 'hunk', text: raw });
        continue;
      }
      if (raw.startsWith('--- ') || raw.startsWith('+++ ')) {
        rows.push({ t: 'file', text: raw });
        continue;
      }
      if (raw.startsWith('\\')) {
        rows.push({ t: 'meta', text: raw });
        continue;
      }
      if (/^(diff |index |new file|deleted file|similarity|rename |old mode|new mode)/.test(raw)) {
        rows.push({ t: 'meta', text: raw });
        continue;
      }
    }
    const c = raw[0];
    if (c === '+') {
      rows.push({ t: 'add', text: raw.slice(1), n: newLn++ });
      added++;
      remNew--;
    } else if (c === '-') {
      rows.push({ t: 'del', text: raw.slice(1), o: oldLn++ });
      removed++;
      remOld--;
    } else if (c === '\\') {
      rows.push({ t: 'meta', text: raw });
    } else {
      rows.push({ t: 'ctx', text: c === ' ' ? raw.slice(1) : raw, o: oldLn++, n: newLn++ });
      remOld--;
      remNew--;
    }
  }
  return { rows, added, removed };
}

/** Build a pseudo diff from edit tool arguments (used while the result is pending or has no diff). */
export function editsToDiff(args) {
  const edits = normalizeEdits(args);
  const out = [];
  edits.forEach((e, i) => {
    const oldLines = String(e.oldText ?? '').split('\n');
    const newLines = String(e.newText ?? '').split('\n');
    out.push(`@@ -${1},${oldLines.length} +${1},${newLines.length} @@ edit ${i + 1}${e.replaceAll ? ' (all)' : ''}`);
    for (const l of oldLines) out.push('-' + l);
    for (const l of newLines) out.push('+' + l);
  });
  return out.join('\n');
}

export function normalizeEdits(args) {
  if (!args || typeof args !== 'object') return [];
  let edits = args.edits;
  if (typeof edits === 'string') {
    try {
      edits = JSON.parse(edits);
    } catch {
      edits = null;
    }
  }
  if (Array.isArray(edits)) {
    return edits.map((e) => ({
      oldText: e.oldText ?? e.old_text ?? e.old_string ?? e.oldString ?? '',
      newText: e.newText ?? e.new_text ?? e.new_string ?? e.newString ?? '',
      replaceAll: e.replaceAll ?? e.replace_all,
    }));
  }
  const oldText = args.oldText ?? args.old_text ?? args.old_string ?? args.oldString;
  const newText = args.newText ?? args.new_text ?? args.new_string ?? args.newString;
  if (oldText != null || newText != null)
    return [{ oldText: oldText ?? '', newText: newText ?? '', replaceAll: args.replaceAll ?? args.replace_all }];
  return [];
}
