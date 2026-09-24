// Tool metadata for the chat view: labels, icons, summary arguments and argument parsing.
import { firstLine, relPath, truncate } from './format.js';

const META = {
  read: { label: 'Read', icon: 'file-text', arg: 'path', view: 'read' },
  write: { label: 'Write', icon: 'file-plus', arg: 'path', view: 'write' },
  edit: { label: 'Edit', icon: 'pencil', arg: 'path', view: 'edit' },
  grep: { label: 'Grep', icon: 'search', arg: 'pattern', view: 'search' },
  find: { label: 'Find', icon: 'search', arg: 'pattern', view: 'search' },
  ls: { label: 'List', icon: 'list-tree', arg: 'path', view: 'search' },
  bash: { label: 'Bash', icon: 'terminal', arg: 'command', view: 'shell' },
  pwsh: { label: 'PowerShell', icon: 'terminal', arg: 'command', view: 'shell' },
  process_list: { label: 'Processes', icon: 'process', arg: null, view: 'process' },
  process_output: { label: 'Process output', icon: 'process', arg: 'id', view: 'process' },
  process_kill: { label: 'Kill process', icon: 'kill', arg: 'id', view: 'process' },
  web_fetch: { label: 'Fetch', icon: 'globe', arg: 'url', view: 'web' },
  web_search: { label: 'Web search', icon: 'search', arg: 'query', view: 'web' },
  screenshot: { label: 'Screenshot', icon: 'image', arg: 'url', view: 'web' },
  todo_write: { label: 'Todo', icon: 'list', arg: null, view: 'todo' },
};

const CATEGORY_ICON = { files: 'file', shell: 'terminal', agents: 'bot', ideas: 'idea', web: 'globe', todo: 'list', general: 'wrench' };
const SUMMARY_KEYS = ['path', 'command', 'pattern', 'query', 'url', 'name', 'task', 'id', 'text', 'title'];

/** Server tool definitions (from tools.list), filled by the app store. */
export const toolDefs = new Map();

const titleCase = (s) => s.replace(/[_-]+/g, ' ').replace(/^\w/, (c) => c.toUpperCase());

export function toolMeta(name, label) {
  const m = META[name];
  if (m) return m;
  const def = toolDefs.get(name);
  if (name?.startsWith('agent')) {
    const verb = name.replace(/^agents?_?/, '');
    return { label: label || def?.label || (verb ? `Agent ${verb}` : 'Agent'), icon: 'bot', arg: null, view: 'agent' };
  }
  return {
    label: label || def?.label || titleCase(name || 'tool'),
    icon: CATEGORY_ICON[def?.category] ?? 'wrench',
    arg: null,
    view: 'generic',
  };
}

const argCache = new WeakMap();

/** Parse raw JSON arguments of a tool_call part (memoized per part object). */
export function parseArgs(call) {
  if (!call) return {};
  let v = argCache.get(call);
  if (v) return v;
  const raw = call.arguments;
  if (raw && typeof raw === 'object') v = raw;
  else {
    try {
      v = raw ? JSON.parse(raw) : {};
      if (typeof v === 'string') v = JSON.parse(v);
    } catch {
      v = { __raw: String(raw ?? '') };
    }
    if (!v || typeof v !== 'object') v = { __raw: String(raw ?? '') };
  }
  argCache.set(call, v);
  return v;
}

const norm = (k) => k.toLowerCase().replace(/[_-]/g, '');

/** Lenient argument lookup (ignores case, `_` and `-`), with aliases. */
export function arg(args, name, ...aliases) {
  if (!args) return undefined;
  if (args[name] !== undefined) return args[name];
  const wanted = [name, ...aliases].map(norm);
  for (const k of Object.keys(args)) if (wanted.includes(norm(k))) return args[k];
  return undefined;
}

export function pathArg(args) {
  return arg(args, 'path', 'filePath', 'file', 'filename', 'dir', 'directory');
}

/** One-line summary for the collapsed tool row. */
/** todo_write arguments → [{ text, status }] (lenient like the tool: strings, other field names, status synonyms). */
export function todoItems(args) {
  const list = Array.isArray(args) ? args : (args?.items ?? args?.todos ?? args?.tasks ?? []);
  if (!Array.isArray(list)) return [];
  const norm = (s) => {
    const v = String(s ?? '').toLowerCase().replace(/[- ]/g, '_');
    if (['done', 'completed', 'complete', 'finished'].includes(v)) return 'done';
    if (['in_progress', 'inprogress', 'active', 'doing', 'current', 'started'].includes(v)) return 'in_progress';
    return 'pending';
  };
  return list
    .map((x) => (typeof x === 'string' ? { text: x, status: 'pending' } : { text: x?.text ?? x?.content ?? x?.title ?? x?.task ?? '', status: norm(x?.status) }))
    .filter((x) => x.text);
}

export function toolSummary(name, args, base) {
  const meta = toolMeta(name);
  switch (name) {
    case 'screenshot':
      return arg(args, 'url') ? truncate(String(arg(args, 'url')), 160) : 'NetPI window';
    case 'todo_write': {
      const items = todoItems(args);
      const cur = items.find((i) => i.status === 'in_progress') ?? items.find((i) => i.status !== 'done');
      return items.length ? truncate(cur?.text ?? 'all done', 160) : 'cleared';
    }
    case 'read': {
      const p = relPath(pathArg(args), base);
      const off = arg(args, 'offset');
      const lim = arg(args, 'limit');
      if (off != null || lim != null) {
        const o = Number(off ?? 1);
        return `${p}${o < 0 ? ` (last ${-o})` : `:${o}${lim ? `-${o + Number(lim) - 1}` : ''}`}`;
      }
      return p;
    }
    case 'write':
    case 'edit':
      return relPath(pathArg(args), base);
    case 'ls':
      return relPath(pathArg(args), base) || '.';
    case 'grep': {
      const p = arg(args, 'path');
      const g = arg(args, 'glob');
      return `${arg(args, 'pattern') ?? ''}${p ? `  in ${relPath(p, base)}` : ''}${g ? `  ${Array.isArray(g) ? g.join(' ') : g}` : ''}`;
    }
    case 'find': {
      const p = arg(args, 'path');
      return `${arg(args, 'pattern') ?? ''}${p ? `  in ${relPath(p, base)}` : ''}`;
    }
    case 'bash':
    case 'pwsh':
      return truncate(firstLine(String(arg(args, 'command', 'cmd', 'script') ?? '')), 200);
    default:
      break;
  }
  if (meta.view === 'agent') {
    const n = arg(args, 'name', 'agent', 'agentId', 'to', 'id');
    const t = arg(args, 'task', 'text', 'message');
    return truncate([n, t && firstLine(String(t))].filter(Boolean).join(' · '), 160);
  }
  if (args?.__raw) return truncate(args.__raw, 120);
  for (const k of SUMMARY_KEYS) {
    const v = arg(args, k);
    if (typeof v === 'string' && v) return truncate(firstLine(v), 160);
  }
  for (const v of Object.values(args ?? {})) if (typeof v === 'string' && v) return truncate(firstLine(v), 160);
  return '';
}

const kchars = (n) => (n >= 1000 ? `${Math.round(n / 1000)}k chars` : `${n} chars`);

/** Short badge for a finished tool (e.g. "+5 −3", "exit 1", "42 matches"). */
export function toolBadge(name, result) {
  const d = result?.details;
  if (!d || typeof d !== 'object') return null;
  switch (name) {
    case 'edit':
      if (d.added != null || d.removed != null) return { text: `+${d.added ?? 0} −${d.removed ?? 0}`, tone: 'diff' };
      return null;
    case 'write':
      if (d.created) return { text: `new · ${d.lines ?? '?'} lines`, tone: 'ok' };
      if (d.added != null || d.removed != null) return { text: `+${d.added ?? 0} −${d.removed ?? 0}`, tone: 'diff' };
      return null;
    case 'read':
      if (d.image) return { text: 'image' };
      if (d.startLine != null && d.totalLines != null)
        return { text: `${d.endLine - d.startLine + 1 || 0}/${d.totalLines} lines` };
      return null;
    case 'grep':
      if (d.matches != null) return { text: `${d.matches} in ${d.files ?? 0} files${d.truncated ? '+' : ''}` };
      return null;
    case 'find':
      if (d.count != null) return { text: `${d.count}${d.truncated ? '+' : ''} results` };
      return null;
    case 'ls':
      if (d.entries != null) return { text: `${d.entries} entries` };
      return null;
    case 'web_fetch':
      if (d.image) return { text: 'image' };
      if (d.status >= 400) return { text: `HTTP ${d.status}`, tone: 'warn' };
      if (d.chars != null) return { text: `${kchars(d.end - d.offset)}${d.nextOffset != null ? ` of ${kchars(d.chars)}` : ''}` };
      return null;
    case 'web_search':
      return d.results ? { text: `${d.results.length} results` } : null;
    case 'screenshot':
      if (d.consoleErrors?.length) return { text: `${d.consoleErrors.length} console error${d.consoleErrors.length === 1 ? '' : 's'}`, tone: 'warn' };
      return d.width ? { text: `${d.width}×${d.height}` } : null;
    case 'todo_write':
      return d.total ? { text: `${d.done}/${d.total}`, tone: d.done === d.total ? 'ok' : undefined } : null;
    case 'bash':
    case 'pwsh':
      if (d.background && d.status === 'running') return { text: `bg ${d.processId ?? ''}`.trim(), tone: 'info' };
      if (d.timedOut) return { text: 'timeout', tone: 'err' };
      if (d.exitCode != null && d.exitCode !== 0) return { text: `exit ${d.exitCode}`, tone: 'warn' };
      return null;
    default:
      if (d.status && typeof d.status === 'string') return { text: d.status };
      return null;
  }
}
