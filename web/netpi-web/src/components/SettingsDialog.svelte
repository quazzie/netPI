<script lang="ts">
  // astra-1 G1: settings live in a proper dialog (the left panel is a
  // temporary home until SessionPicker lands; this component is the
  // permanent surface). Escape closes, focus returns to the opener.
  import { ui } from "../ui.svelte";
  import { store } from "../store.svelte";
  import { ws } from "../ws";
  import type { FontChoice } from "../ui.svelte";

  let { open, onClose }: { open: boolean; onClose: () => void } = $props();
  let dialogRef: HTMLDivElement | null = $state(null);
  let opener: HTMLElement | null = null;
  let font = $derived(ui.font);
  let workspace = $state("");

  $effect(() => {
    workspace = store.session?.workspace ?? "";
  });

  $effect(() => {
    if (!open) return;
    // focus the dialog on open; restore focus to the opener on close.
    opener = document.activeElement as HTMLElement | null;
    const t = setTimeout(() => dialogRef?.focus(), 0);
    return () => {
      clearTimeout(t);
      opener?.focus?.();
    };
  });

  function saveWorkspace() {
    if (!store.session || !workspace.trim()) return;
    ws.request("session.rename", {
      sessionId: store.session.id,
      workspace: workspace.trim(),
    }).catch((e) => store.setError(String(e)));
  }
</script>

{#if open}
  <div
    class="settings-backdrop"
    role="presentation"
    onclick={(e) => {
      if (e.target === e.currentTarget) onClose();
    }}
  >
    <div
      bind:this={dialogRef}
      class="settings-dialog"
      role="dialog"
      aria-modal="true"
      aria-label="Settings"
      tabindex="-1"
      onkeydown={(e) => {
        if (e.key === "Escape") {
          e.stopPropagation();
          onClose();
        }
      }}
    >
      <div class="settings-head">
        <span class="settings-title">Settings</span>
        <button class="settings-close" aria-label="Close settings" onclick={onClose}>×</button>
      </div>

      <div class="setting-group">
        <label for="settings-font">Interface font</label>
        <select
          id="settings-font"
          value={font}
          onchange={(e) => ui.setFont((e.currentTarget as HTMLSelectElement).value as FontChoice)}
        >
          <option value="system">System</option>
          <option value="inter">Inter / UI sans</option>
          <option value="mono">Monospace</option>
          <option value="serif">Serif</option>
        </select>
      </div>

      <label class="setting-check">
        <input
          type="checkbox"
          checked={ui.keepThinkingOpen}
          onchange={(e) => ui.setKeepThinkingOpen((e.currentTarget as HTMLInputElement).checked)}
        />
        <span>
          <strong>Keep thinking open</strong>
          <small>Completed reasoning stays expanded instead of collapsing to a pill.</small>
        </span>
      </label>

      <label class="setting-check">
        <input
          type="checkbox"
          checked={ui.keepToolsOpen}
          onchange={(e) => ui.setKeepToolsOpen((e.currentTarget as HTMLInputElement).checked)}
        />
        <span>
          <strong>Keep tool calls open</strong>
          <small>Tool calls stay expanded by default instead of collapsing to a header row.</small>
        </span>
      </label>

      <div class="setting-group">
        <label for="settings-workspace">Session workspace</label>
        <input id="settings-workspace" bind:value={workspace} placeholder="C:\src\project" />
        <button class="btn" onclick={saveWorkspace} disabled={!store.session || !workspace.trim()}>
          Update workspace
        </button>
      </div>

      <div class="setting-note">
        Model and reasoning are saved with the session. UI preferences are stored locally.
      </div>
    </div>
  </div>
{/if}
