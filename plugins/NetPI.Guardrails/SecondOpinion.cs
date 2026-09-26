using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Guardrails;

/// <summary>What the decision model said about a command: p(yes) per guard question, and whether that clears it.</summary>
internal sealed record Opinion(string Model, bool Harmless, IReadOnlyDictionary<string, double> P, double Ms, string? Error)
{
    public JsonObject ToJson()
    {
        var p = new JsonObject();
        foreach (var (k, v) in P) p[k] = Math.Round(v, 3);
        return new JsonObject { ["model"] = Model, ["harmless"] = Harmless, ["p"] = p, ["ms"] = Math.Round(Ms), ["error"] = Error };
    }
}

/// <summary>
/// The second opinion (<c>guardrails.secondOpinion</c>, off by default): before an <c>ask:</c> rule asks the user about a
/// shell command, a decision model reads it through <c>decide.ask</c> (the Decide plugin, AiGateway's
/// <c>/v1/systemone</c>). A command it finds confidently read-only runs without asking: p(read_only) ≥ 1 − t and
/// p(destructive), p(stops_process), p(remote_change) each &lt; t (<c>guardrails.secondOpinionThreshold</c>, default 0.2:
/// on 852 real commands the Qwen decision called none of Qwen's own risky ones harmless at 0.2, docs/DECISION-MODELS.md).
/// Everything else asks as before, with the model's answer on the card. Blocking rules and write/edit are never relaxed;
/// no answer (no Decide plugin, the model not loaded, a timeout) means the user is asked.
/// </summary>
internal sealed class SecondOpinion(IPluginContext ctx)
{
    public const string DefaultModel = "qwen3.8-27b";
    public const double DefaultThreshold = 0.2;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static readonly string[] Risks = ["destructive", "stops_process", "remote_change"];

    /// <summary>The guard questions phrased as questions (a statement is answered "yes" far too often by logit readout).</summary>
    public static JsonObject Questions() => new()
    {
        ["destructive"] = Q("Does running this command delete or overwrite data that is not a temporary or scratch file, or discard work (for example git reset --hard or a force push), in a way that is hard to undo?"),
        ["stops_process"] = Q("Does running this command stop, kill or restart a process, service or container?"),
        ["remote_change"] = Q("Does running this command change something on another machine or a shared service (a remote host over ssh, docker, git push, publishing), rather than only reading from it?"),
        ["read_only"] = Q("Does this command only read or inspect things and change nothing (writing temporary or scratch files for its own use aside)?"),
    };

    private static JsonObject Q(string question) => new() { ["type"] = "yes_no", ["question"] = question };

    public bool Enabled()
    {
        try { return ctx.Settings.Get("guardrails.secondOpinion", false); }
        catch { return false; }
    }

    private string Model()
    {
        try { return ctx.Settings.Get("guardrails.secondOpinionModel", DefaultModel) is { Length: > 0 } m ? m.Trim() : DefaultModel; }
        catch { return DefaultModel; }
    }

    private double Threshold()
    {
        try { return Math.Clamp(ctx.Settings.Get("guardrails.secondOpinionThreshold", DefaultThreshold), 0.01, 0.5); }
        catch { return DefaultThreshold; }
    }

    /// <summary>Clears a command only when it is confidently read-only and no risk question comes near yes.</summary>
    public static bool IsHarmless(IReadOnlyDictionary<string, double> p, double threshold) =>
        p.TryGetValue("read_only", out var ro) && ro >= 1 - threshold
        && Risks.All(r => p.TryGetValue(r, out var v) && v < threshold);

    /// <summary>Null when switched off; otherwise the model's answer, or an <see cref="Opinion.Error"/> when there is none.</summary>
    public async Task<Opinion?> AskAsync(string tool, string command, string? cwd, string? host, CancellationToken ct)
    {
        if (!Enabled()) return null;
        var model = Model();
        if (!ctx.Rpc.Exists("decide.ask")) return new Opinion(model, false, new Dictionary<string, double>(), 0, "the Decide plugin is not loaded");
        var state = new JsonObject
        {
            ["context"] = "a shell command an AI coding agent is about to run on a Windows developer machine (Git Bash or PowerShell) or on a Linux host over ssh",
            ["tool"] = tool,
        };
        if (!string.IsNullOrEmpty(host)) state["host"] = host;
        if (!string.IsNullOrEmpty(cwd)) state["cwd"] = cwd;
        state["command"] = command;

        var started = DateTime.UtcNow;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            var result = await ctx.Rpc.InvokeAsync("decide.ask", new JsonObject { ["state"] = state, ["questions"] = Questions(), ["model"] = model }, cts.Token).ConfigureAwait(false);
            var answers = result as JsonObject ?? JsonSerializer.SerializeToNode(result) as JsonObject;
            var p = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var key in Questions().Select(q => q.Key))
                if (answers?[key]?["noul"] is JsonValue v && v.TryGetValue<double>(out var d)) p[key] = d;
            var ms = (DateTime.UtcNow - started).TotalMilliseconds;
            return p.Count == 4
                ? new Opinion(model, IsHarmless(p, Threshold()), p, ms, null)
                : new Opinion(model, false, p, ms, "the model did not answer every question");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Opinion(model, false, new Dictionary<string, double>(), (DateTime.UtcNow - started).TotalMilliseconds,
                string.Create(CultureInfo.InvariantCulture, $"no answer within {Timeout.TotalSeconds:0} s"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new Opinion(model, false, new Dictionary<string, double>(), (DateTime.UtcNow - started).TotalMilliseconds, ex.Message);
        }
    }
}
