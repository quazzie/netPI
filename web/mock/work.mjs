// Mock "work" state for the Work tab: lane pools, shell processes, today's usage, and a background session
// whose agent and subagents keep running (so the Work tab always has something live to show).
import path from 'node:path';
import { store, agentFor, mkSession, pushMessage, newId, text, REPO } from './store.mjs';

const MIN = 60_000;
const iso = (ms) => new Date(ms).toISOString();

export function createWork({ publish, log, agentsView }) {
  // ------------------------------------------------------------------ slots of the running calls
  const pools = [
    { key: 'aiproxy/qwen3.8-27b', provider: 'aiproxy', capacity: 2, models: ['aiproxy/qwen3.8-27b'], owners: [], waiters: [], source: 'catalog' },
    { key: 'aiproxy/gemma-4', provider: 'aiproxy', capacity: 1, models: ['aiproxy/gemma-4'], owners: [], waiters: [], source: 'catalog' },
    { key: 'anthropic', provider: 'anthropic', capacity: 4, models: ['anthropic/claude-sonnet-4-6'], owners: [], waiters: [], source: 'settings' },
  ];
  const modelStatus = { 'aiproxy/qwen3.8-27b': 'loaded', 'aiproxy/gemma-4': 'unloaded', 'anthropic/claude-sonnet-4-6': 'available' };

  function poolInfo(p) {
    let status = 'idle';
    if (p.waiters.length) status = 'queued';
    else if (p.owners.length >= p.capacity) status = 'full';
    else if (p.owners.length) status = 'busy';
    else if (p.models.every((m) => modelStatus[m] === 'stopped' || modelStatus[m] === 'offline')) status = modelStatus[p.models[0]];
    return {
      key: p.key,
      provider: p.provider,
      capacity: p.capacity,
      busy: p.owners.length,
      queued: p.waiters.length,
      models: p.models,
      owners: p.owners,
      waiters: p.waiters,
      source: p.source,
      status,
    };
  }
  const slots = () => pools.map(poolInfo);
  // agents.changed carries what agents.list returns (the server adds the agents set up in settings)
  const floating0 = [];
  const agentsChanged = () => publish('agents.changed', { agents: (agentsView ?? slots)(), unassigned: floating0 });

  // ------------------------------------------------------------------ processes
  const procs = new Map(); // id -> { info, out }
  function procInfo(p) {
    return { ...p.info, outputBytes: Buffer.byteLength(p.out) };
  }
  function procStart({ command, shell = 'bash', cwd = REPO, sessionId, agentId, background = false, startedAt = Date.now() }) {
    const id = newId('proc');
    const p = {
      info: { id, pid: 40000 + Math.floor(Math.random() * 20000), shell, command, cwd, sessionId, agentId, background, status: 'running', startedAt: iso(startedAt) },
      out: '',
      tailCalls: 0, // e2e: how many processes.output tails this row asked for (a closed row asking again is the leak)
    };
    procs.set(id, p);
    publish('process.started', { process: procInfo(p) });
    return id;
  }
  function procOutput(id, chunk) {
    const p = procs.get(id);
    if (!p) return;
    p.out = (p.out + chunk).slice(-256 * 1024);
    if (p.info.background) publish('process.output', { id, chunk });
  }
  function procEnd(id, { exitCode = 0, status = 'exited', endedAt = Date.now() } = {}) {
    const p = procs.get(id);
    if (!p || p.info.status !== 'running') return;
    Object.assign(p.info, { status, exitCode: status === 'exited' ? exitCode : undefined, endedAt: iso(endedAt) });
    publish('process.exited', { process: procInfo(p) });
  }
  function procList() {
    return [...procs.values()]
      .map(procInfo)
      .sort((a, b) => (a.status === 'running') - (b.status === 'running') || Date.parse(a.startedAt) - Date.parse(b.startedAt))
      .reverse();
  }
  function procOutputTail(id, tail = 500) {
    const p = procs.get(id);
    if (!p) {
      const e = new Error(`Unknown process ${id}`);
      e.code = 'not_found';
      throw e;
    }
    p.tailCalls++;
    return p.out.split('\n').slice(-tail).join('\n');
  }
  function procStats() {
    // e2e test helper: per-process processes.output tail counts
    return Object.fromEntries([...procs.values()].map((p) => [p.info.id, { tailCalls: p.tailCalls }]));
  }
  function procKill(id) {
    const p = procs.get(id);
    if (!p || p.info.status !== 'running') return false;
    procOutput(id, '^C\n[killed]\n');
    procEnd(id, { status: 'killed' });
    if (p.onKill) p.onKill();
    return true;
  }

  // ------------------------------------------------------------------ usage
  const usage = {
    aiproxy: { inputTokens: 1_184_000, outputTokens: 86_400, cacheReadTokens: 912_000, cacheWriteTokens: 0, calls: 412, budgetTokens: 2_000_000 },
    anthropic: { inputTokens: 402_000, outputTokens: 31_800, cacheReadTokens: 188_000, cacheWriteTokens: 24_100, calls: 57, budgetTokens: 500_000 },
  };
  function recordUsage(provider, model, u) {
    const t = (usage[provider] ??= { inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0, calls: 0 });
    t.inputTokens += u.inputTokens ?? 0;
    t.outputTokens += u.outputTokens ?? 0;
    t.cacheReadTokens += u.cacheReadTokens ?? 0;
    t.cacheWriteTokens += u.cacheWriteTokens ?? 0;
    t.calls++;
    publish('usage.recorded', { provider, model, usage: u });
  }
  function usageSummary() {
    return {
      day: new Date().toISOString().slice(0, 10),
      providers: Object.entries(usage).map(([provider, t]) => ({
        provider,
        ...t,
        ...(t.budgetTokens ? { budgetUsed: t.inputTokens + t.outputTokens + t.cacheWriteTokens } : {}),
      })),
    };
  }

  // ------------------------------------------------------------------ seed + background simulation
  function seed() {
    procs.clear();
    for (const p of pools) {
      p.owners = [];
      p.waiters = [];
    }
    const now = Date.now();
    const projAi = [...store.projects.values()].find((p) => p.name === 'aiproxy');

    // a session whose agent (and two subagents) keep working in the background
    const main = mkSession({ id: 'ses_bg', title: 'Refactor provider retry policy', projectId: projAi?.id ?? null, model: 'aiproxy/qwen3.8-27b', createdAt: iso(now - 40 * MIN) });
    pushMessage(main.id, 'user', [text('Refactor the retry policy in the AiProxy provider: exponential backoff with jitter, honor Retry-After, and cap total wait at 2 minutes. Use subagents to survey the call sites and to review the result.')], {}, now - 38 * MIN);
    main.updatedAt = iso(now - 2 * MIN);
    const exp = mkSession({ id: 'ses_bg_explore', title: 'surveyor: find retry call sites', projectId: main.projectId, parentSessionId: main.id, kind: 'subagent', model: 'aiproxy/qwen3.8-27b', createdAt: iso(now - 9 * MIN) });
    const rev = mkSession({ id: 'ses_bg_review', title: 'reviewer: check backoff math', projectId: main.projectId, parentSessionId: main.id, kind: 'subagent', model: 'aiproxy/qwen3.8-27b', createdAt: iso(now - 2 * MIN) });
    pushMessage(exp.id, 'user', [text('Find every place in plugins/NetPI.Providers.AiProxy that retries a request or sleeps before retrying. Report file:line and the current policy.')], { meta: { source: 'agent:main' } }, now - 9 * MIN);
    pushMessage(rev.id, 'user', [text('Review the new backoff implementation for off-by-one errors and unbounded waits.')], { meta: { source: 'agent:main' } }, now - 2 * MIN);

    const a = agentFor(main.id);
    const e = agentFor(exp.id);
    const r = agentFor(rev.id);
    Object.assign(a, { name: 'main', status: 'yielded', activity: 'waiting for 2 agents', startedAt: iso(now - 38 * MIN), runs: 1, turns: 14, toolCalls: 31, inputTokens: 212_000, outputTokens: 9_800, model: 'aiproxy/qwen3.8-27b', agent: 'qwen', children: [e.id, r.id] });
    Object.assign(e, { name: 'surveyor', status: 'running', activity: 'tool: grep', startedAt: iso(now - 9 * MIN), runs: 1, turns: 6, toolCalls: 17, inputTokens: 64_000, outputTokens: 2_100, parentAgentId: a.id, parentSessionId: main.id, isSubagent: true, depth: 1, task: 'Find every place that retries a request', model: 'aiproxy/qwen3.8-27b', agent: 'qwen' });
    Object.assign(r, { name: 'reviewer', status: 'queued', activity: 'waiting for agent qwen', startedAt: iso(now - 2 * MIN), runs: 1, parentAgentId: a.id, parentSessionId: main.id, isSubagent: true, depth: 1, task: 'Review the new backoff implementation', model: 'aiproxy/qwen3.8-27b', agent: 'qwen' });
    // more finished subagents for the "recent" list
    const doc = mkSession({ id: 'ses_bg_docs', title: 'docs-writer: update PROTOCOL.md', projectId: main.projectId, parentSessionId: main.id, kind: 'subagent', createdAt: iso(now - 30 * MIN) });
    Object.assign(agentFor(doc.id), { name: 'docs-writer', status: 'cancelled', parentAgentId: a.id, parentSessionId: main.id, isSubagent: true, depth: 1, startedAt: iso(now - 30 * MIN), finishedAt: iso(now - 26 * MIN), error: 'Cancelled by the parent agent (superseded)' });
    doc.updatedAt = iso(now - 26 * MIN);

    // a second busy session (holds the other qwen slot) with a live foreground process
    const projNet = [...store.projects.values()].find((p) => p.name === 'netpi');
    const idx = mkSession({ id: 'ses_index', title: 'Index docs for semantic search', projectId: projNet?.id ?? null, model: 'aiproxy/qwen3.8-27b', createdAt: iso(now - 6 * MIN) });
    pushMessage(idx.id, 'user', [text('Build an embedding index of docs/ so agents can search it. Use scripts/embed.py and report the chunk count.')], {}, now - 6 * MIN);
    idx.updatedAt = iso(now - 1 * MIN);
    const ia = agentFor(idx.id);
    Object.assign(ia, { name: 'main', status: 'running', activity: 'tool: bash', startedAt: iso(now - 6 * MIN), runs: 1, turns: 4, toolCalls: 6, inputTokens: 31_000, outputTokens: 1_900, model: 'aiproxy/qwen3.8-27b', agent: 'qwen' });

    const pool = pools[0];
    pool.owners = [
      { agentId: e.id, sessionId: exp.id, label: 'surveyor', since: iso(now - 64_000) },
      { agentId: ia.id, sessionId: idx.id, label: 'main', since: iso(now - 21_000) }, // the runtime labels owners with the agent name
    ];
    pool.waiters = [{ agentId: r.id, sessionId: rev.id, label: 'reviewer', since: iso(now - 7_000) }];

    const emb = procStart({ command: 'python scripts/embed.py docs/ --chunk 800 --out .netpi/index', cwd: REPO, sessionId: idx.id, agentId: ia.id, startedAt: now - 48_000 });
    for (let i = 1; i <= 14; i++) procOutput(emb, `embedding docs/${['PROTOCOL.md', 'TOOLS.md', 'UI.md', 'PLUGIN-IDEAS.md'][i % 4]} chunk ${i}/40\n`);

    // processes: one live background dev server and some finished foreground runs
    const dev = procStart({ command: 'npm run dev -- --port 5173 --strictPort', cwd: path.join(REPO), sessionId: main.id, agentId: a.id, background: true, startedAt: now - 23 * MIN });
    procOutput(dev, '\n> netpi@0.1.0 dev\n> vite --config web/vite.config.js --port 5173 --strictPort\n\n  VITE v8.3.0  ready in 412 ms\n\n  ➜  Local:   http://localhost:5173/\n');
    const fin = [
      { command: 'dotnet build NetPI.slnx -nologo -v q', exitCode: 0, ago: 14, dur: 4.2, out: 'Build succeeded.\n    0 Warning(s)\n    0 Error(s)\n' },
      { command: 'dotnet test tests/NetPI.Providers.Tests', exitCode: 1, ago: 11, dur: 8.9, out: 'Running 23 tests\n  ✗ retry_honors_retry_after (12ms)\n      Expected: 2000ms, Actual: 1000ms\n22 passed, 1 failed\n' },
      { command: 'rg -n "Task.Delay" plugins/NetPI.Providers.AiProxy', exitCode: 0, ago: 8, dur: 0.1, out: 'ChatTransport.cs:212:            await Task.Delay(delay, ct);\nResponsesTransport.cs:98:        await Task.Delay(backoff, ct);\n' },
      { command: 'curl -sS http://127.0.0.1:8090/v1/models', status: 'timeout', ago: 6, dur: 120, out: '[timed out after 120s; the process tree was killed]\n' },
    ];
    for (const f of fin) {
      const id = procStart({ command: f.command, sessionId: main.id, agentId: a.id, startedAt: now - f.ago * MIN });
      procOutput(id, f.out);
      procEnd(id, { exitCode: f.exitCode, status: f.status ?? 'exited', endedAt: now - f.ago * MIN + f.dur * 1000 });
    }
    return { devId: dev, embId: emb, surveyor: e, main: a, reviewer: r, embN: 14 };
  }

  let timer = 0;
  let tickN = 0;
  function start() {
    clearInterval(timer);
    const s = seed();
    const acts = ['tool: grep', 'thinking', 'tool: read', 'writing', 'tool: find', 'thinking'];
    timer = setInterval(() => {
      tickN++;
      // the dev server logs a request now and then
      if (tickN % 2 === 0) {
        const p = procs.get(s.devId);
        if (p?.info.status === 'running') {
          const t = new Date().toLocaleTimeString('en-GB');
          procOutput(s.devId, `${t} [vite] hmr update /src/components/chat/ToolRow.svelte\n`);
        }
      }
      // the embedding script makes progress (foreground: polled by the UI through processes.output)
      const ep = procs.get(s.embId);
      if (ep?.info.status === 'running' && s.embN < 40) {
        s.embN++;
        procOutput(s.embId, `embedding docs/${['PROTOCOL.md', 'TOOLS.md', 'UI.md', 'PLUGIN-IDEAS.md'][s.embN % 4]} chunk ${s.embN}/40\n`);
        if (s.embN === 40) s.embN = 14; // loop forever (a mock)
      }
      // the surveyor keeps working
      if (tickN % 3 === 0 && s.surveyor.status === 'running') {
        s.surveyor.activity = acts[(tickN / 3) % acts.length];
        s.surveyor.turns++;
        if (s.surveyor.activity.startsWith('tool')) s.surveyor.toolCalls++;
        s.surveyor.inputTokens += 2400;
        publish('agent.status', { agent: s.surveyor });
      }
      if (tickN % 20 === 0) log('dbg', 'NetPI.Agents', `pool aiproxy/qwen3.8-27b: 2/2 busy, 1 waiting (reviewer ${Math.round((Date.now() - Date.parse(pools[0].waiters[0]?.since ?? Date.now())) / 1000)}s)`);
    }, 1000);
  }
  function stop() {
    clearInterval(timer);
  }

  // e2e helpers for the fixed-slot layout: a job ends (its slot goes to the first waiter of the pool, if any) and a job starts
  function releaseOwner(sessionId) {
    for (const p of pools) {
      const i = p.owners.findIndex((o) => o.sessionId === sessionId);
      if (i < 0) continue;
      const [gone] = p.owners.splice(i, 1);
      const next = p.waiters.shift();
      if (next) {
        p.owners.push({ ...next, since: iso(Date.now()) });
        const run = agentFor(next.sessionId);
        Object.assign(run, { status: 'running', activity: 'thinking' });
        publish('agent.status', { agent: run });
      }
      agentsChanged();
      return { pool: p.key, owner: gone, promoted: next?.sessionId ?? null, promotedOwner: next ?? null };
    }
    return null;
  }
  function takeSlot(poolKey, owner, waiting = false) {
    const p = pools.find((x) => x.key === poolKey);
    if (!p) return false;
    const entry = { ...owner, since: owner.since ?? iso(Date.now()) };
    if (waiting) {
      p.waiters.push(entry);
      const run = agentFor(owner.sessionId);
      Object.assign(run, { status: 'queued', activity: 'waiting for agent qwen' });
      publish('agent.status', { agent: run });
    } else p.owners.push(entry);
    agentsChanged();
    return true;
  }

  // runs waiting for any of several agents (e2e: mock.workUnassign puts one there)
  const floating = floating0;
  const unassigned = () => floating;
  const setUnassigned = (list) => {
    floating.splice(0, floating.length, ...list);
    agentsChanged();
    return true;
  };

  return {
    slots,
    unassigned,
    setUnassigned,
    agentsChanged,
    releaseOwner,
    takeSlot,
    procStart,
    procOutput,
    procEnd,
    procList,
    procOutputTail,
    procStats,
    procKill,
    recordUsage,
    usageSummary,
    start,
    stop,
    modelStatus,
  };
}
