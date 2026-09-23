using System.Text.Json;

namespace NetPI.Providers.AiProxy;

internal interface IOpenAiStreamParser
{
    /// <summary>True once a terminal event was seen (the rest of the stream can be ignored).</summary>
    bool Finished { get; }
    void Handle(SseEvent sse);
    /// <summary>Servers that ignore <c>stream:true</c> and answer with a plain JSON body.</summary>
    void HandleJsonBody(string json);
    /// <summary>End of input: flush buffers, throw a transient error if the stream was truncated.</summary>
    void Finish();
}

internal static class OpenAiCommon
{
    public const string ImageOmitted = "[image omitted: the selected model does not accept image input]";

    /// <summary>Request max tokens: request value, else catalog value, else configured default; never above the catalog limit.</summary>
    public static int ResolveMaxTokens(ModelRequest req, ModelOptions mo)
    {
        var catalog = req.Model.MaxOutputTokens is > 0 ? req.Model.MaxOutputTokens : null;
        var value = req.MaxOutputTokens is > 0 ? req.MaxOutputTokens.Value : catalog ?? mo.DefaultMaxOutputTokens;
        if (catalog is { } c && value > c) value = c;
        return value;
    }

    /// <summary>Responses usage: input_tokens includes cached tokens, output_tokens includes reasoning.</summary>
    public static Usage ResponsesUsage(JsonElement u)
    {
        var input = u.Long("input_tokens");
        var cached = u.Prop("input_tokens_details").Long("cached_tokens");
        return new Usage
        {
            InputTokens = Math.Max(0, input - cached),
            CacheReadTokens = cached,
            OutputTokens = u.Long("output_tokens"),
            ReasoningTokens = u.Prop("output_tokens_details").Long("reasoning_tokens"),
        };
    }

    /// <summary>Chat usage: prompt_tokens includes cached tokens (prompt_tokens_details.cached_tokens or DeepSeek's prompt_cache_hit_tokens).</summary>
    public static Usage ChatUsage(JsonElement u)
    {
        var prompt = u.Long("prompt_tokens");
        var cached = u.Prop("prompt_tokens_details").Long("cached_tokens");
        if (cached == 0) cached = u.Long("prompt_cache_hit_tokens");
        return new Usage
        {
            InputTokens = Math.Max(0, prompt - cached),
            CacheReadTokens = cached,
            OutputTokens = u.Long("completion_tokens"),
            ReasoningTokens = u.Prop("completion_tokens_details").Long("reasoning_tokens"),
        };
    }
}
