<script lang="ts">
  import { onMount } from "svelte";
  import { store } from "./store.svelte";
  import { ui } from "./ui.svelte";
  import { ws } from "./ws";
  import RightPanel from "./components/RightPanel.svelte";
  import HarnessHeader from "./components/HarnessHeader.svelte";
  import SessionTabs from "./components/SessionTabs.svelte";
  import ConversationViewport from "./components/ConversationViewport.svelte";
  import Composer from "./components/Composer.svelte";
  import SettingsDialog from "./components/SettingsDialog.svelte";
  import SessionPicker from "./components/SessionPicker.svelte";
  import ProjectPicker from "./components/ProjectPicker.svelte";

  let shellStyle = $derived(
    `--right-panel-width:${ui.rightOpen ? ui.rightWidth : 26}px`, /* 26 must match --right-rail-width in app.css */
  );

  // astra-1 G1: settings live in a dialog; the left panel (second
  // "active sessions" list + obsolete left-nav prefs) is removed — the
  // header's Sessions button opens the global SessionPicker, and /settings
  // opens this dialog. The right slot/rail/resizer are preserved.
  let settingsOpen = $state(false);
  let pickerOpen = $state(false);
  // astra-1 C (G1 gap): the header project chip opens the project picker.
  let projectPickerOpen = $state(false);

  // astra-1 H: the Activity panel (a cross-origin iframe on its own Kestrel port)
  // posts { type:"netpi.activity.openSession", payload:{sessionId} } to the shell
  // when the user clicks "Open" on a run. Route it through the socket so the
  // session becomes the visible tab. We only trust the well-known type string
  // from our own panel; an unknown/absent session is a no-op on the server.
  onMount(() => {
    const onMessage = (e: MessageEvent) => {
      const d = e.data;
      if (!d || d.type !== "netpi.activity.openSession") return;
      const sid = (d.payload && typeof d.payload.sessionId === "string") ? d.payload.sessionId : null;
      if (!sid) return;
      ws.openSession(sid).catch(() => {});
    };
    window.addEventListener("message", onMessage);
    return () => window.removeEventListener("message", onMessage);
  });

  // astra-1 G1: /settings (Composer) asks the shell to open the dialog via a
  // monotonically increasing counter (ui holds no component state).
  $effect(() => {
    const n = ui.settingsRequest;
    if (n > 0) settingsOpen = true;
    return () => {};
  });

  function beginPanelResize(e: PointerEvent) {
    e.preventDefault();
    e.stopPropagation();

    const startX = e.clientX;
    const startWidth = ui.rightWidth;

    document.body.classList.add("panel-resizing");

    const move = (ev: PointerEvent) => {
      const delta = ev.clientX - startX;
      ui.setRightWidth(startWidth - delta, false);
    };

    const finish = () => {
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", finish);
      window.removeEventListener("pointercancel", finish);
      document.body.classList.remove("panel-resizing");
      ui.persistLayout();
    };

    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", finish);
    window.addEventListener("pointercancel", finish);
  }
</script>

<div class="app-shell" style={shellStyle}>
  <main class="chat-shell">
    {#if store.errorBanner}
      <div class="error-banner">
        <span>{store.errorBanner}</span>
        <button aria-label="Dismiss error" onclick={() => store.setError(null)}>×</button>
      </div>
    {/if}

    <HarnessHeader onSessions={() => (pickerOpen = true)} onProjects={() => (projectPickerOpen = true)} />
    <SessionTabs />
    <ConversationViewport />
    <Composer />
  </main>

  <RightPanel />

  {#if ui.rightOpen}
    <div
      class="panel-resizer panel-resizer-right"
      style:right={`${ui.rightWidth - 3}px`}
      title="Resize right panel"
      onpointerdown={(e) => beginPanelResize(e)}
    ></div>
  {/if}

  <SettingsDialog open={settingsOpen} onClose={() => (settingsOpen = false)} />
  <SessionPicker open={pickerOpen} onClose={() => (pickerOpen = false)} />
  <ProjectPicker open={projectPickerOpen} onClose={() => (projectPickerOpen = false)} />
</div>
