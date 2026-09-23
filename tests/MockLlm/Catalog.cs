using System.Text.Json.Nodes;

namespace NetPI.MockLlm;

/// <summary>One model of the mock catalog.</summary>
public sealed class MockModel
{
    public required string Id { get; init; }
    public string OwnedBy { get; init; } = "llamacpp";
    public int? ContextWindow { get; set; }
    public int? MaxOutputTokens { get; init; }
    public int? Concurrency { get; init; }
    public string[] Modalities { get; init; } = ["text"];
    public string[]? Efforts { get; init; }
    public string? DefaultEffort { get; init; }
    /// <summary>loaded | unloaded | stopped | offline</summary>
    public string Status { get; init; } = "loaded";

    public JsonObject ToAiProxyJson(long created) => new()
    {
        ["id"] = Id,
        ["object"] = "model",
        ["owned_by"] = OwnedBy,
        ["created"] = created,
        ["context_window"] = ContextWindow,
        ["max_output_tokens"] = MaxOutputTokens,
        ["concurrency"] = Concurrency,
        ["input_modalities"] = new JsonArray(Modalities.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray()),
        ["reasoning"] = Efforts is null
            ? null
            : new JsonObject
            {
                ["supported"] = true,
                ["efforts"] = new JsonArray(Efforts.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()),
                ["default"] = DefaultEffort,
            },
        ["meta"] = new JsonObject { ["n_ctx"] = ContextWindow },
        ["status"] = new JsonObject { ["value"] = Status },
    };
}

/// <summary>The models served by the mock: the AiProxy guide's catalog plus <c>tiny-ctx</c>, and a few Claude models.</summary>
public sealed class Catalog
{
    public Catalog(int tinyContext)
    {
        AiProxy =
        [
            new MockModel
            {
                Id = "gemma-4", ContextWindow = 65536, Concurrency = 1, Efforts = ["none", "max"], DefaultEffort = "max", Status = "unloaded",
            },
            new MockModel
            {
                Id = "ornith15-9b-mtp-128k", ContextWindow = 131072, Concurrency = 1, Modalities = ["text", "image"],
                Efforts = ["none", "max"], DefaultEffort = "max", Status = "unloaded",
            },
            new MockModel { Id = "qwen38-27b-iq3s", ContextWindow = 524288, Status = "stopped" },
            new MockModel
            {
                Id = "qwen3.8-27b", OwnedBy = "ninfer", ContextWindow = 262144, MaxOutputTokens = 16384, Concurrency = 2,
                Modalities = ["text", "image"], Efforts = ["none", "low", "medium", "xhigh"], DefaultEffort = "medium", Status = "loaded",
            },
            new MockModel { Id = "tiny-ctx", ContextWindow = tinyContext, MaxOutputTokens = 2048, Concurrency = 1, Status = "loaded" },
        ];
        Anthropic =
        [
            new MockModel { Id = "claude-sonnet-4-5", ContextWindow = 200_000, MaxOutputTokens = 64000 },
            new MockModel { Id = "claude-haiku-4-5", ContextWindow = 200_000, MaxOutputTokens = 64000 },
        ];
    }

    public List<MockModel> AiProxy { get; }
    public List<MockModel> Anthropic { get; }

    public MockModel? FindAiProxy(string id) => AiProxy.FirstOrDefault(m => m.Id == id);
    public MockModel? FindAnthropic(string id) => Anthropic.FirstOrDefault(m => m.Id == id || id.StartsWith(m.Id + "-", StringComparison.Ordinal));

    public JsonObject AiProxyList()
    {
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new JsonObject { ["object"] = "list", ["data"] = new JsonArray(AiProxy.Select(m => (JsonNode?)m.ToAiProxyJson(created)).ToArray()) };
    }

    public JsonObject AnthropicList()
    {
        var data = new JsonArray();
        foreach (var m in Anthropic)
            data.Add(new JsonObject
            {
                ["type"] = "model",
                ["id"] = m.Id,
                ["display_name"] = m.Id.Replace("claude-", "Claude ").Replace("-4-5", " 4.5").Replace("sonnet", "Sonnet").Replace("haiku", "Haiku"),
                ["created_at"] = "2025-10-01T00:00:00Z",
            });
        return new JsonObject
        {
            ["data"] = data, ["has_more"] = false,
            ["first_id"] = Anthropic[0].Id, ["last_id"] = Anthropic[^1].Id,
        };
    }
}
