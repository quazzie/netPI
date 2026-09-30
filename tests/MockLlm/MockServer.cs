using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace NetPI.MockLlm;

public sealed class MockOptions
{
    public int Port { get; set; } = 7479;
    /// <summary>Stream speed factor (2 = twice as fast). Scenario delays are divided by it.</summary>
    public double Speed { get; set; } = 1;
    /// <summary>context_window of the <c>tiny-ctx</c> model.</summary>
    public int TinyContext { get; set; } = 12000;
    /// <summary>Required x-api-key for the Anthropic API (null = any non-empty key).</summary>
    public string? AnthropicKey { get; set; }
    /// <summary>Print one line per request.</summary>
    public bool Verbose { get; set; }
}

/// <summary>Per-model request statistics (in-flight concurrency, totals).</summary>
public sealed class ModelStats
{
    public int Inflight;
    public int MaxInflight;
    public int Total;
    public int Errors;
    /// <summary>Requests that started while more than the catalog concurrency were in flight.</summary>
    public int OverCapacity;
}

/// <summary>One logged request (<c>GET /_log</c>).</summary>
public sealed class LogEntry
{
    public long Seq { get; set; }
    public DateTimeOffset Time { get; set; }
    public string Api { get; set; } = "";
    public string Model { get; set; } = "";
    public string Scenario { get; set; } = "";
    public int Step { get; set; }
    public int Status { get; set; } = 200;
    public string? Error { get; set; }
    public bool Subagent { get; set; }
    public bool Summarizer { get; set; }
    public int Messages { get; set; }
    public int Tools { get; set; }
    public int InputTokens { get; set; }
    public int CachedTokens { get; set; }
    public int OutputTokens { get; set; }
    public string? Effort { get; set; }
    public int? MaxTokens { get; set; }
    public bool ThinkingEnabled { get; set; }
    public bool ThinkingReplayed { get; set; }
    public string LastRole { get; set; } = "";
    public string LastText { get; set; } = "";
    public List<string> Notices { get; set; } = [];
    public List<string> Calls { get; set; } = [];
    public bool Dropped { get; set; }
    public bool Cancelled { get; set; }
    public long DurationMs { get; set; }
    public string SystemHead { get; set; } = "";
}

/// <summary>The mock model server. See docs/TESTING.md.</summary>
public sealed class MockLlmServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions LogJson = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    /// <summary>Like real servers: no \u0022-style escaping of quotes and non-ASCII text.</summary>
    internal static readonly JsonSerializerOptions WireJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly MockOptions _o;
    private readonly WebApplication _app;
    private readonly ScenarioEngine _engine = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ModelStats> _stats = new(StringComparer.Ordinal);
    private readonly List<LogEntry> _log = [];
    private readonly Dictionary<long, string> _bodies = [];
    private long _seq;

    public Catalog Catalog { get; }
    public string BaseUrl => $"http://127.0.0.1:{_o.Port}";
    /// <summary>Cancelled when the host is asked to stop (SIGTERM).</summary>
    public CancellationToken Stopping => _app.Lifetime.ApplicationStopping;

    private MockLlmServer(MockOptions o, WebApplication app)
    {
        _o = o;
        _app = app;
        Catalog = new Catalog(o.TinyContext);
    }

    public static async Task<MockLlmServer> StartAsync(MockOptions o, CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, o.Port));
        var app = builder.Build();
        var server = new MockLlmServer(o, app);
        server.Map();
        await app.StartAsync(ct).ConfigureAwait(false);
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            try { await _app.StopAsync(cts.Token).ConfigureAwait(false); } catch { }
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    private void Map()
    {
        _app.MapGet("/health", () => Results.Json(new
        {
            status = "ok", proxy = "aiproxy",
            backends = new { llama = new { status = "ok" }, ninfer = new { status = "ok" }, mock = new { status = "ok" } },
        }));

        _app.MapGet("/v1/models", (HttpContext ctx) =>
            IsAnthropic(ctx) ? JsonResult(Catalog.AnthropicList()) : JsonResult(Catalog.AiProxyList()));
        _app.MapGet("/v1/models/{id}", (string id) =>
            Catalog.FindAiProxy(id) is { } m ? JsonResult(m.ToAiProxyJson(DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
                : Results.Json(new { error = new { message = $"model '{id}' not found", type = "not_found_error" } }, statusCode: 404));
        _app.MapGet("/anthropic/v1/models", (HttpContext ctx) =>
            CheckAnthropicAuth(ctx) is { } err ? err : JsonResult(Catalog.AnthropicList()));

        _app.MapPost("/v1/responses", ctx => HandleAsync(ctx, "responses"));
        _app.MapPost("/v1/chat/completions", ctx => HandleAsync(ctx, "chat"));
        _app.MapPost("/v1/messages", ctx => HandleAsync(ctx, "anthropic"));
        _app.MapPost("/anthropic/v1/messages", ctx => HandleAsync(ctx, "anthropic"));

        // ---- test control
        _app.MapGet("/_stats", () => JsonResult(StatsJson()));
        _app.MapPost("/_reset", () =>
        {
            lock (_gate)
            {
                foreach (var s in _stats.Values) { s.MaxInflight = s.Inflight; s.Total = 0; s.Errors = 0; s.OverCapacity = 0; }
                _log.Clear();
                _bodies.Clear();
            }
            _engine.Reset();
            return Results.Json(new { ok = true });
        });
        _app.MapGet("/_log", (HttpContext ctx) =>
        {
            var since = long.TryParse(ctx.Request.Query["since"], out var s) ? s : 0;
            List<LogEntry> entries;
            lock (_gate) entries = _log.Where(e => e.Seq > since).ToList();
            return Results.Text(JsonSerializer.Serialize(entries, LogJson), "application/json");
        });
        _app.MapGet("/_request/{seq}", (long seq) =>
        {
            string? body;
            lock (_gate) _bodies.TryGetValue(seq, out body);
            return body is null ? Results.NotFound() : Results.Text(body, "application/json");
        });
    }

    private static IResult JsonResult(JsonNode node) => Results.Text(node.ToJsonString(WireJson), "application/json");

    private static bool IsAnthropic(HttpContext ctx) =>
        ctx.Request.Headers.ContainsKey("x-api-key") || ctx.Request.Headers.ContainsKey("anthropic-version");

    private IResult? CheckAnthropicAuth(HttpContext ctx)
    {
        var key = ctx.Request.Headers["x-api-key"].ToString();
        if (key.Length == 0 || (_o.AnthropicKey is { } want && key != want))
            return Results.Json(new { type = "error", error = new { type = "authentication_error", message = "invalid x-api-key" } }, statusCode: 401);
        if (string.IsNullOrEmpty(ctx.Request.Headers["anthropic-version"]))
            return Results.Json(new { type = "error", error = new { type = "invalid_request_error", message = "anthropic-version: header is required" } }, statusCode: 400);
        return null;
    }

    public JsonObject StatsJson()
    {
        var models = new JsonObject();
        int total;
        lock (_gate)
        {
            foreach (var (id, s) in _stats)
                models[id] = new JsonObject
                {
                    ["inflight"] = s.Inflight, ["maxInflight"] = s.MaxInflight, ["total"] = s.Total,
                    ["errors"] = s.Errors, ["overCapacity"] = s.OverCapacity,
                };
            total = _log.Count;
        }
        return new JsonObject { ["models"] = models, ["requests"] = total };
    }

    // ------------------------------------------------------------------ model requests

    private async Task HandleAsync(HttpContext ctx, string api)
    {
        var started = DateTimeOffset.UtcNow;
        var entry = new LogEntry { Seq = Interlocked.Increment(ref _seq), Time = started, Api = api };
        string raw;
        using (var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8))
            raw = await reader.ReadToEndAsync(ctx.RequestAborted).ConfigureAwait(false);
        lock (_gate)
        {
            _log.Add(entry);
            _bodies[entry.Seq] = raw;
            if (_bodies.Count > 200) _bodies.Remove(_bodies.Keys.Min());
            if (_log.Count > 2000) _log.RemoveRange(0, 500);
        }

        ModelStats? stats = null;
        try
        {
            if (api == "anthropic" && CheckAnthropicAuth(ctx) is { } authError)
            {
                entry.Status = 401;
                entry.Error = "authentication_error";
                await authError.ExecuteAsync(ctx).ConfigureAwait(false);
                return;
            }

            NRequest req;
            try
            {
                var body = JsonNode.Parse(raw) as JsonObject ?? throw new MockApiException(400, "invalid_request_error", "Body must be a JSON object.");
                req = api switch
                {
                    "responses" => RequestParser.ParseResponses(body),
                    "chat" => RequestParser.ParseChat(body),
                    _ => RequestParser.ParseAnthropic(body),
                };
            }
            catch (JsonException ex)
            {
                throw new MockApiException(400, "invalid_request_error", "Invalid JSON body: " + ex.Message);
            }

            entry.Model = req.Model;
            entry.Subagent = req.IsSubagent;
            entry.Summarizer = req.IsSummarizer;
            entry.Messages = req.Messages.Count;
            entry.Tools = req.Tools.Count;
            entry.InputTokens = req.EstimateTokens();
            entry.Effort = req.Effort;
            entry.MaxTokens = req.MaxTokens;
            entry.ThinkingEnabled = req.ThinkingEnabled;
            entry.ThinkingReplayed = req.Messages.Any(m => m.Role == "assistant" && m.Thinking);
            entry.SystemHead = req.System.Length > 120 ? req.System[..120] : req.System;
            if (req.Messages.Count > 0)
            {
                var last = req.Messages[^1];
                entry.LastRole = last.IsNotice ? "notice:" + last.NoticeKind : last.Role;
                entry.LastText = last.Text.Length > 300 ? last.Text[..300] : last.Text;
            }
            entry.Notices = req.Messages.Where(m => m.IsNotice).Select(m => m.NoticeKind ?? "").ToList();

            var model = api == "anthropic" ? Catalog.FindAnthropic(req.Model) : Catalog.FindAiProxy(req.Model);
            if (model is null) throw new MockApiException(404, "not_found_error", $"model '{req.Model}' not found");
            if (model.Status is "stopped" or "offline")
            {
                await Task.Delay(200, ctx.RequestAborted).ConfigureAwait(false);
                throw new MockApiException(503, "backend_unavailable", $"backend for '{req.Model}' is not running");
            }
            if (api != "anthropic" && req.Effort is { } effort && model.Efforts is { } allowed && !allowed.Contains(effort))
                throw new MockApiException(400, "invalid_request_error", $"Unsupported reasoning effort '{effort}' for {model.Id} (allowed: {string.Join(", ", allowed)}).");
            if (req.MaxTokens is { } mt && model.MaxOutputTokens is { } cap && mt > cap)
                throw new MockApiException(400, "invalid_request_error", $"max_tokens ({mt}) exceeds the model's max_output_tokens ({cap}).");
            if (model.ContextWindow is { } window && entry.InputTokens > window)
                throw new MockApiException(400, api == "anthropic" ? "invalid_request_error" : "exceed_context_size_error",
                    api == "anthropic"
                        ? $"prompt is too long: {entry.InputTokens} tokens > {window} maximum"
                        : $"the request exceeds the available context size (n_ctx={window}, n_prompt_tokens={entry.InputTokens}), try increasing it");
            if (!req.Stream) throw new MockApiException(400, "invalid_request_error", "The mock only supports stream=true.");

            var plan = _engine.Decide(req);
            entry.Scenario = plan.Scenario;
            entry.Step = plan.Step;
            entry.Calls = plan.Calls.Select(c => c.Name).ToList();

            lock (_gate)
            {
                if (!_stats.TryGetValue(req.Model, out stats)) _stats[req.Model] = stats = new ModelStats();
                stats.Inflight++;
                stats.Total++;
                if (stats.Inflight > stats.MaxInflight) stats.MaxInflight = stats.Inflight;
                if (model.Concurrency is { } c && stats.Inflight > c) stats.OverCapacity++;
            }

            if (plan.ErrorStatus is { } status)
                throw new MockApiException(status, plan.ErrorType ?? "server_error", plan.ErrorMessage ?? "mock error");

            if (_o.Verbose)
                Console.WriteLine($"[{entry.Seq}] {api} {req.Model} scenario={plan.Scenario} step={plan.Step} msgs={req.Messages.Count} in={entry.InputTokens}" +
                                  (req.IsSubagent ? " (subagent)" : "") + (plan.Calls.Count > 0 ? " calls=" + string.Join(",", entry.Calls) : ""));

            var stream = new StreamOut(ctx, plan, _o.Speed);
            // Prompt caching: follow-up requests of a conversation hit the cache for most of the prompt.
            var cached = req.Messages.Any(m => m.Role == "assistant") ? entry.InputTokens * 6 / 10 : 0;
            var usage = new UsageOut(entry.InputTokens, cached, plan);
            entry.CachedTokens = usage.Cached;
            entry.OutputTokens = usage.Output;
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-cache";
            var completed = api switch
            {
                "responses" => await EmitResponsesAsync(stream, req, plan, usage).ConfigureAwait(false),
                "chat" => await EmitChatAsync(stream, req, plan, usage).ConfigureAwait(false),
                _ => await EmitAnthropicAsync(stream, req, plan, usage).ConfigureAwait(false),
            };
            if (!completed)
            {
                entry.Dropped = true;
                ctx.Abort();
            }
        }
        catch (MockApiException ex)
        {
            entry.Status = ex.Status;
            entry.Error = ex.Type + ": " + ex.Message;
            if (stats is not null) lock (_gate) stats.Errors++;
            if (_o.Verbose) Console.WriteLine($"[{entry.Seq}] {api} {entry.Model} -> {ex.Status} {ex.Message}");
            if (!ctx.Response.HasStarted)
            {
                object payload = api == "anthropic"
                    ? new { type = "error", error = new { type = ex.Type, message = ex.Message } }
                    : new { error = new { message = ex.Message, type = ex.Type, param = (string?)null, code = ex.Status } };
                ctx.Response.StatusCode = ex.Status;
                await ctx.Response.WriteAsJsonAsync(payload, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            entry.Cancelled = true;
        }
        catch (IOException)
        {
            entry.Cancelled = true;
        }
        finally
        {
            entry.DurationMs = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
            if (stats is not null) lock (_gate) stats.Inflight--;
        }
    }

    // ------------------------------------------------------------------ streaming helpers

    private sealed class StreamOut(HttpContext ctx, Plan plan, double speed)
    {
        private readonly int _delay = (int)Math.Round(plan.ChunkDelayMs / Math.Max(0.01, speed));
        private int _chunksLeftBeforeDrop = -1;
        private bool _held;
        public CancellationToken Ct => ctx.RequestAborted;

        public async Task SendAsync(string? evt, JsonNode data)
        {
            var sb = new StringBuilder();
            if (evt is not null) sb.Append("event: ").Append(evt).Append('\n');
            sb.Append("data: ").Append(data.ToJsonString(WireJson)).Append("\n\n");
            await ctx.Response.WriteAsync(sb.ToString(), Ct).ConfigureAwait(false);
            await ctx.Response.Body.FlushAsync(Ct).ConfigureAwait(false);
        }

        public async Task RawAsync(string text)
        {
            await ctx.Response.WriteAsync(text, Ct).ConfigureAwait(false);
            await ctx.Response.Body.FlushAsync(Ct).ConfigureAwait(false);
        }

        /// <summary>Pause between chunks. Returns false when the stream must drop now.</summary>
        public async Task<bool> TickAsync()
        {
            if (_delay > 0) await Task.Delay(_delay, Ct).ConfigureAwait(false);
            if (!_held && plan.HoldMs > 0)
            {
                _held = true; // once, after the first chunk: the client has the stream start and the first delta, and the rest waits
                await Task.Delay(plan.HoldMs, Ct).ConfigureAwait(false);
            }
            if (_chunksLeftBeforeDrop > 0 && --_chunksLeftBeforeDrop == 0)
            {
                if (plan.Drop) return false;
                if (plan.StallMs > 0) await Task.Delay(plan.StallMs, Ct).ConfigureAwait(false); // silent, connection kept open
            }
            return true;
        }

        /// <summary>Arm the drop/stall: after half of <paramref name="chunks"/> streamed chunks.</summary>
        public void ArmDrop(int chunks)
        {
            if ((plan.Drop || plan.StallMs > 0) && _chunksLeftBeforeDrop < 0) _chunksLeftBeforeDrop = Math.Max(1, chunks / 2);
        }
    }

    private sealed class UsageOut(int input, int cached, Plan plan)
    {
        /// <summary>Whole prompt, cached part included.</summary>
        public int Input { get; } = input;
        public int Cached { get; } = cached;
        public int Reasoning { get; } = (plan.Thinking?.Length ?? 0) / 4;
        public int Output { get; } = Math.Max(1, ((plan.Thinking?.Length ?? 0) + (plan.Text?.Length ?? 0) + plan.Calls.Sum(c => c.Args.Length + c.Name.Length)) / 4);
    }

    private static string NewId(string prefix) => prefix + Guid.NewGuid().ToString("N")[..20];

    private static List<string> ArgChunks(string args) => StreamChunks.Split(args, 10);

    // ------------------------------------------------------------------ Responses API

    private static async Task<bool> EmitResponsesAsync(StreamOut s, NRequest req, Plan plan, UsageOut usage)
    {
        var respId = NewId("resp_");
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var seq = 0;
        JsonObject Resp(string status, JsonArray output, JsonObject? usageJson = null)
        {
            var r = new JsonObject
            {
                ["id"] = respId, ["object"] = "response", ["created_at"] = created, ["status"] = status, ["model"] = req.Model,
                ["output"] = output,
            };
            if (usageJson is not null) r["usage"] = usageJson;
            return r;
        }
        Task Send(string type, JsonObject data)
        {
            data["type"] = type;
            data["sequence_number"] = seq++;
            return s.SendAsync(type, data);
        }

        await Send("response.created", new JsonObject { ["response"] = Resp("in_progress", []) }).ConfigureAwait(false);
        await Send("response.in_progress", new JsonObject { ["response"] = Resp("in_progress", []) }).ConfigureAwait(false);
        var outputs = new JsonArray();
        var index = 0;

        if (!string.IsNullOrEmpty(plan.Thinking))
        {
            var id = NewId("rs_");
            await Send("response.output_item.added", new JsonObject
            {
                ["output_index"] = index, ["item"] = new JsonObject { ["id"] = id, ["type"] = "reasoning", ["summary"] = new JsonArray(), ["content"] = new JsonArray() },
            }).ConfigureAwait(false);
            var chunks = StreamChunks.Split(plan.Thinking);
            if (string.IsNullOrEmpty(plan.Text)) s.ArmDrop(chunks.Count);
            foreach (var c in chunks)
            {
                await Send("response.reasoning_text.delta", new JsonObject { ["item_id"] = id, ["output_index"] = index, ["content_index"] = 0, ["delta"] = c }).ConfigureAwait(false);
                if (!await s.TickAsync().ConfigureAwait(false)) return false;
            }
            await Send("response.reasoning_text.done", new JsonObject { ["item_id"] = id, ["output_index"] = index, ["content_index"] = 0, ["text"] = plan.Thinking }).ConfigureAwait(false);
            var item = new JsonObject
            {
                ["id"] = id, ["type"] = "reasoning", ["summary"] = new JsonArray(),
                ["content"] = new JsonArray(new JsonObject { ["type"] = "reasoning_text", ["text"] = plan.Thinking }),
            };
            await Send("response.output_item.done", new JsonObject { ["output_index"] = index, ["item"] = item }).ConfigureAwait(false);
            outputs.Add(item.DeepClone());
            index++;
        }

        if (!string.IsNullOrEmpty(plan.Text))
        {
            var id = NewId("msg_");
            await Send("response.output_item.added", new JsonObject
            {
                ["output_index"] = index,
                ["item"] = new JsonObject { ["id"] = id, ["type"] = "message", ["status"] = "in_progress", ["role"] = "assistant", ["content"] = new JsonArray() },
            }).ConfigureAwait(false);
            await Send("response.content_part.added", new JsonObject
            {
                ["item_id"] = id, ["output_index"] = index, ["content_index"] = 0,
                ["part"] = new JsonObject { ["type"] = "output_text", ["text"] = "", ["annotations"] = new JsonArray() },
            }).ConfigureAwait(false);
            var chunks = StreamChunks.Split(plan.Text);
            s.ArmDrop(chunks.Count);
            for (var ci = 0; ci < chunks.Count; ci++)
            {
                await Send("response.output_text.delta", new JsonObject { ["item_id"] = id, ["output_index"] = index, ["content_index"] = 0, ["delta"] = chunks[ci] }).ConfigureAwait(false);
                if (!await s.TickAsync().ConfigureAwait(false)) return false;
                if (plan.StreamErrorType is { } et && ci == chunks.Count / 2)
                {
                    var failed = Resp("failed", outputs);
                    failed["error"] = new JsonObject { ["code"] = et, ["message"] = "mock: the backend failed while generating" };
                    await Send("response.failed", new JsonObject { ["response"] = failed }).ConfigureAwait(false);
                    return true;
                }
            }
            await Send("response.output_text.done", new JsonObject { ["item_id"] = id, ["output_index"] = index, ["content_index"] = 0, ["text"] = plan.Text }).ConfigureAwait(false);
            var part = new JsonObject { ["type"] = "output_text", ["text"] = plan.Text, ["annotations"] = new JsonArray() };
            await Send("response.content_part.done", new JsonObject { ["item_id"] = id, ["output_index"] = index, ["content_index"] = 0, ["part"] = part.DeepClone() }).ConfigureAwait(false);
            var item = new JsonObject { ["id"] = id, ["type"] = "message", ["status"] = "completed", ["role"] = "assistant", ["content"] = new JsonArray(part) };
            await Send("response.output_item.done", new JsonObject { ["output_index"] = index, ["item"] = item }).ConfigureAwait(false);
            outputs.Add(item.DeepClone());
            index++;
        }

        foreach (var call in plan.Calls)
        {
            var id = NewId("fc_");
            call.Id = NewId("call_");
            await Send("response.output_item.added", new JsonObject
            {
                ["output_index"] = index,
                ["item"] = new JsonObject { ["id"] = id, ["type"] = "function_call", ["status"] = "in_progress", ["call_id"] = call.Id, ["name"] = call.Name, ["arguments"] = "" },
            }).ConfigureAwait(false);
            foreach (var c in ArgChunks(call.Args))
            {
                await Send("response.function_call_arguments.delta", new JsonObject { ["item_id"] = id, ["output_index"] = index, ["delta"] = c }).ConfigureAwait(false);
                if (!await s.TickAsync().ConfigureAwait(false)) return false;
            }
            await Send("response.function_call_arguments.done", new JsonObject { ["item_id"] = id, ["output_index"] = index, ["arguments"] = call.Args }).ConfigureAwait(false);
            var item = new JsonObject { ["id"] = id, ["type"] = "function_call", ["status"] = "completed", ["call_id"] = call.Id, ["name"] = call.Name, ["arguments"] = call.Args };
            await Send("response.output_item.done", new JsonObject { ["output_index"] = index, ["item"] = item }).ConfigureAwait(false);
            outputs.Add(item.DeepClone());
            index++;
        }

        var usageJson = new JsonObject
        {
            ["input_tokens"] = usage.Input,
            ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = usage.Cached },
            ["output_tokens"] = usage.Output,
            ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = usage.Reasoning },
            ["total_tokens"] = usage.Input + usage.Output,
        };
        if (plan.Stop == "length")
        {
            var r = Resp("incomplete", outputs, usageJson);
            r["incomplete_details"] = new JsonObject { ["reason"] = "max_output_tokens" };
            await Send("response.incomplete", new JsonObject { ["response"] = r }).ConfigureAwait(false);
        }
        else await Send("response.completed", new JsonObject { ["response"] = Resp("completed", outputs, usageJson) }).ConfigureAwait(false);
        return true;
    }

    // ------------------------------------------------------------------ Chat Completions

    private static async Task<bool> EmitChatAsync(StreamOut s, NRequest req, Plan plan, UsageOut usage)
    {
        var id = NewId("chatcmpl-");
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Task Chunk(JsonObject delta, string? finish = null) => s.SendAsync(null, new JsonObject
        {
            ["id"] = id, ["object"] = "chat.completion.chunk", ["created"] = created, ["model"] = req.Model,
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish }),
        });

        await Chunk(new JsonObject { ["role"] = "assistant", ["content"] = null }).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(plan.Thinking))
        {
            var chunks = StreamChunks.Split(plan.Thinking);
            if (string.IsNullOrEmpty(plan.Text)) s.ArmDrop(chunks.Count);
            foreach (var c in chunks)
            {
                await Chunk(new JsonObject { ["reasoning_content"] = c }).ConfigureAwait(false);
                if (!await s.TickAsync().ConfigureAwait(false)) return false;
            }
        }
        if (!string.IsNullOrEmpty(plan.Text))
        {
            var chunks = StreamChunks.Split(plan.Text);
            s.ArmDrop(chunks.Count);
            for (var ci = 0; ci < chunks.Count; ci++)
            {
                await Chunk(new JsonObject { ["content"] = chunks[ci] }).ConfigureAwait(false);
                if (!await s.TickAsync().ConfigureAwait(false)) return false;
                if (plan.StreamErrorType is { } et && ci == chunks.Count / 2)
                {
                    // llama.cpp style: an error object as a data chunk, then the stream ends
                    await s.SendAsync(null, new JsonObject { ["error"] = new JsonObject { ["code"] = 500, ["message"] = "mock: the backend failed while generating", ["type"] = et } }).ConfigureAwait(false);
                    return true;
                }
            }
        }
        for (var i = 0; i < plan.Calls.Count; i++)
        {
            var call = plan.Calls[i];
            call.Id = NewId("call_");
            await Chunk(new JsonObject
            {
                ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["index"] = i, ["id"] = call.Id, ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = "" },
                }),
            }).ConfigureAwait(false);
            foreach (var c in ArgChunks(call.Args))
            {
                await Chunk(new JsonObject
                {
                    ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = i, ["function"] = new JsonObject { ["arguments"] = c } }),
                }).ConfigureAwait(false);
                if (!await s.TickAsync().ConfigureAwait(false)) return false;
            }
        }
        var finish = plan.Calls.Count > 0 ? "tool_calls" : plan.Stop == "length" ? "length" : "stop";
        await Chunk(new JsonObject(), finish).ConfigureAwait(false);
        await s.SendAsync(null, new JsonObject
        {
            ["id"] = id, ["object"] = "chat.completion.chunk", ["created"] = created, ["model"] = req.Model, ["choices"] = new JsonArray(),
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = usage.Input, ["completion_tokens"] = usage.Output, ["total_tokens"] = usage.Input + usage.Output,
                ["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = usage.Cached },
                ["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = usage.Reasoning },
            },
        }).ConfigureAwait(false);
        await s.RawAsync("data: [DONE]\n\n").ConfigureAwait(false);
        return true;
    }

    // ------------------------------------------------------------------ Anthropic Messages

    private static async Task<bool> EmitAnthropicAsync(StreamOut s, NRequest req, Plan plan, UsageOut usage)
    {
        Task Send(string type, JsonObject data)
        {
            data["type"] = type;
            return s.SendAsync(type, data);
        }

        await Send("message_start", new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["id"] = NewId("msg_"), ["type"] = "message", ["role"] = "assistant", ["model"] = req.Model, ["content"] = new JsonArray(),
                ["stop_reason"] = null, ["stop_sequence"] = null,
                ["usage"] = new JsonObject
                {
                    // Anthropic reports the uncached part only; cached tokens are separate
                    ["input_tokens"] = usage.Input - usage.Cached, ["cache_creation_input_tokens"] = 0, ["cache_read_input_tokens"] = usage.Cached, ["output_tokens"] = 1,
                },
            },
        }).ConfigureAwait(false);
        await Send("ping", new JsonObject()).ConfigureAwait(false);
        var index = 0;

        // With thinking enabled the real API always starts with a thinking block.
        var thinking = req.ThinkingEnabled ? plan.Thinking ?? "Considering the next step." : null;
        if (thinking is not null)
        {
            await Send("content_block_start", new JsonObject
            {
                ["index"] = index, ["content_block"] = new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "" },
            }).ConfigureAwait(false);
            var chunks = StreamChunks.Split(thinking);
            if (string.IsNullOrEmpty(plan.Text)) s.ArmDrop(chunks.Count);
            foreach (var c in chunks)
            {
                await Send("content_block_delta", new JsonObject { ["index"] = index, ["delta"] = new JsonObject { ["type"] = "thinking_delta", ["thinking"] = c } }).ConfigureAwait(false);
                if (!await s.TickAsync().ConfigureAwait(false)) return false;
            }
            await Send("content_block_delta", new JsonObject
            {
                ["index"] = index, ["delta"] = new JsonObject { ["type"] = "signature_delta", ["signature"] = RequestParser.Sig(thinking) },
            }).ConfigureAwait(false);
            await Send("content_block_stop", new JsonObject { ["index"] = index }).ConfigureAwait(false);
            index++;
        }
        if (!string.IsNullOrEmpty(plan.Text))
        {
            await Send("content_block_start", new JsonObject { ["index"] = index, ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" } }).ConfigureAwait(false);
            var chunks = StreamChunks.Split(plan.Text);
            s.ArmDrop(chunks.Count);
            for (var ci = 0; ci < chunks.Count; ci++)
            {
                await Send("content_block_delta", new JsonObject { ["index"] = index, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = chunks[ci] } }).ConfigureAwait(false);
                if (!await s.TickAsync().ConfigureAwait(false)) return false;
                if (plan.StreamErrorType is { } et && ci == chunks.Count / 2)
                {
                    await Send("error", new JsonObject { ["error"] = new JsonObject { ["type"] = et, ["message"] = "Overloaded" } }).ConfigureAwait(false);
                    return true;
                }
            }
            await Send("content_block_stop", new JsonObject { ["index"] = index }).ConfigureAwait(false);
            index++;
        }
        foreach (var call in plan.Calls)
        {
            call.Id = NewId("toolu_");
            await Send("content_block_start", new JsonObject
            {
                ["index"] = index,
                ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = call.Id, ["name"] = call.Name, ["input"] = new JsonObject() },
            }).ConfigureAwait(false);
            foreach (var c in ArgChunks(call.Args))
            {
                await Send("content_block_delta", new JsonObject { ["index"] = index, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = c } }).ConfigureAwait(false);
                if (!await s.TickAsync().ConfigureAwait(false)) return false;
            }
            await Send("content_block_stop", new JsonObject { ["index"] = index }).ConfigureAwait(false);
            index++;
        }
        var stop = plan.Calls.Count > 0 ? "tool_use" : plan.Stop == "length" ? "max_tokens" : "end_turn";
        await Send("message_delta", new JsonObject
        {
            ["delta"] = new JsonObject { ["stop_reason"] = stop, ["stop_sequence"] = null },
            ["usage"] = new JsonObject { ["output_tokens"] = usage.Output },
        }).ConfigureAwait(false);
        await Send("message_stop", new JsonObject()).ConfigureAwait(false);
        return true;
    }
}
