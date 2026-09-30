# External MCP tools

NetPI.Mcp connects to external servers using stdio or Streamable HTTP. It supports MCP 2026-07-28 discovery and subscriptions and the stateful 2025-11-25 initialize flow. It does not expose NetPI as an MCP server.

## Minimal model context

Remote tools stay in the registry, picker, UI catalog and local search index. By default the model receives only `mcp_search` and `mcp_call`; increasing the catalog does not increase that schema prefix. Search returns up to three summaries (five maximum). Request `detail: "schema"` with one exact id, then call with that id, its revision and arguments. Search ranks names, descriptions, server synonyms and argument names locally.

Disclosure lives in retained tool-result history, not a process cache. Forks inheriting that history retain it; independent chats and compacted-away disclosures require rediscovery. Revisions cover schema, descriptive metadata and local execution overrides. Only definitions a chat has seen trigger revision notices. Oversized schemas are written completely to a local file rather than truncated.

Concrete tools obey global disabled tools, agent allowlists, profile selection and session switches. Granting only `mcp_call` grants no remote targets. The runtime resolves the target before policy hooks, rechecks selection before execution and preserves the outer provider call id. Journal events and result details identify the resolved target and server.

## Configuration

Use the MCP tab to add, edit, enable, inspect, refresh or reconnect servers. Settings are a map at `mcp.servers` keyed by a stable server id:

```json
{
  "mcp": {
    "servers": {
      "example": {
        "enabled": true,
        "transport": "stdio",
        "command": "node",
        "args": ["C:/tools/example/server.js"],
        "cwd": "C:/tools/example",
        "env": { "API_KEY": "EXAMPLE_API_KEY" },
        "pinned": [],
        "readOnly": []
      },
      "remote": {
        "enabled": true,
        "transport": "http",
        "url": "https://example.com/mcp",
        "headerEnv": { "Authorization": "EXAMPLE_AUTHORIZATION" }
      }
    }
  }
}
```

Environment and header values name source environment variables, never literal credentials. Set them before starting NetPI. The stdio environment includes only basic platform variables plus explicit mappings; processes run directly without a shell in a fixed absolute working directory. HTTP redirects are disabled.

`tools` optionally restricts raw remote tool names; an empty array exposes none. `pinned` opts named tools into ordinary eager schemas. `readOnly` is a local override allowing concurrent direct calls; remote annotations do not grant this. Both lists contain raw remote names. `synonyms` adds server search terms. Connection and call deadlines default to 5 seconds and 60 seconds.

## Lifecycle and limits

Catalog pagination completes before registration. Duplicate names, cursor loops, unsupported schema assertions and resource bounds fail explicitly. Known entries remain during temporary outages, with unavailable status; execution reports failure rather than silently replaying a possibly completed action. Reconnection uses backoff, notifications refresh catalogs and polling covers servers without notifications.

Tool outputs preserve text, structured data, supported images, links and embedded text. Binary payloads stay out of model text. Links are not fetched automatically. Server instructions are not injected into the system prompt. Sampling, elicitation, roots, OAuth, prompts, resources and legacy HTTP+SSE endpoints are outside this release. Native provider search adapters, code execution and projection are follow-ups.

A replacement plugin prepares previously healthy servers before taking over. A failed candidate retains the old plugin. Disposal settles pending calls, cancels listeners, removes registrations and terminates owned children after bounded grace. Builds do not install into the running app.

## Validation (2026-09-30)

All 23 MCP cases passed: 17 auxiliary transport/discovery/lifecycle cases, three agent runtime/context cases, one real host replacement/unload case and two end-to-end/UI cases. The normal build/test gate rebuilt all plugins and both UI layers. Providers, tools and host regression suites passed; the agent regressions found during implementation were fixed. The unchanged Chrome attachment case in the auxiliary suite still reports `user.HasExited`, so the overall gate is not fully green.

Actual OpenAI Responses requests captured by MockLlm contained the same 16,614 characters of serialized tools and 4,812 characters of instructions with 10, 100 and 1,000 catalog entries. These totals include existing native tools. Each task disclosed one schema, made one successful call and had zero argument errors. First successful-call times in one local run were 914, 441 and 471 ms respectively; these are fixture timings, not external-server performance guarantees. Provider token savings were not measured.

The MCP UI passed browser checks at 230, 380 and 1,000 px, including catalog controls and retaining drafts after a failed save. Host tests verified owned-child cleanup, collectible contexts and retaining a healthy process after failed replacement. No real servers, user credentials or running app installation were changed.
