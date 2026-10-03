using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace NetPI.E2E;

// The assertions (Check, with its check counter the shards read), the Wait helpers and AssertException are the
// shared harness, linked from tests/Shared. What remains here is E2E's own: the case registry, the runner that
// shards and triages against a live server, the routed console and the JsonElement conveniences.

/// <summary>One registered test. <see cref="Id"/> is stable (runs, reports and reruns name it); <see cref="Name"/> is the readable sentence.</summary>
public sealed record TestCase(string Id, string Name, Func<Task> Body, int TimeoutSeconds, int Order)
{
    /// <summary>The tags of the case: its area (the id up to the first dot) plus the ones <see cref="Catalog"/> lists for it.</summary>
    public IReadOnlyList<string> Tags => Catalog.TagsOf(Id);
}

/// <summary>The registry the <c>XTests.Register</c> methods fill. A runner is bound to one <see cref="Env"/> (or to none, to list).</summary>
public sealed class TestRunner
{
    private readonly List<TestCase> _tests = [];

    public void Add(string id, string name, Func<Task> body, int timeoutSeconds = 90)
    {
        if (_tests.Any(t => t.Id == id)) throw new InvalidOperationException($"duplicate test id '{id}'");
        _tests.Add(new TestCase(id, name, body, timeoutSeconds, _tests.Count));
    }

    public IReadOnlyList<TestCase> Cases => _tests;
}

/// <summary>
/// Console output of the test body that is running in this async flow goes to that test's buffer, so that shards running at
/// once do not interleave their lines: the runner prints a test's lines together with its result.
/// </summary>
public sealed class RoutedConsole(TextWriter inner) : TextWriter
{
    private static readonly AsyncLocal<StringBuilder?> Buffer = new();
    private readonly Lock _gate = new();

    public override Encoding Encoding => inner.Encoding;

    public static void Attach(StringBuilder? buffer) => Buffer.Value = buffer;

    public static void Install()
    {
        if (Console.Out is not RoutedConsole) Console.SetOut(new RoutedConsole(Console.Out));
    }

    public override void Write(char value) => Write(value.ToString());

    public override void Write(string? value)
    {
        if (value is null) return;
        if (Buffer.Value is { } b) { lock (b) b.Append(value); return; }
        lock (_gate) { inner.Write(value); inner.Flush(); }
    }

    public override void WriteLine(string? value) => Write((value ?? "") + "\n");

    public override void WriteLine() => Write("\n");
}

/// <summary>JsonElement conveniences.</summary>
public static class J
{
    public static string? S(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : null;

    public static long L(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    public static bool B(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static JsonElement P(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    public static bool Has(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    public static IEnumerable<JsonElement> Arr(this JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];

    public static IEnumerable<JsonElement> Arr(this JsonElement e, string name) => e.P(name).Arr();
}
