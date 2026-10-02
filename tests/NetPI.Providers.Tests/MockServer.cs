using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NetPI.Providers.Tests;

internal sealed record RecordedRequest(string Method, string Path, string Body, Dictionary<string, string> Headers)
{
    public JsonObject Json => JsonNode.Parse(Body)!.AsObject();
}

/// <summary>
/// In-process Kestrel emulating AiProxy (/v1/models, /v1/responses, /v1/chat/completions) and the Anthropic
/// Messages API (/v1/messages, /v1/models when x-api-key is sent). Scenarios are selected by the request's model id.
/// </summary>
internal sealed class MockServer : IAsyncDisposable
{
    private WebApplication _app = null!;
    public string BaseUrl { get; private set; } = "";
    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();
    public volatile bool ModelsDown;
    /// <summary>Next /v1/responses call fails with response.failed (nInfer style: no code) and these ids.</summary>
    public (string ResponseId, string RequestId, string Message)? NextResponsesFailure;
    private int _modelsCalls;
    public int ModelsCalls => _modelsCalls;

    public RecordedRequest Last(string path) => Requests.Last(r => r.Path == path);

    public const string ModelsJson = """
    {
      "object": "list",
      "data": [
        {
          "id": "gemma-4", "object": "model", "owned_by": "llamacpp", "created": 1790183814,
          "context_window": 65536, "max_output_tokens": null, "concurrency": 1,
          "input_modalities": ["text"],
          "reasoning": { "supported": true, "efforts": ["none", "max"], "default": "max" },
          "meta": { "n_ctx": 65536 }, "status": { "value": "unloaded" }
        },
        {
          "id": "ornith15-9b-mtp-128k", "object": "model", "owned_by": "llamacpp", "created": 1790183814,
          "context_window": 131072, "max_output_tokens": null, "concurrency": 1,
          "input_modalities": ["text", "image"],
          "reasoning": { "supported": true, "efforts": ["none", "max"], "default": "max" },
          "meta": { "n_ctx": 131072 }, "status": { "value": "unloaded" }
        },
        {
          "id": "qwen38-27b-iq3s", "object": "model", "owned_by": "llamacpp", "created": 1790183814,
          "context_window": 524288, "max_output_tokens": null, "concurrency": null,
          "input_modalities": ["text"], "reasoning": null,
          "meta": { "n_ctx": 524288 }, "status": { "value": "stopped" }
        },
        {
          "id": "qwen3.8-27b", "object": "model", "owned_by": "ninfer", "created": 1790183814,
          "context_window": 262144, "max_output_tokens": 16384, "concurrency": 2,
          "input_modalities": ["text", "image"],
          "reasoning": { "supported": true, "efforts": ["none", "low", "medium", "xhigh"], "default": "medium" },
          "meta": { "n_ctx": 262144 }, "status": { "value": "loaded" }
        },
        {
          "id": "kev-9b", "object": "model", "owned_by": "llamacpp", "created": 1790183814,
          "context_window": 16384, "input_modalities": null, "reasoning": null,
          "meta": { "n_ctx": 16384 }, "status": { "value": "loaded" }, "api": "systemone"
        }
      ]
    }
    """;

    public const string AnthropicModelsJson = """
    {
      "data": [
        { "type": "model", "id": "claude-sonnet-4-5-20250929", "display_name": "Claude Sonnet 4.5", "created_at": "2025-09-29T00:00:00Z" },
        { "type": "model", "id": "claude-3-5-haiku-20241022", "display_name": "Claude Haiku 3.5", "created_at": "2024-10-22T00:00:00Z" }
      ],
      "has_more": false
    }
    """;

    /// <summary>OpenRouter catalog shapes (2026): reasoning objects with mandatory/supported_efforts, tool support flags.</summary>
    public const string OpenRouterModelsJson = """
    {
      "data": [
        { "id": "stealth/bunny", "name": "Space Bunny", "context_length": 1000000,
          "architecture": { "input_modalities": ["text", "image", "video"] }, "pricing": { "prompt": "0", "completion": "0" },
          "top_provider": { "context_length": 1000000, "max_completion_tokens": 524288 },
          "supported_parameters": ["include_reasoning", "max_tokens", "reasoning", "reasoning_effort", "tools", "tool_choice"],
          "reasoning": { "mandatory": true, "supported_efforts": ["max", "xhigh", "high", "medium", "low"], "default_effort": "max" } },
        { "id": "vendor/no-tools", "name": "No Tools", "context_length": 8192,
          "architecture": { "input_modalities": ["text"] }, "supported_parameters": ["max_tokens", "temperature"] },
        { "id": "vendor/plain:free", "name": "Plain (free)", "context_length": 32768,
          "architecture": { "input_modalities": ["text"] }, "top_provider": { "context_length": 32768, "max_completion_tokens": null },
          "supported_parameters": ["tools", "max_tokens"] },
        { "id": "vendor/switch", "name": "Switch", "context_length": 131072,
          "architecture": { "input_modalities": ["text"] }, "supported_parameters": ["tools", "reasoning"],
          "reasoning": { "mandatory": false, "default_enabled": true } },
        { "id": "anthropic/claude-x", "name": "Claude X", "context_length": 200000,
          "architecture": { "input_modalities": ["text", "image"] }, "top_provider": { "context_length": 200000, "max_completion_tokens": 64000 },
          "supported_parameters": ["tools", "reasoning"],
          "reasoning": { "mandatory": false, "supports_max_tokens": true, "supported_efforts": ["high", "medium", "low"], "default_effort": "medium" } },
        { "id": "vendor/any-effort", "name": "Any effort", "context_length": 65536,
          "architecture": { "input_modalities": ["text"] }, "supported_parameters": ["tools", "reasoning"],
          "reasoning": { "mandatory": false, "supported_efforts": null } }
      ]
    }
    """;

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();

        _app.MapGet("/v1/models", async ctx =>
        {
            Record(ctx, "");
            if (ctx.Request.Headers.ContainsKey("x-api-key"))
            {
                if (ctx.Request.Headers["x-api-key"] == "bad-key")
                {
                    ctx.Response.StatusCode = 401;
                    await ctx.Response.WriteAsync("""{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""");
                    return;
                }
                await Json(ctx, AnthropicModelsJson);
                return;
            }
            Interlocked.Increment(ref _modelsCalls);
            if (ModelsDown) { ctx.Abort(); return; }
            await Json(ctx, ModelsJson);
        });
        _app.MapPost("/v1/responses", Responses);
        _app.MapPost("/v1/chat/completions", Chat);
        _app.MapPost("/v1/messages", Messages);
        _app.MapGet("/openrouter/api/v1/models", async ctx => { Record(ctx, ""); await Json(ctx, OpenRouterModelsJson); });
        _app.MapPost("/openrouter/api/v1/chat/completions", OpenRouter);

        await _app.StartAsync();
        var addr = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        BaseUrl = addr.TrimEnd('/');
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private void Record(HttpContext ctx, string body)
    {
        var headers = ctx.Request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString());
        Requests.Enqueue(new RecordedRequest(ctx.Request.Method, ctx.Request.Path.Value ?? "", body, headers));
    }

    private async Task<JsonObject> ReadBody(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        Record(ctx, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private static Task Json(HttpContext ctx, string json, int status = 200)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(json);
    }

    private static async Task Sse(HttpContext ctx, IEnumerable<string> frames)
    {
        ctx.Response.ContentType = "text/event-stream";
        foreach (var f in frames)
        {
            await ctx.Response.WriteAsync(f);
            await ctx.Response.Body.FlushAsync();
        }
    }

    private static string D(object o) => "data: " + JsonSerializer.Serialize(o) + "\n\n";
    private static string E(string evt, object o) => $"event: {evt}\ndata: " + JsonSerializer.Serialize(o) + "\n\n";

    // ================================================================ /v1/responses

    private async Task Responses(HttpContext ctx)
    {
        var body = await ReadBody(ctx);
        var model = body["model"]!.GetValue<string>();
        if (NextResponsesFailure is { } nf)
        {
            NextResponsesFailure = null;
            ctx.Response.Headers["x-request-id"] = nf.RequestId;
            await Sse(ctx,
            [
                D(new { type = "response.created", response = new { id = nf.ResponseId, status = "in_progress" } }),
                D(new { type = "response.failed", response = new { id = nf.ResponseId, status = "failed", error = new { message = nf.Message } } }),
            ]);
            return;
        }
        switch (model)
        {
            case "err-503":
                await Json(ctx, """{"error":{"type":"backend_unavailable","message":"backend 'llama' is offline"}}""", 503);
                return;
            case "overflow":
                await Json(ctx, """{"error":{"code":400,"message":"the request exceeds the available context size, try increasing it","type":"exceed_context_size_error","n_prompt_tokens":70000,"n_ctx":65536}}""", 400);
                return;
            case "bad-json":
                // 200 + application/json, but an HTML page from whatever sits in front of the endpoint.
                await Json(ctx, "<!doctype html>\n<html><body><h1>502 Bad Gateway</h1></body></html>");
                return;
            case "rejected-tools":
                // The cause is outside error.message, and past the 2,000 characters the error message quotes.
                await Json(ctx, """{"error":{"type":"invalid_request_error","message":"3 tools rejected"},"rejected_tools":[""" +
                    string.Join(",", Enumerable.Range(0, 80).Select(i => $$"""{"name":"tool_{{i}}","reason":"schema too large for the model ({{i}} characters of explanation)"}""")) +
                    """],"trace_id":"tr-abc-9999"}""", 400);
                return;
            case "cutoff":
                await Sse(ctx, [E("response.output_text.delta", new { type = "response.output_text.delta", item_id = "m", output_index = 0, delta = "par" })]);
                ctx.Abort();
                return;
            case "eof":
                await Sse(ctx, [D(new { type = "response.output_text.delta", item_id = "m", output_index = 0, delta = "partial" })]);
                return;
            case "failed":
                await Sse(ctx, [D(new { type = "response.failed", response = new { status = "failed", error = new { code = "server_error", message = "boom" } } })]);
                return;
            case "slow":
                await Sse(ctx, [D(new { type = "response.output_text.delta", item_id = "m", output_index = 0, delta = "tick" })]);
                try { await Task.Delay(30_000, ctx.RequestAborted); } catch (OperationCanceledException) { }
                return;
            case "incomplete":
                await Sse(ctx,
                [
                    D(new { type = "response.output_item.added", output_index = 0, item = new { id = "msg_1", type = "message", role = "assistant", content = Array.Empty<object>() } }),
                    D(new { type = "response.output_text.delta", item_id = "msg_1", output_index = 0, content_index = 0, delta = "Truncated ans" }),
                    D(new { type = "response.incomplete", response = new { status = "incomplete", incomplete_details = new { reason = "max_output_tokens" }, usage = new { input_tokens = 10, output_tokens = 5 } } }),
                ]);
                return;
            case "done-only":
                await Sse(ctx,
                [
                    D(new { type = "response.output_item.done", output_index = 0, item = new { id = "rs_1", type = "reasoning", summary = new[] { new { type = "summary_text", text = "Summary A" }, new { type = "summary_text", text = "Summary B" } } } }),
                    D(new { type = "response.output_item.done", output_index = 1, item = new { id = "msg_1", type = "message", role = "assistant", content = new[] { new { type = "output_text", text = "Only done" } } } }),
                    D(new { type = "response.output_item.done", output_index = 2, item = new { id = "fc_1", type = "function_call", call_id = "call_d", name = "ls", arguments = "{\"dir\":\".\"}" } }),
                    D(new { type = "response.completed", response = new { status = "completed", usage = new { input_tokens = 7, output_tokens = 3 } } }),
                ]);
                return;
            case "output-only":
                await Sse(ctx,
                [
                    D(new
                    {
                        type = "response.completed",
                        response = new
                        {
                            status = "completed",
                            output = new object[]
                            {
                                new { id = "msg_9", type = "message", role = "assistant", content = new[] { new { type = "output_text", text = "From output" } } },
                                new { id = "fc_9", type = "function_call", call_id = "", name = "read", arguments = "" },
                            },
                            usage = new { input_tokens = 5, output_tokens = 2 },
                        },
                    }),
                ]);
                return;
        }

        // Default: reasoning + text + streamed function call + usage. One frame is split across two writes.
        var textDelta = D(new { type = "response.output_text.delta", item_id = "msg_1", output_index = 1, content_index = 0, delta = " world" });
        await Sse(ctx,
        [
            E("response.created", new { type = "response.created", response = new { id = "resp_1", status = "in_progress" } }),
            D(new { type = "response.output_item.added", output_index = 0, item = new { id = "rs_1", type = "reasoning", summary = Array.Empty<object>() } }),
            D(new { type = "response.reasoning_text.delta", item_id = "rs_1", output_index = 0, content_index = 0, delta = "Let me " }),
            D(new { type = "response.reasoning_text.delta", item_id = "rs_1", output_index = 0, content_index = 0, delta = "think." }),
            D(new { type = "response.output_item.done", output_index = 0, item = new { id = "rs_1", type = "reasoning", summary = Array.Empty<object>(), content = new[] { new { type = "reasoning_text", text = "Let me think." } }, encrypted_content = "ENC123" } }),
            D(new { type = "response.output_item.added", output_index = 1, item = new { id = "msg_1", type = "message", role = "assistant", content = Array.Empty<object>() } }),
            D(new { type = "response.content_part.added", item_id = "msg_1", output_index = 1, content_index = 0, part = new { type = "output_text", text = "" } }),
            D(new { type = "response.output_text.delta", item_id = "msg_1", output_index = 1, content_index = 0, delta = "Hello" }),
            textDelta[..25], textDelta[25..],
            D(new { type = "response.output_text.done", item_id = "msg_1", output_index = 1, content_index = 0, text = "Hello world" }),
            D(new { type = "response.output_item.done", output_index = 1, item = new { id = "msg_1", type = "message", content = new[] { new { type = "output_text", text = "Hello world" } } } }),
            D(new { type = "response.output_item.added", output_index = 2, item = new { id = "fc_1", type = "function_call", call_id = "call_abc", name = "read", arguments = "" } }),
            D(new { type = "response.function_call_arguments.delta", item_id = "fc_1", output_index = 2, delta = "{\"path\":" }),
            D(new { type = "response.function_call_arguments.delta", item_id = "fc_1", output_index = 2, delta = "\"a.txt\"}" }),
            D(new { type = "response.function_call_arguments.done", item_id = "fc_1", output_index = 2, arguments = "{\"path\":\"a.txt\"}" }),
            D(new { type = "response.output_item.done", output_index = 2, item = new { id = "fc_1", type = "function_call", call_id = "call_abc", name = "read", arguments = "{\"path\":\"a.txt\"}" } }),
            ": keep-alive comment\n\n",
            D(new
            {
                type = "response.completed",
                response = new
                {
                    id = "resp_1", status = "completed",
                    usage = new { input_tokens = 1000, input_tokens_details = new { cached_tokens = 600 }, output_tokens = 50, output_tokens_details = new { reasoning_tokens = 20 }, total_tokens = 1050 },
                },
            }),
        ]);
    }

    // ================================================================ OpenRouter /api/v1/chat/completions

    private static string OrChunk(object delta, string? finish = null, object? usage = null) =>
        D(new { id = "gen-1", @object = "chat.completion.chunk", provider = "Stealth", choices = new[] { new { index = 0, delta, finish_reason = finish } }, usage });

    private async Task OpenRouter(HttpContext ctx)
    {
        var body = await ReadBody(ctx);
        var model = body["model"]!.GetValue<string>();
        ctx.Response.Headers["X-Generation-Id"] = "gen-" + model.Replace('/', '-');
        switch (model)
        {
            case "vendor/rate-limited":
                ctx.Response.Headers["Retry-After"] = "7";
                await Json(ctx, """{"error":{"code":429,"message":"Rate limit exceeded: free-models-per-min.","metadata":{"headers":{"X-RateLimit-Limit":"20"}}}}""", 429);
                return;
            case "vendor/upstream-502":
                await Json(ctx, """{"error":{"code":502,"message":"Provider returned error","metadata":{"provider_name":"SomeHost","raw":"{\"error\":\"upstream exploded\"}"}}}""", 502);
                return;
            case "vendor/bad-json":
                await Json(ctx, "<!doctype html>\n<html><body><h1>502 Bad Gateway</h1></body></html>");
                return;
            case "vendor/rejected-tools":
                await Json(ctx, """{"error":{"type":"invalid_request_error","message":"3 tools rejected"},"rejected_tools":[""" +
                    string.Join(",", Enumerable.Range(0, 80).Select(i => $$"""{"name":"tool_{{i}}","reason":"schema too large for the model ({{i}} characters of explanation)"}""")) +
                    """],"trace_id":"tr-or-7777"}""", 400);
                return;
            case "vendor/mid-error":
                await Sse(ctx,
                [
                    OrChunk(new { role = "assistant", content = "Partial" }),
                    D(new { id = "gen-mid", @object = "chat.completion.chunk", provider = "UpstreamX", error = new { code = "server_error", message = "Provider disconnected unexpectedly" },
                            choices = new[] { new { index = 0, delta = new { content = "" }, finish_reason = "error" } } }),
                ]);
                return;
            case "vendor/frag":
                // A tool name split across chunks ("wr" + "ite_file"), and an id that only arrives after the part exists.
                await Sse(ctx,
                [
                    OrChunk(new { tool_calls = new[] { new { index = 0, type = "function", function = new { name = "wr" } } } }),
                    OrChunk(new { tool_calls = new[] { new { index = 0, id = "call_of1", type = "function", function = new { name = "ite_file" } } } }),
                    OrChunk(new { tool_calls = new[] { new { index = 0, function = new { arguments = "{\"path\":\"b.txt\"}" } } } }, "tool_calls"),
                    "data: [DONE]\n\n",
                ]);
                return;
            case "vendor/arg-types":
                // Typed arguments a gateway may send as a JSON object instead of a string, plus the same value split
                // across chunks. Nothing may be stringified or reshaped on the way in.
                await Sse(ctx,
                [
                    OrChunk(new { tool_calls = new[] { new { index = 0, id = "call_typed", type = "function", function = new { name = "set_dashboard",
                        arguments = new { views = new[] { new { title = "Probe", n = 1 } }, flag = true, n = 10, text = "10" } } } } }),
                    OrChunk(new { tool_calls = new[] { new { index = 1, id = "call_typed2", type = "function", function = new { name = "set_dashboard", arguments = "{\"views\":[" } } } }),
                    OrChunk(new { tool_calls = new[] { new { index = 1, function = new { arguments = "{\"n\":1}],\"flag\":true}" } } } }, "tool_calls"),
                    "data: [DONE]\n\n",
                ]);
                return;
        }

        // Default: reasoning text + reasoning_details streamed in pieces (keep-alive comments between), text, a tool call
        // in two pieces, then OpenRouter's usage chunk that repeats finish_reason, then [DONE].
        await Sse(ctx,
        [
            ": OPENROUTER PROCESSING\n\n",
            OrChunk(new { role = "assistant", content = "", reasoning = "Let me ",
                          reasoning_details = new object[] { new { type = "reasoning.text", text = "Let me ", format = "anthropic-claude-v1", index = 0 } } }),
            OrChunk(new { content = "", reasoning = "think.",
                          reasoning_details = new object[] { new { type = "reasoning.text", text = "think.", signature = "sig-1", format = "anthropic-claude-v1", index = 0 } } }),
            OrChunk(new { reasoning_details = new object[] { new { type = "reasoning.encrypted", data = "ENC", id = "rs_7", format = "openai-responses-v1", index = 1 } } }),
            ": OPENROUTER PROCESSING\n\n",
            OrChunk(new { content = "Reading " }),
            OrChunk(new { content = "it." }),
            OrChunk(new { tool_calls = new[] { new { index = 0, id = "call_or1", type = "function", function = new { name = "read", arguments = "{\"path\":" } } } }),
            OrChunk(new { tool_calls = new[] { new { index = 0, function = new { arguments = "\"a.txt\"}" } } } }, "tool_calls"),
            OrChunk(new { role = "assistant", content = "" }, "tool_calls", new
            {
                prompt_tokens = 1000, prompt_tokens_details = new { cached_tokens = 600, cache_write_tokens = 100 },
                completion_tokens = 50, completion_tokens_details = new { reasoning_tokens = 20 }, total_tokens = 1050, cost = 0.0012,
            }),
            "data: [DONE]\n\n",
        ]);
    }

    // ================================================================ /v1/chat/completions

    private static string Chunk(object delta, string? finish = null) =>
        D(new { id = "c1", @object = "chat.completion.chunk", choices = new[] { new { index = 0, delta, finish_reason = finish } } });

    private async Task Chat(HttpContext ctx)
    {
        var body = await ReadBody(ctx);
        var model = body["model"]!.GetValue<string>();
        switch (model)
        {
            case "err-503":
                await Json(ctx, """{"error":{"type":"backend_unavailable","message":"held for 120s"}}""", 503);
                return;
            case "bad-json":
                await Json(ctx, "<!doctype html>\n<html><body><h1>502 Bad Gateway</h1></body></html>");
                return;
            case "rejected-tools":
                await Json(ctx, """{"error":{"type":"invalid_request_error","message":"3 tools rejected"},"rejected_tools":[""" +
                    string.Join(",", Enumerable.Range(0, 80).Select(i => $$"""{"name":"tool_{{i}}","reason":"schema too large for the model ({{i}} characters of explanation)"}""")) +
                    """],"trace_id":"tr-or-7777"}""", 400);
                return;
            case "overflow":
                await Json(ctx, """{"error":{"message":"This model's maximum context length is 8192 tokens. However, your messages resulted in 9000 tokens.","type":"invalid_request_error","param":"messages","code":"context_length_exceeded"}}""", 400);
                return;
            case "cutoff":
                await Sse(ctx, [Chunk(new { content = "Hel" })]);
                ctx.Abort();
                return;
            case "eof":
                await Sse(ctx, [Chunk(new { content = "Hel" }), Chunk(new { content = "lo" })]);
                return;
            case "err-chunk":
                await Sse(ctx, [Chunk(new { content = "x" }), D(new { error = new { message = "slot unavailable", type = "server_error", code = 500 } })]);
                return;
            case "length":
                await Sse(ctx, [Chunk(new { content = "long answer" }), Chunk(new { }, "length"), "data: [DONE]\n\n"]);
                return;
            case "json":
                await Json(ctx, """
                {"id":"c2","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"<think>t</think>Plain JSON","tool_calls":[{"id":"call_j","type":"function","function":{"name":"ls","arguments":"{}"}}]},"finish_reason":"tool_calls"}],
                 "usage":{"prompt_tokens":12,"completion_tokens":4,"prompt_cache_hit_tokens":2}}
                """);
                return;
            case "frag":
                // A tool name split across chunks ("wr" + "ite_file"), and an id that only arrives after the part exists.
                await Sse(ctx,
                [
                    Chunk(new { tool_calls = new[] { new { index = 0, type = "function", function = new { name = "wr" } } } }),
                    Chunk(new { tool_calls = new[] { new { index = 0, id = "call_f1", type = "function", function = new { name = "ite_file" } } } }),
                    Chunk(new { tool_calls = new[] { new { index = 0, function = new { arguments = "{\"path\":" } } } }),
                    Chunk(new { tool_calls = new[] { new { index = 0, function = new { arguments = "\"a.txt\"}" } } } }, "tool_calls"),
                    "data: [DONE]\n\n",
                ]);
                return;
        }

        await Sse(ctx,
        [
            Chunk(new { role = "assistant", reasoning_content = "Plan: " }),
            Chunk(new { reasoning_content = "call tools." }),
            Chunk(new { content = "<thi" }),
            Chunk(new { content = "nk>inline " }),
            Chunk(new { content = "thought</th" }),
            Chunk(new { content = "ink>\n\nAnswer: 42 <" }),
            Chunk(new { content = "b>ok</b>" }),
            Chunk(new { tool_calls = new[] { new { index = 0, id = "call_1", type = "function", function = new { name = "read", arguments = "" } } } }),
            Chunk(new { tool_calls = new[] { new { index = 0, function = new { arguments = "{\"path\":" } } } }),
            Chunk(new { tool_calls = new[] { new { index = 1, id = "call_2", type = "function", function = new { name = "ls", arguments = "{}" } } } }),
            Chunk(new { tool_calls = new[] { new { index = 0, function = new { arguments = "\"b.txt\"}" } } } }),
            Chunk(new { tool_calls = new[] { new { index = 2, function = new { name = "bash", arguments = "{\"cmd\":\"ls\"}" } } } }),
            Chunk(new { }, "tool_calls"),
            D(new
            {
                id = "c1", choices = Array.Empty<object>(),
                usage = new { prompt_tokens = 500, completion_tokens = 80, total_tokens = 580, prompt_tokens_details = new { cached_tokens = 200 }, completion_tokens_details = new { reasoning_tokens = 30 } },
            }),
            "data: [DONE]\n\n",
        ]);
    }

    // ================================================================ /v1/messages (Anthropic)

    private async Task Messages(HttpContext ctx)
    {
        var body = await ReadBody(ctx);
        var model = body["model"]!.GetValue<string>();
        switch (model)
        {
            case "overloaded":
                await Json(ctx, """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""", 529);
                return;
            case "prompt-too-long":
                await Json(ctx, """{"type":"error","error":{"type":"invalid_request_error","message":"prompt is too long: 250000 tokens > 200000 maximum"}}""", 400);
                return;
            case "stream-error":
                await Sse(ctx,
                [
                    E("message_start", new { type = "message_start", message = new { id = "msg_e", usage = new { input_tokens = 5, output_tokens = 1 } } }),
                    E("error", new { type = "error", error = new { type = "overloaded_error", message = "Overloaded" } }),
                ]);
                return;
            case "cutoff":
                await Sse(ctx, [E("message_start", new { type = "message_start", message = new { id = "m", usage = new { input_tokens = 5, output_tokens = 1 } } })]);
                ctx.Abort();
                return;
            case "redacted":
                await Sse(ctx,
                [
                    E("message_start", new { type = "message_start", message = new { id = "msg_r", usage = new { input_tokens = 3, output_tokens = 1 } } }),
                    E("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "redacted_thinking", data = "REDACTED_BLOB" } }),
                    E("content_block_stop", new { type = "content_block_stop", index = 0 }),
                    E("content_block_start", new { type = "content_block_start", index = 1, content_block = new { type = "text", text = "" } }),
                    E("content_block_delta", new { type = "content_block_delta", index = 1, delta = new { type = "text_delta", text = "Done." } }),
                    E("content_block_stop", new { type = "content_block_stop", index = 1 }),
                    E("message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn" }, usage = new { output_tokens = 9 } }),
                    E("message_stop", new { type = "message_stop" }),
                ]);
                return;
            case "max-tokens":
                await Sse(ctx,
                [
                    E("message_start", new { type = "message_start", message = new { id = "msg_m", usage = new { input_tokens = 3, output_tokens = 1 } } }),
                    E("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } }),
                    E("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "cut" } }),
                    E("content_block_stop", new { type = "content_block_stop", index = 0 }),
                    E("message_delta", new { type = "message_delta", delta = new { stop_reason = "max_tokens" }, usage = new { output_tokens = 100 } }),
                    E("message_stop", new { type = "message_stop" }),
                ]);
                return;
        }

        await Sse(ctx,
        [
            E("message_start", new { type = "message_start", message = new { id = "msg_1", type = "message", role = "assistant", model, content = Array.Empty<object>(), usage = new { input_tokens = 100, cache_creation_input_tokens = 20, cache_read_input_tokens = 50, output_tokens = 1 } } }),
            E("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "thinking", thinking = "", signature = "" } }),
            E("ping", new { type = "ping" }),
            E("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "thinking_delta", thinking = "Hmm, " } }),
            E("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "thinking_delta", thinking = "let me see." } }),
            E("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "signature_delta", signature = "SIG==" } }),
            E("content_block_stop", new { type = "content_block_stop", index = 0 }),
            E("content_block_start", new { type = "content_block_start", index = 1, content_block = new { type = "text", text = "" } }),
            E("content_block_delta", new { type = "content_block_delta", index = 1, delta = new { type = "text_delta", text = "Sure" } }),
            E("content_block_delta", new { type = "content_block_delta", index = 1, delta = new { type = "text_delta", text = ", reading." } }),
            E("content_block_stop", new { type = "content_block_stop", index = 1 }),
            E("content_block_start", new { type = "content_block_start", index = 2, content_block = new { type = "tool_use", id = "toolu_01", name = "read", input = new { } } }),
            E("content_block_delta", new { type = "content_block_delta", index = 2, delta = new { type = "input_json_delta", partial_json = "" } }),
            E("content_block_delta", new { type = "content_block_delta", index = 2, delta = new { type = "input_json_delta", partial_json = "{\"path\": \"c" } }),
            E("content_block_delta", new { type = "content_block_delta", index = 2, delta = new { type = "input_json_delta", partial_json = ".txt\"}" } }),
            E("content_block_stop", new { type = "content_block_stop", index = 2 }),
            E("message_delta", new { type = "message_delta", delta = new { stop_reason = "tool_use", stop_sequence = (string?)null }, usage = new { output_tokens = 77 } }),
            E("message_stop", new { type = "message_stop" }),
        ]);
    }
}
