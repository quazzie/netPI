// The desktop app's taskbar badge: blue while chats work, orange while chats wait for the user (MainForm, message
// "taskbar"). In a browser there is no desktop app, and nothing happens.

const webview = () => globalThis.chrome?.webview;

/**
 * Counts for the badge from [{ kind, status }] (one per session) and the number of items waiting for the user.
 * Subagent chats are the parent's business: the parent shows as working while they run.
 */
export function taskbarCounts(sessions, waiting) {
  let working = 0;
  for (const s of sessions) {
    if (s.kind === 'subagent') continue;
    if (s.status === 'running' || s.status === 'queued' || s.status === 'yielded' || s.status === 'waiting') working++;
  }
  return { working, waiting };
}

let last = '';

export function setTaskbarState({ working, waiting }) {
  const key = `${working}/${waiting}`;
  if (key === last) return;
  last = key;
  webview()?.postMessage({ type: 'taskbar', working, waiting });
}
