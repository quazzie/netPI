<script lang="ts">
  // astra-1 G1: open-session tab strip. Tabs represent OPEN sessions, not just
  // running agents. Order is stable (a run finishing never moves or removes a
  // tab). Closing a tab is NOT cancelling a run or deleting the session — the
  // run keeps going and the session stays in the global list; opening it again
  // restores the tab. Arrow keys / Home / End switch between tabs (keyboard).
  import { store } from "../store.svelte";
  import { ws } from "../ws";

  let tabs = $derived(
    store.openTabIds.map((id) => {
      const info = store.sessions.find((s) => s.id === id);
      return {
        id,
        title: info?.title || (info?.workspace ? info.workspace : id.slice(0, 8)),
        running: store.busySessions[id] !== undefined,
        unread: !!store.unreadTabs[id],
        selected: store.session?.id === id,
      };
    }),
  );

  function select(id: string) {
    if (store.session?.id === id) return;
    ws.openSession(id).catch((e) => store.setError(String(e)));
  }

  function close(e: Event, id: string) {
    e.stopPropagation();
    store.closeTab(id);
  }

  function onKey(e: KeyboardEvent) {
    if (!tabs.length) return;
    const idx = store.openTabIds.indexOf(store.session?.id ?? "");
    const cur = idx < 0 ? 0 : idx; // not a tab → treat first tab as current
    let target: (typeof tabs)[number] | undefined;
    if (e.key === "ArrowRight") target = tabs[Math.min(tabs.length - 1, cur + 1)];
    else if (e.key === "ArrowLeft") target = tabs[Math.max(0, cur - 1)];
    else if (e.key === "Home") target = tabs[0];
    else if (e.key === "End") target = tabs[tabs.length - 1];
    else return;
    e.preventDefault();
    if (target) select(target.id);
  }
</script>

{#if tabs.length > 0}
  <div
    class="session-tabs"
    class:multi={tabs.length > 3}
    role="tablist"
    aria-label="Open sessions"
    tabindex="-1"
    onkeydown={onKey}
  >
    {#each tabs as t (t.id)}
      <div
        class="session-tab"
        class:selected={t.selected}
        role="tab"
        aria-selected={t.selected}
        title={t.title}
        onclick={() => select(t.id)}
      >
        <span
          class="tab-dot"
          class:running={t.running}
          class:unread={t.unread}
          aria-hidden="true"></span>
        <span class="tab-title">{t.title}</span>
        <button
          class="tab-close"
          aria-label={`Close ${t.title}`}
          onkeydown={(e) => e.stopPropagation()}
          onclick={(e) => close(e, t.id)}>×</button>
      </div>
    {/each}
  </div>
{/if}
