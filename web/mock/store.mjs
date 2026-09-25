// In-memory data for the mock server + seeding.
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import * as C from './content.mjs';

export const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');

let idCounter = 0;
export const newId = (prefix) => `${prefix}_${(Date.now() + idCounter++).toString(36)}${Math.random().toString(36).slice(2, 6)}`;

const iso = (ms) => new Date(ms).toISOString();

export const store = {
  projects: new Map(),
  sessions: new Map(),
  messages: new Map(), // sessionId -> ChatMessage[]
  agents: new Map(), // sessionId -> AgentInfo
  queues: new Map(), // sessionId -> QueuedInput[]
  uiState: new Map(),
  settings: {
    defaultModel: 'aiproxy/qwen3.8-27b',
    agents: {
      qwen: { model: 'aiproxy/qwen3.8-27b', use: 'The local model: free, for everyday work.' },
      gemma: { model: 'aiproxy/gemma-4', use: 'Quick answers.' },
    },
    providers: {
      aiproxy: { baseUrl: 'http://127.0.0.1:8090/v1', transport: 'responses' },
      anthropic: { apiKeyEnv: 'ANTHROPIC_API_KEY', concurrency: 4 },
    },
    shell: { timeoutSeconds: 120 },
    files: { newFileEol: 'lf' },
    retry: { maxAttempts: 5, baseDelaySeconds: 2 },
    ui: { port: 7431 },
  },
  nextMsgId: 1,
  seqs: new Map(),
};

export const MODELS = [
  {
    provider: 'aiproxy',
    id: 'qwen3.8-27b',
    ref: 'aiproxy/qwen3.8-27b',
    displayName: 'Qwen3.8 27B',
    contextWindow: 262144,
    maxOutputTokens: 32768,
    concurrency: 2,
    inputModalities: ['text', 'image'],
    reasoning: { supported: true, efforts: ['none', 'low', 'medium', 'xhigh'], default: 'medium' },
    status: 'loaded',
    isLocal: true,
  },
  {
    provider: 'aiproxy',
    id: 'gemma-4',
    ref: 'aiproxy/gemma-4',
    displayName: 'Gemma 4',
    contextWindow: 131072,
    maxOutputTokens: 8192,
    concurrency: 1,
    inputModalities: ['text', 'image'],
    reasoning: { supported: false, efforts: [] },
    status: 'unloaded',
    isLocal: true,
  },
  {
    provider: 'aiproxy',
    id: 'devstral-small',
    ref: 'aiproxy/devstral-small',
    displayName: 'Devstral Small',
    contextWindow: 131072,
    concurrency: 1,
    inputModalities: ['text'],
    reasoning: { supported: false, efforts: [] },
    status: 'stopped',
    isLocal: true,
  },
  {
    provider: 'anthropic',
    id: 'claude-sonnet-4-6',
    ref: 'anthropic/claude-sonnet-4-6',
    displayName: 'Claude Sonnet 4.6',
    contextWindow: 200000,
    maxOutputTokens: 64000,
    concurrency: 4,
    inputModalities: ['text', 'image'],
    reasoning: { supported: true, efforts: ['low', 'medium', 'high'], default: 'medium' },
    status: 'available',
    isLocal: false,
  },
];
export const DEFAULT_MODEL = 'aiproxy/qwen3.8-27b';

export function modelInfo(ref) {
  return MODELS.find((m) => m.ref === ref) ?? MODELS[0];
}

// ------------------------------------------------------------------------------------------ builders

export function mkMessage(sessionId, role, parts, extra = {}, at = Date.now()) {
  const seq = (store.seqs.get(sessionId) ?? 0) + 1;
  store.seqs.set(sessionId, seq);
  return {
    id: store.nextMsgId++,
    seq,
    sessionId,
    role,
    parts,
    createdAt: iso(at),
    compacted: false,
    ...extra,
  };
}

export function pushMessage(sessionId, role, parts, extra = {}, at = Date.now()) {
  const m = mkMessage(sessionId, role, parts, extra, at);
  let arr = store.messages.get(sessionId);
  if (!arr) store.messages.set(sessionId, (arr = []));
  arr.push(m);
  const s = store.sessions.get(sessionId);
  if (s) {
    s.messageCount = arr.length;
    s.updatedAt = m.createdAt;
  }
  return m;
}

export const text = (t) => ({ type: 'text', text: t });
export const thinking = (t, durationMs) => ({ type: 'thinking', text: t, durationMs });
export const call = (id, name, args) => ({ type: 'tool_call', id, name, arguments: JSON.stringify(args) });
export const result = (callId, name, content, details, extra = {}) => ({
  type: 'tool_result',
  callId,
  name,
  content,
  isError: false,
  details,
  durationMs: extra.durationMs ?? 40,
  ...extra,
});

export function usage(input, output, cache = 0) {
  return { inputTokens: input, outputTokens: output, cacheReadTokens: cache, cacheWriteTokens: 0, reasoningTokens: Math.round(output / 3) };
}

export function mkSession(o) {
  const now = new Date().toISOString();
  const s = {
    id: o.id ?? newId('ses'),
    title: o.title ?? '',
    projectId: o.projectId ?? null,
    parentSessionId: o.parentSessionId ?? null,
    kind: o.kind ?? 'chat',
    model: o.model ?? null,
    reasoning: o.reasoning ?? null,
    createdAt: o.createdAt ?? now,
    updatedAt: o.updatedAt ?? now,
    archived: !!o.archived,
    messageCount: 0,
    contextTokens: o.contextTokens ?? 0,
    meta: o.meta ?? null,
  };
  store.sessions.set(s.id, s);
  store.messages.set(s.id, []);
  return s;
}

export function agentFor(sessionId) {
  let a = store.agents.get(sessionId);
  if (!a) {
    const s = store.sessions.get(sessionId);
    a = {
      id: `ag_${sessionId.slice(4)}`,
      sessionId,
      name: s?.kind === 'subagent' ? (s.title.split(':')[0] || 'subagent') : 'main',
      parentAgentId: null,
      parentSessionId: s?.parentSessionId ?? null,
      isSubagent: s?.kind === 'subagent',
      depth: s?.kind === 'subagent' ? 1 : 0,
      status: 'idle',
      model: s?.model ?? DEFAULT_MODEL,
      agent: 'qwen',
      activity: null,
      createdAt: s?.createdAt ?? new Date().toISOString(),
      startedAt: null,
      finishedAt: null,
      runs: 0,
      turns: 0,
      toolCalls: 0,
      inputTokens: 0,
      outputTokens: 0,
      queuedMessages: 0,
      task: null,
      result: null,
      error: null,
      children: [],
    };
    store.agents.set(sessionId, a);
  }
  return a;
}

// ------------------------------------------------------------------------------------------ seed

const MIN = 60_000;
const HOUR = 60 * MIN;
const DAY = 24 * HOUR;

function shellDetails(command, exitCode, durationMs, cwd) {
  return {
    command,
    shell: 'bash',
    cwd,
    exitCode,
    durationMs,
    truncated: false,
    background: false,
    processId: newId('proc'),
    pid: 40000 + Math.floor(Math.random() * 9999),
    status: 'exited',
  };
}

/** One "cycle" of a realistic agent exchange (10 messages). */
function seedCycle(sid, i, t, cwd) {
  const prompt = C.USER_PROMPTS[i % C.USER_PROMPTS.length];
  const answer = C.ANSWERS[i % C.ANSWERS.length];
  const file = 'src/NetPI.Host/Lanes/AgentScheduler.cs';
  const c1 = newId('call'),
    c2 = newId('call'),
    c3 = newId('call'),
    c4 = newId('call');
  let at = t;
  const step = (ms) => (at += ms);
  pushMessage(sid, 'user', [text(`${prompt}${i >= C.USER_PROMPTS.length ? ` (round ${Math.floor(i / C.USER_PROMPTS.length) + 1})` : ''}`)], {}, step(0));
  pushMessage(
    sid,
    'assistant',
    [thinking(C.THINK_1, 2400 + (i % 5) * 300), text('Let me look at the scheduler first.'), call(c1, 'read', { path: file, offset: 1, limit: 40 })],
    { provider: 'aiproxy', model: 'qwen3.8-27b', stopReason: 'tool_use', usage: usage(12000 + i * 900, 180, 9000), durationMs: 4100 },
    step(4200),
  );
  pushMessage(
    sid,
    'tool',
    [result(c1, 'read', C.READ_CONTENT, { path: `${cwd}/${file}`, startLine: 1, endLine: 33, totalLines: 33, truncated: false, eol: 'lf', bom: false }, { durationMs: 6 })],
    {},
    step(50),
  );
  pushMessage(
    sid,
    'assistant',
    [call(c2, 'grep', { pattern: '_pools\\[|Release\\(', path: 'src', glob: '*.cs' })],
    { provider: 'aiproxy', model: 'qwen3.8-27b', stopReason: 'tool_use', usage: usage(13500 + i * 900, 60, 12000), durationMs: 1300 },
    step(1400),
  );
  pushMessage(
    sid,
    'tool',
    [result(c2, 'grep', C.GREP_OUTPUT, { pattern: '_pools\\[|Release\\(', path: `${cwd}/src`, outputMode: 'content', matches: 5, files: 3, filesSearched: 214, truncated: false }, { durationMs: 38 })],
    {},
    step(60),
  );
  pushMessage(
    sid,
    'assistant',
    [thinking(C.THINK_3, 1800), call(c3, 'edit', C.EDIT_ARGS)],
    { provider: 'aiproxy', model: 'qwen3.8-27b', stopReason: 'tool_use', usage: usage(14800 + i * 900, 420, 13000), durationMs: 5200 },
    step(5300),
  );
  pushMessage(
    sid,
    'tool',
    [result(c3, 'edit', 'Applied 2 edits to ' + file + ' (+6 −3)', { path: `${cwd}/${file}`, diff: C.EDIT_DIFF, added: 6, removed: 3, edits: 2, firstChangedLine: 19, eol: 'lf', bom: false }, { durationMs: 12 })],
    {},
    step(40),
  );
  const cmd = i % 3 === 2 ? 'dotnet test tests/NetPI.Tools.Tests' : 'dotnet build NetPI.slnx -nologo -v q';
  const fail = i % 3 === 2 && i % 2 === 0;
  const out = (fail ? C.TEST_OUTPUT_FAIL : C.BUILD_OUTPUT).join('\n');
  pushMessage(
    sid,
    'assistant',
    [call(c4, 'bash', { command: cmd, timeout: 300 })],
    { provider: 'aiproxy', model: 'qwen3.8-27b', stopReason: 'tool_use', usage: usage(15900 + i * 900, 50, 14000), durationMs: 900 },
    step(1000),
  );
  pushMessage(
    sid,
    'tool',
    [result(c4, 'bash', out + (fail ? '\n[exit code 1]' : ''), shellDetails(cmd, fail ? 1 : 0, 4210, cwd), { durationMs: 4210 })],
    {},
    step(4300),
  );
  pushMessage(
    sid,
    'assistant',
    [text(answer)],
    { provider: 'aiproxy', model: 'qwen3.8-27b', stopReason: 'stop', usage: usage(17000 + i * 900, 260, 15000), durationMs: 3900 },
    step(4000),
  );
  return at;
}

/** Drop everything (used by the mock.reset RPC) — call seed() afterwards. */
export function resetStore() {
  for (const k of ['projects', 'sessions', 'messages', 'agents', 'queues', 'uiState', 'seqs']) store[k].clear();
  store.nextMsgId = 1;
}

export function seed() {
  const now = Date.now();
  const netpi = { id: 'prj_netpi', name: 'netpi', path: REPO, createdAt: iso(now - 30 * DAY), updatedAt: iso(now - 2 * DAY), lastUsedAt: iso(now - 10 * MIN) };
  const aiproxy = { id: 'prj_aiproxy', name: 'aiproxy', path: 'C:\\src\\aiproxy', createdAt: iso(now - 60 * DAY), updatedAt: iso(now - 5 * DAY), lastUsedAt: iso(now - DAY) };
  const site = { id: 'prj_site', name: 'website', path: path.join(os.homedir(), 'src', 'website'), createdAt: iso(now - 90 * DAY), updatedAt: iso(now - 20 * DAY), lastUsedAt: iso(now - 4 * DAY) };
  for (const p of [netpi, aiproxy, site]) store.projects.set(p.id, p);

  // --- long session (300+ messages) for pruning / "Load earlier"
  const long = mkSession({ id: 'ses_long', title: 'Lane scheduler hardening', projectId: netpi.id, model: DEFAULT_MODEL, reasoning: 'medium', createdAt: iso(now - 6 * HOUR) });
  let t = now - 6 * HOUR;
  for (let i = 0; i < 32; i++) t = seedCycle(long.id, i, t, REPO) + 20_000;
  long.updatedAt = iso(now - 12 * MIN);
  long.contextTokens = 48_200;

  // --- showcase session: subagent, notices, errors, summary
  const show = mkSession({ id: 'ses_show', title: 'Fix streaming reconnect bug', projectId: netpi.id, model: DEFAULT_MODEL, createdAt: iso(now - 3 * HOUR) });
  const sub = mkSession({ id: 'ses_sub1', title: 'explorer: map websocket handlers', projectId: netpi.id, parentSessionId: show.id, kind: 'subagent', model: 'aiproxy/qwen3.8-27b', createdAt: iso(now - 170 * MIN) });
  const sub2 = mkSession({ id: 'ses_sub2', title: 'tester: reproduce disconnect', projectId: netpi.id, parentSessionId: show.id, kind: 'subagent', model: 'aiproxy/gemma-4', createdAt: iso(now - 165 * MIN) });
  t = now - 3 * HOUR;
  pushMessage(show.id, 'summary', [text('## Summary of earlier conversation\n\n- The UI loses stream events after a reconnect because `sub` is not re-sent.\n- We agreed to re-subscribe on `hello` and refetch the active session.\n- Open question: should the server replay missed events (it keeps a ring buffer)?')], { meta: { kind: 'compaction', upToSeq: 0 } }, t);
  pushMessage(show.id, 'user', [text('After a reconnect the chat stops updating until I switch tabs. Can you find out why? Use a subagent to map the websocket handlers while you look at the client.')], {}, (t += 60_000));
  const sa = newId('call');
  const sa2 = newId('call');
  pushMessage(show.id, 'assistant', [thinking('Two parallel tracks: a subagent maps the server side, I read the client reconnect code.', 1600), call(sa, 'agent_spawn', { name: 'explorer', task: 'Map every WebSocket handler in src/NetPI.Server: which ones handle `sub`, and what happens to subscriptions on reconnect. Report file:line references.', model: 'aiproxy/qwen3.8-27b' }), call(sa2, 'agent_spawn', { name: 'tester', task: 'Write a script that drops the websocket mid-stream and checks whether events resume.', model: 'aiproxy/gemma-4' })], { provider: 'aiproxy', model: 'qwen3.8-27b', stopReason: 'tool_use', usage: usage(8000, 300, 2000), durationMs: 3000, meta: { ttftMs: 1400 } }, (t += 3200));
  pushMessage(show.id, 'tool', [
    result(sa, 'agent_spawn', 'Spawned explorer (ses_sub1).', { sessionId: sub.id, agentId: 'ag_sub1', name: 'explorer', status: 'completed' }, { durationMs: 20 }),
    result(sa2, 'agent_spawn', 'Spawned tester (ses_sub2).', { sessionId: sub2.id, agentId: 'ag_sub2', name: 'tester', status: 'failed' }, { durationMs: 18 }),
  ], {}, (t += 100));
  const r1 = newId('call');
  pushMessage(show.id, 'assistant', [call(r1, 'read', { path: 'web/src/lib/rpc.svelte.js', offset: 60, limit: 40 })], { provider: 'aiproxy', model: 'qwen3.8-27b', stopReason: 'tool_use', usage: usage(9000, 60, 8000), durationMs: 800, meta: { ttftMs: 180 } }, (t += 900));
  pushMessage(show.id, 'tool', [result(r1, 'read', "  sock.onopen = () => {\n    conn.status = 'open';\n    flushOutbox();\n  };", { path: `${REPO}/web/src/lib/rpc.svelte.js`, startLine: 60, endLine: 63, totalLines: 240, truncated: true, eol: 'lf', bom: false }, { durationMs: 4 })], {}, (t += 50));
  const e1 = newId('call');
  pushMessage(show.id, 'assistant', [call(e1, 'edit', { path: 'web/src/lib/rpc.svelte.js', oldText: "    conn.status = 'open';\n    flushOutbox();", newText: "    conn.status = 'open';\n    resubscribe();\n    flushOutbox();" })], { provider: 'aiproxy', model: 'qwen3.8-27b', stopReason: 'tool_use', usage: usage(9500, 90, 9000), durationMs: 1200, meta: { ttftMs: 240 } }, (t += 1300));
  pushMessage(show.id, 'tool', [{ ...result(e1, 'edit', "Edit 1 of 1 failed in web/src/lib/rpc.svelte.js: oldText not found. The most similar line is 62: \"    conn.status = 'open';\" — include more surrounding context or re-read the file.", { path: `${REPO}/web/src/lib/rpc.svelte.js`, failedEdit: 1, edits: 1 }, { durationMs: 9 }), isError: true }], {}, (t += 40));
  pushMessage(show.id, 'notice', [text('You tried to edit a file you only partially read. Re-read the surrounding lines before editing again.')], { meta: { kind: 'nudge' } }, (t += 200));
  pushMessage(show.id, 'notice', [text('**explorer** finished:\n\n- `WsEndpoint.HandleAsync` (src/NetPI.Server/WsEndpoint.cs:41) owns the socket loop.\n- Subscriptions live in `ClientConnection.Subscriptions` and are **dropped** when the socket closes (line 88).\n- There is no replay: events published while disconnected are lost.\n\nRecommendation: the client must re-send `sub` on every (re)connect and refetch the active session.')], { meta: { kind: 'agent-result', sessionId: sub.id, agentName: 'explorer' } }, (t += 60_000));
  const b1 = newId('call');
  pushMessage(show.id, 'assistant', [thinking('The subagent confirms subscriptions are per-connection. Run the reconnect test script.', 900), call(b1, 'bash', { command: 'node web/mock/reconnect-test.mjs --drop-after 2s' })], { provider: 'aiproxy', model: 'qwen3.8-27b', stopReason: 'tool_use', usage: usage(11000, 100, 10000), durationMs: 1500, meta: { ttftMs: 260 } }, (t += 1600));
  pushMessage(show.id, 'tool', [result(b1, 'bash', 'connecting ws://127.0.0.1:7431/ws\nsubscribed ses_show\nstream.delta × 41\n-- dropping socket --\nreconnected in 412ms\nwaiting for events… (timeout 5s)\nFAIL: no events after reconnect\n[exit code 1]', shellDetails('node web/mock/reconnect-test.mjs --drop-after 2s', 1, 6120, REPO), { durationMs: 6120 })], {}, (t += 6200));
  pushMessage(show.id, 'assistant', [text("Found it. The server drops a connection's subscriptions when the socket closes, and the client never re-sends `sub` after reconnecting — so scoped events (`stream.*`, `message.added`) stop arriving for the open session until a tab switch triggers a new `sub`.\n\n**Fix:** re-send the subscription in `onopen` and refetch the active session's messages (events published while disconnected are not replayed). I've made that change; the reconnect test now passes.")], { provider: 'aiproxy', model: 'qwen3.8-27b', stopReason: 'stop', usage: usage(12000, 210, 11000), durationMs: 2600, meta: { ttftMs: 310 } }, (t += 2700));
  pushMessage(show.id, 'notice', [text(`Project changed to **aiproxy** (\`C:\\src\\aiproxy\`). The working directory is now \`C:\\src\\aiproxy\`.`)], { meta: { kind: 'project', projectId: aiproxy.id } }, (t += 30_000));
  pushMessage(show.id, 'notice', [text(`Project changed to **netpi** (\`${REPO}\`). The working directory is now \`${REPO}\`.`)], { meta: { kind: 'project', projectId: netpi.id } }, (t += 20_000));
  show.updatedAt = iso(now - 25 * MIN);
  show.contextTokens = 21_400;

  // subagent transcripts
  pushMessage(sub.id, 'user', [text('Map every WebSocket handler in src/NetPI.Server: which ones handle `sub`, and what happens to subscriptions on reconnect. Report file:line references.')], { meta: { source: 'agent:main' } }, now - 170 * MIN);
  const g1 = newId('call');
  pushMessage(sub.id, 'assistant', [call(g1, 'grep', { pattern: '"sub"|Subscriptions', path: 'src/NetPI.Server' })], { provider: 'aiproxy', model: 'qwen3.8-27b', stopReason: 'tool_use', usage: usage(3000, 40), durationMs: 700, meta: { ttftMs: 350 } }, now - 169 * MIN);
  pushMessage(sub.id, 'tool', [result(g1, 'grep', 'src/NetPI.Server/WsEndpoint.cs:41:    case "sub":\nsrc/NetPI.Server/ClientConnection.cs:22:    public HashSet<string> Subscriptions { get; } = [];\nsrc/NetPI.Server/ClientConnection.cs:88:        Subscriptions.Clear();', { pattern: '"sub"|Subscriptions', path: `${REPO}/src/NetPI.Server`, outputMode: 'content', matches: 3, files: 2, filesSearched: 12, truncated: false })], {}, now - 169 * MIN + 100);
  pushMessage(sub.id, 'assistant', [text('- `WsEndpoint.HandleAsync` (src/NetPI.Server/WsEndpoint.cs:41) owns the socket loop.\n- Subscriptions live in `ClientConnection.Subscriptions` and are **dropped** when the socket closes (line 88).\n- There is no replay.')], { provider: 'aiproxy', model: 'qwen3.8-27b', stopReason: 'stop', usage: usage(3500, 120), durationMs: 1900, meta: { ttftMs: 400 } }, now - 168 * MIN);
  pushMessage(sub2.id, 'user', [text('Write a script that drops the websocket mid-stream and checks whether events resume.')], { meta: { source: 'agent:main' } }, now - 165 * MIN);
  pushMessage(sub2.id, 'assistant', [text('I started on the script but the model backend went offline.')], { provider: 'aiproxy', model: 'gemma-4', stopReason: 'error', usage: usage(900, 30), durationMs: 120000, meta: { error: 'backend_unavailable: llama-local did not come back within the 2 minute grace window' } }, now - 163 * MIN);
  sub.updatedAt = iso(now - 168 * MIN);
  sub2.updatedAt = iso(now - 163 * MIN);

  // --- other sessions
  const cat = mkSession({ id: 'ses_catalog', title: 'AiProxy model catalog aliases', projectId: aiproxy.id, model: 'anthropic/claude-sonnet-4-6', createdAt: iso(now - DAY - 3 * HOUR) });
  t = now - DAY - 3 * HOUR;
  pushMessage(cat.id, 'user', [text('How do policy aliases pick a candidate when two backends are loaded?')], {}, t);
  pushMessage(cat.id, 'assistant', [thinking('Policy aliases resolve to a candidate list with conservative capability (worst candidate).', 2100), text('Policy aliases resolve to an ordered **candidate list**. At request time the proxy picks the first candidate whose backend is up and has the model loaded; if none is loaded it picks the first routable one and lets the router auto-load it.\n\nCapabilities (context window, modalities) are the *minimum* across candidates, so a client never sees a window larger than the weakest backend can serve.')], { provider: 'anthropic', model: 'claude-sonnet-4-6', stopReason: 'stop', usage: usage(4000, 320), durationMs: 7300 }, t + 8000);
  cat.updatedAt = iso(now - DAY - 2 * HOUR);

  const land = mkSession({ id: 'ses_landing', title: 'Landing page copy', projectId: site.id, createdAt: iso(now - 4 * DAY) });
  pushMessage(land.id, 'user', [text('Draft three hero headlines for the NetPI landing page.')], {}, now - 4 * DAY);
  pushMessage(land.id, 'assistant', [text('1. **Your agents, your hardware, your rules.**\n2. **A harness that gets out of the way.**\n3. **Hot-reload everything — even the agent.**')], { provider: 'aiproxy', model: 'qwen3.8-27b', stopReason: 'stop', usage: usage(600, 80), durationMs: 2100 }, now - 4 * DAY + 3000);
  land.updatedAt = iso(now - 4 * DAY + 3000);

  const scratch = mkSession({ id: 'ses_scratch', title: 'Scratch', createdAt: iso(now - 12 * DAY) });
  pushMessage(scratch.id, 'user', [text('What is 2^32?')], {}, now - 12 * DAY);
  pushMessage(scratch.id, 'assistant', [text('2³² = **4,294,967,296**.')], { provider: 'aiproxy', model: 'qwen3.8-27b', stopReason: 'stop', usage: usage(200, 12), durationMs: 600 }, now - 12 * DAY + 800);
  scratch.updatedAt = iso(now - 12 * DAY + 800);

  const old = mkSession({ id: 'ses_old', title: 'Old experiment (archived)', archived: true, createdAt: iso(now - 40 * DAY) });
  pushMessage(old.id, 'user', [text('hello')], {}, now - 40 * DAY);
  old.updatedAt = iso(now - 40 * DAY);

  // agents
  for (const s of store.sessions.values()) agentFor(s.id);
  Object.assign(agentFor(sub.id), { status: 'completed', name: 'explorer', task: 'Map every WebSocket handler', finishedAt: iso(now - 168 * MIN), parentAgentId: agentFor(show.id).id });
  Object.assign(agentFor(sub2.id), { status: 'failed', name: 'tester', error: 'backend_unavailable', finishedAt: iso(now - 163 * MIN), parentAgentId: agentFor(show.id).id });
  agentFor(show.id).children = [agentFor(sub.id).id, agentFor(sub2.id).id];
}
