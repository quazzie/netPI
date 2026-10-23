# External MCP tools

NetPI.Mcp connects to external servers using stdio or Streamable HTTP. It supports MCP 2026-07-28 discovery and subscriptions and the stateful 2025-11-25 initialize flow. It does not expose NetPI as an MCP server.

## Minimal model context

Every remote tool is registered as an ordinary tool (`mcp_<server>_<tool>_<hash>`) that is **deferred**: a chat is not sent
its schema until it loads it. The model finds one with [`tool_search`](TOOLS.md#tool_search-deferred-tools), which loads
the matches into the chat (from the next call they are declared with their own schemas and called directly), or calls
one by name, or reaches it from a [`codemode`](TOOLS.md) script (`searchTools`, `describeTool`, `tools.<name>`) without
loading it at all. A larger catalog does not grow the request: only loaded tools are sent. `pinned` declares a tool from
the start. Remote tools obey global disabled tools, agent allowlists, profile selection and session switches like any
other, and the policy hooks (guardrails, plan mode) see the call itself.

Revisions cover schema, descriptive metadata and local execution overrides. A loaded or pinned tool whose revision
changes gets a targeted "tools" notice (cause `remote-server`); its new schema is in the next request.

## Arguments the model wrote as text

A model often writes JSON as a string inside a typed argument: `"list_only":"true"`, `"timeout":"10"`, `"config":"{\"views\":[…]}"`. When the arguments fail the tool's schema, the call repairs them in one pass under the same rule: the schema is the only authority, and a value changes only where the schema for that exact path leaves no other reading of it.

- A string where the schema asks for a boolean, integer, number, object or array is re-read, and only when the text parses exactly to it (`"007"` and `"+1"` are not the numbers written).
- An object with a single `item`/`items` property standing where the schema says `array` is unwrapped: a wrapped list is the list, anything else is a one-element list. The same value is sometimes wrapped more than once — `{"item":{"item":[…]}}` — so wrappers are peeled until the value is reached.
- The repair is skipped entirely — and the model's own error stands — when a property permits a string, when the schema also allows an object at that path, when the wrapper holds more than one property, when the wrapper's key is unknown, and when the repaired arguments are not the ones the whole schema accepts.

So arguments that already validate are never rewritten, a string the schema allows is never re-read, a list of lists keeps its nesting, and anything left over fails with the model's own error. The repair is recorded in the log; the server receives the repaired types.

## Scripts and outside agents

A script or an outside agent calls a tool through NetPI rather than opening its own MCP session with the server's URL
(idea-fhtryf): `node scripts/netpi.mjs mcp.read --params-file args.json` runs a tool marked read-only (its `readOnlyHint`,
or the server's `readOnly` list), and `mcp.call --write` runs any exposed tool, with
`{ "id": "homeassistant/ha_get_state", "arguments": { … } }`. The arguments take the model's path (the schema, then the
one repair above), the answer is `{ content, structuredContent, isError, … }`, and `diag.tools` lists the call.
`mcp.list` masks what would let a reader use a server without NetPI: a private URL path, query values, a token among a
stdio server's arguments (idea-3q0uzn); `mcp.config`, which is not read-only, returns the configuration whole for the
editor. The methods are in [PROTOCOL.md](PROTOCOL.md#mcp-plugin).

## Resources

Remote resources are negotiated, listed and read. A server may advertise resources and no tools at all — a skills library, a document set — and connects on that promise alone; `tools` is no longer a required capability.

`mcp_resource {query}` lists the closest resources (up to five, ranked by uri, name, description and MIME type) and `mcp_resource {uri, server?}` reads one. The rules that keep this honest:

- **The URI is the gate.** Only a resource the server itself listed can be read, so a link that merely appears in a tool result is never fetched. "Links are not fetched automatically" still holds.
- **Binary never reaches model text.** A `blob` is reported by uri, mime type and size; its bytes are neither decoded into the conversation nor stored.
- **Oversized is whole, not truncated.** A read over `mcp.maxResourceChars` is written complete to `<temp>/mcp-resources/` and the model is given a preview plus the path.
- **`resources` mirrors `tools` as an allow-list.** Omitted exposes every advertised resource, `[]` exposes none, and an entry may be an exact URI or a prefix ending in `*`.
- **Subscriptions are out of scope.** `resources/subscribe` needs the server→client request path NetPI does not open; `subscribe: true` is recorded, not acted on.

`notifications/resources/list_changed` refreshes the catalog exactly like the tools path: a server that announces `resources.listChanged` is listened to even when it has no tools (or its tools announce nothing), and the modern `subscriptions/listen` asks for `resourcesListChanged` beside `toolsListChanged`; an acknowledgement that confirms either is accepted. `resources/templates/list`, prompts, elicitation, sampling and OAuth remain outside this release.

## Configuration

Use the MCP tab to add, edit, enable, inspect, refresh or reconnect servers. In its form, "Only these tools", "Schemas always in the chat" and "Safe to call at the same time" choose from the tools the server offers now (`mcp.tools`; the kit's `PickList`), and a name it does not offer yet can be typed. Settings are a map at `mcp.servers` keyed by a stable server id:

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

Environment and header values name source environment variables, never literal credentials. A name is resolved against the current user and machine environment on Windows (the registry) first — the user's current intent wins over the process's start-time snapshot — with NetPI's own environment as the fallback for a variable only the launcher set. A variable set after NetPI started is picked up at the next connect, request or reconnect, without a restart. The stdio environment includes only basic platform variables plus explicit mappings; processes run directly without a shell in a fixed absolute working directory. HTTP redirects are disabled.

`tools` optionally restricts raw remote tool names; an empty array exposes none. `resources` does the same for resource URIs (exact, or a prefix ending in `*`). `pinned` opts named tools into ordinary eager schemas. `readOnly` is a local override allowing concurrent direct calls; remote annotations do not grant this. Both lists contain raw remote names. Connection and call deadlines default to 5 seconds and 60 seconds.

## Lifecycle and limits

Catalog pagination completes before registration. Duplicate names, cursor loops, unsupported schema assertions and resource bounds fail explicitly. Known entries remain during temporary outages, with unavailable status; execution reports failure rather than silently replaying a possibly completed action. Reconnection uses backoff, notifications refresh catalogs and polling covers servers without notifications.

Tool outputs preserve text, structured data, supported images, links and embedded text. Binary payloads stay out of model text. Links are not fetched automatically. Server instructions are not injected into the system prompt. Sampling, elicitation, roots, OAuth, prompts and legacy HTTP+SSE endpoints are outside this release. Native provider search adapters and projection are follow-ups.

A replacement plugin prepares previously healthy servers before taking over. A failed candidate retains the old plugin. Disposal settles pending calls, cancels listeners, removes registrations and terminates owned children after bounded grace. Builds do not install into the running app.

## Validation

The end-to-end case `mcp.deferred` (real server, MockLlm) loads one tool out of a catalog of 10, 100 and 1,000 with
`tool_search` and calls it: the first request is the same size for all three (2026-10-11: 9,125 characters of tools,
6,881 of instructions). The MCP UI passed browser checks at 230, 380 and 1,000 px (2026-09-30). Until 2026-10-11 the
model reached remote tools through its own `mcp_search` / `mcp_call` pair; `tool_search` and `codemode` replaced it.
