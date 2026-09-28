// UI-only state: panel layout, preferences, modals, toasts and the composer bridge.
import { load, persist, fetchRemote } from '../persist.js';

const LAYOUT_KEY = 'netpi.layout';
const PREFS_KEY = 'netpi.prefs';

const defaultLayout = {
  left: { width: 272, collapsed: false, active: 'core/sessions' },
  right: { width: 360, collapsed: false, active: null },
};
const defaultPrefs = {
  theme: 'dark', // dark | light | system
  // thinking/tool step groups: 'open' never fold · 'done' fold groups of more than 3 steps when the run ends ·
  // 'folded' fold from the second step on, also while the agent works
  steps: 'done',
  chatWidth: 'normal', // normal (900px) | wide (1200px) | full
  enterSends: true, // false → Ctrl+Enter sends, Enter = newline
  expandThinking: false,
  turnDetails: true, // under each model turn's steps: time to first token, cache reuse, output speed, tokens
  spellcheck: true, // spell checking in the composer
  favorites: [], // project ids shown as quick new-session buttons after the tab bar's +
  notifications: true, // desktop app: a Windows notification when a chat needs you while NetPI is in the background
};

const CHAT_WIDTH = { normal: '900px', wide: '1200px', full: 'none' };

function withDefaults(v) {
  const saved = v && typeof v === 'object' ? v : {};
  const p = { ...defaultPrefs, ...saved };
  if (saved.steps == null && saved.collapseSteps === false) p.steps = 'open'; // saved before the steps choice existed
  if (!['open', 'done', 'folded'].includes(p.steps)) p.steps = defaultPrefs.steps;
  if (!Array.isArray(p.favorites)) p.favorites = [];
  if (!(p.chatWidth in CHAT_WIDTH)) p.chatWidth = defaultPrefs.chatWidth;
  delete p.collapseSteps;
  return p;
}

function merge(base, v) {
  if (!v || typeof v !== 'object') return structuredClone(base);
  return {
    left: { ...base.left, ...(v.left ?? {}) },
    right: { ...base.right, ...(v.right ?? {}) },
  };
}

export const layout = $state(merge(defaultLayout, load(LAYOUT_KEY, null)));
export const prefs = $state(withDefaults(load(PREFS_KEY, {})));

export function saveLayout() {
  persist(LAYOUT_KEY, $state.snapshot(layout));
}
export function savePrefs() {
  persist(PREFS_KEY, $state.snapshot(prefs));
  applyTheme();
}

/** Add or remove a project from the favorites (quick new-session buttons in the top bar). */
export function toggleFavorite(projectId) {
  prefs.favorites = prefs.favorites.includes(projectId)
    ? prefs.favorites.filter((x) => x !== projectId)
    : [...prefs.favorites, projectId];
  savePrefs();
}

/** Pull the host copies (used when local storage was empty, e.g. a fresh WebView profile). */
export async function syncUiStateFromHost() {
  if (load(LAYOUT_KEY, null) == null) {
    const v = await fetchRemote(LAYOUT_KEY);
    if (v) Object.assign(layout, merge(defaultLayout, v));
  }
  if (load(PREFS_KEY, null) == null) {
    const v = await fetchRemote(PREFS_KEY);
    if (v) {
      Object.assign(prefs, withDefaults(v));
      applyTheme();
    }
  }
}

let mql = null;
/** Theme and the other prefs that live on the document (chat width). */
export function applyTheme() {
  let t = prefs.theme;
  if (t === 'system') {
    mql ??= matchMedia('(prefers-color-scheme: light)');
    t = mql.matches ? 'light' : 'dark';
  }
  document.documentElement.dataset.theme = t;
  document.documentElement.style.setProperty('--chat-max', CHAT_WIDTH[prefs.chatWidth] ?? CHAT_WIDTH.normal);
}
if (typeof window !== 'undefined') {
  matchMedia('(prefers-color-scheme: light)').addEventListener?.('change', () => {
    if (prefs.theme === 'system') applyTheme();
  });
  applyTheme(); // before the first render: no flash of the default width
}

export function togglePanel(side) {
  layout[side].collapsed = !layout[side].collapsed;
  saveLayout();
}

// ------------------------------------------------------------------------------------------ modals

export const modals = $state({
  settings: false, // true, or the page to open on (e.g. 'agents')
  palette: false,
  help: false,
  folder: null, // { initial, title, resolve }
  confirm: null, // { title, message, confirmLabel, danger, resolve }
  prompt: null, // { title, label, value, resolve }
  lightbox: null, // { src }
  projectPicker: null, // { sessionId, anchor } attaches a project; { select: true, anchor } picks the one new sessions use
  projects: null, // { view: 'list' | 'new' | 'edit', id?, sessionId?, select? }
});

/**
 * The projects dialog: view 'list' (all projects), 'new' or 'edit' (project `id`). A project created from a picker is
 * attached to `sessionId`, or with `select` becomes the project new sessions start in.
 */
export function openProjects(opts = {}) {
  modals.projectPicker = null;
  modals.projects = { view: 'list', ...opts };
}

export function confirmDialog({ title = 'Are you sure?', message = '', confirmLabel = 'Confirm', danger = false } = {}) {
  return new Promise((resolve) => {
    modals.confirm = { title, message, confirmLabel, danger, resolve };
  });
}

/** A text prompt; resolves with the text, or null when cancelled. multiline: a text box (Ctrl+Enter confirms). */
export function promptDialog({ title, label = '', value = '', placeholder = '', multiline = false, confirmLabel = 'OK', hint = '' }) {
  return new Promise((resolve) => {
    modals.prompt = { title, label, value, placeholder, multiline, confirmLabel, hint, resolve };
  });
}

// ------------------------------------------------------------------------------------------ toasts

export const toasts = $state([]);
let toastId = 1;

export function toast(text, level = 'info', { timeout } = {}) {
  const id = toastId++;
  toasts.push({ id, text: String(text), level });
  if (toasts.length > 5) toasts.shift();
  const ms = timeout ?? (level === 'error' ? 7000 : 3800);
  if (ms > 0) setTimeout(() => dismissToast(id), ms);
  return id;
}
export function dismissToast(id) {
  const i = toasts.findIndex((t) => t.id === id);
  if (i >= 0) toasts.splice(i, 1);
}

// ------------------------------------------------------------------------------------------ composer bridge

/** The active composer registers itself here so plugins/commands can insert text or focus it. */
export const composer = { insertText: null, focus: null, setText: null };
