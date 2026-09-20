<script lang="ts">
  import { store } from "../store.svelte";
  import { ui } from "../ui.svelte";
  import { ws } from "../ws";

  let text = $state("");
  let el: HTMLTextAreaElement | null = null;
  let menu = $state<null | "commands" | "model" | "reasoning" | "at" | "attach">(null);
  let atCursor = $state(0);
  let atFiles = $state<{ path: string; full: string; size: number }[]>([]);
  let serverCommands = $state<{ cmd: string; desc: string }[]>([]);

  const baselineCommands = [
    { cmd: "/model", desc: "Select model" },
    { cmd: "/reasoning", desc: "Set reasoning level" },
    { cmd: "/new", desc: "New session" },
    { cmd: "/plugins", desc: "Open plugin panel" },
    { cmd: "/settings", desc: "Open settings" },
    { cmd: "/workspace", desc: "Workspace settings" },
    { cmd: "/compact", desc: "Compact context now" },
  ];

  $effect(() => {
    let alive = true;
    ws.request("commands.list")
      .then((r) => {
        if (!alive) return;
        const raw = (r as any)?.commands ?? [];
        serverCommands = raw
          .filter((c: any) => c?.name)
          .map((c: any) => ({ cmd: String(c.name), desc: String(c.description ?? "") }));
      })
      .catch(() => {});
    return () => { alive = false; };
  });

  const commands = $derived(
    [...baselineCommands, ...serverCommands].filter(
      (c, i, arr) => arr.findIndex((x) => x.cmd === c.cmd) === i,
    ),
  );

  let filteredCommands = $derived(
    commands.filter((c) =>
      !text.startsWith("/") ||
      c.cmd.startsWith(text) ||
      c.desc.toLowerCase().includes(text.slice(1).toLowerCase()),
    ),
  );

  $effect(() => {
    if (text.startsWith("/")) menu = "commands";
    else if (menu === "commands") menu = null;
  });

  // ---- @ file/session picker --------------------------------------------
  let atQuery = $derived.by(() => {
    const i = text.lastIndexOf("@");
    if (i < 0) return null;
    const rest = text.slice(i + 1);
    if (/\s/.test(rest) && !rest.startsWith('"')) return null;
    return rest.replace(/^"/, "").replace(/"$/, "");
  });

  $effect(() => {
    if (menu !== "at" || atQuery === null) return;
    atCursor = 0;
    const basePath = store.session?.workspace ?? "";
    let alive = true;
    ws.request("workspace.files", {
      path: basePath,
      query: atQuery,
      limit: 50,
    })
      .then((r) => { if (alive) atFiles = (r as any)?.files ?? []; })
      .catch(() => { if (alive) atFiles = []; });
    return () => { alive = false; };
  });

  let atItems = $derived.by(() =>
    atFiles.map((f) => ({ kind: "file" as const, label: f.path, value: f.path })),
  );

  function quoteMention(value: string): string {
    return /\s/.test(value) ? `@"${value}"` : "@" + value;
  }

  function insertAt(item: { kind: "file"; value: string }) {
    const i = text.lastIndexOf("@");
    const mention = quoteMention(item.value);
    if (i >= 0) text = text.slice(0, i) + mention + " ";
    else text += (text && !text.endsWith(" ") ? " " : "") + mention + " ";
    menu = null;
    queueMicrotask(() => el?.focus());
  }

  function openAtPicker() {
    if (text.lastIndexOf("@") < 0 || atQuery === null)
      text += (text && !text.endsWith(" ") ? " " : "") + "@";
    menu = "at";
    queueMicrotask(() => el?.focus());
  }

  // ---- native + button ---------------------------------------------------
  function nativeWebView(): any {
    return (window as any).chrome?.webview;
  }

  function chooseNativeFiles() {
    const bridge = nativeWebView();
    if (!bridge) {
      openAtPicker();
      return;
    }

    const requestId = "pick_" + Date.now().toString(36);
    const handler = (ev: any) => {
      const data = ev?.data;
      if (!data || data.type !== "native.filesPicked" || data.requestId !== requestId) return;
      bridge.removeEventListener("message", handler);
      const paths = Array.isArray(data.paths) ? data.paths : [];
      if (paths.length) {
        const mentions = paths.map((p: string) => quoteMention(p)).join(" ");
        text += (text && !text.endsWith(" ") ? " " : "") + mentions + " ";
        queueMicrotask(() => el?.focus());
      }
    };
    bridge.addEventListener("message", handler);
    bridge.postMessage({ type: "native.pickFiles", requestId });
    menu = null;
  }

  // ---- submit ------------------------------------------------------------
  async function doSubmit() {
    const t = text.trim();
    if (!t) return;

    if (t.startsWith("/")) {
      handleCommand(t);
      text = "";
      return;
    }

    store.beginSubmit();
    const kind = store.submit(t);
    text = "";

    try {
      if (kind === "sent") {
        await ws.request("chat.send", {
          text: t,
          sessionId: store.session?.id,
          workspace: store.session?.workspace || undefined,
          model: store.currentModel || undefined,
          reasoning: store.reasoningLevel || undefined,
        });
      } else {
        await ws.request("chat.steer", {
          text: t,
          sessionId: store.session?.id,
        });
      }
      store.requestAccepted();
    } catch (e) {
      store.requestFailed(e instanceof Error ? e.message : String(e));
    }
  }

  function onKeyDown(e: KeyboardEvent) {
    if (menu === "at" && atItems.length) {
      if (e.key === "ArrowDown") { e.preventDefault(); atCursor = (atCursor + 1) % atItems.length; return; }
      if (e.key === "ArrowUp") { e.preventDefault(); atCursor = (atCursor - 1 + atItems.length) % atItems.length; return; }
      if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); insertAt(atItems[atCursor]); return; }
    }
    if (menu === "commands" && filteredCommands.length && e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      pickCommand(filteredCommands[0].cmd);
      return;
    }
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      void doSubmit();
      return;
    }
    if (e.key === "Escape") menu = null;
  }

  function handleCommand(t: string) {
    const [name, ...rest] = t.split(" ");
    switch (name) {
      case "/new":
        ws.request("session.create", { workspace: store.session?.workspace || undefined }).catch((e) => store.setError(String(e)));
        break;
      case "/plugins":
        ui.setRightTab("plugins", true);
        break;
      case "/settings":
      case "/workspace":
        ui.leftOpen = true;
        ui.leftPage = "settings";
        break;
      case "/model":
        openModelPicker();
        break;
      case "/reasoning":
        openReasoning();
        break;
      case "/compact":
        ws.request("session.compact", {
          sessionId: store.session?.id,
          model: store.currentModel || undefined,
          reasoning: store.reasoningLevel || undefined,
        }).catch((e) => store.setError(String(e)));
        break;
      case "/reload":
        if (rest[0]) ws.request("plugin.reload", { pluginId: rest[0] }).catch((e) => store.setError(String(e)));
        break;
    }
  }

  function pickCommand(cmd: string) {
    text = cmd + " ";
    menu = null;
    queueMicrotask(() => el?.focus());
  }

  $effect(() => {
    if (el) {
      el.style.height = "auto";
      el.style.height = Math.min(el.scrollHeight, 220) + "px";
    }
  });

  // ---- model/reasoning ---------------------------------------------------
  let reasoningLevels = $derived(store.currentModelInfo?.reasoning?.levels ?? []);
  let reasoningOptions = $derived(reasoningLevels.length ? ["off", ...reasoningLevels.filter((x) => x !== "off")] : []);
  let modelLabel = $derived(store.currentModel || "Select model");

  function openModelPicker() {
    menu = "model";
    store.modelsRefreshing = true;
    ws.request("models.refresh")
      .catch((e) => {
        store.modelsRefreshing = false;
        store.setError(e instanceof Error ? e.message : String(e));
      });
  }

  function openReasoning() {
    if (reasoningOptions.length) menu = "reasoning";
  }

  async function selectModel(id: string) {
    store.setModel(id);
    menu = null;
    if (store.session) {
      try {
        await ws.request("session.model", {
          sessionId: store.session.id,
          modelId: id,
          reasoning: store.reasoningLevel || undefined,
        });
      } catch (e) {
        store.setError(e instanceof Error ? e.message : String(e));
      }
    }
  }

  async function selectReasoning(level: string) {
    store.setReasoning(level);
    menu = null;
    if (store.session) {
      try {
        await ws.request("session.reasoning", {
          sessionId: store.session.id,
          level,
        });
      } catch (e) {
        store.setError(e instanceof Error ? e.message : String(e));
      }
    }
  }

  function capLine(m: (typeof store.models)[0]): string {
    const bits: string[] = [];
    if (m.contextWindow) bits.push(`${Math.round(m.contextWindow / 1000)}k ctx`);
    if (m.reasoning?.levels?.length) bits.push(m.reasoning.levels.join("/"));
    if (m.inputModalities.includes("image")) bits.push("vision");
    return bits.join(" · ");
  }

  let modelMatches = $derived.by(() => {
    const q = store._modelQuery.trim().toLowerCase();
    return q ? store.models.filter((m) => m.id.toLowerCase().includes(q)) : store.models;
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
      <button class="icon-btn" title="Add context" onclick={() => (menu = menu === "attach" ? null : "attach")}>＋</button>
      <button class="icon-btn" title="Commands" onclick={() => (menu = menu === "commands" ? null : "commands")}>/</button>
      <button class="icon-btn" title="Reference file or session" onclick={openAtPicker}>@</button>

      <span class="spacer"></span>

      {#if store.busy || store.requestPending}
        <button class="stop-btn" onclick={() => { store.cancel(); ws.request("agent.cancel", {}).catch(() => {}); }} title="Stop">■</button>
      {/if}

      <button class="model-pick" onclick={openModelPicker}>{modelLabel} ▾</button>

      <button
        class="reason-pick"
        class:na={!reasoningOptions.length}
        disabled={!reasoningOptions.length}
        title={reasoningOptions.length ? "Reasoning effort" : "Selected model does not advertise reasoning levels"}
        onclick={openReasoning}
      >
        {reasoningOptions.length ? (store.reasoningLevel || "off") : "reasoning n/a"} ▾
      </button>

      {#if store.busy}<span class="mode-label">Steer</span>{/if}

      <button
        class="send-btn"
        onclick={() => void doSubmit()}
        disabled={!text.trim() || store.requestPending}
        title={store.busy ? "Steer at next turn boundary" : "Send"}
      >
        {store.requestPending ? "…" : "↑"}
      </button>
    </div>
  </div>

  {#if menu === "attach"}
    <div class="menu-pop composer-left-menu">
      <div class="title">add context</div>
      <button class="item" onclick={openAtPicker}>
        <span class="check">@</span>
        <span>Workspace file</span>
        <span class="dim">reference</span>
      </button>
      <button class="item" onclick={chooseNativeFiles}>
        <span class="check">＋</span>
        <span>{nativeWebView() ? "Choose local file…" : "Choose workspace file…"}</span>
        <span class="dim">{nativeWebView() ? "Windows picker" : "browser"}</span>
      </button>
    </div>
  {/if}

  {#if menu === "commands" && filteredCommands.length}
    <div class="menu-pop composer-left-menu">
      <div class="title">commands</div>
      {#each filteredCommands as c (c.cmd)}
        <button class="item" onclick={() => pickCommand(c.cmd)}>
          <span>{c.cmd}</span>
          <span class="dim">{c.desc}</span>
        </button>
      {/each}
    </div>
  {/if}

  {#if menu === "at"}
    <div class="menu-pop composer-left-menu resource-menu">
      <div class="title">workspace files</div>
      {#if !store.session?.workspace}
        <div class="cap">Create/open a session with a workspace to browse files.</div>
      {:else if atItems.length}
        {#each atItems as it, i (it.value + it.kind)}
          <button
            class="item"
            class:hl={i === atCursor}
            onclick={() => insertAt(it)}
            onmouseover={() => (atCursor = i)}
          >
            <span class="check">@</span>
            <span class="resource-label">{it.label}</span>
            <span class="dim">file</span>
          </button>
        {/each}
      {:else}
        <div class="cap">No matching workspace files.</div>
      {/if}
    </div>
  {/if}

  {#if menu === "model"}
    <div class="menu-pop composer-right-menu model-menu">
      <div class="title">select model</div>
      <input class="search" placeholder="Search models…" bind:value={store._modelQuery} />
      {#if store.modelsRefreshing}<div class="cap">Refreshing catalog…</div>{/if}
      {#each modelMatches as m (m.id)}
        <button class="item model-item" onclick={() => void selectModel(m.id)}>
          <span class="check">{m.id === store.currentModel ? "✓" : ""}</span>
          <span class="model-item-main">
            <strong>{m.id}</strong>
            {#if capLine(m)}<small>{capLine(m)}</small>{/if}
          </span>
        </button>
      {/each}
      {#if !modelMatches.length && !store.modelsRefreshing}
        <div class="cap">No models returned by AiProxy.</div>
      {/if}
    </div>
  {/if}

  {#if menu === "reasoning"}
    <div class="menu-pop composer-right-menu reasoning-menu">
      <div class="title">reasoning effort</div>
      {#each reasoningOptions as level}
        <button class="item" onclick={() => void selectReasoning(level)}>
          <span class="check">{(store.reasoningLevel || "off") === level ? "✓" : ""}</span>
          <span>{level}</span>
        </button>
      {/each}
    </div>
  {/if}
</div>
