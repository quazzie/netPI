<script lang="ts">
  import { store } from "./store.svelte";
  import "./ws";
  import HarnessHeader from "./components/HarnessHeader.svelte";
  import ConversationViewport from "./components/ConversationViewport.svelte";
  import Composer from "./components/Composer.svelte";
  import SessionDrawer from "./components/overlays/SessionDrawer.svelte";
  import PluginManager from "./components/overlays/PluginManager.svelte";
  import Settings from "./components/overlays/Settings.svelte";
  import Diagnostics from "./components/overlays/Diagnostics.svelte";
</script>

{#if store.errorBanner}
  <div class="error-banner">
    <span>{store.errorBanner}</span>
    <button aria-label="Dismiss error" onclick={() => store.setError(null)}>×</button>
  </div>
{/if}

<SessionDrawer />
<HarnessHeader />
<ConversationViewport />
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
