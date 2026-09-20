<script lang="ts">
  import { store } from "../store.svelte";
  import { ws } from "../ws";

  let text = $state("");
  let el: HTMLTextAreaElement | null = null;

  // Which popup is open.
  let menu = $state<null | "commands" | "model" | "reasoning" | "at">(null);
  let atCursor = $state(0);
  let atFiles = $state<{ path: string; full: string; size: number }[]>([]);

  // --------------------------------------------------------------------------
  // PLAN §37: slash commands. A static baseline for UI-only commands (no server
  // plugin) plus commands the host reports (e.g. /compact from AutoCompact).
  // --------------------------------------------------------------------------
  const baselineCommands = [
    { cmd: "/model", desc: "Select model" },
    { cmd: "/reasoning", desc: "Set reasoning level" },
    { cmd: "/new", desc: "New session" },
    { cmd: "/clear", desc: "Clear conversation" },
  ];
  let serverCommands = $state<{ cmd: string; desc: string }[]>([]);

  $effect(() => {
    let alive = true;
    ws.request("commands.list")
      .then((r) => {
        if (!alive) return;
        const raw = (r as any)?.commands ?? [];
        serverCommands = raw
          .filter((c) => c?.name)
          .map((c) => ({ cmd: String(c.name), desc: String(c.description ?? "") }));
      })
      .catch(() => { /* keep baseline */ });
    return () => { alive = false; };
  });

  const commands = $derived(
    [...baselineCommands, ...serverCommands].filter(
      (c, i, arr) => arr.findIndex((x) => x.cmd === c.cmd) === i,
    ),
  );

  let filteredCommands = $derived(
    commands.filter(
      (c) =>
        c.cmd === text ||
        c.cmd.startsWith(text) ||
        c.desc.toLowerCase().includes(text.toLowerCase()),
    ),
  );

  // Open the command menu while the buffer is a "/" prefix (PLAN §37).
  $effect(() => {
    if (text.startsWith("/")) menu = "commands";
    else if (menu === "commands") menu = null;
  });

  // --------------------------------------------------------------------------
  // PLAN §37: @ picker — files + sessions.
  // --------------------------------------------------------------------------
  let atQuery = $derived(
    (() => {
      const i = text.lastIndexOf("@");
      if (i < 0) return null;
      const rest = text.slice(i + 1);
      if (/\s/.test(rest)) return null; // a space ends the @ token
      return rest;
    })(),
  );
  let atActive = $derived(atQuery !== null);

  $effect(() => {
    if (!atActive) return;
    const q = atQuery ?? "";
    atCursor = 0;
    const base = store.session?.workspace ?? "";
    let alive = true;
    ws.request("workspace.files", { path: base, query: q, limit: 20 })
      .then((r) => { if (alive) atFiles = (r as any)?.files ?? []; })
      .catch(() => { if (alive) atFiles = []; });
    return () => { alive = false; };
  });

  let sessionMatches = $derived(
    (store.sessions ?? []).filter((s) => {
      const q = (atQuery ?? "").toLowerCase();
      if (!q) return true;
      return (s.title || s.id).toLowerCase().includes(q);
    }),
  );

  let atItems = $derived.by(() => {
    const out: { kind: "file" | "session"; label: string; value: string }[] = [];
    for (const f of atFiles) out.push({ kind: "file", label: f.path, value: "@" + f.path });
    for (const s of sessionMatches) out.push({ kind: "session", label: s.title || s.id, value: "@session:" + s.id });
    return out;
  });
  let atTitle = $derived(
    (store.sessions.length || store.session?.workspace)
      ? "files & sessions"
      : "no active workspace",
  );

  function insertAt(item: { value: string }) {
    const i = text.lastIndexOf("@");
    if (i >= 0) text = text.slice(0, i) + item.value + " ";
    else text += item.value + " ";
    menu = null;
    el?.focus();
  }

  function onKeyDown(e: KeyboardEvent) {
    if (menu === "at" && atItems.length) {
      if (e.key === "ArrowDown") { e.preventDefault(); atCursor = (atCursor + 1) % atItems.length; return; }
      if (e.key === "ArrowUp") { e.preventDefault(); atCursor = (atCursor - 1 + atItems.length) % atItems.length; return; }
      if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); insertAt(atItems[atCursor]); return; }
    }
    if (menu === "commands" && filteredCommands.length) {
      if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); pickCommand(filteredCommands[0].cmd); return; }
    }
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      doSubmit();
      return;
    }
    if (e.key === "Escape") menu = null;
  }

  function doSubmit() {
    const t = text.trim();
    if (!t) return;
    if (t.startsWith("/")) {
      handleCommand(t);
      text = "";
      return;
    }
    const kind = store.submit(t);
    if (kind === "sent") {
      ws.request("chat.send", {
        text: t,
        model: store.currentModel || undefined,
        reasoning: store.reasoningLevel || undefined,
      });
    } else {
      ws.request("chat.steer", { text: t });
    }
    text = "";
  }

  function handleCommand(t: string) {
    const [name, ...rest] = t.split(" ");
    switch (name) {
      case "/new":
        ws.request("session.create", {});
        break;
      case "/clear":
        store.resetTranscript();
        break;
      case "/plugins":
        store.overlay = "plugins";
        break;
      case "/model":
        menu = "model";
        text = "";
        break;
      case "/reasoning":
        menu = "reasoning";
        text = "";
        break;
      case "/compact":
        ws.request("session.compact", {});
        break;
      case "/reload":
        ws.request("plugin.reload", { pluginId: rest[0] ?? "" });
        break;
      case "/workspace":
        store.overlay = "settings";
        break;
      default:
        break;
    }
  }

  function pickCommand(cmd: string) {
    text = cmd + " ";
    menu = null;
    el?.focus();
  }

  // auto-grow the textarea
  $effect(() => {
    if (el) {
      el.style.height = "auto";
      el.style.height = Math.min(el.scrollHeight, 200) + "px";
    }
  });

  let reasoningLevels = $derived(
    store.currentModelInfo?.reasoning?.levels ?? [],
  );
  let modelLabel = $derived(store.currentModel || "select model");

  function openModelPicker() {
    menu = "model";
    ws.send("models.refresh");
  }
  function openReasoning() {
    if (reasoningLevels.length) menu = "reasoning";
  }

  function capLine(m: (typeof store.models)[0]): string {
    const bits: string[] = [];
    if (m.contextWindow) bits.push(`${Math.round(m.contextWindow / 1000)}k ctx`);
    if (m.reasoning?.levels?.length) bits.push("reasoning");
    if (m.inputModalities.includes("image")) bits.push("vision");
    return bits.join(" · ");
  }

  let modelMatches = $derived.by(() => {
    const q = store._modelQuery;
    if (!q) return store.models;
    return store.models.filter((m) => m.id.toLowerCase().includes(q.toLowerCase()));
  });
</script>

<div class="composer-wrap">
  {#if store.queuedSteer.length}
    <div class="queued">
      <div>Queued for next turn:</div>
      {#each store.queuedSteer as q (q.id)}
        <div>“{q.text}”</div>
      {/each}
    </div>
  {/if}

  <div class="composer">
    <textarea
      bind:this={el}
      bind:value={text}
      rows="1"
      placeholder="Message, / commands, @ files…"
      onkeydown={onKeyDown}
    ></textarea>

    <div class="toolbar">
      <button class="icon-btn" title="attach (future)">+</button>
      <button class="icon-btn" title="commands" onclick={() => (menu = menu === "commands" ? null : "commands")}>/</button>
      <button class="icon-btn" title="file/session picker" onclick={() => { if (text.length && !text.includes("@")) text += "@"; menu = menu === "at" ? null : "at"; }}>@</button>

      <span class="spacer"></span>

      {#if store.busy}
        <button class="stop-btn" onclick={() => ws.request("agent.cancel", {})} title="Stop">■</button>
        <span class="mode-label">Steer</span>
      {/if}

      <button
        class="model-pick"
        class:na={!store.models.length}
        onclick={openModelPicker}
      >{modelLabel}</button>

      {#if reasoningLevels.length}
        <button class="reason-pick" onclick={openReasoning}>{store.reasoningLevel || "reasoning"} ▾</button>
      {/if}

      <button
        class="send-btn"
        onclick={doSubmit}
        disabled={!text.trim()}
        title={store.busy ? "Steer" : "Send"}
      >↑</button>
    </div>
  </div>

  <!-- / command menu -->
  {#if menu === "commands" && filteredCommands.length}
    <div class="menu-pop" style="bottom: 66px; left: 12px; right: auto">
      <div class="title">commands</div>
      {#each filteredCommands as c (c.cmd)}
        <button class="item" onclick={() => pickCommand(c.cmd)}>
          <span>{c.cmd}</span>
          <span class="dim">{c.desc}</span>
        </button>
      {/each}
    </div>
  {/if}

  <!-- @ picker: files + sessions (PLAN §37) -->
  {#if menu === "at" && (atActive || atItems.length)}
    <div class="menu-pop" style="bottom: 66px; left: 12px; right: auto">
      <div class="title">{atTitle}</div>
      {#if atItems.length}
        {#each atItems as it, i (it.value + it.kind)}
          <button
            class="item"
            class:hl={i === atCursor}
            onclick={() => insertAt(it)}
            onmouseover={() => (atCursor = i)}
          >
            <span class="check">{it.kind === "session" ? "@session" : "@"}</span>
            <span style="flex:1">{it.label}</span>
          </button>
        {/each}
      {:else}
        <div class="cap">{store.session?.workspace ? "no matches" : "no active workspace"}</div>
      {/if}
    </div>
  {/if}

  <!-- model picker -->
  {#if menu === "model"}
    <div class="menu-pop" style="bottom: 66px; right: 12px; left: auto">
      <div class="title">select model</div>
      <input
        class="search"
        placeholder="Search models…"
        bind:value={store._modelQuery}
        onclick={(e) => e.stopPropagation()}
      />
      {#if store.modelsRefreshing}
        <div class="cap">refreshing…</div>
      {/if}
      {#each modelMatches as m (m.id)}
        <button
          class="item"
          onclick={() => {
            store.setModel(m.id);
            ws.request("session.model", { modelId: m.id, reasoning: store.reasoningLevel || undefined });
            menu = null;
          }}
        >
          <span class="check">{m.id === store.currentModel ? "✓" : ""}</span>
          <span style="flex:1">
            {m.id}
            {#if capLine(m)}<div class="cap">{capLine(m)}</div>{/if}
          </span>
        </button>
      {/each}
      {#if !modelMatches.length && !store.modelsRefreshing}
        <div class="cap">no models — is AiProxy running?</div>
      {/if}
    </div>
  {/if}

  <!-- reasoning picker -->
  {#if menu === "reasoning"}
    <div class="menu-pop" style="bottom: 66px; right: 12px; left: auto">
      <div class="title">reasoning</div>
      {#each reasoningLevels as lvl}
        <button
          class="item"
          onclick={() => {
            store.setReasoning(lvl);
            ws.request("session.reasoning", { level: lvl });
            menu = null;
          }}
        >
          <span class="check">{lvl === store.reasoningLevel ? "✓" : ""}</span>
          <span>{lvl}</span>
        </button>
      {/each}
    </div>
  {/if}
</div>

<style>
  .item.hl { background: var(--accent-soft, rgba(255, 255, 255, 0.12)); }
</style>
