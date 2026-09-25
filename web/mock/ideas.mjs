// Mock ideas backlog (docs/PLUGIN-IDEAS.md): one in-memory "file" per project plus the global one.
import os from 'node:os';
import path from 'node:path';
import { store } from './store.mjs';

const STATUSES = ['open', 'parked', 'planned', 'in-progress', 'done', 'rejected'];
const SYN = { 'in progress': 'in-progress', wip: 'in-progress', deferred: 'parked', todo: 'open' };
const KINDS = ['note', 'research', 'plan', 'requirements', 'design', 'decision', 'blocker', 'links', 'todo'];
const KSYN = { spec: 'requirements', tasks: 'todo', findings: 'research' };
const GLOBAL = path.join(os.homedir(), '.netpi', 'ideas.json');

const rid = (n) => Math.random().toString(36).slice(2, 2 + n).padEnd(n, '0');
const now = () => new Date().toISOString().replace(/\.\d+Z$/, 'Z');

function err(code, message) {
  const e = new Error(message);
  e.code = code;
  return e;
}

export function createIdeas({ publish }) {
  const files = new Map(); // file -> { ideas: [] }

  function resolve(p = {}) {
    let project = null;
    if (p.projectId) {
      project = store.projects.get(p.projectId);
      if (!project) throw err('not_found', `Unknown project ${p.projectId}`);
    } else if (p.sessionId) {
      const s = store.sessions.get(p.sessionId);
      if (!s) throw err('not_found', `Unknown session ${p.sessionId}`);
      if (s.projectId) project = store.projects.get(s.projectId) ?? null;
    }
    const file = project ? path.join(project.path, '.netpi', 'ideas.json') : GLOBAL;
    let doc = files.get(file);
    if (!doc) files.set(file, (doc = { ideas: [], exists: false }));
    return { file, doc, project };
  }
  const changed = (file) => setTimeout(() => publish('ideas.changed', { file }), 250);

  function find(doc, id) {
    const i =
      doc.ideas.find((x) => x.id === id) ??
      doc.ideas.find((x) => x.id.toLowerCase() === String(id).toLowerCase()) ??
      doc.ideas.find((x) => x.id === `idea-${id}`);
    if (!i) throw err('not_found', `No idea ${id}. Use ideas.list to see the ids.`);
    return i;
  }
  const status = (s) => {
    const v = SYN[String(s).toLowerCase()] ?? String(s).toLowerCase();
    if (!STATUSES.includes(v)) throw err('bad_request', `Invalid status "${s}". Valid: ${STATUSES.join(', ')}`);
    return v;
  };
  const kind = (k) => {
    const v = KSYN[String(k ?? 'note').toLowerCase()] ?? String(k ?? 'note').toLowerCase();
    return KINDS.includes(v) ? v : 'note';
  };
  const tags = (t) => {
    const arr = Array.isArray(t) ? t : String(t ?? '').split(',');
    const seen = new Set();
    return arr
      .map((x) => String(x).trim().replace(/^#/, ''))
      .filter((x) => x && !seen.has(x.toLowerCase()) && seen.add(x.toLowerCase()));
  };
  const section = (s) => ({ id: `sec-${rid(4)}`, kind: kind(s.kind), ...(s.title ? { title: s.title } : {}), content: s.content ?? '', updatedAt: now() });

  const api = {
    'ideas.list': (p) => {
      const { file, doc, project } = resolve(p);
      return {
        file,
        fileName: 'ideas.json',
        scope: project ? 'project' : 'global',
        ...(project ? { projectId: project.id, projectName: project.name } : {}),
        exists: doc.exists,
        ideas: doc.ideas,
      };
    },
    'ideas.get': (p) => find(resolve(p).doc, p.id),
    'ideas.add': (p) => {
      const { file, doc } = resolve(p);
      const i = p.idea ?? {};
      if (!i.title?.trim()) throw err('bad_request', 'title is required');
      const { sections, ...extra } = i;
      const idea = {
        ...extra,
        id: `idea-${rid(6)}`,
        title: i.title.trim(),
        summary: i.summary ?? '',
        status: i.status ? status(i.status) : 'open',
        priority: i.priority ?? 'medium',
        tags: tags(i.tags),
        createdAt: now(),
        updatedAt: now(),
        createdBy: 'user',
        sections: (sections ?? []).map(section),
        sessionIds: p.sessionId ? [p.sessionId] : [],
      };
      p.prepend ? doc.ideas.unshift(idea) : doc.ideas.push(idea);
      doc.exists = true;
      changed(file);
      return idea;
    },
    'ideas.update': (p) => {
      const { file, doc } = resolve(p);
      const idea = find(doc, p.id);
      const patch = p.patch ?? {};
      for (const [k, v] of Object.entries(patch)) {
        if (['id', 'createdAt', 'createdBy', 'updatedAt', 'sessionIds'].includes(k)) continue;
        if (k === 'status') idea.status = status(v);
        else if (k === 'tags') idea.tags = tags(v);
        else if (k === 'title') {
          if (!String(v).trim()) throw err('bad_request', 'title must not be empty');
          idea.title = String(v).trim();
        } else if (k === 'sections') {
          idea.sections = v.map((s) => {
            const old = s.id && idea.sections.find((x) => x.id === s.id);
            return old ? { ...old, ...s, kind: kind(s.kind ?? old.kind), updatedAt: now() } : section(s);
          });
        } else if (k === 'addSections') idea.sections.push(...v.map(section));
        else if (k === 'updateSections') {
          for (const u of v) {
            const s = idea.sections.find((x) => x.id === u.id);
            if (!s) throw err('not_found', `No section ${u.id}`);
            if (u.title !== undefined) s.title = u.title;
            if (u.content !== undefined) s.content = u.content;
            if (u.kind !== undefined) s.kind = kind(u.kind);
            s.updatedAt = now();
          }
        } else if (k === 'removeSectionIds') idea.sections = idea.sections.filter((s) => !v.includes(s.id));
        else if (v === null) delete idea[k];
        else idea[k] = v;
      }
      idea.updatedAt = now();
      if (p.sessionId && !idea.sessionIds.includes(p.sessionId)) idea.sessionIds.push(p.sessionId);
      changed(file);
      return idea;
    },
    'ideas.delete': (p) => {
      const { file, doc } = resolve(p);
      const idea = find(doc, p.id);
      doc.ideas = doc.ideas.filter((x) => x !== idea);
      changed(file);
      return true;
    },
    'ideas.reorder': (p) => {
      const { file, doc } = resolve(p);
      const ids = p.ids ?? [];
      const listed = ids.map((id) => doc.ideas.find((x) => x.id === id)).filter(Boolean);
      doc.ideas = [...listed, ...doc.ideas.filter((x) => !listed.includes(x))];
      changed(file);
      return true;
    },
    'ideas.toPrompt': (p) => {
      const { doc, project } = resolve(p);
      const idea = find(doc, p.id);
      const lines = [
        `Implement the following idea from the ideas backlog (\`${idea.id}\` in ${project ? '.netpi/ideas.json' : GLOBAL}).`,
        'Its sections contain earlier research, plans and decisions — use them. Keep the idea up to date with the ideas tool (action update): set the status to "in-progress" when you start and "done" when finished, and add a note section for anything important you learn.',
        '',
        `# ${idea.title}`,
        `Priority: ${idea.priority}${idea.tags.length ? ` · Tags: ${idea.tags.join(', ')}` : ''}`,
      ];
      if (idea.summary) lines.push('', idea.summary);
      for (const s of idea.sections) lines.push('', `## ${s.kind[0].toUpperCase()}${s.kind.slice(1)}${s.title ? `: ${s.title}` : ''}`, s.content);
      return lines.join('\n');
    },
    'ideas.quickAdd': (p) => {
      if (!p.args?.trim()) throw err('bad_request', 'Usage: /idea <title>');
      const idea = api['ideas.add']({ sessionId: p.sessionId, idea: { title: p.args.trim() } });
      const { project } = resolve({ sessionId: p.sessionId });
      return `Idea added (${project ? `project ${project.name}` : 'global'}): ${idea.title} (${idea.id})`;
    },
  };

  function seed() {
    files.clear();
    const net = [...store.projects.values()].find((p) => p.name === 'netpi');
    if (!net) return;
    const { doc } = resolve({ projectId: net.id });
    const t = (h) => new Date(Date.now() - h * 3600_000).toISOString().replace(/\.\d+Z$/, 'Z');
    const mk = (o) => ({
      id: `idea-${rid(6)}`,
      summary: '',
      status: 'open',
      priority: 'medium',
      tags: [],
      createdBy: 'user',
      sessionIds: [],
      sections: [],
      ...o,
      createdAt: t(o.age ?? 5),
      updatedAt: t(o.upd ?? o.age ?? 5),
      age: undefined,
      upd: undefined,
    });
    doc.ideas = [
      mk({
        title: 'Cache the model list per provider',
        summary: 'Avoid refetching /v1/models on every session switch; AiProxy takes ~800ms to answer when a backend is cold.',
        status: 'in-progress',
        priority: 'high',
        tags: ['perf', 'aiproxy'],
        createdBy: 'agent:ag_long',
        age: 30,
        upd: 1,
        sections: [
          { id: 'sec-r1a2', kind: 'research', title: 'Findings', content: '- `models.list` takes **~800ms** on AiProxy when the llama router probes backends.\n- The UI calls it on every `models.changed` and on startup.\n- Anthropic model list is static.', updatedAt: t(20) },
          { id: 'sec-p3b4', kind: 'plan', title: 'Rollout', content: '1. Cache per provider for 60s (`models.cacheSeconds`).\n2. `models.list { refresh: true }` bypasses the cache.\n3. Emit `models.changed` only when the list actually differs.', updatedAt: t(2) },
          { id: 'sec-d5c6', kind: 'decision', content: 'Keep the cache in the host, not in providers — one place to invalidate.', updatedAt: t(1) },
        ],
      }),
      mk({
        title: 'Retry-After aware backoff in AiProxy provider',
        summary: 'Honor Retry-After on 429/503 and cap the total wait at 2 minutes.',
        status: 'planned',
        priority: 'high',
        tags: ['aiproxy', 'reliability'],
        age: 20,
        sections: [{ id: 'sec-q7d8', kind: 'requirements', content: '- Exponential backoff with jitter (base 2s, max 30s)\n- `Retry-After` wins when present\n- Surface the countdown through `agent.notice`', updatedAt: t(19) }],
      }),
      mk({ title: 'Syntax-highlight diffs in the edit view', summary: 'Use the file extension to highlight both sides of the diff.', status: 'open', priority: 'low', tags: ['ui'], age: 12 }),
      mk({ title: 'Per-project default model', summary: 'A project can pin a default model and reasoning effort.', status: 'open', tags: ['ui', 'settings'], age: 9, createdBy: 'agent:ag_show' }),
      mk({ title: 'Semantic search over docs/', summary: 'Embed docs and expose a `docs_search` tool.', status: 'parked', priority: 'low', tags: ['agents'], age: 50 }),
      mk({ title: 'Stream compaction summaries', summary: 'Show the summary while it is generated instead of a spinner.', status: 'done', tags: ['ui'], age: 80, upd: 40 }),
      mk({ title: 'Replace SQLite with flat files', summary: 'Rejected: concurrent writers and search need a real store.', status: 'rejected', priority: 'low', tags: ['storage'], age: 120, upd: 100 }),
    ];
    doc.exists = true;
    const g = resolve({});
    g.doc.ideas = [mk({ title: 'Try a local reranker for @ mentions', summary: 'Rank files by recent edits and open tabs.', tags: ['ui'], age: 3 })];
    g.doc.exists = true;
  }

  return { api, seed };
}
