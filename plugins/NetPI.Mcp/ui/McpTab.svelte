<script>
  import { Menu, IconButton, Icon, SearchInput, useRefresh } from '@netpi/kit';
  import ServerForm from './ServerForm.svelte';
  let { ctx } = $props();
  let servers = $state([]), tools = $state([]), selected = $state(''), error = $state(''), busy = $state(false);
  let editing = $state(false), isNew = $state(false), id = $state(''), draft = $state.raw({}), query = $state('');
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
  /** A server is a disclosure: its tools show while it is the chosen one, a second click closes it, and none is open at the start. */
  async function choose(server) {
    if (selected === server.id) { selected = ''; query = ''; return; }
    selected = server.id; query = ''; await tab.refresh();
  }
  async function action(method, args) {
    busy = true; error = '';
    try { await ctx.rpc(method, args); await tab.refresh(); }
    catch (e) { error = e.message ?? String(e); }
    finally { busy = false; }
  }
  /** The server's configuration whole. mcp.list masks a secret in a URL or an argument (idea-3q0uzn), so whatever saves
   *  the configuration back starts from mcp.config; a masked one is never written back. */
  async function rawConfig(server) {
    try {
      const answer = await ctx.rpc('mcp.config', { id: server.id });
      if (answer?.config) return answer.config;
    } catch (e) {
      if (server.secretsMasked) throw e;
    }
    if (server.secretsMasked) throw new Error(`The configuration of ${server.id} could not be read whole; nothing was saved.`);
    return JSON.parse(JSON.stringify(server.config ?? {})); // a $state proxy, which structuredClone cannot take
  }
  async function edit(server) {
    error = '';
    let config = null;
    if (server) {
      try { config = await rawConfig(server); }
      catch (e) { error = e.message ?? String(e); return; }
    }
    id = server?.id ?? '';
    isNew = !server;
    draft = config ?? { enabled: true, transport: 'stdio' };
    editing = true;
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
  async function save(newId, config) {
    busy = true; error = '';
    try {
      await ctx.rpc('mcp.save', { id: newId, config });
      editing = false; await tab.refresh();
    } catch (e) { error = e.message ?? String(e); }
    finally { busy = false; }
  }
  async function exposure(server, tool, enabled) {
    let config;
    try { config = await rawConfig(server); }
    catch (e) { error = e.message ?? String(e); return; }
    const current = config.tools ?? tools.map(t => t.name);
    config.tools = enabled ? [...new Set([...current, tool.name])] : current.filter(n => n !== tool.name);
    await action('mcp.save', { id: server.id, config });
  }
  async function override(server, tool, key, enabled) {
    let config;
    try { config = await rawConfig(server); }
    catch (e) { error = e.message ?? String(e); return; }
    config[key] = [...new Set(enabled ? [...(config[key] ?? []), tool.name] : (config[key] ?? []).filter(n => n !== tool.name))];
    await action('mcp.save', { id: server.id, config });
  }
</script>

<div class="mcp np-scroll">
  <div class="np-section-title">
    <span class="np-section-label">MCP servers</span>
    <span class="np-section-actions"><button class="np-btn np-btn-sm" onclick={() => edit(null)} disabled={busy}>Add server</button></span>
  </div>
  <p class="np-dim np-small hint">External tools are discovered on demand. Only search and call schemas enter a chat by default.</p>
  {#if error && !editing}<p class="err np-small" role="alert">{error}</p>{/if}
  {#if editing}
    <ServerForm {isNew} initialId={id} base={draft} {busy} {error} onsave={save} oncancel={() => { editing = false; error = ''; }} />
  {/if}
  {#if !servers.length && !editing}<p class="np-empty">No servers configured.</p>{/if}
  {#each servers as server (server.id)}
    <section class="server" class:chosen={selected === server.id}>
      <div class="head">
        <button class="pick" onclick={() => choose(server)}>
          <span class="np-strong np-ellipsis"><Icon name={selected === server.id ? 'chevron-down' : 'chevron-right'} size={12} /> {server.id}</span>
          <span class="np-meta">{server.status ?? 'invalid'} · {server.toolCount ?? 0} tools</span>
        </button>
        <Menu items={actions(server)} minWidth={172}>
          {#snippet trigger({ toggle })}
            <IconButton icon="more" size="sm" title="Actions for {server.id}" onclick={toggle} disabled={busy} />
          {/snippet}
        </Menu>
      </div>
      {#if server.error}<p class="err np-small">{server.error}</p>{/if}
      {#each server.rejected ?? [] as rejected (rejected.name)}<p class="err np-small">{rejected.name}: {rejected.error}</p>{/each}
      {#if selected === server.id}
        <SearchInput bind:value={query} placeholder="Filter tools" aria-label="Filter tools" />
        {#each filtered as tool (tool.id)}
          <details class="tool">
            <summary>{tool.name}</summary>
            <p class="np-small desc">{tool.description}</p>
            <code class="np-mono np-small np-dim">{tool.id}</code>
            <label class="np-check np-small"><input type="checkbox" checked={tool.exposed} disabled={busy} onchange={(e) => exposure(server, tool, e.currentTarget.checked)} />Expose tool</label>
            <label class="np-check np-small"><input type="checkbox" checked={!tool.deferred} disabled={busy} onchange={(e) => override(server, tool, 'pinned', e.currentTarget.checked)} />Pin schema in chat ({JSON.stringify(tool.schema).length} chars)</label>
            <label class="np-check np-small"><input type="checkbox" checked={tool.readOnly} disabled={busy} onchange={(e) => override(server, tool, 'readOnly', e.currentTarget.checked)} />Allow concurrent read-only calls</label>
            <pre class="np-mono schema">{JSON.stringify(tool.schema, null, 2)}</pre>
          </details>
        {:else}
          <p class="np-empty">{query ? 'No tool matches' : 'No tools'}</p>
        {/each}
      {/if}
    </section>
  {/each}
</div>

<style>
  /* the tab fills the panel and scrolls inside it; everything is one column that shrinks (min-width: 0), never sideways */
  .mcp { flex: 1 1 auto; min-height: 0; min-width: 0; padding: 8px 12px 12px; font-size: var(--fs-sm); }
  .hint { margin: 0 0 8px; line-height: 1.5; }
  .err { margin: 4px 0; color: var(--err); overflow-wrap: anywhere; }
  .field { display: block; min-width: 0; font-size: var(--fs-xs); color: var(--fg-muted); }
  .field .np-input { margin-top: 4px; }
  textarea.np-input { font-size: 11px; }
  .server { padding: 8px 0; border-top: 1px solid var(--border); min-width: 0; }
  .head { display: flex; align-items: flex-start; gap: 6px; min-width: 0; }
  /* the server line is a button (click to list its tools) with no chrome of its own: two lines, each ellipsized */
  .pick { flex: 1 1 auto; min-width: 0; display: flex; flex-direction: column; gap: 2px; padding: 0 0 4px; border: 0; background: transparent; color: inherit; font: inherit; text-align: left; cursor: pointer; }
  .pick .np-strong { display: block; }
  .chosen .pick .np-strong { color: var(--accent); }
  .server :global(.np-search) { margin: 4px 0 2px; }
  .tool { padding: 6px 0; border-top: 1px solid var(--border); min-width: 0; }
  .tool summary { cursor: pointer; overflow-wrap: anywhere; }
  .desc { margin: 4px 0; line-height: 1.4; overflow-wrap: anywhere; }
  code { display: block; margin: 2px 0 6px; overflow-wrap: anywhere; }
  .np-check { margin: 4px 0; }
  .schema { margin: 6px 0 0; padding: 6px 8px; max-height: 240px; overflow: auto; border-radius: var(--radius-sm); background: var(--code-bg, var(--bg-2)); font-size: 11px; white-space: pre-wrap; overflow-wrap: anywhere; }
</style>
