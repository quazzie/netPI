// Mock diagnostics data: plugin manager state, tool registrations, runtime numbers.
import path from 'node:path';
import { REPO } from './store.mjs';

const PLUGINS = [
  ['netpi.providers.aiproxy', 'AiProxy provider', 10, 'NetPI.Providers.AiProxy'],
  ['netpi.providers.anthropic', 'Anthropic provider', 10, 'NetPI.Providers.Anthropic'],
  ['netpi.retry', 'Retry', 15, 'NetPI.Retry'],
  ['netpi.tools.files', 'File tools', 20, 'NetPI.Tools.Files'],
  ['netpi.tools.shell', 'Shell tools', 20, 'NetPI.Tools.Shell'],
  ['netpi.lanes', 'Agents', 30, 'NetPI.Lanes'],
  ['netpi.context', 'Context', 40, 'NetPI.Context'],
  ['netpi.agentsmd', 'AGENTS.md', 40, 'NetPI.AgentsMd'],
  ['netpi.agent', 'Agent runtime', 50, 'NetPI.Agent'],
  ['netpi.tools.agents', 'Agent tools', 55, 'NetPI.Tools.Agents'],
  ['netpi.compaction', 'Compaction', 60, 'NetPI.Compaction'],
  ['netpi.nudge', 'Nudge', 60, 'NetPI.Nudge'],
  ['netpi.toolrepair', 'Tool repair', 60, 'NetPI.ToolRepair'],
  ['netpi.ideas', 'Ideas', 80, 'NetPI.Ideas'],
  ['netpi.work', 'Work', 80, 'NetPI.Work'],
  ['netpi.diagnostics', 'Diagnostics', 90, 'NetPI.Diagnostics'],
  ['netpi.sample', 'Sample plugin', 100, 'sample-plugin'],
];

export function createDiag({ publish, log }) {
  const started = Date.now();
  let plugins = [];

  function seed() {
    const t0 = Date.now() - 40 * 60_000;
    plugins = PLUGINS.map(([id, name, order, folder], i) => ({
      id,
      name,
      description: `${name} plugin`,
      version: '0.1.0',
      directory: folder === 'sample-plugin' ? path.join(REPO, 'web/mock/sample-plugin') : path.join(REPO, 'artifacts/app/plugins', folder),
      assembly: `${folder}.dll`,
      state: 'running',
      loadedAt: new Date(t0 + i * 20).toISOString(),
      loadCount: 1,
      loadMs: [14, 9, 3, 22, 11, 8, 2, 1, 19, 4, 3, 1, 2, 8, 0, 1, 12][i] ?? 5,
      order,
      enabled: true,
    }));
    const tr = plugins.find((p) => p.id === 'netpi.toolrepair');
    Object.assign(tr, {
      state: 'failed',
      loadCount: 2,
      error: "System.IO.FileNotFoundException: Could not load file or assembly 'Json.More, Version=2.1.0.0'. The system cannot find the file specified.",
    });
    const nudge = plugins.find((p) => p.id === 'netpi.nudge');
    Object.assign(nudge, { state: 'disabled', enabled: false, loadedAt: undefined, loadCount: 0, loadMs: 0 });
    plugins.find((p) => p.id === 'netpi.ideas').loadCount = 3; // hot-reloaded twice
    for (const p of plugins)
      if (p.state === 'running') log('inf', 'NetPI.Plugins', `Plugin ${p.id} ${p.version} started in ${p.loadMs} ms`);
    log('err', 'NetPI.Plugins', `Plugin netpi.toolrepair failed to start: ${tr.error}`, tr.error + '\n   at NetPI.Host.Plugins.PluginManager.StartAsync(PluginEntry e)');
    log('inf', 'NetPI.Host', 'NetPI 0.1.0-mock ready at http://127.0.0.1:7431');
    log('wrn', 'plugin:netpi.providers.aiproxy', 'aiproxy: model gemma-4 is unloaded; the first request will load it');
  }

  const list = () => plugins;

  function reload(id) {
    const p = plugins.find((x) => x.id === id || x.id.endsWith(`.${id}`) || x.name.toLowerCase() === String(id).toLowerCase());
    if (!p) {
      const e = new Error(`Unknown plugin "${id}". Known: ${plugins.map((x) => x.id).join(', ')}`);
      e.code = 'not_found';
      throw e;
    }
    p.state = 'loading';
    publish('plugins.changed', {});
    setTimeout(() => {
      if (!p.enabled) p.state = 'disabled';
      else if (p.id === 'netpi.toolrepair') p.state = 'failed';
      else {
        p.state = 'running';
        delete p.error;
      }
      p.loadCount++;
      p.loadedAt = new Date().toISOString();
      p.loadMs = 5 + Math.floor(Math.random() * 20);
      log(p.state === 'failed' ? 'err' : 'inf', 'NetPI.Plugins', p.state === 'failed' ? `Plugin ${p.id} failed to start: ${p.error}` : `Plugin ${p.id} ${p.version} reloaded in ${p.loadMs} ms`);
      publish('plugins.changed', {});
      publish('ui.changed', {});
    }, 350);
    return p;
  }

  function setEnabled(id, enabled) {
    const p = plugins.find((x) => x.id === id);
    if (!p) return false;
    p.enabled = !!enabled;
    p.state = enabled ? 'running' : 'disabled';
    if (enabled) {
      p.loadCount++;
      p.loadedAt = new Date().toISOString();
    }
    log('inf', 'NetPI.Plugins', `Plugin ${id} ${enabled ? 'enabled' : 'disabled'}`);
    publish('plugins.changed', {});
    return true;
  }

  function runtime() {
    const m = process.memoryUsage();
    return {
      pid: process.pid,
      framework: `.NET 10.0.12 (mock on Node ${process.versions.node})`,
      os: `${process.platform} ${process.arch}`,
      workingSetMb: Math.round(m.rss / 1048576),
      gcHeapMb: Math.round(m.heapUsed / 1048576),
      threads: 21,
      uptimeSeconds: Math.round((Date.now() - started) / 1000),
    };
  }

  return { seed, list, reload, setEnabled, runtime };
}

/** tools.list rows, including a shadowed registration and a disabled tool. */
export function toolRows() {
  const rows = [
    ['read', 'Read', 'files', true, 'netpi.tools.files'],
    ['write', 'Write', 'files', false, 'netpi.tools.files'],
    ['edit', 'Edit', 'files', false, 'netpi.tools.files'],
    ['grep', 'Grep', 'files', true, 'netpi.tools.files'],
    ['find', 'Find', 'files', true, 'netpi.tools.files'],
    ['ls', 'List', 'files', true, 'netpi.tools.files'],
    ['bash', 'Bash', 'shell', false, 'netpi.tools.shell'],
    ['pwsh', 'PowerShell', 'shell', false, 'netpi.tools.shell'],
    ['process_list', 'Processes', 'shell', true, 'netpi.tools.shell'],
    ['process_output', 'Process output', 'shell', true, 'netpi.tools.shell'],
    ['process_kill', 'Kill process', 'shell', false, 'netpi.tools.shell'],
    ['agent_spawn', 'Spawn agent', 'agents', false, 'netpi.tools.agents'],
    ['agent_wait', 'Wait for agents', 'agents', true, 'netpi.tools.agents'],
    ['agent_send', 'Message agent', 'agents', false, 'netpi.tools.agents'],
    ['agent_list', 'Agents', 'agents', true, 'netpi.tools.agents'],
    ['agent_result', 'Agent result', 'agents', true, 'netpi.tools.agents'],
    ['agent_cancel', 'Cancel agent', 'agents', false, 'netpi.tools.agents'],
    ['agent_choices', 'Agents', 'agents', true, 'netpi.lanes'],
    ['idea_add', 'Add idea', 'ideas', false, 'netpi.ideas'],
    ['idea_list', 'Ideas', 'ideas', true, 'netpi.ideas'],
    ['idea_get', 'Idea', 'ideas', true, 'netpi.ideas'],
    ['idea_update', 'Update idea', 'ideas', false, 'netpi.ideas'],
    ['idea_remove', 'Remove idea', 'ideas', false, 'netpi.ideas'],
  ].map(([name, label, category, readOnly, pluginId]) => ({
    name,
    label,
    description: `${label} (${category} tool from ${pluginId}).`,
    category,
    readOnly,
    pluginId,
    active: true,
    disabled: false,
    priority: 0,
  }));
  // a plugin overrides `grep` with a higher priority → the built-in registration is shadowed
  rows.push({ name: 'grep', label: 'Grep (ripgrep)', description: 'ripgrep-backed grep with the same arguments.', category: 'files', readOnly: true, pluginId: 'netpi.sample', active: true, disabled: false, priority: 10 });
  rows.find((r) => r.name === 'grep' && r.pluginId === 'netpi.tools.files').active = false;
  rows.find((r) => r.name === 'pwsh').disabled = true;
  return rows;
}
