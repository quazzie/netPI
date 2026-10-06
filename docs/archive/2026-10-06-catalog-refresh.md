# Automatic catalog updates after backend/profile changes

AiGateway previously read endpoint/model configuration only at startup. NetPI's model signatures ignored backend metadata, and local OpenAI-compatible provider caches were only refreshed when queried.

The gateway now validates and reloads config every second, refreshes live residency, retains gates/counters for unchanged endpoints, and waits for active/held requests before replacing/removing endpoints. Prefix routes use the current registry. NInfer upstream created timestamps reach the catalog so a restart under the same model ID is visible.

NetPI's local OpenAI-compatible providers poll every 2 seconds respecting modelsCacheSeconds. Provider and host change signatures include locality and Extra metadata, including created and engine/host/GPU. Existing models.changed subscribers refresh UI controls and agent scheduling. Plugin unload cancels and joins the polling task.

Verification: full zero-warning solution build and core-size check; all 79 E2E tests passed (907 checks), including the new models.background-refresh case. Six unit suites ran: Host 225/225, Agent 245/245, Tools 105/105, Storage 118/118; Providers 76/77 and Aux 406/407 initially exposed two fixture assumptions. Background polling discovers the extra endpoint's text-only model capabilities, so the image request test now selects a known vision model. The embedding fixture used per-process randomized String.GetHashCode, making rankings nondeterministic; it now uses a fixed FNV hash. The affected provider/cache cases passed 12/12 and the expanded embedding/memory/decide/MCP cases passed 43/43 after repair. No production prompt was replayed and no owner process was stopped.

Delivery: changes merged before publishing; NetPI staged with build.ps1 -Publish -NextStart. The gateway build is staged in AiSwitcher publish/staging/gateway for the owner's Replace action; NetPI consumes its pending build on its next start. The already-installed Swift15 template override takes effect on the next Swift15 launch.
