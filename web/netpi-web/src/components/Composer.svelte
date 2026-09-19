<script lang="ts">
  import { store } from "../store.svelte";
  import { ws } from "../ws";

  let text = $state("");
  let el: HTMLTextAreaElement | null = $state(null);

  let menu = $state<null | "commands" | "model" | "reasoning">(null);

  const commands = [
    { cmd: "/model", desc: "Select model" },
    { cmd: "/reasoning", desc: "Set reasoning level" },
    { cmd: "/new", desc: "New session" },
    { cmd: "/compact", desc: "Compact context now" },
    { cmd: "/reload", desc: "Reload plugin" },
    { cmd: "/plugins", desc: "Plugin manager" },
    { cmd: "/workspace", desc: "Change workspace" },
    { cmd: "/clear", desc: "Clear conversation" },
  ];

  let filteredCommands = $derived(
    commands.filter(
      (c) =>
        c.cmd === text ||
        c.cmd.startsWith(text) ||
        c.desc.toLowerCase().includes(text.toLowerCase()),
    ),
  );

  // Open the command menu while the buffer is a "/" prefix (PLAN §commands).
  $effect(() => {
    if (text.startsWith("/")) menu = "commands";
    else if (menu === "commands") menu = null;
  });

  function onKeyDown(e: KeyboardEvent) {
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
    // Local /commands
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
    // opening the picker triggers a refresh (PLAN §42): cached first, then GET
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
      placeholder="Message, / commands…"
      onkeydown={onKeyDown}
    ></textarea>

    <div class="toolbar">
      <button class="icon-btn" title="attach (future)">+</button>
      <button class="icon-btn" title="commands" onclick={() => (menu = menu === "commands" ? null : "commands")}>/</button>
      <button class="icon-btn" title="file/session picker (future)">@</button>

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
