<script lang="ts">
  import { store } from "../../store.svelte";
  import { ws } from "../../ws";

  let baseUrl = $state("http://127.0.0.1:8080");
  let port = $state("7331");
  let workspace = $state(store.session?.workspace ?? "");
  let saved = $state(false);

  function save() {
    saved = true;
    ws.request("config.update", {
      plugins: { aiProxy: { baseUrl }, web: { listen: "127.0.0.1", port } },
    });
    if (store.session && store.session.workspace !== workspace) {
      ws.request("session.rename", { sessionId: store.session.id, workspace });
    }
    setTimeout(() => (saved = false), 1500);
  }
</script>

<div class="overlay" onclick={() => (store.overlay = null)}>
  <div class="panel" onclick={(e) => e.stopPropagation()}>
    <h2>
      <span>Settings</span>
      <button class="close" onclick={() => (store.overlay = null)}>×</button>
    </h2>
    <div class="kv">
      <span class="k">AiProxy base URL</span>
      <span class="v"><input style="width:100%;background:var(--bg-soft);border:1px solid var(--border-soft);border-radius:6px;padding:5px 8px;outline:none" bind:value={baseUrl} /></span>
      <span class="k">Web listen port</span>
      <span class="v"><input style="width:120px;background:var(--bg-soft);border:1px solid var(--border-soft);border-radius:6px;padding:5px 8px;outline:none" bind:value={port} /></span>
      <span class="k">Session workspace</span>
      <span class="v"><input style="width:100%;background:var(--bg-soft);border:1px solid var(--border-soft);border-radius:6px;padding:5px 8px;outline:none" bind:value={workspace} placeholder="C:\src\project" /></span>
    </div>
    <div style="margin-top:16px;display:flex;justify-content:flex-end;gap:8px">
      {#if saved}<span class="cap" style="color:var(--green);align-self:center">saved</span>{/if}
      <button class="btn primary" onclick={save}>Save</button>
    </div>
    <div class="cap" style="margin-top:12px;font-size:11px;color:var(--text-faint)">
      Changes take effect on the next plugin reload for web / aiproxy.
    </div>
  </div>
</div>
