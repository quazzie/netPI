<script lang="ts">
  import { marked } from "marked";
  import DOMPurify from "dompurify";
  import { store } from "../../store.svelte";
  import type { AssistantBlock, ToolCall } from "../../types";
  import ThinkingBlock from "./ThinkingBlock.svelte";
  import ToolCallBlock from "./ToolCallBlock.svelte";

  let { block }: { block: AssistantBlock } = $props();

  // ---- streaming markdown: stable-prefix incremental rendering -----------
  // Re-parsing the whole message every tick re-lays-out already-complete
  // text (mid-word wrap state, paragraph/fence re-evaluation), so the
  // message's height flickered on every re-render and the pinned viewport
  // bounced with it. Instead, only the paragraph currently being written is
  // re-rendered: when a paragraph completes it is promoted into the stable
  // prefix, whose DOM (same string -> Svelte leaves the nodes alone) is
  // never touched again.
  let stableHtml = $state("");
  let tailHtml = $state("");
  let finalHtml = $state("");
  let stableText = "";
  let lastCut = 0;
  let fenceOffsets: number[] = [];
  let fenceScanPos = 0;
  let timer: ReturnType<typeof setTimeout> | null = null;

  const knownFileExtensions = /\.(?:cs|fs|vb|csproj|sln|props|targets|ts|tsx|js|jsx|mjs|cjs|svelte|vue|py|rs|go|java|kt|kts|cpp|cc|c|h|hpp|json|jsonl|ya?ml|toml|xml|html?|css|scss|md|txt|sql|ps1|sh|bash|cmd|bat|ini|cfg)$/i;

  function looksLikeFile(value: string): boolean {
    const v = value.trim().replace(/^["'`]|["'`]$/g, "");
    if (!v || v.length > 320 || /[\r\n]/.test(v)) return false;
    return /^[a-zA-Z]:[\\/]/.test(v) ||
      v.startsWith("./") ||
      v.startsWith("../") ||
      v.includes("/") ||
      v.includes("\\") ||
      knownFileExtensions.test(v);
  }

  function fileHref(path: string): string {
    const params = new URLSearchParams({ path: path.trim() });
    if (store.session?.id) params.set("sessionId", store.session.id);
    return `/api/file?${params.toString()}`;
  }

  function openInShell(path: string) {
    const params = new URLSearchParams({ path: path.trim() });
    if (store.session?.id) params.set("sessionId", store.session.id);
    // fetch does not reject on HTTP error statuses — check res.ok and
    // surface the server's reason, otherwise a 404 would fail silently.
    fetch(`/api/open?${params.toString()}`)
      .then(async (res) => {
        if (!res.ok)
          store.setError(`Could not open ${path}: ${(await res.text().catch(() => "")) || res.statusText}`);
      })
      .catch((err) => store.setError(String(err)));
  }

  // File-like links render into {@html}, so per-node handlers are lost in the
  // innerHTML round-trip; intercept at the container instead. Plain clicks
  // shell-open via /api/open; ctrl/middle-click keeps the /api/file viewer.
  function mdClick(e: MouseEvent) {
    if (e.ctrlKey || e.metaKey || e.button !== 0) return;
    const a = (e.target as HTMLElement | null)?.closest<HTMLAnchorElement>("a.file-link");
    if (!a?.dataset.path) return;
    e.preventDefault();
    e.stopPropagation();
    openInShell(a.dataset.path);
  }

  // File-like links are only created in the final (done) render: recognizing
  // a path mid-stream and turning the <code> span into a link reflows the
  // line (font + metrics change). Streaming renders strip file-like hrefs
  // so nothing navigates; the single re-link at completion is a one-time
  // shift at the tail of a finished message.
  function decorateFileLinks(raw: string, decorateFiles: boolean): string {
    const template = document.createElement("template");
    template.innerHTML = raw;

    for (const anchor of template.content.querySelectorAll<HTMLAnchorElement>("a[href]")) {
      const href = anchor.getAttribute("href") ?? "";
      if (
        href &&
        !href.startsWith("#") &&
        !/^[a-z][a-z0-9+.-]*:/i.test(href) &&
        looksLikeFile(href)
      ) {
        anchor.target = "_blank";
        anchor.rel = "noopener";
        if (decorateFiles) {
          anchor.href = fileHref(href);
          anchor.classList.add("file-link");
          anchor.dataset.path = href;
        } else {
          // no href while streaming: a bare relative target would navigate
          // the SPA away on an accidental click
          anchor.removeAttribute("href");
        }
      } else if (/^https?:/i.test(href)) {
        anchor.target = "_blank";
        anchor.rel = "noopener noreferrer";
      }
    }

    for (const code of template.content.querySelectorAll<HTMLElement>("code")) {
      if (!decorateFiles) break; // no code→link reflow while streaming
      if (code.parentElement?.tagName === "PRE") continue;
      const value = code.textContent?.trim() ?? "";
      if (!looksLikeFile(value) || code.closest("a")) continue;

      const a = document.createElement("a");
      a.href = fileHref(value);
      a.target = "_blank";
      a.rel = "noopener";
      a.className = "file-link file-code-link";
      a.dataset.path = value;
      code.replaceWith(a);
      a.append(code);
    }

    return template.innerHTML;
  }

  // Record code-fence line-start offsets (append-only scan over the growing
  // text) so paragraph-boundary cuts can avoid splitting inside a fence.
  function noteFences(text: string) {
    if (text.length < fenceScanPos) {
      fenceOffsets = [];
      fenceScanPos = 0;
    }
    let lineStart = fenceScanPos === 0 || text[fenceScanPos - 1] === "\n";
    for (let pos = fenceScanPos; pos < text.length; pos++) {
      if (lineStart && /^[ \t]{0,3}(`{3,}|~{3,})/.test(text.slice(pos, pos + 8)))
        fenceOffsets.push(pos);
      lineStart = text[pos] === "\n";
    }
    fenceScanPos = text.length;
  }

  function fenceCountBefore(pos: number): number {
    let lo = 0, hi = fenceOffsets.length;
    while (lo < hi) {
      const mid = (lo + hi) >> 1;
      if (fenceOffsets[mid] < pos) lo = mid + 1;
      else hi = mid;
    }
    return lo;
  }

  // Highest paragraph boundary ("<newline><newline>") at/after lastCut with
  // an even fence count before it (never inside a code block). The cut only
  // ever moves forward: streaming text is append-only, so promoted stable
  // text is immutable.
  function computeCut(text: string): number {
    if (lastCut > text.length) lastCut = 0;
    let cut = lastCut;
    let from = cut;
    for (;;) {
      const idx = text.indexOf("\n\n", from);
      if (idx < 0) break;
      const cand = idx + 2;
      if (fenceCountBefore(cand) % 2 === 0) cut = cand;
      from = cand;
    }
    return cut;
  }

  function sanitizeHtml(raw: string): string {
    return DOMPurify.sanitize(raw, { ADD_ATTR: ["target", "rel"] }) as string;
  }

  function renderStream(text: string) {
    noteFences(text);
    const cut = computeCut(text);
    if (cut > 0) {
      const stable = text.slice(0, cut);
      if (stable !== stableText) {
        stableText = stable;
        lastCut = cut;
        stableHtml = decorateFileLinks(
          sanitizeHtml(marked.parse(stable, { async: false }) as string),
          false,
        );
      }
    }
    const tail = text.slice(cut);
    tailHtml = tail
      ? decorateFileLinks(sanitizeHtml(marked.parse(tail, { async: false }) as string), false)
      : "";
  }

  function renderFinal(text: string): string {
    stableText = "";
    lastCut = 0;
    fenceOffsets = [];
    fenceScanPos = 0;
    const raw = marked.parse(text, { async: false }) as string;
    return decorateFileLinks(sanitizeHtml(raw), true);
  }

  $effect(() => {
    const text = block.text;
    const done = block.done;

    if (!text) {
      stableHtml = "";
      tailHtml = "";
      finalHtml = "";
      return;
    }

    if (done) {
      if (timer) {
        clearTimeout(timer);
        timer = null;
      }
      finalHtml = renderFinal(text);
      return;
    }

    if (!timer) {
      timer = setTimeout(() => {
        timer = null;
        if (!block.done) renderStream(block.text);
      }, 80);
    }
  });

  function artifactPath(call: ToolCall): string | null {
    if (call.result === undefined || call.isError) return null;
    if (call.name !== "write" && call.name !== "edit") return null;
    try {
      const args = JSON.parse(call.argsJson || "{}");
      return typeof args.path === "string" && args.path.trim() ? args.path.trim() : null;
    } catch {
      return null;
    }
  }

  let artifacts = $derived.by(() => {
    const seen = new Set<string>();
    const result: { path: string; action: "created" | "modified" }[] = [];
    for (const call of block.toolCalls) {
      const path = artifactPath(call);
      if (!path || seen.has(path)) continue;
      seen.add(path);
      result.push({ path, action: call.name === "write" ? "created" : "modified" });
    }
    return result;
  });

  function baseName(path: string): string {
    const parts = path.replace(/\\/g, "/").split("/");
    return parts[parts.length - 1] || path;
  }
</script>

<article class="msg-assistant">
  <div class="assistant-content">
    {#if block.thinking && block.thinking.text}
      <ThinkingBlock thinking={block.thinking} />
    {/if}

    {#if block.text}
      <div class="md" onclick={mdClick}>
        {#if block.done && finalHtml}
          {@html finalHtml}
        {:else if stableHtml || tailHtml}
          {@html stableHtml}
          {@html tailHtml}
        {:else if block.text}
          <div class="streaming-raw">{block.text}</div>
        {/if}
      </div>
    {/if}

    {#if block.toolCalls.length}
      <div class="tool-stack">
        {#each block.toolCalls as call (call.id)}
          <ToolCallBlock call={call} />
        {/each}
      </div>
    {/if}

    {#if block.done && artifacts.length}
      <!-- astra-1 G3: artifact pills are a done-state surface — during a run
           the write/edit results may still be empty or changing. -->
      <div class="artifacts" aria-label="Artifacts">
        {#each artifacts as artifact (artifact.path)}
          <a
            class="artifact-pill"
            href={fileHref(artifact.path)}
            target="_blank"
            rel="noopener"
            title={`Open ${artifact.path} in its default application`}
            onclick={(e) => {
              // astra-1 G3: plain click shell-opens; ctrl/meta/middle keeps
              // the /api/file viewer (the anchor's href) — matches mdClick.
              if (e.ctrlKey || e.metaKey || e.button !== 0) return;
              e.preventDefault();
              e.stopPropagation();
              openInShell(artifact.path);
            }}
          >
            <span class="artifact-icon">▱</span>
            <span class="artifact-name">{baseName(artifact.path)}</span>
            <span class="artifact-action">{artifact.action}</span>
          </a>
        {/each}
      </div>
    {/if}

    {#if !block.done && !block.text && (!block.thinking || !block.thinking.text) && block.toolCalls.length === 0}
      <div class="assistant-waiting"><span></span><span></span><span></span></div>
    {/if}
  </div>
</article>
