// Mock ideas backlog (docs/PLUGIN-IDEAS.md): one global "file" (~/.netpi/ideas.json), every idea carrying
// a project property.
import os from 'node:os';
import path from 'node:path';
import { store, pushMessage, text, mkSession, agentFor } from './store.mjs';

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
  const file = GLOBAL;
  const doc = { ideas: [], exists: false };
  // Attached image bytes, keyed by their reference path (the host writes them under <home>/idea-images).
  const files = new Map();
  const suggestions = []; // the cards of closed chats (~/.netpi/ideas-pending.json)
  const checked = new Map(); // sessionId → the user-turn count it was last checked at
  const leavePlans = new Map(); // sessionId → the plan its closed tab should leave behind
  const refines = []; // what ideas.refine was asked, for the walkthrough to look at

  const changed = () => setTimeout(() => publish('ideas.changed', { file }), 250);

  function find(id) {
    const i =
      doc.ideas.find((x) => x.id === id) ??
      doc.ideas.find((x) => x.id.toLowerCase() === String(id).toLowerCase()) ??
      doc.ideas.find((x) => x.id === `idea-${id}`);
    if (!i) throw err('not_found', `No idea ${id}. Use ideas.list to see the ids.`);
    return i;
  }
  // A project reference: an id or name, or "global"/"none" → null (unbound). Unknown → bad_request.
  function resolveProject(ref) {
    const v = String(ref ?? '').trim();
    if (v.length === 0 || v === 'global' || v === 'none' || v === 'unbound') return null;
    const p = store.projects.get(v) ?? [...store.projects.values()].find((x) => x.name.toLowerCase() === v.toLowerCase());
    if (!p) {
      const known = [...store.projects.values()].map((x) => x.name).join(', ');
      throw err('bad_request', `Unknown project ${v}. Known projects: ${known}.`);
    }
    return p;
  }
  // The stamp of a new idea: an explicit projectId, else the session's project, else unbound.
  function stamp(p) {
    if (p.projectId) {
      if (p.sessionId && !store.sessions.has(p.sessionId)) throw err('not_found', `Unknown session ${p.sessionId}`);
      const proj = resolveProject(p.projectId);
      return proj ? { id: proj.id, name: proj.name } : null;
    }
    if (p.sessionId) {
      const s = store.sessions.get(p.sessionId);
      if (!s) throw err('not_found', `Unknown session ${p.sessionId}`);
      const proj = s.projectId ? store.projects.get(s.projectId) : null;
      if (proj) return { id: proj.id, name: proj.name };
    }
    return null;
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
    'ideas.list': () => ({ file, fileName: 'ideas.json', exists: doc.exists, ideas: doc.ideas }),
    // The lean, ranked list the welcome screen reads on every window start (idea-ky14bu): the target project's own
    // open/planned ideas first, then the global ones. Same rule as plugins/NetPI.Ideas (IdeaOps.Picks).
    'ideas.picks': (p) => {
      const rank = { open: 0, planned: 1 };
      const weight = { high: 0, medium: 1, low: 2 };
      const target = p.projectId ? String(p.projectId) : null;
      const limit = Math.min(24, Math.max(1, Number(p.limit ?? 5) || 5));
      return {
        picks: doc.ideas
          .map((idea, ord) => ({ idea, ord, project: idea.project ?? null }))
          .filter((x) => !x.project || (target && x.project.id === target))
          .filter((x) => rank[x.idea.status] !== undefined)
          .sort(
            (a, b) =>
              ((target && a.project ? 0 : 1) - (target && b.project ? 0 : 1)) ||
              rank[a.idea.status] - rank[b.idea.status] ||
              (weight[a.idea.priority] ?? 1) - (weight[b.idea.priority] ?? 1) ||
              String(b.idea.updatedAt ?? '').localeCompare(String(a.idea.updatedAt ?? '')) ||
              a.ord - b.ord,
          )
          .slice(0, limit)
          .map((x) => ({
            id: x.idea.id,
            title: x.idea.title,
            summary: String(x.idea.summary ?? '').replace(/[\r\n]+/g, ' ').trim(),
            status: x.idea.status,
            priority: x.idea.priority ?? 'medium',
            projectId: x.project?.id ?? null,
            projectName: x.project?.name ?? null,
            updatedAt: x.idea.updatedAt,
          })),
      };
    },
    'ideas.get': (p) => find(p.id),
    'ideas.add': (p) => {
      const proj = stamp(p);
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
        revision: 1,
        sections: (sections ?? []).map(section),
        sessionIds: p.sessionId ? [p.sessionId] : [],
      };
      if (proj) idea.project = proj;
      p.prepend ? doc.ideas.unshift(idea) : doc.ideas.push(idea);
      doc.exists = true;
      changed();
      return idea;
    },
    'ideas.update': (p) => {
      const idea = find(p.id);
      // like the host: what the editor had when it opened; something else wrote the idea since → a conflict, not an overwrite
      if (p.expectedRevision != null && (idea.revision ?? 1) !== p.expectedRevision)
        throw err('conflict', `Idea ${idea.id} changed since you read it (revision ${idea.revision ?? 1}, you had ${p.expectedRevision}). Reload it and apply your change again.`);
      const patch = p.patch ?? {};
      for (const [k, v] of Object.entries(patch)) {
        if (['id', 'createdAt', 'createdBy', 'updatedAt', 'sessionIds'].includes(k)) continue;
        if (k === 'project') {
          // { id, name? }, a bare id/name, or null/"global" → unbound
          if (v && typeof v === 'object') {
            const ref = v.id ?? v.name;
            const proj = resolveProject(ref);
            if (proj) idea.project = { id: v.id ?? proj.id, name: v.name ?? proj.name };
            else delete idea.project;
          } else {
            const proj = resolveProject(v);
            if (proj) idea.project = { id: proj.id, name: proj.name };
            else delete idea.project;
          }
        } else if (k === 'status') idea.status = status(v);
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
      idea.revision = (idea.revision ?? 1) + 1;
      if (p.sessionId && !idea.sessionIds.includes(p.sessionId)) idea.sessionIds.push(p.sessionId);
      changed();
      return idea;
    },
    'ideas.delete': (p) => {
      const idea = find(p.id);
      doc.ideas = doc.ideas.filter((x) => x !== idea);
      changed();
      return true;
    },
    // Images: the mock keeps the bytes in memory instead of on disk, and hands them back the way the host does.
    'ideas.addImage': (p) => {
      const mediaType = p.mediaType ?? 'image/png';
      if (!/^image\/(png|jpeg|gif|webp)$/.test(mediaType)) throw err('bad_request', 'An idea image must be a png, jpeg, gif or webp.');
      const data = String(p.data ?? '').replace(/^data:[^,]+,/, '');
      const bytes = Math.floor((data.length * 3) / 4);
      if (!bytes) throw err('bad_request', 'The image was empty.');
      if (bytes > 4 * 1024 * 1024) throw err('bad_request', 'The image is larger than 4 MB — shrink it before attaching.');
      const ref = { path: `idea-images/img-${rid(6)}.${mediaType === 'image/jpeg' ? 'jpg' : mediaType.split('/')[1]}`, name: p.name ?? 'image', mediaType, bytes };
      files.set(ref.path, { mediaType, data });
      return ref;
    },
    'ideas.removeImage': (p) => {
      files.delete(p.path);
      return true;
    },
    'ideas.image': (p) => {
      const f = files.get(p.path);
      if (!f) throw err('not_found', 'The image is gone from disk.');
      return { path: p.path, mediaType: f.mediaType, bytes: Math.floor((f.data.length * 3) / 4), data: f.data };
    },
    'ideas.reorder': (p) => {
      const ids = p.ids ?? [];
      const listed = ids.map((id) => doc.ideas.find((x) => x.id === id)).filter(Boolean);
      doc.ideas = [...listed, ...doc.ideas.filter((x) => !listed.includes(x))];
      changed();
      return true;
    },
    'ideas.toPrompt': (p) => {
      const idea = find(p.id);
      const proj = idea.project ? (idea.project.name ?? idea.project.id) : 'global';
      const lines = [
        `Implement the following idea from the ideas backlog (\`${idea.id}\` in ${GLOBAL}).`,
        'Its sections contain earlier research, plans and decisions — use them. Keep the idea up to date with the ideas tool (action update): set the status to "in-progress" when you start and "done" when finished, and add a note section for anything important you learn.',
        '',
        `# ${idea.title}`,
        `Priority: ${idea.priority} · Project: ${proj}${idea.tags.length ? ` · Tags: ${idea.tags.join(', ')}` : ''}`,
      ];
      if (idea.summary) lines.push('', idea.summary);
      if (idea.images?.length) {
        lines.push('', '## Images');
        for (const i of idea.images) lines.push(`- ${i.name} — read it: ${i.path}`);
        lines.push('Read the ones that matter before you start; they are the report.');
      }
      for (const s of idea.sections) lines.push('', `## ${s.kind[0].toUpperCase()}${s.kind.slice(1)}${s.title ? `: ${s.title}` : ''}`, s.content);
      return lines.join('\n');
    },
    'ideas.attach': (p) => {
      const idea = find(p.id);
      if (!store.sessions.has(p.sessionId)) throw err('not_found', `Unknown session ${p.sessionId}`);
      if (!idea.sessionIds.includes(p.sessionId)) idea.sessionIds.push(p.sessionId);
      const body = [
        `The user added an idea from the ideas backlog to this chat (\`${idea.id}\` in ~/.netpi/ideas.json). Use its notes; keep it up to date with the ideas tool when the work changes it.`,
        '',
        `# ${idea.title}`,
      ];
      if (idea.summary) body.push('', idea.summary);
      for (const sec of idea.sections) body.push('', `## ${sec.kind}${sec.title ? `: ${sec.title}` : ''}`, sec.content);
      const m = pushMessage(p.sessionId, 'notice', [text(body.join('\n'))], { meta: { kind: 'idea', ideaId: idea.id } });
      publish('message.added', { sessionId: p.sessionId, message: m }, p.sessionId);
      publish('session.updated', { session: store.sessions.get(p.sessionId) });
      changed();
      return { noticeId: m.id, ideaId: idea.id };
    },
    // Task an agent to define an idea better: a chat on the idea's project with the idea attached and the task sent. The mock
    // runs no agent; it records what it was asked so the walkthrough can check it.
    'ideas.refine': (p) => {
      const idea = find(p.id);
      const s = mkSession({ title: `Refine idea: ${idea.title}`.slice(0, 80), projectId: idea.project?.id ?? null });
      agentFor(s.id);
      api['ideas.attach']({ sessionId: s.id, id: idea.id });
      (idea.sessions ??= []).push({ sessionId: s.id, title: s.title, at: now(), note: 'Refining this idea' });
      refines.push({ id: idea.id, sessionId: s.id, agent: p.agent ?? 'any', hint: p.hint ?? null });
      changed();
      return { sessionId: s.id, title: s.title, agent: p.agent ?? 'any' };
    },
    'ideas.mockRefines': () => refines,
    'ideas.quickAdd': (p) => {
      if (!p.args?.trim()) throw err('bad_request', 'Usage: /idea <title>');
      const idea = api['ideas.add']({ sessionId: p.sessionId, idea: { title: p.args.trim() } });
      const proj = idea.project ? (idea.project.name ?? idea.project.id) : null;
      return `Idea added (${proj ? `project ${proj}` : 'global'}): ${idea.title} (${idea.id})`;
    },
    // Save on tab close (phase 2). The mock answers NOTHING unless the mock session was told to leave a plan, so the
    // walkthrough can show a card: sessions.closeLeavesPlan('<substring of the first user turn>').
    'ideas.closed': (p) => {
      const s = store.sessions.get(p.sessionId);
      if (!s) return { checked: false, reason: 'no_session' };
      if (s.kind === 'subagent') return { checked: false, reason: 'subagent' };
      const users = (store.messages.get(p.sessionId) ?? []).filter((m) => m.role === 'user');
      if (users.length < 2) return { checked: false, reason: 'short' };
      const at = Number(checked.get(p.sessionId) ?? 0);
      if (at >= users.length) return { checked: false, reason: 'already' };
      checked.set(p.sessionId, users.length);

      // Which open idea the chat worked on: the one sharing the most title words (the real one is a decision).
      const open = doc.ideas.filter((i) => !['done', 'rejected'].includes(i.status) && (!i.project || i.project.id === s.projectId));
      const words = new Set(
        users
          .flatMap((m) => m.parts ?? [])
          .filter((x) => x.type === 'text')
          .join(' ')
          .toLowerCase()
          .split(/\W+/)
          .filter((w) => w.length > 3),
      );
      let best = null;
      let score = 0;
      for (const i of open) {
        const n = i.title.toLowerCase().split(/\W+/).filter((w) => words.has(w)).length;
        if (n > score) ((best = i), (score = n));
      }
      if (best && score >= 2) {
        (best.sessions ??= []).some((x) => x.sessionId === s.id) ||
          best.sessions.push({ sessionId: s.id, title: s.title, at: now(), seen: false });
        changed();
      }

      const plan = leavePlans.get(p.sessionId);
      if (!plan) return { checked: true, reason: 'started' };
      const suggestion = {
        id: `sg_${rid(8)}`,
        kind: 'save',
        sessionId: s.id,
        sessionTitle: s.title,
        title: plan,
        summary: `Left over from the “${s.title}” chat: the plan was written but nothing was built from it.`,
        at: now(),
        project: s.projectId ? (store.projects.get(s.projectId) ?? null) : null,
      };
      suggestions.push(suggestion);
      setTimeout(() => publish('ideas.suggested', { suggestion }), 120); // the real check answers in the background
      return { checked: true, reason: 'started' };
    },
    'ideas.suggestions': () => ({ suggestions: suggestions.map((s) => ({ ...s })) }),
    'ideas.resolve': (p) => {
      if (!['save', 'done', 'discard'].includes(p.action)) throw err('bad_request', 'action must be "save", "done" or "discard"');
      const i = suggestions.findIndex((s) => s.id === p.id);
      if (i < 0) throw err('not_found', 'That card is gone (already answered, or NetPI restarted).');
      const [card] = suggestions.splice(i, 1);
      if (p.action === 'discard') return { saved: null, discarded: true };
      if (p.action === 'done') {
        // The commit check's card: an idea already in the backlog, only its status in question.
        const idea = doc.ideas.find((x) => x.id === card.ideaId);
        if (!idea) throw err('not_found', `No idea ${card.ideaId}.`);
        idea.status = 'done';
        idea.updatedAt = now();
        changed();
        return { saved: idea, discarded: false, marked: 'done' };
      }
      const idea = api['ideas.add']({
        sessionId: card.sessionId,
        idea: { title: p.edit?.title ?? card.title, summary: p.edit?.summary ?? card.summary },
      });
      (idea.sessions ??= []).push({ sessionId: card.sessionId, title: card.sessionTitle, at: now(), seen: true });
      changed();
      return { saved: idea, discarded: false };
    },
  };

  /** The mock walkthrough: offer the commit check's card for an idea (the real one needs a repository). */
  api.commitFinishesIdea = (phrase) => {
    const idea = doc.ideas.find((i) => i.title.toLowerCase().includes(String(phrase).toLowerCase()));
    if (!idea) return null;
    const suggestion = {
      id: `sg_${rid(8)}`,
      kind: 'done',
      ideaId: idea.id,
      title: idea.title,
      commits: [`${rid(7)} the ${idea.title.toLowerCase()}, measured`],
      at: now(),
      project: idea.project ?? null,
    };
    suggestions.push(suggestion);
    publish('ideas.suggested', { suggestion });
    return suggestion.id;
  };

  /** The mock walkthrough: make a chat leave the given plan when its tab is closed. */
  api.closeLeavesPlan = (phrase, title = `Plan: ${phrase}`) => {
    for (const [sid, messages] of store.messages) {
      const said = messages
        .filter((m) => m.role === 'user')
        .flatMap((m) => m.parts ?? [])
        .map((x) => x.text ?? '')
        .join(' ');
      if (said.includes(phrase)) {
        leavePlans.set(sid, title);
        return sid;
      }
    }
    return null;
  };

  function seed() {
    doc.ideas.length = 0;
    const all = [...store.projects.values()];
    const net = all.find((p) => p.name === 'netpi');
    const ai = all.find((p) => p.name === 'aiproxy');
    const st = (p) => (p ? { id: p.id, name: p.name } : undefined);
    if (!net) return;
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
        project: st(net),
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
        project: st(net),
        title: 'Retry-After aware backoff in AiProxy provider',
        summary: 'Honor Retry-After on 429/503 and cap the total wait at 2 minutes.',
        status: 'planned',
        priority: 'high',
        tags: ['aiproxy', 'reliability'],
        age: 20,
        sections: [{ id: 'sec-q7d8', kind: 'requirements', content: '- Exponential backoff with jitter (base 2s, max 30s)\n- `Retry-After` wins when present\n- Surface the countdown through `agent.notice`', updatedAt: t(19) }],
      }),
      mk({ project: st(net), title: 'Syntax-highlight diffs in the edit view', summary: 'Use the file extension to highlight both sides of the diff.', status: 'open', priority: 'low', tags: ['ui'], age: 12 }),
      mk({ project: st(net), title: 'Per-project default model', summary: 'A project can pin a default model and reasoning effort.', status: 'open', tags: ['ui', 'settings'], age: 9, createdBy: 'agent:ag_show' }),
      mk({ project: st(net), title: 'Semantic search over docs/', summary: 'Embed docs and expose a `docs_search` tool.', status: 'parked', priority: 'low', tags: ['agents'], age: 50 }),
      mk({ project: st(net), title: 'Stream compaction summaries', summary: 'Show the summary while it is generated instead of a spinner.', status: 'done', tags: ['ui'], age: 80, upd: 40 }),
      mk({ project: st(net), title: 'Replace SQLite with flat files', summary: 'Rejected: concurrent writers and search need a real store.', status: 'rejected', priority: 'low', tags: ['storage'], age: 120, upd: 100 }),
      // other projects share the same file: the tab filters them out
      mk({ project: st(ai), title: 'Serve /v1/models from a snapshot', summary: 'The aiproxy backend could answer models.list in <5ms with a 60s cache.', status: 'open', tags: ['perf'], age: 10 }),
      mk({ project: st(ai), title: 'Stream chunk size negotiation', summary: 'Accept a preferred chunk size on the /v1/responses stream.', status: 'parked', priority: 'low', tags: ['perf'], age: 25 }),
      // unbound ("global")
      mk({ title: 'Try a local reranker for @ mentions', summary: 'Rank files by recent edits and open tabs.', tags: ['ui'], age: 3 }),
    ];
    doc.exists = true;
  }

  return { api, seed, closeLeavesPlan: (phrase, title) => api.closeLeavesPlan(phrase, title), commitFinishesIdea: (phrase) => api.commitFinishesIdea(phrase) };
}
