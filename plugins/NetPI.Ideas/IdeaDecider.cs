using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Ideas;

/// <summary>
/// The one place an ideas check asks its pick-one question. The save check, the commit sweep and the recall each had
/// their own copy of the same block — resolve the model, take a slot on it, call <c>decide.decision</c>, read the
/// probabilities back — so a change to how a decision is asked (the admission, the timeout, what counts as an
/// answer) had to be made three times, and nothing kept the three alike.
/// <para>
/// What comes back is the distribution over the labels that were offered, or why there is none: a <b>skip</b> is a
/// decision the configuration will make again on every retry (no model, or a paid one the user did not allow), a
/// <b>drop</b> is the queue and the work may run later, and a <b>failure</b> is a timeout or an error. The three
/// callers read those differently — the commit sweep leaves its cursor alone on a drop and moves it on a skip, the
/// recall says "no slot", the save check attaches nothing — so the kinds are told apart here and each caller decides
/// what one means.
/// </para>
/// </summary>
internal sealed class IdeaDecider(IPluginContext ctx)
{
    private readonly IdeaAdmission _admission = new(ctx);

    /// <summary>Why a decision did not answer, in the ways a caller has to tell apart.</summary>
    internal enum AnswerKind
    {
        /// <summary>It answered: the probabilities over the labels it was offered.</summary>
        Answered,
        /// <summary>Nothing was asked: no model, or a paid one the user did not allow. The same will happen next time.</summary>
        Skipped,
        /// <summary>The queue was full, so nothing was asked. The work may run later.</summary>
        Dropped,
        /// <summary>It was asked and did not answer: a timeout, an error, or an answer we cannot read.</summary>
        Failed,
    }

    /// <summary>One decision: the probabilities over the labels it was offered, or why there are none.</summary>
    internal sealed record Answer(AnswerKind Kind, IReadOnlyDictionary<string, double>? Probs, string? Reason)
    {
        public static Answer Of(IReadOnlyDictionary<string, double> probs) => new(AnswerKind.Answered, probs, null);
        public static Answer Skip(string reason) => new(AnswerKind.Skipped, null, reason);
        public static Answer Drop(string reason) => new(AnswerKind.Dropped, null, reason);
        public static Answer Fail(string reason) => new(AnswerKind.Failed, null, reason);
        /// <summary>A decision this configuration would make again on every retry, so a caller may settle for it.</summary>
        public bool WillRepeat => Kind == AnswerKind.Skipped;
    }

    /// <summary>
    /// Ask one pick-one question and read the answer. Every label in <paramref name="labels"/> must come back with a
    /// probability: a label the decision did not weigh is not a label it rejected. Labels the answer brings that were
    /// never offered are ignored (<see cref="IdeaMatch.Pick"/> reads only the ones we asked about).
    /// </summary>
    public async Task<Answer> AskAsync(JsonObject system, string question, JsonArray labels, string purpose,
        string? sessionId, string? projectId, TimeSpan? wait, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ctx.Stopping, ct);
        deadline.CancelAfter(timeout);
        try
        {
            var name = ctx.Settings.GetOr("ideas.model", IdeaRecall.DefaultModel) is { Length: > 0 } m ? m.Trim() : IdeaRecall.DefaultModel;
            var model = await ctx.Models.FindAsync(name, ct).ConfigureAwait(false);
            // The decision runs on the same backend as the chats, so it takes a slot like everything else.
            var admission = await _admission.EnterAsync(model, purpose, sessionId, projectId, ct, wait, decision: true).ConfigureAwait(false);
            if (!admission.Admitted)
                return admission.Retryable
                    ? Answer.Drop(admission.Reason ?? "the model was full")
                    : Answer.Skip(admission.Reason ?? "the model is not available");
            using var slot = admission.Lease!;
            var raw = await DecisionCapabilities.InvokeAsync(ctx.Services, ctx.Rpc, "decide.decision", new JsonObject
            {
                ["model"] = name,
                ["messages"] = new JsonArray(system),
                ["branches"] = new JsonArray(new JsonObject { ["id"] = "pick", ["content"] = question, ["labels"] = labels }),
            }, deadline.Token, admission.Slot, model?.Ref, IdeaAdmission.Priority, lane: true).ConfigureAwait(false);
            var answer = raw as JsonObject ?? JsonSerializer.SerializeToNode(raw) as JsonObject;
            if (answer?["branches"] is not JsonArray { Count: > 0 } branches || branches[0]?["probabilities"] is not JsonObject probs)
                return Answer.Fail("the decision did not come back as a branch of probabilities");
            var outp = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (label, node) in probs)
                if (node is JsonValue v && v.TryGetValue<double>(out var d)) outp[label] = d;
            foreach (var label in labels.OfType<JsonValue>())
                if (outp.ContainsKey(IdeaOps.Str(label) ?? "") is false)
                    return Answer.Fail($"the decision did not weigh \"{IdeaOps.Str(label)}\"");
            return Answer.Of(outp);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !ctx.Stopping.IsCancellationRequested)
        {
            ctx.Logger.LogWarning("Ideas: {Purpose} got no answer within {Seconds:0} s.", purpose, timeout.TotalSeconds);
            return Answer.Fail($"no answer within {timeout.TotalSeconds:0} s");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.Logger.LogWarning("Ideas: {Purpose} failed: {Message}", purpose, ex.Message);
            return Answer.Fail(ex.Message);
        }
    }
}
