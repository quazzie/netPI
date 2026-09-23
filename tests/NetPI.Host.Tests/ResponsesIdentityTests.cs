using System.Reflection;
using System.Text.Json;
using NetPI.Abstractions;
using NetPI.Provider.AiProxy;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-1 Package A (responses system content + message identity): the
/// Responses-wire payload carries EVERY system message into
/// <c>instructions</c> (a compaction summary must not be dropped), in
/// deterministic order; and transcript identities are content-derived so
/// fingerprints stay stable across runs without weakening the check.
/// </summary>
public class ResponsesIdentityTests
{
    private static readonly MethodInfo BuildPayload = typeof(AiProxyProvider)
        .GetMethod("BuildResponsesPayload", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo Fp = typeof(AiProxyProvider)
        .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
        .First(m => m.Name == "Fingerprint" && m.GetParameters().Length == 1)!;

    private static AiProxyProvider Provider()
        => new(new HttpClient(new ThrowingHandler()), "http://test", new NullLogger());

    private static IReadOnlyList<AgentMessage> Msgs(MessageRole r, string id, string text)
        => [new AgentMessage(id, r, [new TextPart(text)], DateTimeOffset.UtcNow)];

    private static string Instructions(ModelRequest req, AiProxyProvider p)
    {
        var result = (System.ValueTuple<object, List<string>>)BuildPayload.Invoke(p, [req])!;
        var dict = (System.Collections.IDictionary)result.Item1;
        return dict.Contains("instructions") ? (string)dict["instructions"]! : null;
    }

    /// <summary>Two system messages (base prompt + compaction summary) BOTH reach
    /// the wire, in transcript order — the first non-empty one used to win alone.</summary>
    [Fact]
    public void Instructions_CarryEverySystemMessage_InTranscriptOrder()
    {
        var p = Provider();
        var req = new ModelRequest
        {
            ModelId = "m",
            Messages =
            [
                new AgentMessage("sys1", MessageRole.System, [new TextPart("BASE PROMPT")], DateTimeOffset.UtcNow),
                new AgentMessage("u1", MessageRole.User, [new TextPart("hi")], DateTimeOffset.UtcNow),
                new AgentMessage("sys2", MessageRole.System, [new TextPart("SUMMARY of earlier work")], DateTimeOffset.UtcNow),
            ],
        };

        var instr = Instructions(req, p);

        Assert.Equal("BASE PROMPT\n\nSUMMARY of earlier work", instr);
    }

    /// <summary>Empty system messages are skipped; a single system message is
    /// carried verbatim; no system message → no instructions key.</summary>
    [Fact]
    public void Instructions_SkipEmpty_AndOmitWhenAbsent()
    {
        var p = Provider();
        var emptyPlusOne = new ModelRequest
        {
            ModelId = "m",
            Messages =
            [
                new AgentMessage("s0", MessageRole.System, [new TextPart("  ")], DateTimeOffset.UtcNow),
                new AgentMessage("s1", MessageRole.System, [new TextPart("only")], DateTimeOffset.UtcNow),
                new AgentMessage("u", MessageRole.User, [new TextPart("hi")], DateTimeOffset.UtcNow),
            ],
        };
        Assert.Equal("only", Instructions(emptyPlusOne, p));

        var noSystem = new ModelRequest
        {
            ModelId = "m",
            Messages = [new AgentMessage("u", MessageRole.User, [new TextPart("hi")], DateTimeOffset.UtcNow)],
        };
        Assert.Null(Instructions(noSystem, p));
    }

    /// <summary>DeterministicId: same prefix+content → same id; any change → new id.</summary>
    [Fact]
    public void DeterministicId_IsStableAndContentSensitive()
    {
        var a = MessageIdentity.DeterministicId("user", "hello");
        var b = MessageIdentity.DeterministicId("user", "hello");
        var c = MessageIdentity.DeterministicId("user", "hello!");
        var d = MessageIdentity.DeterministicId("system", "hello");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d);
        Assert.Equal(16, a.Length);
    }

    /// <summary>Identical user messages with DIFFERENT ids remain distinct
    /// fingerprints; identical ids → identical fingerprints (no weakening).</summary>
    [Fact]
    public void Fingerprint_DistinguishesIds_AndIsStableForSameMessage()
    {
        string FpOf(AgentMessage m) => (string)Fp.Invoke(null, [m])!;

        var sameTextA = new AgentMessage("id-1", MessageRole.User, [new TextPart("identical")], DateTimeOffset.UtcNow);
        var sameTextB = new AgentMessage("id-2", MessageRole.User, [new TextPart("identical")], DateTimeOffset.UtcNow);
        var again = new AgentMessage("id-1", MessageRole.User, [new TextPart("identical")], DateTimeOffset.UtcNow);

        Assert.NotEqual(FpOf(sameTextA), FpOf(sameTextB));
        Assert.Equal(FpOf(sameTextA), FpOf(again));
    }

    /// <summary>docs/plans/compaction-tool-history.md §5.5: a tool call's NAME and
    /// ARGUMENTS are part of the wire history and must be fingerprinted — a same-id
    /// call with corrected name/args must NOT reuse an obsolete chain prefix.</summary>
    [Fact]
    public void Fingerprint_CoversToolCallNameAndArguments()
    {
        string FpOf(AgentMessage m) => (string)Fp.Invoke(null, [m])!;
        ToolCallPart Call(string name, string args) =>
            new("call_1", name, JsonDocument.Parse(args).RootElement.Clone());

        var same = new AgentMessage("a1", MessageRole.Assistant, [Call("bash", "{\"cmd\":\"ls\"}")], DateTimeOffset.UtcNow);
        var sameAgain = new AgentMessage("a1", MessageRole.Assistant, [Call("bash", "{\"cmd\":\"ls\"}")], DateTimeOffset.UtcNow);
        var diffName = new AgentMessage("a1", MessageRole.Assistant, [Call("powershell", "{\"cmd\":\"ls\"}")], DateTimeOffset.UtcNow);
        var diffArgs = new AgentMessage("a1", MessageRole.Assistant, [Call("bash", "{\"cmd\":\"pwd\"}")], DateTimeOffset.UtcNow);

        Assert.Equal(FpOf(same), FpOf(sameAgain));
        Assert.NotEqual(FpOf(same), FpOf(diffName));  // same id, different tool name
        Assert.NotEqual(FpOf(same), FpOf(diffArgs));  // same id, different arguments
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new NotSupportedException("no network in tests");
    }

    private sealed class NullLogger : IPluginLogger
    {
        public void Debug(string m) { }
        public void Information(string m) { }
        public void Warning(string m) { }
        public void Error(string m, Exception? e = null) { }
    }
}
