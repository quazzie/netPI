using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.MockLlm;

/// <summary>A tool call in the neutral conversation.</summary>
public sealed class NCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Args { get; set; } = "{}";
}

/// <summary>One message of the neutral conversation (user | assistant | tool).</summary>
public sealed class NMsg
{
    public string Role { get; set; } = "user";
    public string Text { get; set; } = "";
    public List<NCall> Calls { get; } = [];
    /// <summary>Tool messages: the call they answer.</summary>
    public string? CallId { get; set; }
    public bool IsError { get; set; }
    /// <summary>Assistant: reasoning/thinking was replayed.</summary>
    public bool Thinking { get; set; }
    public int Images { get; set; }

    public bool IsNotice => Role == "user" && Text.TrimStart().StartsWith("<system-notice", StringComparison.Ordinal);

    /// <summary>kind="…" of a system notice, else null.</summary>
    public string? NoticeKind
    {
        get
        {
            if (!IsNotice) return null;
            var t = Text.TrimStart();
            var i = t.IndexOf("kind=\"", StringComparison.Ordinal);
            if (i < 0 || i > 40) return "";
            var end = t.IndexOf('"', i + 6);
            return end < 0 ? "" : t[(i + 6)..end];
        }
    }
}

/// <summary>A model request converted from any of the three wire formats.</summary>
public sealed class NRequest
{
    public string Api { get; set; } = "";
    public string Model { get; set; } = "";
    public string System { get; set; } = "";
    public List<NMsg> Messages { get; } = [];
    public List<string> Tools { get; } = [];
    public int ToolSchemaChars { get; set; }
    public string? Effort { get; set; }
    public int? MaxTokens { get; set; }
    public bool ThinkingEnabled { get; set; }
    public int? ThinkingBudget { get; set; }
    public double? Temperature { get; set; }
    public bool Stream { get; set; } = true;
    public int CacheControlBlocks { get; set; }

    public bool IsSubagent => System.Contains("a subagent working for", StringComparison.Ordinal);
    public bool IsSummarizer => System.StartsWith("You are a context summarization assistant", StringComparison.Ordinal);

    /// <summary>Approximate prompt size (chars/4 of everything the model reads).</summary>
    public int EstimateTokens()
    {
        long chars = System.Length + ToolSchemaChars;
        foreach (var m in Messages)
        {
            chars += m.Text.Length + 8 + m.Images * 4000;
            foreach (var c in m.Calls) chars += c.Name.Length + c.Args.Length + 16;
        }
        return (int)(chars / 4);
    }

    /// <summary>Stable hash of the conversation (used to recognize a retried request).</summary>
    public string Fingerprint()
    {
        var sb = new StringBuilder(Model).Append('|').Append(System.Length).Append('|');
        foreach (var m in Messages)
        {
            sb.Append(m.Role).Append(':').Append(m.Text).Append('|');
            foreach (var c in m.Calls) sb.Append(c.Name).Append(c.Args).Append('|');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..24];
    }
}

/// <summary>A request the mock refuses (API-specific 4xx error).</summary>
public sealed class MockApiException(int status, string type, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Type { get; } = type;
}

/// <summary>Parsing and validation of the three request formats.</summary>
public static class RequestParser
{
    public static string Sig(string thinking) =>
        "mocksig_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(thinking)))[..20].ToLowerInvariant();

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? Int(JsonNode? n) => n is JsonValue v ? (v.TryGetValue<int>(out var i) ? i : v.TryGetValue<double>(out var d) ? (int)d : null) : null;

    private static string ContentText(JsonNode? content, ref int images)
    {
        switch (content)
        {
            case JsonValue v when v.TryGetValue<string>(out var s): return s;
            case JsonArray arr:
            {
                var sb = new StringBuilder();
                foreach (var p in arr)
                {
                    if (p is not JsonObject o) continue;
                    var type = Str(o["type"]);
                    if (type is "input_image" or "image" or "image_url") { images++; continue; }
                    var t = Str(o["text"]) ?? Str(o["refusal"]);
                    if (t is null) continue;
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(t);
                }
                return sb.ToString();
            }
            default: return "";
        }
    }

    // ------------------------------------------------------------------ Responses

    public static NRequest ParseResponses(JsonObject body)
    {
        var r = new NRequest { Api = "responses", Model = Str(body["model"]) ?? "" };
        r.System = Str(body["instructions"]) ?? "";
        r.Effort = Str(body["reasoning"]?["effort"]);
        r.MaxTokens = Int(body["max_output_tokens"]);
        r.Stream = body["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var st) && st;
        if (body["temperature"] is JsonValue tv && tv.TryGetValue<double>(out var temp)) r.Temperature = temp;
        if (body["tools"] is JsonArray tools)
            foreach (var t in tools)
            {
                if (Str(t?["name"]) is { } n) r.Tools.Add(n);
                r.ToolSchemaChars += t?.ToJsonString().Length ?? 0;
            }

        switch (body["input"])
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                r.Messages.Add(new NMsg { Role = "user", Text = s });
                break;
            case JsonArray items:
                foreach (var node in items)
                {
                    if (node is not JsonObject item) continue;
                    var type = Str(item["type"]) ?? (item["role"] is not null ? "message" : "");
                    var last = r.Messages.Count > 0 ? r.Messages[^1] : null;
                    switch (type)
                    {
                        case "message":
                        {
                            var role = Str(item["role"]) ?? "user";
                            var images = 0;
                            var text = ContentText(item["content"], ref images);
                            if (role == "assistant")
                            {
                                if (last is { Role: "assistant", Calls.Count: 0, Text.Length: 0 }) last.Text = text;
                                else r.Messages.Add(new NMsg { Role = "assistant", Text = text });
                            }
                            else if (role is "system" or "developer") r.System += "\n" + text;
                            else r.Messages.Add(new NMsg { Role = "user", Text = text, Images = images });
                            break;
                        }
                        case "reasoning":
                            if (last is { Role: "assistant", Calls.Count: 0, Text.Length: 0 }) last.Thinking = true;
                            else r.Messages.Add(new NMsg { Role = "assistant", Thinking = true });
                            break;
                        case "function_call":
                        {
                            var call = new NCall { Id = Str(item["call_id"]) ?? "", Name = Str(item["name"]) ?? "", Args = Str(item["arguments"]) ?? "{}" };
                            if (last is { Role: "assistant" }) last.Calls.Add(call);
                            else
                            {
                                var m = new NMsg { Role = "assistant" };
                                m.Calls.Add(call);
                                r.Messages.Add(m);
                            }
                            break;
                        }
                        case "function_call_output":
                        {
                            var images = 0;
                            r.Messages.Add(new NMsg { Role = "tool", CallId = Str(item["call_id"]), Text = ContentText(item["output"], ref images) });
                            break;
                        }
                        default:
                            throw new MockApiException(400, "invalid_request_error", $"Invalid input item type '{type}'.");
                    }
                }
                break;
            default:
                throw new MockApiException(400, "invalid_request_error", "Missing required parameter: 'input'.");
        }

        // OpenAI rules: every output answers an earlier call, every call has an output.
        var calls = new HashSet<string>(StringComparer.Ordinal);
        var answered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in r.Messages)
        {
            foreach (var c in m.Calls)
            {
                if (string.IsNullOrEmpty(c.Id)) throw new MockApiException(400, "invalid_request_error", "Missing required parameter: 'input[].call_id'.");
                calls.Add(c.Id);
            }
            if (m.Role == "tool")
            {
                if (m.CallId is null || !calls.Contains(m.CallId))
                    throw new MockApiException(400, "invalid_request_error", $"No tool call found for function call output with call_id {m.CallId}.");
                answered.Add(m.CallId);
            }
        }
        foreach (var c in calls)
            if (!answered.Contains(c)) throw new MockApiException(400, "invalid_request_error", $"No tool output found for function call {c}.");
        return r;
    }

    // ------------------------------------------------------------------ Chat Completions

    public static NRequest ParseChat(JsonObject body)
    {
        var r = new NRequest { Api = "chat", Model = Str(body["model"]) ?? "" };
        r.Effort = Str(body["reasoning_effort"]);
        r.MaxTokens = Int(body["max_tokens"]) ?? Int(body["max_completion_tokens"]);
        r.Stream = body["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var st) && st;
        if (body["temperature"] is JsonValue tv && tv.TryGetValue<double>(out var temp)) r.Temperature = temp;
        if (body["tools"] is JsonArray tools)
            foreach (var t in tools)
            {
                if (Str(t?["function"]?["name"]) is { } n) r.Tools.Add(n);
                r.ToolSchemaChars += t?.ToJsonString().Length ?? 0;
            }
        if (body["messages"] is not JsonArray msgs) throw new MockApiException(400, "invalid_request_error", "Missing required parameter: 'messages'.");

        var pending = new List<string>();
        foreach (var node in msgs)
        {
            if (node is not JsonObject m) continue;
            var role = Str(m["role"]) ?? "";
            var images = 0;
            var text = ContentText(m["content"], ref images);
            if (role != "tool" && pending.Count > 0)
                throw new MockApiException(400, "invalid_request_error",
                    "An assistant message with 'tool_calls' must be followed by tool messages responding to each 'tool_call_id'. " +
                    "The following tool_call_ids did not have response messages: " + string.Join(", ", pending));
            switch (role)
            {
                case "system" or "developer":
                    r.System += (r.System.Length > 0 ? "\n" : "") + text;
                    break;
                case "user":
                    r.Messages.Add(new NMsg { Role = "user", Text = text, Images = images });
                    break;
                case "assistant":
                {
                    var a = new NMsg { Role = "assistant", Text = text, Thinking = !string.IsNullOrEmpty(Str(m["reasoning_content"])) };
                    if (m["tool_calls"] is JsonArray tcs)
                        foreach (var tc in tcs)
                        {
                            var call = new NCall { Id = Str(tc?["id"]) ?? "", Name = Str(tc?["function"]?["name"]) ?? "", Args = Str(tc?["function"]?["arguments"]) ?? "{}" };
                            a.Calls.Add(call);
                            pending.Add(call.Id);
                        }
                    r.Messages.Add(a);
                    break;
                }
                case "tool":
                {
                    var id = Str(m["tool_call_id"]);
                    if (id is null || !pending.Remove(id))
                        throw new MockApiException(400, "invalid_request_error",
                            "Invalid parameter: messages with role 'tool' must be a response to a preceeding message with 'tool_calls'.");
                    r.Messages.Add(new NMsg { Role = "tool", CallId = id, Text = text });
                    break;
                }
                default:
                    throw new MockApiException(400, "invalid_request_error", $"Invalid role '{role}'.");
            }
        }
        if (pending.Count > 0)
            throw new MockApiException(400, "invalid_request_error", "tool_call_ids without response messages: " + string.Join(", ", pending));
        return r;
    }

    // ------------------------------------------------------------------ Anthropic Messages

    public static NRequest ParseAnthropic(JsonObject body)
    {
        var r = new NRequest { Api = "anthropic", Model = Str(body["model"]) ?? "" };
        r.MaxTokens = Int(body["max_tokens"]) ?? throw new MockApiException(400, "invalid_request_error", "max_tokens: Field required");
        r.Stream = body["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var st) && st;
        if (body["temperature"] is JsonValue tv && tv.TryGetValue<double>(out var temp)) r.Temperature = temp;
        var cache = 0;

        switch (body["system"])
        {
            case JsonValue v when v.TryGetValue<string>(out var s): r.System = s; break;
            case JsonArray arr:
                foreach (var b in arr)
                {
                    r.System += Str(b?["text"]) ?? "";
                    if (b?["cache_control"] is not null) cache++;
                }
                break;
        }
        if (body["thinking"] is JsonObject th)
        {
            var type = Str(th["type"]);
            if (type == "enabled")
            {
                r.ThinkingEnabled = true;
                r.ThinkingBudget = Int(th["budget_tokens"]);
                if (r.ThinkingBudget is null or < 1024)
                    throw new MockApiException(400, "invalid_request_error", "thinking.enabled.budget_tokens: Input should be greater than or equal to 1024");
                if (r.MaxTokens <= r.ThinkingBudget)
                    throw new MockApiException(400, "invalid_request_error", "`max_tokens` must be greater than `thinking.budget_tokens`.");
            }
            else if (type == "adaptive") r.ThinkingEnabled = true;
            if (r.ThinkingEnabled && r.Temperature is { } t && t != 1)
                throw new MockApiException(400, "invalid_request_error", "`temperature` may only be set to 1 when thinking is enabled.");
            r.Effort = Str(body["output_config"]?["effort"]) ?? (r.ThinkingBudget is { } b ? $"budget:{b}" : type);
        }
        if (body["tools"] is JsonArray tools)
            foreach (var t in tools)
            {
                if (Str(t?["name"]) is { } n) r.Tools.Add(n);
                if (t?["cache_control"] is not null) cache++;
                r.ToolSchemaChars += t?.ToJsonString().Length ?? 0;
            }

        if (body["messages"] is not JsonArray msgs || msgs.Count == 0)
            throw new MockApiException(400, "invalid_request_error", "messages: at least one message is required");

        string? prevRole = null;
        List<string> prevToolUses = [];
        var index = 0;
        foreach (var node in msgs)
        {
            if (node is not JsonObject m) continue;
            var role = Str(m["role"]) ?? "";
            if (index == 0 && role != "user") throw new MockApiException(400, "invalid_request_error", "messages: first message must use the \"user\" role");
            if (role == prevRole) throw new MockApiException(400, "invalid_request_error", "messages: roles must alternate between \"user\" and \"assistant\", but found multiple \"" + role + "\" roles in a row");
            var blocks = m["content"] switch
            {
                JsonArray a => a.OfType<JsonObject>().ToList(),
                JsonValue v when v.TryGetValue<string>(out var s) => [new JsonObject { ["type"] = "text", ["text"] = s }],
                _ => throw new MockApiException(400, "invalid_request_error", $"messages.{index}.content: Field required"),
            };
            if (blocks.Count == 0) throw new MockApiException(400, "invalid_request_error", $"messages.{index}: all messages must have non-empty content");
            cache += blocks.Count(b => b["cache_control"] is not null);

            if (role == "user")
            {
                var results = new HashSet<string>(StringComparer.Ordinal);
                var user = new NMsg { Role = "user" };
                var sawText = false;
                foreach (var b in blocks)
                {
                    switch (Str(b["type"]))
                    {
                        case "tool_result":
                        {
                            if (sawText) throw new MockApiException(400, "invalid_request_error", $"messages.{index}: tool_result blocks must come before any other content");
                            var id = Str(b["tool_use_id"]) ?? "";
                            if (!prevToolUses.Contains(id))
                                throw new MockApiException(400, "invalid_request_error", $"messages.{index}.content: unexpected `tool_use_id` found in `tool_result` blocks: {id}. Each `tool_result` block must have a corresponding `tool_use` block in the previous message.");
                            results.Add(id);
                            var images = 0;
                            r.Messages.Add(new NMsg
                            {
                                Role = "tool", CallId = id, Text = ContentText(b["content"], ref images),
                                IsError = b["is_error"] is JsonValue ev && ev.TryGetValue<bool>(out var e) && e,
                            });
                            break;
                        }
                        case "text":
                            sawText = true;
                            user.Text += (user.Text.Length > 0 ? "\n" : "") + (Str(b["text"]) ?? "");
                            break;
                        case "image":
                            sawText = true;
                            user.Images++;
                            break;
                        default:
                            throw new MockApiException(400, "invalid_request_error", $"messages.{index}.content: unsupported block type '{Str(b["type"])}' in a user message");
                    }
                }
                var missing = prevToolUses.Where(id => !results.Contains(id)).ToList();
                if (missing.Count > 0)
                    throw new MockApiException(400, "invalid_request_error", $"messages.{index}: `tool_use` ids were found without `tool_result` blocks immediately after: {string.Join(", ", missing)}. Each `tool_use` block must have a corresponding `tool_result` block in the next message.");
                if (sawText) r.Messages.Add(user);
                prevToolUses = [];
            }
            else if (role == "assistant")
            {
                var a = new NMsg { Role = "assistant" };
                prevToolUses = [];
                for (var bi = 0; bi < blocks.Count; bi++)
                {
                    var b = blocks[bi];
                    switch (Str(b["type"]))
                    {
                        case "thinking":
                        {
                            var text = Str(b["thinking"]) ?? "";
                            var sig = Str(b["signature"]);
                            if (sig != Sig(text))
                                throw new MockApiException(400, "invalid_request_error", $"messages.{index}.content.{bi}: Invalid `signature` in `thinking` block");
                            a.Thinking = true;
                            break;
                        }
                        case "redacted_thinking":
                            a.Thinking = true;
                            break;
                        case "text":
                            a.Text += (a.Text.Length > 0 ? "\n" : "") + (Str(b["text"]) ?? "");
                            break;
                        case "tool_use":
                        {
                            var id = Str(b["id"]) ?? "";
                            if (id.Length == 0 || id.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-')))
                                throw new MockApiException(400, "invalid_request_error", $"messages.{index}.content.{bi}.tool_use.id: String should match pattern '^[a-zA-Z0-9_-]+$'");
                            a.Calls.Add(new NCall { Id = id, Name = Str(b["name"]) ?? "", Args = b["input"]?.ToJsonString() ?? "{}" });
                            prevToolUses.Add(id);
                            break;
                        }
                        default:
                            throw new MockApiException(400, "invalid_request_error", $"messages.{index}.content.{bi}: unsupported block type '{Str(b["type"])}' in an assistant message");
                    }
                }
                r.Messages.Add(a);
            }
            else throw new MockApiException(400, "invalid_request_error", $"messages.{index}.role: Input should be 'user' or 'assistant'");
            prevRole = role;
            index++;
        }
        if (prevToolUses.Count > 0)
            throw new MockApiException(400, "invalid_request_error", "The final assistant message has tool_use blocks without tool_result blocks.");
        if (cache > 4) throw new MockApiException(400, "invalid_request_error", $"A maximum of 4 blocks with cache_control may be provided. Found {cache}.");
        r.CacheControlBlocks = cache;

        // With thinking on, a final assistant turn that is continued with tool results must start with a thinking block.
        if (r.ThinkingEnabled && msgs.Count >= 2 && msgs[^1] is JsonObject lastUser && msgs[^2] is JsonObject lastAsst
            && Str(lastAsst["role"]) == "assistant" && lastUser["content"] is JsonArray lc && lc.OfType<JsonObject>().Any(b => Str(b["type"]) == "tool_result")
            && lastAsst["content"] is JsonArray ac && ac.Count > 0 && Str(ac[0]?["type"]) is not ("thinking" or "redacted_thinking"))
        {
            throw new MockApiException(400, "invalid_request_error",
                $"messages.{msgs.Count - 2}.content.0.type: Expected `thinking` or `redacted_thinking`, but found `{Str(ac[0]?["type"])}`. " +
                "When `thinking` is enabled, a final `assistant` message must start with a thinking block.");
        }
        return r;
    }
}
