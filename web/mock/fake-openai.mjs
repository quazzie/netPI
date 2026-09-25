// Scripted OpenAI-compatible model server (GET /v1/models, streaming POST /v1/chat/completions) for exercising
// the REAL host without a model: runs, subagents, lanes, processes and usage all become genuine.
//
//   node web/mock/fake-openai.mjs [port=7468]
//   rpc settings.set { path: "providers.aiproxy", value: { baseUrl: "http://127.0.0.1:7468", transport: "chat" } }
//
// Models: qwen3.8-27b (loaded, concurrency 2) and gemma-4 (unloaded). A marker in the user message picks the script:
//   [demo]      background bash + 3 subagents (2 slots → one queued), foreground bash, agent_wait, final answer
//   [sub-slow]  ls, a ~30s bash, report          [sub-fast]  answers right away
//   [fail]      HTTP 500                          anything else: echoes the message
// NOTE: [demo] runs shell commands (echo/sleep loops, `cat plugins/*/*.cs | wc -l`) in the session's project folder.
import http from 'node:http';

const PORT = Number(process.argv[2] ?? 7468);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let seq = 0;

const MODELS = [
  { id: 'qwen3.8-27b', object: 'model', status: { value: 'loaded' }, context_window: 131072, concurrency: 2 },
  { id: 'gemma-4', object: 'model', status: 'unloaded', context_window: 32768, concurrency: 1 },
];

const call = (name, args) => ({ name, args });

function script(first, turn) {
  if (first.includes('[demo]')) {
    return [
      {
        think: 'The user wants the dev server running, a survey of the plugins and a test run. I will start the server in the background and hand the survey to subagents.',
        text: 'Starting the dev server and spawning three subagents for the survey.',
        calls: [
          call('bash', { command: 'for i in $(seq 1 900); do echo "[vite] hmr update /src/App.svelte ($i)"; sleep 1; done', background: true }),
          call('agent_spawn', { name: 'surveyor', task: '[sub-slow] Survey plugins/ and list every plugin with its start order and the RPC methods it registers.' }),
          call('agent_spawn', { name: 'reviewer', task: '[sub-slow] Review plugins/NetPI.Agents for races between Acquire and Release; report findings with file:line.' }),
          call('agent_spawn', { name: 'docs-writer', task: '[sub-fast] Check docs/PROTOCOL.md for RPC methods that are missing from the table.' }),
        ],
      },
      {
        think: 'While they work, run the test suite in the foreground.',
        calls: [call('bash', { command: 'for i in $(seq 1 60); do echo "  ok $i - AgentScheduler handles case $i"; sleep 0.5; done; echo "60 passed"', timeout: 120 })],
      },
      { think: 'Tests pass. Now wait for the subagents.', calls: [call('agent_wait', { timeout: 600 })] },
      { think: 'All reports are in.', text: 'Done. The dev server is still running in the background; the survey and review reports are above.' },
    ][turn];
  }
  if (first.includes('[sub-slow]')) {
    return [
      { think: 'Look at the layout first.', calls: [call('ls', { path: 'plugins' })] },
      { think: 'Count lines per plugin.', calls: [call('bash', { command: 'for d in plugins/*/; do echo "$d $(cat $d*.cs 2>/dev/null | wc -l)"; sleep 1.5; done; sleep 20', timeout: 120 })] },
      { text: '## Report\n\n- 17 plugins, start orders 10 to 90\n- No blocking issues found.' },
    ][turn];
  }
  if (first.includes('[sub-fast]')) {
    return [{ think: 'Quick check.', text: 'All RPC methods in the code are documented in docs/PROTOCOL.md.' }][turn];
  }
  if (first.includes('[fail]')) return { error: true };
  return [{ think: 'Simple question.', text: `Echo: ${first.slice(0, 120)}` }][turn] ?? { text: 'OK.' };
}

function text(content) {
  if (typeof content === 'string') return content;
  if (Array.isArray(content)) return content.map((p) => p.text ?? '').join('');
  return '';
}

async function chat(req, res, body) {
  const msgs = body.messages ?? [];
  // the last real user message picks the script; assistant messages after it give the turn
  let last = -1;
  msgs.forEach((m, i) => { if (m.role === 'user' && /\[(demo|sub-slow|sub-fast|fail)\]/.test(text(m.content))) last = i; });
  if (last < 0) last = msgs.findLastIndex((m) => m.role === 'user');
  const firstUser = text(msgs[last]?.content);
  const turn = msgs.slice(last + 1).filter((m) => m.role === 'assistant').length;
  const step = script(firstUser, turn) ?? { text: 'Nothing left to do.' };
  if (step.error) {
    res.writeHead(500, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ error: { message: 'scripted failure' } }));
    return;
  }
  const id = `chatcmpl-${++seq}`;
  res.writeHead(200, { 'content-type': 'text/event-stream', 'cache-control': 'no-cache', connection: 'keep-alive' });
  const send = (delta, extra = {}) =>
    res.write(`data: ${JSON.stringify({ id, object: 'chat.completion.chunk', model: body.model, choices: [{ index: 0, delta, finish_reason: null }], ...extra })}\n\n`);
  const chunks = (s, n) => s.match(new RegExp(`[\\s\\S]{1,${n}}`, 'g')) ?? [];
  let out = 0;
  for (const c of chunks(step.think ?? '', 6)) {
    send({ reasoning_content: c });
    out += 2;
    await sleep(45);
  }
  for (const c of chunks(step.text ?? '', 5)) {
    send({ content: c });
    out += 2;
    await sleep(35);
  }
  (step.calls ?? []).forEach((c, i) => {
    const args = JSON.stringify(c.args);
    send({ tool_calls: [{ index: i, id: `call_${seq}_${i}`, type: 'function', function: { name: c.name, arguments: '' } }] });
    for (const a of chunks(args, 24)) send({ tool_calls: [{ index: i, function: { arguments: a } }] });
    out += Math.ceil(args.length / 4);
  });
  res.write(`data: ${JSON.stringify({ id, object: 'chat.completion.chunk', choices: [{ index: 0, delta: {}, finish_reason: step.calls?.length ? 'tool_calls' : 'stop' }] })}\n\n`);
  const prompt = Math.round(JSON.stringify(msgs).length / 4) + 1800;
  res.write(`data: ${JSON.stringify({ id, object: 'chat.completion.chunk', choices: [], usage: { prompt_tokens: prompt, completion_tokens: out + 40, total_tokens: prompt + out + 40, prompt_tokens_details: { cached_tokens: Math.round(prompt * 0.6) } } })}\n\n`);
  res.write('data: [DONE]\n\n');
  res.end();
}

http
  .createServer(async (req, res) => {
    let raw = '';
    for await (const c of req) raw += c;
    const url = req.url.replace(/\?.*$/, '');
    console.log(new Date().toISOString(), req.method, url, raw.length);
    if (req.method === 'GET' && /\/models$/.test(url)) {
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ object: 'list', data: MODELS }));
      return;
    }
    if (req.method === 'POST' && /\/chat\/completions$/.test(url)) return chat(req, res, JSON.parse(raw || '{}'));
    res.writeHead(404, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ error: { message: `no route ${req.method} ${url}` } }));
  })
  .listen(PORT, '127.0.0.1', () => console.log(`fake openai on ${PORT}`));
