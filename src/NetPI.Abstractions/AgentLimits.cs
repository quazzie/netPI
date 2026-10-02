namespace NetPI;

/// <summary>
/// The shared numbers of the agent stack, typed once here and in the settings schema, not at every read site:
/// a read site that typed its own default could drift from the schema (models.localSlots did: 1 with the Agents
/// plugin, 2 without it, so the local capacity of a model changed when the plugin reloaded) (idea-88ik5k).
/// </summary>
public static class ModelCapacity
{
    public const string LocalSetting = "models.localSlots";
    public const string CloudSetting = "models.cloudSlots";
    /// <summary>Concurrent runs of a local model without a valid catalog concurrency.</summary>
    public const int DefaultLocal = 1;
    /// <summary>Concurrent runs across one cloud provider's models.</summary>
    public const int DefaultCloud = 4;

    /// <summary>Concurrent runs of one local model: its catalog concurrency when valid, else <c>models.localSlots</c>.</summary>
    public static int Local(ISettings? settings, ModelInfo? model) =>
        Math.Max(1, model?.Concurrency is > 0 and var c ? c : Get(settings, LocalSetting, DefaultLocal));

    /// <summary>Concurrent runs across one cloud provider's models.</summary>
    public static int Cloud(ISettings? settings) => Math.Max(1, Get(settings, CloudSetting, DefaultCloud));

    private static int Get(ISettings? settings, string key, int fallback)
    {
        try { return settings?.Get(key, fallback) ?? fallback; } catch { return fallback; }
    }
}

/// <summary>
/// The output limit of a model call: the model's own maximum when it declares one, else
/// <c>agent.defaultMaxOutputTokens</c> (the run's request and the budget's reservation both take it from here).
/// </summary>
public static class OutputLimit
{
    public const string Setting = "agent.defaultMaxOutputTokens";
    public const int Default = 16_384;

    /// <summary>The limit for <paramref name="model"/>: its own maximum when valid, else the setting.</summary>
    public static int Model(ModelInfo? model, ISettings? settings)
    {
        var own = model?.MaxOutputTokens is > 0 and var m ? m : 0;
        return own > 0 ? own : Get(settings);
    }

    public static int Get(ISettings? settings)
    {
        try { return settings?.Get(Setting, Default) ?? Default; } catch { return Default; }
    }
}
