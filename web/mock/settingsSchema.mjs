// settings.schema for the mock: a representative part of what the host and the plugins declare (SettingsSection[]).
const S = (key, type, label, extra = {}) => ({ key, type, label, ...extra });

// the built-in texts, as the context and AGENTS.md plugins declare them
export const IDENTITY =
  "You are a coding agent running in NetPI, an agent harness on the user's own machine. " +
  'Work through the tools you have: act rather than describe, check the results and verify your work when practical. ' +
  'Ask only when a request is genuinely ambiguous or an action would be destructive. ' +
  'Be concise, and end with a short summary of what you did or found.';
const AGENTS_MD =
  'AGENTS.md and CLAUDE.md files reach you as notices. They are the lean entry point for agents: the essentials, plus ' +
  'pointers to deeper docs. When your task touches something they point to, read that doc first.';

export const SETTINGS_SCHEMA = [
  {
    id: 'core',
    title: 'NetPI',
    group: 'General',
    order: 0,
    settings: [
      S('defaultModel', 'model', 'Default model', { help: 'For sessions that have none.', placeholder: 'first available' }),
      S('workspace.default', 'folder', 'Default working folder', { help: 'For sessions without a project.', placeholder: 'C:\\Users\\me\\.netpi\\workspace' }),
      S('logging.level', 'choice', 'Log level', { default: 'Information', options: ['Trace', 'Debug', 'Information', 'Warning', 'Error'] }),
      S('server.port', 'int', 'Port', { default: 7431, min: 1, max: 65535, applies: 'restart' }),
    ],
  },
  {
    id: 'budget',
    title: 'Budget',
    group: 'Models',
    order: 20,
    settings: [
      S('budget.monthlyUsd', 'number', 'Monthly budget', { help: 'Spend on paid models per month; empty = no limit.', min: 0, unit: '$' }),
      S('budget.dailyUsd', 'number', 'Daily budget', { min: 0, unit: '$' }),
      S('budget.resetDay', 'int', 'The month starts on day', { default: 1, min: 1, max: 28 }),
      S('budget.warnPercent', 'int', 'Warn at', { default: 80, min: 1, max: 100, unit: '%' }),
      S('budget.onLimit', 'choice', 'When the budget is spent', { default: 'stop', options: ['stop', 'ask'] }),
    ],
  },
  {
    id: 'aiproxy',
    title: 'AiProxy / nInfer',
    group: 'Models',
    order: 30,
    settings: [
      S('providers.aiproxy.baseUrl', 'string', 'Server URL', { default: 'http://127.0.0.1:8090', help: 'A trailing /v1 is fine.' }),
      S('providers.aiproxy.transport', 'choice', 'Transport', { default: 'responses', options: ['responses', 'chat'] }),
      S('providers.aiproxy.enabled', 'bool', 'Enabled', { default: true }),
    ],
  },
  {
    id: 'openrouter',
    title: 'OpenRouter',
    group: 'Models',
    order: 40,
    settings: [
      S('providers.openrouter.apiKey', 'secret', 'API key', { placeholder: 'env OPENROUTER_API_KEY', help: 'env:NAME reads another environment variable.' }),
      S('providers.openrouter.include', 'list', 'Models to offer', { placeholder: 'all models with tool calls' }),
      S('providers.openrouter.maxOutputTokens', 'int', 'Output limit', { default: 32768, min: 256, unit: 'tokens' }),
      S('providers.openrouter.enabled', 'bool', 'Enabled', { default: true }),
    ],
  },
  {
    id: 'retry',
    title: 'Retries',
    group: 'Models',
    order: 60,
    settings: [S('retry.maxAttempts', 'int', 'Attempts', { default: 4, min: 1, max: 10 })],
  },
  {
    id: 'agents',
    title: 'Runs',
    group: 'Agents',
    order: 10,
    settings: [
      S('agent.maxTurns', 'int', 'Model calls per run', { default: 200, min: 1, max: 10000, help: 'A run stops after this many.' }),
      S('agents.maxDepth', 'int', 'Subagent depth', { default: 3, min: 1, max: 10 }),
      S('agent.parallelReadOnlyTools', 'bool', 'Run read-only tools in parallel', { default: true }),
    ],
  },
  {
    id: 'goals',
    title: 'Goals',
    group: 'Agents',
    order: 20,
    settings: [S('goal.maxContinuations', 'int', 'Automatic runs before a goal pauses', { default: 100, min: 1 })],
  },
  {
    id: 'context',
    title: 'System prompt',
    group: 'Context',
    order: 10,
    settings: [
      S('context.appendPrompt', 'text', 'Custom instructions', { help: "Added to the end of every session's system prompt.", applies: 'new sessions' }),
      S('context.customPrompt', 'text', 'Identity', { help: 'The opening of the system prompt: who the agent is and how it works. A profile can have its own.', default: IDENTITY, applies: 'new sessions' }),
    ],
  },
  {
    id: 'agentsMd',
    title: 'AGENTS.md',
    group: 'Context',
    order: 20,
    settings: [S('agentsMd.guidance', 'text', 'How agents use AGENTS.md', { help: 'The "# Instruction files" section of the system prompt; empty leaves it out.', default: AGENTS_MD, applies: 'new sessions' })],
  },
  {
    id: 'shell',
    title: 'Shell',
    group: 'Tools',
    order: 20,
    settings: [
      S('shell.timeoutSeconds', 'int', 'Default timeout', { default: 120, min: 1, max: 1800, unit: 's' }),
      S('shell.bashPath', 'file', 'bash', { help: 'Git Bash on Windows, never WSL. Empty: the one found.', placeholder: 'C:\\Program Files\\Git\\bin\\bash.exe' }),
    ],
  },
  {
    id: 'web',
    title: 'Web',
    group: 'Tools',
    order: 30,
    settings: [
      S('web.search.provider', 'choice', 'Search with', { default: 'auto', options: ['auto', 'searxng', 'brave'] }),
      S('web.search.searxngUrl', 'string', 'SearXNG URL', { placeholder: 'e.g. http://192.168.1.3:8888' }),
    ],
  },
];
