<script lang="ts">
  import { onMount } from "svelte";
  import { store } from "../store.svelte";
  import { ui } from "../ui.svelte";
  import { ws } from "../ws";
  import ContextUsage from "./ContextUsage.svelte";
  import type { ImageAttachment } from "../types";

  let text = $state("");
  let el: HTMLTextAreaElement | null = null;
  let imagePicker: HTMLInputElement;
  let composerRoot: HTMLDivElement | null = $state(null);
  let imageDrafts = $state<Record<string, ImageAttachment[]>>({});
  let newDraft = $state("");
  let images = $derived(imageDrafts[store.session?.id ?? "new"] ?? []);
  let processingImages = $state(false);
  let supportsImages = $derived(store.currentModelInfo?.inputModalities.includes("image") ?? false);

  async function attachImages(files: File[]) {
    if (!supportsImages) { store.setError("Choose an image-capable model before attaching images."); return; }
    const key = store.session?.id ?? "new";
    if (processingImages) return;
    processingImages = true;
    try {
      const next = [...(imageDrafts[key] ?? [])];
      for (const file of files) {
        if (next.length >= 4) throw new Error("Attach at most four images.");
        if (!["image/png", "image/jpeg", "image/gif", "image/webp"].includes(file.type))
          throw new Error("Use PNG, JPEG, GIF or WebP images.");
        if (file.size > 20 * 1024 * 1024) throw new Error("Choose images smaller than 20 MiB.");
        const bitmap = await createImageBitmap(file);
        try {
          const scale = Math.min(1, 1600 / Math.max(bitmap.width, bitmap.height));
          const canvas = document.createElement("canvas");
          canvas.width = Math.max(1, Math.round(bitmap.width * scale));
          canvas.height = Math.max(1, Math.round(bitmap.height * scale));
          const ctx = canvas.getContext("2d")!;
          ctx.fillStyle = "white"; ctx.fillRect(0, 0, canvas.width, canvas.height);
          ctx.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
          let url = canvas.toDataURL("image/jpeg", 0.85);
          for (let quality = 0.7; url.length > 195000 && quality >= 0.3; quality -= 0.15)
            url = canvas.toDataURL("image/jpeg", quality);
          if (url.length > 195000) throw new Error("Image is too large after resizing; crop it before attaching.");
          next.push({ mimeType: "image/jpeg", data: url.split(",")[1] });
        } finally { bitmap.close(); }
      }
      imageDrafts[key] = next;
    } catch (error) { store.setError(error instanceof Error ? error.message : String(error)); }
    finally { processingImages = false; }
  }

  function onPaste(event: ClipboardEvent) {
    const files = Array.from(event.clipboardData?.items ?? [])
      .filter(item => item.kind === "file" && item.type.startsWith("image/"))
      .map(item => item.getAsFile()).filter((file): file is File => !!file);
    if (files.length) { event.preventDefault(); void attachImages(files); }
  }

  // astra-1 G1: drafts are per-session and survive tab switches (bounded in
  // the store). Load the draft whenever the visible session changes; save on
  // every edit. A session with no draft restores as empty, not stale text.
  // Keyed on session id (not the session object — applySession replaces it
  // on every session.updated, which must not clobber in-progress text).
  let draftKey = $derived(store.session?.id ?? null);
  $effect(() => {
    const sid = draftKey;
    text = sid ? store.getDraft(sid) : newDraft;
  });
  function onInput() {
    const sid = store.session?.id ?? null;
    if (sid) store.setDraft(sid, text);
    else newDraft = text;
  }
  $effect(() => {
    // Ideas panel "insert into chat": the store already appended the idea to
    // this session's draft (chat.prefill) — pull it into the local text (the
    // session-switch effect above won't re-run, the id didn't change) and put
    // the caret back in the prompt.
    const tick = store.prefillTick;
    if (!tick) return;
    const sid = store.session?.id ?? null;
    if (sid && (store.drafts[sid] ?? "") !== text) {
      text = store.getDraft(sid);
      queueMicrotask(() => el?.focus({ preventScroll: true }));
    }
  });
  let menu = $state<null | "commands" | "model" | "reasoning" | "at" | "attach">(null);
  $effect(() => {
    if (!menu) return;
    const outside = (event: PointerEvent) => {
      if (event.target instanceof Node && !composerRoot?.contains(event.target)) menu = null;
    };
    const outsideClick = (event: MouseEvent) => {
      if (event.target instanceof Node && !composerRoot?.contains(event.target)) menu = null;
    };
    const escape = (event: KeyboardEvent) => { if (event.key === "Escape") menu = null; };
    document.addEventListener("pointerdown", outside);
    document.addEventListener("click", outsideClick, true);
    document.addEventListener("keydown", escape);
    return () => {
      document.removeEventListener("pointerdown", outside);
      document.removeEventListener("click", outsideClick, true);
      document.removeEventListener("keydown", escape);
    };
  });
  let atCursor = $state(0);
  let atFiles = $state<{ path: string; full: string; size: number }[]>([]);
  let serverCommands = $state<{ cmd: string; desc: string }[]>([]);

  onMount(() => {
    // After a (re)load, put the caret back in the prompt so typing works immediately.
    el?.focus({ preventScroll: true });
  });

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
    if ((!t && !images.length) || processingImages || store.requestPending) return;
    if (images.length && (!supportsImages || store.busy)) {
      store.setError(store.busy ? "Wait for the current run to finish before sending images." : "Choose an image-capable model before sending images.");
      return;
    }
    const sentImages = [...images];
    const sentSid = store.session?.id ?? null;
    const sentKey = sentSid ?? "new";

    if (t.startsWith("/") && !sentImages.length) {
      handleCommand(t);
      text = "";
      if (sentSid) store.setDraft(sentSid, "");
      else newDraft = "";
      return;
    }

    store.beginSubmit();
    const kind = store.submit(t, sentImages);
    imageDrafts[sentKey] = [];
    text = "";
    if (sentSid) store.setDraft(sentSid, "");
    else newDraft = "";
    // astra-1 G3: if the send is REJECTED, the optimistic user block must not
    // look accepted — put the text back in the draft and retract it.
    const onSendError = (e: unknown) => {
      if ((store.session?.id ?? null) === sentSid) {
        store.retractLastUser(t);
        text = t;
      }
      if (sentSid) store.setDraft(sentSid, t);
      else newDraft = t;
      imageDrafts[sentKey] = sentImages;
      store.requestFailed(e instanceof Error ? e.message : String(e));
      store.setError(e instanceof Error ? e.message : String(e));
    };

    try {
      if (kind === "sent") {
        await ws.request("chat.send", {
          text: t,
          images: sentImages,
          sessionId: store.session?.id,
          workspace: store.session?.workspace || undefined,
          model: store.currentModel || undefined,
          reasoning: store.reasoningLevel || undefined,
          // astra-1 §11a (F/A): a stable operationId names this send, so a retry of the
          // SAME operation replays the host's existing result instead of appending a
          // duplicate message / starting a duplicate run. Each deliberate tap makes a
          // fresh send(), hence a fresh id (identical text still gets a distinct id).
          operationId: globalThis.crypto?.randomUUID?.() ?? `op-${Date.now()}-${Math.random().toString(16).slice(2)}`,
        });
      } else {
        await ws.request("chat.steer", {
          text: t,
          sessionId: store.session?.id,
        });
      }
      store.requestAccepted();
    } catch (e) {
      onSendError(e);
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
        // astra-1 F: explicit navigation — the created session becomes visible.
        ws.createSession({ workspace: store.session?.workspace || undefined }).catch((e) => store.setError(String(e)));
        break;
      case "/plugins":
        ui.setRightTab("diagnostics", true);
        break;
      case "/settings":
      case "/workspace":
        // astra-1 G1: settings moved to the dialog — signal the shell.
        ui.requestSettings();
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
    // Track the draft explicitly. Measuring only when the textarea element was
    // bound let a bad first-layout width leave a huge inline height behind.
    text;
    if (!el) return;

    queueMicrotask(() => {
      if (!el) return;
      el.style.height = "0px";
      const next = Math.min(160, Math.max(38, el.scrollHeight));
      el.style.height = next + "px";
      el.style.overflowY = el.scrollHeight > 160 ? "auto" : "hidden";
    });
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

<div class="composer-wrap" bind:this={composerRoot}>
  {#if store.queuedSteer.length}
    <div class="queued">
      <div>Queued for next turn:</div>
      {#each store.queuedSteer as q (q.id)}
        <div>“{q.text}”</div>
      {/each}
    </div>
  {/if}

  <div class="composer">
    <input type="file" accept="image/png,image/jpeg,image/gif,image/webp" multiple hidden bind:this={imagePicker}
      onchange={() => { void attachImages(Array.from(imagePicker.files ?? [])); imagePicker.value = ""; }} />
    {#if images.length}
      <div class="prompt-images">
        {#each images as image, index}
          <div><img src={`data:${image.mimeType};base64,${image.data}`} alt={`Attachment ${index + 1}`} />
            <button title="Remove image" onclick={() => { imageDrafts[store.session?.id ?? "new"] = images.filter((_, i) => i !== index); }}>×</button></div>
        {/each}
      </div>
    {/if}
    {#if processingImages}<small>Preparing images…</small>{/if}
    <textarea
      bind:this={el}
      bind:value={text}
      rows="1"
      placeholder="Message, / commands, @ files…"
      onkeydown={onKeyDown}
      onpaste={onPaste}
     oninput={onInput}></textarea>

    <div class="toolbar">
      <button class="icon-btn" title="Add context" onclick={() => (menu = menu === "attach" ? null : "attach")}>＋</button>
      <button class="icon-btn" title="Commands" onclick={() => (menu = menu === "commands" ? null : "commands")}>/</button>
      <button class="icon-btn" title="Reference file or session" onclick={openAtPicker}>@</button>

      <span class="spacer"></span>

      <button class="model-pick" onclick={openModelPicker}>{modelLabel} ▾</button>

      <ContextUsage />

      <button
        class="reason-pick"
        class:na={!reasoningOptions.length}
        disabled={!reasoningOptions.length}
        title={reasoningOptions.length ? "Reasoning effort" : "Selected model does not advertise reasoning levels"}
        onclick={openReasoning}
      >
        {reasoningOptions.length ? (store.reasoningLevel || "off") : "reasoning n/a"} ▾
      </button>

      {#if store.busy || store.requestPending}
      <button
        class="stop-btn"
        title="Stop"
        onclick={() => { store.cancel(); ws.request("agent.cancel", {}).catch(() => {}); }}
      >■</button>
      {:else}
      <button
        class="send-btn"
        onclick={() => void doSubmit()}
        disabled={(!text.trim() && !images.length) || processingImages}
        title="Send"
      >↑</button>
      {/if}
    </div>
  </div>

  {#if menu === "attach"}
    <div class="menu-pop composer-left-menu">
      <div class="title">add context</div>
      <button class="item" disabled={!supportsImages || processingImages} onclick={() => { imagePicker.click(); menu = null; }}>Attach image…</button>
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

<style>
  .prompt-images { display:flex; gap:8px; padding:8px; }
  .prompt-images div { position:relative; }
  .prompt-images img { width:76px; height:60px; object-fit:cover; border-radius:5px; }
  .prompt-images button { position:absolute; right:0; top:0; background:#222; color:white; border:0; border-radius:4px; cursor:pointer; }
</style>
