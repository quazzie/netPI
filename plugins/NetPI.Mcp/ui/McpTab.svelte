<script>
  import { onMount } from 'svelte';
  let { ctx } = $props();
  let servers = $state([]), tools = $state([]), selected = $state(''), error = $state(''), busy = $state(false);
  let editing = $state(false), id = $state(''), draft = $state(''), query = $state('');
  // Which server's action menu is open ('' = none), and the handler that closes it on Escape or an outside click.
  let menuFor = $state('');
  let menuEl = $state(null);
  $effect(() => {
    if (!menuFor) return;
    // Close on Escape or a click anywhere else. Both listeners are on the bubble phase, so a click on an item runs
    // that item's own handler first — a capture-phase guard would close the menu before the action ever fired.
    const close = (e) => {
      if (e.type === 'keydown') { if (e.key === 'Escape') menuFor = ''; return; }
      if (menuEl?.contains(e.target)) return;
      menuFor = '';
    };
    window.addEventListener('click', close);
    window.addEventListener('keydown', close);
    return () => {
      window.removeEventListener('click', close);
      window.removeEventListener('keydown', close);
    };
  });
  const filtered = $derived(tools.filter(t => (t.name + ' ' + t.description).toLowerCase().includes(query.toLowerCase())));
  export async function refresh() {
    try {
      servers = (await ctx.rpc('mcp.list', {})).servers ?? [];
      if (selected) tools = (await ctx.rpc('mcp.tools', { serverId: selected })).tools ?? [];
    } catch (e) { error = e.message ?? String(e); }
  }
  async function choose(server) { selected = server.id; query = ''; menuFor = ''; await refresh(); }
  async function action(method, args) {
    busy = true; error = ''; menuFor = '';
    try { await ctx.rpc(method, args); await refresh(); }
    catch (e) { error = e.message ?? String(e); }
    finally { busy = false; }
  }
  function edit(server) {
    id = server?.id ?? '';
    draft = JSON.stringify(server?.config ?? { enabled: true, transport: 'stdio', command: '', args: [], cwd: '', env: {}, pinned: [], readOnly: [] }, null, 2);
    editing = true; error = ''; menuFor = '';
  }
  async function save() {
    busy = true; error = '';
    try {
      await ctx.rpc('mcp.save', { id, config: JSON.parse(draft) });
      selected = id; editing = false; await refresh();
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
  onMount(() => {
    refresh();
    const off = ctx.on('mcp.toolsChanged', refresh);
    const status = ctx.on('mcp.serverChanged', refresh);
    return () => { off(); status(); };
  });
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
        <!-- The per-server actions live in one menu: five buttons in a row crowd a narrow panel (idea-pii7hv). -->
        <div class="menu" bind:this={menuEl}>
          <button
            class="dots"
            aria-haspopup="menu"
            aria-expanded={menuFor === server.id}
            aria-label={`Actions for ${server.id}`}
            title="Actions"
            disabled={busy}
            onclick={() => (menuFor = menuFor === server.id ? '' : server.id)}
          >⋯</button>
          {#if menuFor === server.id}
            <div class="pop" role="menu" aria-label={`Actions for ${server.id}`}>
              <button role="menuitem" onclick={() => edit(server)} disabled={busy}>Edit configuration…</button>
              <button role="menuitem" onclick={() => action('mcp.setEnabled', { id: server.id, enabled: !server.config?.enabled })} disabled={busy || !server.config}>{server.config?.enabled ? 'Disable' : 'Enable'}</button>
              <button role="menuitem" onclick={() => action('mcp.reconnect', { id: server.id })} disabled={busy || !server.config?.enabled}>Reconnect</button>
              <button role="menuitem" onclick={() => action('mcp.refresh', { id: server.id })} disabled={busy || !server.config?.enabled}>Refresh tools</button>
              <button role="menuitem" class="danger" onclick={() => action('mcp.remove', { id: server.id })} disabled={busy}>Remove</button>
            </div>
          {/if}
        </div>
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
  header,.actions { display:flex; flex-wrap:wrap; align-items:center; gap:6px; }
  header { justify-content:space-between; }
  button { padding:5px 8px; color:inherit; background:var(--bg-secondary, #252525); border:1px solid var(--border, #555); border-radius:4px; cursor:pointer; }
  button:disabled { opacity:.5; cursor:default; }
  .head { display:flex; align-items:flex-start; gap:6px; }
  .menu { position:relative; flex:0 0 auto; }
  .dots { width:26px; padding:5px 0; text-align:center; line-height:1; font-size:15px; }
  .pop { position:absolute; z-index:20; right:0; top:calc(100% + 4px); min-width:172px; display:flex; flex-direction:column; gap:1px; padding:4px;
         background:var(--bg-1, #1e1e1e); border:1px solid var(--border-strong, #666); border-radius:6px; box-shadow:0 6px 18px rgba(0,0,0,.45); }
  .pop button { border:0; background:transparent; text-align:left; padding:6px 8px; border-radius:4px; }
  .pop button:hover:not(:disabled) { background:var(--bg-3, #333); }
  .pop .danger { color:var(--err, #ff8a80); }
  button { padding:5px 8px; color:inherit; background:var(--bg-secondary, #252525); border:1px solid var(--border, #555); border-radius:4px; cursor:pointer; }
  button:disabled { opacity:.5; cursor:default; }
  .hint { opacity:.7; line-height:1.5; }
  .error { color:var(--danger, #ff8a80); overflow-wrap:anywhere; }
  label { display:block; margin:8px 0; }
  input:not([type=checkbox]),textarea { display:block; box-sizing:border-box; width:100%; padding:6px; margin-top:4px; color:inherit; background:var(--bg-secondary, #252525); border:1px solid var(--border, #555); border-radius:4px; }
  textarea,code,pre { font-family:monospace; font-size:11px; }
  section { padding:9px 0; border-top:1px solid var(--border, #555); }
  .server { flex:1 1 auto; min-width:0; text-align:left; border:0; background:transparent; padding:0 0 8px; display:flex; flex-direction:column; gap:3px; }
  .server span { opacity:.7; }
  .chosen > .server strong { color:var(--accent, #8ab4ff); }
  details { border-top:1px solid var(--border, #555); padding:8px 0; }
  summary { cursor:pointer; overflow-wrap:anywhere; }
  code { display:block; overflow-wrap:anywhere; }
  pre { white-space:pre-wrap; overflow-wrap:anywhere; max-height:240px; overflow:auto; }
  .check { display:flex; gap:6px; align-items:center; }
</style>
