using System.Text.Json;
using NetPI.Abstractions;

namespace NetPI.Storage.Sqlite;

/// <summary>
/// astra-2 §11.2 (package F): the <see cref="ICloudExecutionGate"/>
/// implementation. Resolves a model's cloud team policy from configuration
/// (allowances, denomination, default output bound, allowlist) and draws the
/// atomic reservation from the shared <see cref="ICloudBudgetStore"/>.
///
/// Config surface (the storage plugin's <c>cloudBudgets</c> section in
/// <c>~/.netpi/config.json</c>):
/// <code>
/// "cloudBudgets": {
///   "defaultOutputTokens": 8192,
///   "teams": [
///     {
///       "teamId": "team-a",
///       "currency": "USD",
///       "unit": "currency",            // or "tokens" (token-based policy path)
///       "limit": 100,
///       "pricePer1kTokens": 0.001,      // optional; absent => token-based
///       "models": ["m-cloud-1", ...]    // optional allowlist (empty = all direct-cloud)
///     }
///   ]
/// }
/// </code>
///
/// Default posture: cloud spend is ALLOWED whenever a team's policy covers the
/// model (the explicit-selection authorization of §11.1 — choosing a cloud
/// model in a session is the user's spend authorization). The negative
/// default is the ABSENCE of a covering team policy: a direct-cloud model no
/// team claims has NO budget, so <see cref="SqliteCloudBudgetStore.ReserveAsync"/>
/// rejects it with an actionable reason and the provider never sends the
/// paid request (16: "Cloud disabled or team disallows cloud -> No paid
/// provider call"). Agents never reach this config: it is user-owned, and
/// agents cannot enable deployments or raise limits (§11.2).
/// </summary>
public sealed class ConfigCloudExecutionGate : ICloudExecutionGate
{
    private sealed record TeamPolicy(string TeamId, string Currency, CloudBudgetUnit Unit,
        double? PricePer1kTokens, IReadOnlyList<string> Models);

    private readonly ICloudBudgetStore _store;
    private readonly IReadOnlyList<TeamPolicy> _teams;
    private readonly int _defaultOutputTokens;
    private readonly IPluginLogger _log;

    /// <summary>
    /// Residency check (astra-2 §11): ONLY direct-cloud models are cloud spend
    /// — pooled models are paid through their lane (admission already gates
    /// them), legacy direct models have no cloud policy. The lanes plugin
    /// registers the policy source under service "deployments"; the factory
    /// resolves it lazily (the lanes plugin may load after storage).
    /// </summary>
    private readonly Func<IDeploymentPolicySource?>? _policyFactory;

    private readonly Dictionary<string, TeamPolicy> _models = new(StringComparer.Ordinal);

    public ConfigCloudExecutionGate(ICloudBudgetStore store, JsonElement config, IPluginLogger log,
        Func<IDeploymentPolicySource?>? policySourceFactory = null, string section = "cloudBudgets")
    {
        _store = store;
        _log = log;
        _policyFactory = policySourceFactory;
        var teams = new List<TeamPolicy>();
        var limits = new Dictionary<string, double>(StringComparer.Ordinal);
        _defaultOutputTokens = 8192;

        // The policy lives under <section> in the plugin's config section
        // (the storage plugin's own section is config.json's netpi.storage.sqlite).
        JsonElement root = config.ValueKind == JsonValueKind.Object
            && config.TryGetProperty(section, out var sub) && sub.ValueKind == JsonValueKind.Object
                ? sub : config; // tolerate a section-less config (policyless)

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("defaultOutputTokens", out var dot) && dot.ValueKind == JsonValueKind.Number
                && dot.TryGetInt32(out var d) && d > 0)
                _defaultOutputTokens = d;

            if (root.TryGetProperty("teams", out var teamsEl) && teamsEl.ValueKind == JsonValueKind.Array)
                foreach (var t in teamsEl.EnumerateArray())
                {
                    if (t.ValueKind != JsonValueKind.Object
                        || !t.TryGetProperty("teamId", out var tid) || tid.ValueKind != JsonValueKind.String
                        || !t.TryGetProperty("limit", out var lim) || lim.ValueKind != JsonValueKind.Number)
                        continue; // malformed team entry: skip, never guess
                    limits[tid.GetString()!] = lim.GetDouble();
                    var unit = t.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String
                        && string.Equals(u.GetString(), "tokens", StringComparison.OrdinalIgnoreCase)
                        ? CloudBudgetUnit.Tokens : CloudBudgetUnit.Currency;
                    double? price = null;
                    if (t.TryGetProperty("pricePer1kTokens", out var p) && p.ValueKind == JsonValueKind.Number)
                        price = p.GetDouble();
                    var models = new List<string>();
                    if (t.TryGetProperty("models", out var m) && m.ValueKind == JsonValueKind.Array)
                        foreach (var mm in m.EnumerateArray())
                            if (mm.ValueKind == JsonValueKind.String) models.Add(mm.GetString()!);
                    teams.Add(new TeamPolicy(
                        tid.GetString()!,
                        t.TryGetProperty("currency", out var c) && c.ValueKind == JsonValueKind.String
                            ? c.GetString()! : "",
                        unit, price, models));
                }
        }

        _teams = teams;
        foreach (var team in teams)
            foreach (var model in team.Models)
                _models[model] = team; // last writer wins if two teams list one model (misconfig)

        // Upsert every team's allowance (limit comes from the config too).
        // A team that never got a policy (no row) is rejected by the store —
        // the fail-closed default.
        foreach (var team in teams)
            if (limits.TryGetValue(team.TeamId, out var lim))
                _ = _store.UpsertAllowanceAsync(new CloudBudgetAllowance(
                    team.TeamId, team.Currency, lim, team.Unit));
    }

    private static IDeploymentPolicySource? EffectivePolicy(Func<IDeploymentPolicySource?>? factory)
        => factory?.Invoke();

    public async ValueTask<CloudReservationResult> AuthorizeAsync(
        string? modelId, string? deploymentId, int? maxOutputTokens, string? runId,
        CancellationToken cancellationToken = default)
    {
        // 1) Residency: only DIRECT-CLOUD models are cloud spend. Pooled models
        //    are governed by lane admission; legacy direct models have no cloud
        //    policy. No policy known for the model -> not direct cloud (fail
        //    closed: the runner already rejected disabled direct-cloud
        //    deployments before inference, so a missing policy here is
        //    "legacy direct", not an error).
        // Fresh resolution per call: the policy source is owned by the lanes
        // plugin and can be reloaded (new generation/instance) — caching it
        // here would pin a dead generation.
        var policy = EffectivePolicy(_policyFactory);
        if (policy is null)
        {
            // Legacy mode (lanes disabled or not loaded): the "deployments"
            // service is not registered, so NO model is a known DirectCloud
            // deployment. That is exactly the step-(1) posture above — "no
            // policy known -> legacy direct -> not a paid request" — so the
            // request proceeds WITHOUT cloud accounting (the provider treats
            // None as "no reservation, execute, no conclude"). Denying here
            // instead would block every model on a lanes-disabled host,
            // regressing pre-astra-2 behavior this gate must not introduce.
            return CloudReservationResult.None;
        }
        var dep = policy.PolicyFor(modelId ?? "");
        if (dep is null || dep.Mode != DeploymentExecutionMode.DirectCloud)
            return CloudReservationResult.None; // not a paid cloud request

        // 2) Team policy: which allowance pays for this model.
        var team = !string.IsNullOrEmpty(modelId) && _models.TryGetValue(modelId, out var t) ? t
            : !string.IsNullOrEmpty(deploymentId) && _models.TryGetValue(deploymentId, out var d) ? d
            : null;
        if (team is null)
            return CloudReservationResult.Denied(
                $"Direct-cloud model '{modelId ?? deploymentId ?? "?"}' has no cloud budget policy — no team allowance covers it, so the request is not paid and is not executed. Configure the team in the storage plugin's cloudBudgets config.", 0);

        // 3) The calculable reservation ceiling (11.2): bound the output, price
        //    it in the team's denomination. Token-based policy path: the
        //    allowance itself is denominated in tokens (rate 1).
        var tokens = Math.Max(1, maxOutputTokens ?? _defaultOutputTokens);
        var rate = team.PricePer1kTokens is { } price ? price / 1000.0
            : team.Unit == CloudBudgetUnit.Tokens ? 1.0
            : double.NaN; // currency team with no configured price -> reject
        if (double.IsNaN(rate))
            return CloudReservationResult.Denied(
                $"Team '{team.TeamId}' is currency-denominated but has no pricePer1kTokens for model '{modelId}' — configure the price (or a token-based team) before the paid request can be reserved.", 0);
        var estimate = tokens * rate;

        var result = await _store.ReserveAsync(team.TeamId, estimate, rate, runId, cancellationToken);
        if (result.Admitted)
            _log.Information($"cloud budget: reserved {estimate:0.###} for run '{runId ?? "?"}' (team {team.TeamId}, {result.Remaining:0.###} remaining)");
        return result;
    }

    public async ValueTask ConcludeAsync(string reservationId, bool succeeded, double? actual,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(reservationId)) return;
        if (!succeeded)
        {
            // The paid request produced no reconcilable content: refund the
            // drawn estimate untouched.
            await _store.ReleaseAsync(reservationId, cancellationToken);
            return;
        }

        // Convert reported tokens into the team's denomination using the
        // DURATION-persisted rate (a host reload cannot lose it).
        var info = await _store.ReserveInfoAsync(reservationId, cancellationToken);
        var denominated = info is not null && actual is { } a ? info.ToDenominated(a) : 0;
        await _store.SettleAsync(reservationId, denominated, cancellationToken);
    }
}
