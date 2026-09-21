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

  // astra-1 G1: the common 2–3-session case stays immediately accessible;
  // many tabs fold into a compact overflow list (not a horizontal scroll
  // that hides tabs from keyboard and mouse alike).
  const VISIBLE_MAX = 5;
  let overflowOpen = $state(false);
  let visibleTabs = $derived(
    tabs.length <= VISIBLE_MAX ? tabs : tabs.slice(0, VISIBLE_MAX - 1),
  );
  let overflowTabs = $derived(
    tabs.length <= VISIBLE_MAX ? [] : tabs.slice(VISIBLE_MAX - 1),
  );
  let overflowHasSelected = $derived(
    overflowTabs.some((t) => t.selected),
  );
  let overflowHasBusy = $derived(overflowTabs.some((t) => t.running || t.unread));

  $effect(() => {
    // astra-1 G1: when keyboard/Home-End moves the selection into the
    // overflowed region, open the list so the selected tab is visible.
    if (overflowHasSelected && !overflowOpen) overflowOpen = true;
  });

  $effect(() => {
    // Escape closes the overflow list (keyboard parity with other popups).
    if (!overflowOpen) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        overflowOpen = false;
        tablistEl?.focus({ preventScroll: true });
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  });

  let tablistEl: HTMLElement | null = $state(null);

  function select(id: string) {
    if (store.session?.id === id) return;
    if (overflowTabs.some((t) => t.id === id)) {
      overflowOpen = true; // target lives in the overflow list — keep it open
    } else {
      overflowOpen = false;
    }
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
    bind:this={tablistEl}
    class="session-tabs"
    class:multi={tabs.length > 3}
    role="tablist"
    aria-label="Open sessions"
    tabindex="-1"
    onkeydown={onKey}
    onclick={(e) => {
      // a click on the empty strip (not a tab) closes the overflow list
      if (overflowOpen && e.target === e.currentTarget) overflowOpen = false;
    }}
  >
    {#each visibleTabs as t (t.id)}
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

    {#if overflowTabs.length}
      <div class="tab-overflow">
        <button
          class="tab-overflow-btn"
          class:active={overflowOpen}
          class:busy={overflowHasBusy}
          aria-label={`More sessions (${overflowTabs.length})`}
          aria-expanded={overflowOpen}
          aria-haspopup="true"
          onclick={(e) => {
            e.stopPropagation();
            overflowOpen = !overflowOpen;
          }}
          onkeydown={(e) => {
            if (e.key === "Enter" || e.key === " ") {
              e.preventDefault();
              e.stopPropagation();
              overflowOpen = !overflowOpen;
            }
          }}
        >
          {overflowTabs.length > 9 ? "9+" : overflowTabs.length}
        </button>

        {#if overflowOpen}
          <div class="tab-overflow-list" role="menu" aria-label="More open sessions">
            {#each overflowTabs as t (t.id)}
              <div
                class="tab-overflow-item"
                class:selected={t.selected}
                role="menuitem"
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
      </div>
    {/if}
  </div>
{/if}
