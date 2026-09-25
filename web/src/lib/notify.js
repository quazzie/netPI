// Windows notifications through the desktop app, for what needs the user while NetPI is in the background: a chat's
// run finished or failed, its goal was completed or is blocked, the budget asks. The desktop app shows one only while
// its window is not the active one (MainForm, message "notify"); clicking it brings the window up and opens the chat.
// In a browser there is no desktop app, and nothing happens.
import { prefs } from './state/ui.svelte.js';

const webview = () => globalThis.chrome?.webview;

export function notify(sessionId, title, body) {
  if (!prefs.notifications) return;
  webview()?.postMessage({ type: 'notify', sessionId, title, body });
}

/** `open(sessionId)` runs when the user clicks a notification. */
export function onNotificationClick(open) {
  webview()?.addEventListener('message', (e) => {
    if (e.data?.type === 'openSession' && typeof e.data.sessionId === 'string') open(e.data.sessionId);
  });
}

/** The first line of a text, shortened for a notification. */
export function firstLine(text, max = 160) {
  const line = String(text ?? '').trim().split(/\r?\n/)[0] ?? '';
  return line.length > max ? line.slice(0, max - 1) + '…' : line;
}
