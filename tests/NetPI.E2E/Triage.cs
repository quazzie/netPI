using System.Text;

namespace NetPI.E2E;

/// <summary>One question put to a server that may be stuck, and whether it answered in time.</summary>
public sealed record Probe(string Name, bool Ok, long Ms, string? Error = null);

/// <summary>What the operating system says about the server process (not the server: it can say this much when it is frozen).</summary>
public sealed record ServerVitals(bool Alive, int? ExitCode = null, int Threads = 0, int Handles = 0, long WorkingSetMb = 0, double CpuPercent = 0);

/// <summary>
/// Whether a server is fine, and if not how it is not: the answer to "why did this RPC time out?" that a failed test used to
/// leave unsaid. A server that does not answer is one of a few different things, and each one points somewhere else:
/// the process is gone, the HTTP pipeline is frozen (the thread pool is starved), RPC dispatch is stuck while HTTP works,
/// the database path is blocked, or only this test's WebSocket is dead.
/// </summary>
public sealed class ServerTriage
{
    public const string Health = "health", Rpc = "app.info", Db = "sessions.list", Ws = "ws app.info";

    public required ServerVitals Vitals { get; init; }
    public required IReadOnlyList<Probe> Probes { get; init; }
    public required string Verdict { get; init; }
    /// <summary>True when the server answered every question: a failure was then the test's own, not a wedged server.</summary>
    public required bool Healthy { get; init; }
    /// <summary>Every thread's managed stack of a server that did not answer, read from outside by <see cref="StackCapture"/>; null when none was taken.</summary>
    public string? Stacks { get; init; }
    /// <summary>How long the RUNNER's own thread pool took to start a trivial work item just before the probes (ms). The probes are made from
    /// the runner, so a runner that is starved makes a healthy server look dead: when this is large, the probes prove nothing.</summary>
    public long RunnerPoolMs { get; init; }

    public const long RunnerStarvedMs = 1000;

    public ServerTriage WithStacks(string stacks) => new() { Vitals = Vitals, Probes = Probes, Verdict = Verdict, Healthy = Healthy, Stacks = stacks, RunnerPoolMs = RunnerPoolMs };

    public static ServerTriage Unknown { get; } = new() { Vitals = new ServerVitals(true), Probes = [], Verdict = "not checked", Healthy = true };

    public static ServerTriage Of(string verdict) => new() { Vitals = new ServerVitals(true), Probes = [], Verdict = verdict, Healthy = false };

    /// <summary>The verdict the vitals and the probes add up to. Pure: the same inputs always give the same words.</summary>
    public static ServerTriage Classify(ServerVitals vitals, IReadOnlyList<Probe> probes, long runnerPoolMs = 0)
    {
        Probe? Find(string name) => probes.FirstOrDefault(p => p.Name == name);
        bool Answered(string name) => Find(name)?.Ok == true;
        var cpu = vitals.CpuPercent >= 50 ? $"busy ({vitals.CpuPercent:0}% CPU: a thread is spinning or working)" : $"idle ({vitals.CpuPercent:0}% CPU: its threads are blocked, not working)";

        string verdict;
        var healthy = false;
        if (!vitals.Alive)
            verdict = $"the server process is gone (exit code {(vitals.ExitCode is { } c ? c.ToString() : "unknown")}): it crashed or was stopped";
        else if (!Answered(Health))
            verdict = $"wedged: the process is alive but does not even answer /api/health ({Find(Health)?.Error ?? "no answer"}), and is {cpu}. " +
                      "Nothing is being scheduled on its thread pool; see the server log for a watchdog line (\"The thread pool has not started a queued work item\")";
        else if (!Answered(Rpc))
            verdict = $"RPC dispatch is stuck: /api/health answers, app.info (which touches nothing) does not ({Find(Rpc)?.Error ?? "no answer"}); the process is {cpu}";
        else if (!Answered(Db))
            verdict = $"the database path is blocked: /api/health and app.info answer, sessions.list does not ({Find(Db)?.Error ?? "no answer"}); the process is {cpu}. " +
                      "Something holds the database gate or the pool is full of calls waiting for it";
        else if (probes.Any(p => p.Name == Ws) && !Answered(Ws))
            verdict = $"this test's WebSocket connection is dead or silent ({Find(Ws)?.Error ?? "no answer"}) while the server answers over HTTP";
        else
        {
            healthy = true;
            verdict = "responsive: every probe was answered, so the server was not wedged and the failure is the test's own";
        }
        // The probes are made from the runner. A runner that cannot start a work item for a second is the one that is stuck.
        if (runnerPoolMs >= RunnerStarvedMs && !healthy)
            verdict = $"UNRELIABLE: the runner itself is overloaded (its own thread pool took {runnerPoolMs} ms to start a trivial work item), so the probes below may blame a healthy server. {verdict}";
        return new ServerTriage { Vitals = vitals, Probes = probes, Verdict = verdict, Healthy = healthy, RunnerPoolMs = runnerPoolMs };
    }

    /// <summary>One line for the console and the report.</summary>
    public string Summary => Healthy ? "responsive" : Verdict;

    /// <summary>The full account, for the failure file.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Verdict);
        foreach (var p in Probes)
            sb.AppendLine($"  probe {p.Name,-16} {(p.Ok ? "answered" : "NO ANSWER"),-9} {p.Ms} ms{(p.Error is null ? "" : "  " + p.Error)}");
        var v = Vitals;
        sb.AppendLine($"  runner: its thread pool started a trivial work item in {RunnerPoolMs} ms");
        sb.AppendLine(v.Alive
            ? $"  process: {v.Threads} threads, {v.Handles} handles, {v.WorkingSetMb} MB working set, {v.CpuPercent:0}% CPU over the probes"
            : $"  process: exited{(v.ExitCode is { } c ? $" with code {c}" : "")}");
        return sb.ToString().TrimEnd();
    }
}

/// <summary>What a test left running when it returned: the agents it never waited for.</summary>
public sealed record SettleResult(string? Leak, bool Clean)
{
    public static SettleResult Quiet { get; } = new(null, true);
}

/// <summary>Reads a failure file the way a person does: a section that is empty is evidence that says nothing.</summary>
public static class Evidence
{
    /// <summary>The names of the <c>--- section</c>s of a failure file that hold nothing at all.</summary>
    public static List<string> EmptySections(string text)
    {
        var empty = new List<string>();
        var lines = text.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("--- ", StringComparison.Ordinal)) continue;
            var j = i + 1;
            while (j < lines.Length && !lines[j].StartsWith("--- ", StringComparison.Ordinal) && lines[j].Trim().Length == 0) j++;
            if (j >= lines.Length || lines[j].StartsWith("--- ", StringComparison.Ordinal)) empty.Add(lines[i][4..].Split('(')[0].Trim());
        }
        return empty;
    }
}
