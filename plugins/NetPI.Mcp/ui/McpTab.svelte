<script>
  import { Menu, IconButton, useRefresh } from '@netpi/kit';
  let { ctx } = $props();
  let servers = $state([]), tools = $state([]), selected = $state(''), error = $state(''), busy = $state(false);
  let editing = $state(false), id = $state(''), draft = $state(''), query = $state('');
  const filtered = $derived(tools.filter(t => (t.name + ' ' + t.description).toLowerCase().includes(query.toLowerCase())));
  async function load() {
    // which server this load is about: two clicks in a row race, and the slower answer must not list the first
    // server's tools under the second one
    const sid = selected;
    try {
      servers = (await ctx.rpc('mcp.list', {})).servers ?? [];
      const list = sid ? ((await ctx.rpc('mcp.tools', { serverId: sid })).tools ?? []) : null;
      if (selected !== sid) return;
      if (list) tools = list;
    } catch (e) { error = e.message ?? String(e); }
  }
  // svelte-ignore state_referenced_locally
  const tab = useRefresh(ctx, { load, events: ['mcp.toolsChanged', 'mcp.serverChanged'] });
  /** Called by main.js (onShow): the list is stale after the tab was hidden. */
  export function setVisible(v) { if (v) tab.refresh(); }
  async function choose(server) { selected = server.id; query = ''; await tab.refresh(); }
  async function action(method, args) {
    busy = true; error = '';
    try { await ctx.rpc(method, args); await tab.refresh(); }
    catch (e) { error = e.message ?? String(e); }
    finally { busy = false; }
  }
  function edit(server) {
    id = server?.id ?? '';
    draft = JSON.stringify(server?.config ?? { enabled: true, transport: 'stdio', command: '', args: [], cwd: '', env: {}, pinned: [], readOnly: [] }, null, 2);
    editing = true; error = '';
  }
  // The per-server actions, in the one menu every other card uses. The kit's Menu is fixed-position and clamped to the
  // viewport, which is the point: this tab lives in a short, scrolling side panel, and a menu positioned inside that
  // panel is clipped by it — the first item showed, the rest did not (idea-pii7hv).
  const actions = (server) => [
    { label: 'Edit configuration…', onclick: () => edit(server), disabled: busy },
    { label: server.config?.enabled ? 'Disable' : 'Enable', onclick: () => action('mcp.setEnabled', { id: server.id, enabled: !server.config?.enabled }), disabled: busy || !server.config },
    { label: 'Reconnect', onclick: () => action('mcp.reconnect', { id: server.id }), disabled: busy || !server.config?.enabled },
    { label: 'Refresh tools', onclick: () => action('mcp.refresh', { id: server.id }), disabled: busy || !server.config?.enabled },
    { divider: true },
    { label: 'Remove', danger: true, onclick: () => action('mcp.remove', { id: server.id }), disabled: busy },
  ];
  async function save() {
    busy = true; error = '';
    try {
      await ctx.rpc('mcp.save', { id, config: JSON.parse(draft) });
      selected = id; editing = false; await tab.refresh();
    } catch (e) { error = e.message ?? String(e); }
    finally { busy = false; }
  }
  async function exposure(server, tool, enabled) {
    const config = structuredClone(server.config);
    const current = config.tools ?? tools.map(t => t.name);
    config.tools = enabled ? [...new Set([...current, tool.name])] : current.filter(n => n !== tool.name);
    await action('mcp.save', { id: server.id, config });
  }
  async function override(server, tool, key, enabled) {
    const config = structuredClone(server.config);
    config[key] = [...new Set(enabled ? [...(config[key] ?? []), tool.name] : (config[key] ?? []).filter(n => n !== tool.name))];
    await action('mcp.save', { id: server.id, config });
  }
</script>

<div class="mcp">
  <header><strong>MCP servers</strong><button onclick={() => edit(null)} disabled={busy}>Add server</button></header>
  <p class="hint">External tools are discovered on demand. Only search and call schemas enter a chat by default.</p>
  {#if error}<p class="error" role="alert">{error}</p>{/if}
  {#if editing}
    <form onsubmit={(e) => { e.preventDefault(); save(); }}>
      <label>Server id<input bind:value={id} required pattern={'[A-Za-z0-9_-]{1,48}'} /></label>
      <label>Configuration<textarea bind:value={draft} rows="16" spellcheck="false"></textarea></label>
      <p class="hint">stdio: command, args and absolute cwd. HTTP: transport "http" and url. env and headerEnv map names to environment-variable names; credentials stay outside settings. tools restricts remote tool names; pinned and readOnly contain remote tool names.</p>
      <div class="actions"><button type="submit" disabled={busy}>Save</button><button type="button" onclick={() => editing = false} disabled={busy}>Cancel</button></div>
    </form>
  {/if}
  {#if !servers.length && !editing}<p>No servers configured.</p>{/if}
  {#each servers as server (server.id)}
    <section class:chosen={selected === server.id}>
      <div class="head">
        <button class="server" onclick={() => choose(server)}><strong>{server.id}</strong><span>{server.status ?? 'invalid'} · {server.toolCount ?? 0} tools</span></button>
        <Menu items={actions(server)} minWidth={172}>
          {#snippet trigger({ toggle })}
            <IconButton icon="more" size="sm" title="Actions for {server.id}" onclick={toggle} disabled={busy} />
          {/snippet}
        </Menu>
      </div>
      {#if server.error}<p class="error">{server.error}</p>{/if}
      {#each server.rejected ?? [] as rejected}<p class="error">{rejected.name}: {rejected.error}</p>{/each}
      {#if selected === server.id}
        <input aria-label="Filter tools" placeholder="Filter tools" bind:value={query} />
        {#each filtered as tool (tool.id)}
          <details>
            <summary>{tool.name}</summary>
            <p>{tool.description}</p>
            <code>{tool.id}</code>
            <label class="check"><input type="checkbox" checked={tool.exposed} disabled={busy} onchange={(e) => exposure(server, tool, e.currentTarget.checked)} />Expose tool</label>
            <label class="check"><input type="checkbox" checked={!tool.deferred} disabled={busy} onchange={(e) => override(server, tool, 'pinned', e.currentTarget.checked)} />Pin schema in chat ({JSON.stringify(tool.schema).length} chars)</label>
            <label class="check"><input type="checkbox" checked={tool.readOnly} disabled={busy} onchange={(e) => override(server, tool, 'readOnly', e.currentTarget.checked)} />Allow concurrent read-only calls</label>
            <pre>{JSON.stringify(tool.schema, null, 2)}</pre>
          </details>
        {/each}
      {/if}
    </section>
  {/each}
</div>

<style>
  .mcp { padding:12px; overflow:auto; min-width:0; font-size:13px; }
  button { padding:5px 8px; color:inherit; background:var(--bg-2); border:1px solid var(--border, #555); border-radius:4px; cursor:pointer; }
  button:disabled { opacity:.5; cursor:default; }
  header,.actions { display:flex; flex-wrap:wrap; align-items:center; gap:6px; }
  header { justify-content:space-between; }
  .head { display:flex; align-items:flex-start; gap:6px; }
  .hint { opacity:.7; line-height:1.5; }
  .error { color:var(--err); overflow-wrap:anywhere; }
  label { display:block; margin:8px 0; }
  input:not([type=checkbox]),textarea { display:block; box-sizing:border-box; width:100%; padding:6px; margin-top:4px; color:inherit; background:var(--bg-2); border:1px solid var(--border, #555); border-radius:4px; }
  textarea,code,pre { font-family:monospace; font-size:11px; }
  section { padding:9px 0; border-top:1px solid var(--border, #555); }
  .server { flex:1 1 auto; min-width:0; text-align:left; border:0; background:transparent; padding:0 0 8px; display:flex; flex-direction:column; gap:3px; }
  .server span { opacity:.7; }
  details { border-top:1px solid var(--border, #555); padding:8px 0; }
  summary { cursor:pointer; overflow-wrap:anywhere; }
  code { display:block; overflow-wrap:anywhere; }
  pre { white-space:pre-wrap; overflow-wrap:anywhere; max-height:240px; overflow:auto; }
  .check { display:flex; gap:6px; align-items:center; }
</style>
