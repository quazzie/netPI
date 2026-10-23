// Pure helpers for the MCP server editor (unit-testable: no Svelte, no DOM). The form edits a server's configuration as
// fields; anything the form does not know about (a key a newer plugin adds) is carried through untouched.

/** The configuration keys the form has a field for. */
const HANDLED = ['enabled', 'transport', 'command', 'args', 'cwd', 'url', 'env', 'headerEnv', 'connectTimeoutMs', 'callTimeoutMs', 'tools', 'resources', 'pinned', 'readOnly'];

/** Non-empty trimmed lines of a text box. */
export const lines = (text) => (text ?? '').split('\n').map((s) => s.trim()).filter(Boolean);

/** An object of name -> value as `NAME=VALUE` lines, and back. */
export const pairsToText = (obj) => Object.entries(obj ?? {}).map(([k, v]) => `${k}=${v}`).join('\n');

export function textToPairs(text) {
  const map = {};
  const bad = [];
  for (const line of lines(text)) {
    const i = line.indexOf('=');
    if (i <= 0 || i === line.length - 1) bad.push(line);
    else map[line.slice(0, i).trim()] = line.slice(i + 1).trim();
  }
  return { map, bad };
}

const list = (v) => (Array.isArray(v) ? v.join('\n') : '');

/** A configuration as the form's fields (every field a string, so a box can show it). */
export function configToForm(cfg = {}) {
  return {
    enabled: cfg.enabled !== false,
    transport: cfg.transport === 'http' || (!cfg.transport && cfg.url && !cfg.command) ? 'http' : 'stdio',
    command: cfg.command ?? '',
    args: list(cfg.args),
    cwd: cfg.cwd ?? '',
    url: cfg.url ?? '',
    env: pairsToText(cfg.env),
    headerEnv: pairsToText(cfg.headerEnv),
    connectTimeoutMs: cfg.connectTimeoutMs ?? '',
    callTimeoutMs: cfg.callTimeoutMs ?? '',
    tools: list(cfg.tools),
    // an empty list is "none exposed", which an empty box cannot say: keep it as its own flag
    toolsNone: Array.isArray(cfg.tools) && cfg.tools.length === 0,
    resources: list(cfg.resources),
    resourcesNone: Array.isArray(cfg.resources) && cfg.resources.length === 0,
    pinned: list(cfg.pinned),
    readOnly: list(cfg.readOnly),
  };
}

/**
 * The form's fields as a configuration, on top of `base` (the configuration the form started from, whose unknown keys
 * survive). Returns { config, errors }: errors say what to fix, and no config is meant to be saved while there are any.
 */
export function formToConfig(f, base = {}) {
  const errors = [];
  const config = {};
  for (const [k, v] of Object.entries(base)) if (!HANDLED.includes(k)) config[k] = v;
  config.enabled = !!f.enabled;
  config.transport = f.transport;
  if (f.transport === 'http') {
    const url = f.url.trim();
    if (!url) errors.push('The URL is required.');
    else if (!/^https?:\/\/\S+$/i.test(url)) errors.push('The URL must start with http:// or https://.');
    config.url = url;
    const headers = textToPairs(f.headerEnv);
    if (headers.bad.length) errors.push(`Headers are NAME=ENVIRONMENT_VARIABLE, one per line (“${headers.bad[0]}”).`);
    if (Object.keys(headers.map).length) config.headerEnv = headers.map;
  } else {
    const command = f.command.trim();
    if (!command) errors.push('The command is required.');
    config.command = command;
    if (lines(f.args).length) config.args = lines(f.args);
    if (f.cwd.trim()) config.cwd = f.cwd.trim();
  }
  const env = textToPairs(f.env);
  if (env.bad.length) errors.push(`Environment entries are NAME=ENVIRONMENT_VARIABLE, one per line (“${env.bad[0]}”).`);
  if (Object.keys(env.map).length) config.env = env.map;
  for (const key of ['connectTimeoutMs', 'callTimeoutMs']) {
    const raw = String(f[key] ?? '').trim();
    if (!raw) continue;
    const n = Number(raw);
    if (!Number.isInteger(n) || n < 100) errors.push(`${key === 'connectTimeoutMs' ? 'Connect' : 'Call'} timeout must be a whole number of milliseconds (at least 100).`);
    else config[key] = n;
  }
  for (const key of ['tools', 'resources', 'pinned', 'readOnly']) if (lines(f[key]).length) config[key] = lines(f[key]);
  // an absent list exposes everything and an empty one nothing: a box left empty keeps "nothing" until it is cleared on purpose
  if (f.toolsNone && !config.tools) config.tools = [];
  if (f.resourcesNone && !config.resources) config.resources = [];
  return { config, errors };
}
