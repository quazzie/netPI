<script lang="ts">
  import { marked } from "marked";
  import DOMPurify from "dompurify";
  import { store } from "../../store.svelte";
  import type { AssistantBlock, ToolCall } from "../../types";
  import ThinkingBlock from "./ThinkingBlock.svelte";
  import ToolCallBlock from "./ToolCallBlock.svelte";

  let { block }: { block: AssistantBlock } = $props();

  let html = $state("");
  let renderedText = "";
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

  function decorateFileLinks(raw: string): string {
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
        anchor.href = fileHref(href);
        anchor.target = "_blank";
        anchor.rel = "noopener";
        anchor.classList.add("file-link");
      } else if (/^https?:/i.test(href)) {
        anchor.target = "_blank";
        anchor.rel = "noopener noreferrer";
      }
    }

    for (const code of template.content.querySelectorAll<HTMLElement>("code")) {
      if (code.parentElement?.tagName === "PRE") continue;
      const value = code.textContent?.trim() ?? "";
      if (!looksLikeFile(value) || code.closest("a")) continue;

      const a = document.createElement("a");
      a.href = fileHref(value);
      a.target = "_blank";
      a.rel = "noopener";
      a.className = "file-link file-code-link";
      code.replaceWith(a);
      a.append(code);
    }

    return template.innerHTML;
  }

  function renderMarkdown(text: string) {
    const raw = marked.parse(text, { async: false }) as string;
    const clean = DOMPurify.sanitize(raw, {
      ADD_ATTR: ["target", "rel"],
    });
    html = decorateFileLinks(clean);
    renderedText = text;
  }

  $effect(() => {
    const text = block.text;
    const done = block.done;

    if (!text) {
      html = "";
      renderedText = "";
      return;
    }

    if (done) {
      if (timer) {
        clearTimeout(timer);
        timer = null;
      }
      renderMarkdown(text);
      return;
    }

    if (!timer) {
      timer = setTimeout(() => {
        timer = null;
        renderMarkdown(block.text);
      }, 80);
    }

    return () => {
      if (done && timer) {
        clearTimeout(timer);
        timer = null;
      }
    };
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
      <div class="md">
        {#if html && renderedText === block.text}
          {@html html}
        {:else if html}
          {@html html}
          <span class="stream-tail">{block.text.slice(renderedText.length)}</span>
        {:else}
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

    {#if artifacts.length}
      <div class="artifacts" aria-label="Artifacts">
        {#each artifacts as artifact (artifact.path)}
          <a
            class="artifact-pill"
            href={fileHref(artifact.path)}
            target="_blank"
            rel="noopener"
            title={artifact.path}
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
