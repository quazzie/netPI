// The UI preferences on Settings → General as data: the dialog renders them (PrefField), the settings search finds them
// and Changed lists the ones that differ from their default. They live in `prefs` (ui.svelte.js, localStorage and the
// host's ui.state), not in settings.json; the defaults here are the ones prefs starts from.

/** The sections of General they are shown in, in order. */
export const PREF_SECTIONS = [
  { id: 'appearance', title: 'Appearance' },
  { id: 'chat', title: 'Chat' },
  { id: 'notifications', title: 'Notifications', desktop: true },
];

/**
 * One preference: `type` choice (options [value, label]), bool, or zoom (the desktop app's WebView zoom, not a pref).
 * `desktop`: shown in the desktop app only.
 */
export const UI_PREFS = [
  {
    key: 'theme', section: 'appearance', type: 'choice', label: 'Theme', default: 'dark',
    help: 'Dark, light or follow the system.',
    options: [['dark', 'Dark'], ['light', 'Light'], ['system', 'System']],
    keywords: ['dark mode', 'light mode', 'colors'],
  },
  {
    key: 'chatWidth', section: 'appearance', type: 'choice', label: 'Chat width', default: 'normal',
    help: 'How wide messages get on a large window.',
    options: [['normal', 'Normal'], ['wide', 'Wide'], ['full', 'Full']],
  },
  { key: 'zoom', section: 'appearance', type: 'zoom', label: 'Zoom', keywords: ['font size', 'scale', 'bigger', 'smaller'] },
  {
    key: 'enterSends', section: 'chat', type: 'choice', label: 'Send with', default: true,
    help: 'The other combination inserts a newline (Shift+Enter always does).',
    options: [[true, 'Enter'], [false, 'Ctrl+Enter']],
    keywords: ['keyboard', 'newline', 'submit'],
  },
  {
    key: 'steps', section: 'chat', type: 'choice', label: 'Step groups', default: 'done',
    help: 'The thinking and tool calls between two texts. Expanded: always shown, never folded. Fold when done: a group folds into “N steps · time” once the next text (or message) follows it. Folded: folds into one line from the start, which keeps the chat still while the agent works.',
    options: [['open', 'Expanded'], ['done', 'Fold when done'], ['folded', 'Folded']],
    keywords: ['collapse', 'fold'],
  },
  {
    key: 'expandThinking', section: 'chat', type: 'choice', label: 'Thinking', default: false,
    help: 'Whether reasoning blocks start expanded, also while they stream.',
    options: [[false, 'Collapsed'], [true, 'Expanded']],
    keywords: ['reasoning'],
  },
  {
    key: 'expandTools', section: 'chat', type: 'choice', label: 'Tool calls', default: 'closed',
    help: 'Whether each tool call shows its input and output at once. Errors: only a failed call starts open.',
    options: [['closed', 'Collapsed'], ['errors', 'Errors'], ['open', 'Expanded']],
  },
  {
    key: 'turnDetails', section: 'chat', type: 'bool', label: 'Model turn details', default: true,
    help: "A line under each model turn's steps: time to the first token, how much of the prompt the backend reused from its cache, output speed, tokens in and out. An answer's footer has them too.",
    keywords: ['tokens', 'speed', 'cache'],
  },
  {
    key: 'spellcheck', section: 'chat', type: 'bool', label: 'Spellcheck', default: true,
    help: 'Underline misspelled words in the message box.',
  },
  {
    key: 'notifications', section: 'notifications', type: 'bool', label: 'Notifications', default: true, desktop: true,
    help: 'A Windows notification when a chat finishes, fails, completes its goal or needs you while NetPI is in the background.',
    keywords: ['toast', 'alert'],
  },
];

/** The preferences `prefs` starts from (ui.svelte.js adds the ones that are not on the page). */
export const PREF_DEFAULTS = Object.fromEntries(UI_PREFS.filter((p) => 'default' in p).map((p) => [p.key, p.default]));

/** The preferences shown here: the desktop-only ones in the desktop app only. */
export const prefsFor = (desktop) => UI_PREFS.filter((p) => desktop || !p.desktop);

/** Whether a preference differs from its default. */
export const prefChanged = (p, prefs) => 'default' in p && prefs?.[p.key] !== undefined && prefs[p.key] !== p.default;
