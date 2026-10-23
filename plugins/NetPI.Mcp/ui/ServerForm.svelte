<script>
  // The add/edit dialog of an MCP server: fields for what a server needs (how to reach it, what to pass it), the limits and
  // tool lists folded away, and a JSON view for anything the fields do not cover. The configuration is the plugin's own
  // `mcp.servers` entry; unknown keys in it are carried through untouched (config.js).
  import { Modal, Segmented, Collapsible, PickList } from '@netpi/kit';
  import { configToForm, formToConfig, lines } from './config.js';

  /** tools: the tools the server offers now (mcp.tools), for the tool lists to choose from; a name can still be typed. */
  let { isNew = false, initialId = '', base = {}, tools = [], busy = false, error = '', onsave, oncancel } = $props();
  const toolChoices = $derived(tools.map((t) => ({ value: t.name, hint: (t.description ?? '').split('\n')[0].slice(0, 90) })));

  // svelte-ignore state_referenced_locally
  let id = $state(initialId);
  // svelte-ignore state_referenced_locally
  let origin = $state(base);
  // svelte-ignore state_referenced_locally
  let f = $state(configToForm(base));
  let mode = $state('form');
  let json = $state('');
  let advanced = $state(false);
  let problems = $state([]);

  const transports = [
    { value: 'stdio', label: 'Command (stdio)' },
    { value: 'http', label: 'Web (HTTP)' },
  ];

  function toJson() {
    problems = [];
    json = JSON.stringify(formToConfig(f, origin).config, null, 2);
    mode = 'json';
  }
  function toForm() {
    try {
      const parsed = JSON.parse(json);
      if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) throw new Error('it must be an object');
      origin = parsed;
      f = configToForm(parsed);
      problems = [];
      mode = 'form';
    } catch (e) {
      problems = [`The JSON is not valid: ${e.message}`];
    }
  }
  function submit() {
    problems = [];
    if (!/^[A-Za-z0-9_-]{1,48}$/.test(id)) {
      problems = ['The name must be 1–48 characters of letters, digits, - or _.'];
      return;
    }
    let config;
    if (mode === 'json') {
      try {
        config = JSON.parse(json);
        if (!config || typeof config !== 'object' || Array.isArray(config)) throw new Error('it must be an object');
      } catch (e) {
        problems = [`The JSON is not valid: ${e.message}`];
        return;
      }
    } else {
      const built = formToConfig(f, origin);
      if (built.errors.length) {
        problems = built.errors;
        return;
      }
      config = built.config;
    }
    onsave?.(id, config);
  }
  const shown = $derived(problems[0] ?? error);
</script>

<Modal title={isNew ? 'Add MCP server' : `Edit ${id}`} width={600} onclose={() => !busy && oncancel?.()}>
  <form id="mcp-edit" class="form" onsubmit={(e) => { e.preventDefault(); submit(); }}>
    <div class="top">
      <label class="fld grow">
        <span class="f-label">Name</span>
        <input class="np-input" bind:value={id} readonly={!isNew} placeholder="e.g. homeassistant" spellcheck="false" />
      </label>
      <label class="np-check enabled"><input type="checkbox" bind:checked={f.enabled} disabled={mode === 'json'} /><span>Enabled</span></label>
    </div>

    {#if mode === 'form'}
      <div class="fld">
        <span class="f-label">How it is reached</span>
        <Segmented options={transports} bind:value={f.transport} fit={false} />
      </div>

      {#if f.transport === 'http'}
        <label class="fld">
          <span class="f-label">URL</span>
          <input class="np-input np-mono" bind:value={f.url} placeholder="http://192.168.1.6:9583/…" spellcheck="false" />
          <span class="np-small np-dim">Use an IP address where you can: a .local name is resolved by mDNS and can be slow.</span>
        </label>
        <label class="fld">
          <span class="f-label">Headers</span>
          <textarea class="np-input np-mono" rows="2" bind:value={f.headerEnv} placeholder="Authorization=MY_SERVER_TOKEN" spellcheck="false"></textarea>
          <span class="np-small np-dim">One per line: HEADER=NAME of an environment variable on this PC that holds the value. The credential itself never goes into settings.</span>
        </label>
      {:else}
        <label class="fld">
          <span class="f-label">Command</span>
          <input class="np-input np-mono" bind:value={f.command} placeholder="npx, uvx, a program on this PC" spellcheck="false" />
        </label>
        <label class="fld">
          <span class="f-label">Arguments</span>
          <textarea class="np-input np-mono" rows="3" bind:value={f.args} placeholder="one argument per line" spellcheck="false"></textarea>
        </label>
        <label class="fld">
          <span class="f-label">Working directory</span>
          <input class="np-input np-mono" bind:value={f.cwd} placeholder="absolute path (optional)" spellcheck="false" />
        </label>
      {/if}

      <label class="fld">
        <span class="f-label">Environment for the server</span>
        <textarea class="np-input np-mono" rows="2" bind:value={f.env} placeholder="API_KEY=NAME_OF_MY_ENV_VARIABLE" spellcheck="false"></textarea>
        <span class="np-small np-dim">One per line: VARIABLE=NAME of an environment variable on this PC to copy into the server's.</span>
      </label>

      <Collapsible title="Limits and tool lists" bind:open={advanced}>
        <div class="adv">
          <div class="pair">
            <label class="fld">
              <span class="f-label">Connect timeout (ms)</span>
              <input class="np-input" bind:value={f.connectTimeoutMs} placeholder="5000" inputmode="numeric" />
            </label>
            <label class="fld">
              <span class="f-label">Call timeout (ms)</span>
              <input class="np-input" bind:value={f.callTimeoutMs} placeholder="60000" inputmode="numeric" />
            </label>
          </div>
          <div class="fld">
            <span class="f-label">Only these tools</span>
            <PickList value={lines(f.tools)} candidates={toolChoices} placeholder={f.toolsNone ? 'No tools exposed' : 'Every tool the server offers'} label="Only these tools" onchange={(v) => { f.tools = v.join('\n'); f.toolsNone = false; }} />
            {#if f.toolsNone}<button type="button" class="np-btn np-btn-sm np-btn-ghost" onclick={() => (f.toolsNone = false)}>Expose every tool</button>{/if}
          </div>
          <div class="fld">
            <span class="f-label">Schemas always in the chat</span>
            <PickList value={lines(f.pinned)} candidates={toolChoices} label="Schemas always in the chat" onchange={(v) => (f.pinned = v.join('\n'))} />
          </div>
          <div class="fld">
            <span class="f-label">Safe to call at the same time (read-only)</span>
            <PickList value={lines(f.readOnly)} candidates={toolChoices} label="Safe to call at the same time (read-only)" onchange={(v) => (f.readOnly = v.join('\n'))} />
          </div>
          <label class="fld">
            <span class="f-label">Resources</span>
            <textarea class="np-input np-mono" rows="2" bind:value={f.resources} oninput={() => (f.resourcesNone = false)} placeholder={f.resourcesNone ? 'none exposed' : 'a URI, or a prefix ending in *; empty = all'} spellcheck="false"></textarea>
            {#if f.resourcesNone}<button type="button" class="np-btn np-btn-sm np-btn-ghost" onclick={() => (f.resourcesNone = false)}>Expose every resource</button>{/if}
          </label>
        </div>
      </Collapsible>
    {:else}
      <label class="fld">
        <span class="f-label">Configuration (JSON)</span>
        <textarea class="np-input np-mono json" rows="16" bind:value={json} spellcheck="false"></textarea>
      </label>
    {/if}
  </form>
  {#snippet footer()}
    <button class="np-btn np-btn-sm np-btn-ghost link" type="button" onclick={() => (mode === 'form' ? toJson() : toForm())} disabled={busy}>{mode === 'form' ? 'Edit as JSON' : 'Back to the form'}</button>
    {#if shown}<span class="err np-small" role="alert">{shown}</span>{/if}
    <button class="np-btn np-btn-sm" type="button" onclick={() => oncancel?.()} disabled={busy}>Cancel</button>
    <button class="np-btn np-btn-sm np-btn-primary" type="submit" form="mcp-edit" disabled={busy}>{busy ? 'Saving…' : 'Save'}</button>
  {/snippet}
</Modal>

<style>
  .form { display: flex; flex-direction: column; gap: 12px; min-width: 0; }
  .top { display: flex; align-items: flex-end; gap: 12px; }
  .grow { flex: 1; min-width: 0; }
  .enabled { padding-bottom: 6px; white-space: nowrap; }
  .fld { display: flex; flex-direction: column; gap: 4px; min-width: 0; }
  .f-label { font-size: var(--fs-xs); color: var(--fg-muted); }
  .adv { display: flex; flex-direction: column; gap: 12px; padding: 8px 0 0 18px; }
  .pair { display: flex; gap: 10px; }
  .pair .fld { flex: 1; }
  .json { font-size: 12px; }
  .err { flex: 1; min-width: 0; margin: 0; color: var(--err); text-align: left; overflow-wrap: anywhere; }
  .link { margin-right: auto; }
  textarea.np-input { font-size: 12px; resize: vertical; }
</style>
