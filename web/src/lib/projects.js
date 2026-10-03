// What the projects panel (the left tab) and the projects dialog both show and do, in one place: the session count on a
// project row, the filter and order they list in, and removing a project. Each of the three was written out twice,
// which is how a row count and a remove dialog could drift apart.
//
// The two list helpers take the state they read, so a view keeps them reactive with its own
// `$derived.by(() => matchingProjects(app.projects, q))`. removeProject is the flow itself: ask, delete, say what
// happened — a caller only has to know whether the project is gone.
import { deleteProject } from './state/app.svelte.js';
import { confirmDialog, toast } from './state/ui.svelte.js';

/**
 * Sessions per project, for the counts a project row shows: its own chats, not an archived one and not a subagent's
 * (which belongs to the chat that spawned it).
 */
export function projectCounts(sessions) {
  const counts = new Map();
  for (const s of sessions) {
    if (s.projectId && !s.archived && !s.parentSessionId) counts.set(s.projectId, (counts.get(s.projectId) ?? 0) + 1);
  }
  return counts;
}

/**
 * The projects a filter box matches (empty query: all of them), most recently used first — lastUsedAt, or updatedAt
 * for a project nothing has run in yet. The filter builds a new array, so sorting it never touches the caller's.
 */
export function matchingProjects(projects, query) {
  const q = (query ?? '').trim().toLowerCase();
  return projects
    .filter((p) => !q || p.name.toLowerCase().includes(q) || p.path.toLowerCase().includes(q))
    .sort((a, b) => (Date.parse(b.lastUsedAt ?? b.updatedAt) || 0) - (Date.parse(a.lastUsedAt ?? a.updatedAt) || 0));
}

/** Remove a project after asking about it: files stay where they are, its sessions are kept. True when it is gone. */
export async function removeProject(p) {
  const ok = await confirmDialog({
    title: 'Remove project?',
    message: `“${p.name}” will be removed from NetPI. Files in ${p.path} are not touched; its sessions are kept.`,
    confirmLabel: 'Remove',
    danger: true,
  });
  if (!ok) return false;
  try {
    await deleteProject(p.id);
    toast(`Project “${p.name}” removed`);
    return true;
  } catch (err) {
    toast(err.message, 'error');
    return false;
  }
}
