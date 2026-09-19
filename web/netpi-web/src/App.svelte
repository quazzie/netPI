<script lang="ts">
  import { store } from "./store.svelte";
  import "./ws";
  import HarnessHeader from "./components/HarnessHeader.svelte";
  import ConversationViewport from "./components/ConversationViewport.svelte";
  import Composer from "./components/Composer.svelte";
  import RunStatusLine from "./components/RunStatusLine.svelte";
  import SessionDrawer from "./components/overlays/SessionDrawer.svelte";
  import PluginManager from "./components/overlays/PluginManager.svelte";
  import Settings from "./components/overlays/Settings.svelte";
  import Diagnostics from "./components/overlays/Diagnostics.svelte";
</script>

{#if store.errorBanner}
  <div
    style="flex:none;padding:6px 14px;background:color-mix(in srgb, var(--red) 18%, transparent);color:var(--red);font-size:12.5px;display:flex;justify-content:space-between;gap:10px"
  >
    <span>{store.errorBanner}</span>
    <button style="cursor:pointer" onclick={() => store.setError(null)}>×</button>
  </div>
{/if}

<SessionDrawer />
<HarnessHeader />
<ConversationViewport />
<RunStatusLine />
<Composer />

{#if store.overlay === "plugins"}
  <PluginManager />
{/if}
{#if store.overlay === "settings"}
  <Settings />
{/if}
{#if store.overlay === "diagnostics"}
  <Diagnostics />
{/if}
