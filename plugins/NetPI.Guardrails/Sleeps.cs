using System.Globalization;
using System.Text.Json;

namespace NetPI.Guardrails;

/// <summary>
/// The typed guardrail of <c>guardrails.maxSleepSeconds</c>: a deliberate wait in a <c>bash</c>, <c>pwsh</c> or <c>ssh run</c>
/// command that outlasts the limit. A regular expression cannot say "longer than N seconds" (a rule for <c>^sleep\s</c> takes
/// <c>sleep 1</c> with it), so the value is parsed and compared — <c>sleep 300</c> with its GNU suffixes and decimals,
/// <c>Start-Sleep -Seconds 300</c>, <c>ping -n 300</c>, <c>timeout 400 sleep 300</c> (the timeout bounds it) and the script
/// a shell is handed with <c>-c</c> / <c>-Command</c>.
/// <para>Only a wait in command position counts, which is what keeps it off the data: the <c>sleep 60</c> an
/// <c>echo "sleep 60"</c> or a <c>git commit -m "sleep 60"</c> quotes is read as a word, not run. A line of a heredoc body
/// looks like a command line once the command is split at its newlines, so it is caught; that is the known false positive
/// here, and the reason the block gives tells the agent why.</para>
/// </summary>
internal static class Sleeps
{
    /// <summary>The rule a verdict carries: the setting, which is also what a session allowance is remembered under.</summary>
    public const string Rule = "guardrails.maxSleepSeconds";
    /// <summary>Long enough for a command that takes a while, short enough that waiting is a bug.</summary>
    public const double DefaultMaxSeconds = 30;
    /// <summary>A shell inside a shell is still read, but not without end.</summary>
    private const int MaxDepth = 4;

    /// <summary>The verdict for a command that waits longer than <paramref name="maxSeconds"/> (0 switches the rule off),
    /// null when it may run.</summary>
    public static Verdict? Check(string tool, JsonElement args, double maxSeconds, bool ask)
    {
        if (maxSeconds <= 0 || RuleSet.CommandOf(tool, args) is not { Length: > 0 } command) return null;
        foreach (var part in RuleSet.Parts(command))
            if (Waits(part, 0) is { } seconds && seconds > maxSeconds)
                return new Verdict(ask ? GuardAction.Ask : GuardAction.Block, Rule, "sleep", part, Detail(seconds, maxSeconds));
        return null;
    }

    /// <summary>How long the verdict says the call waits: the value it read, and what the setting allows.</summary>
    private static string Detail(double seconds, double maxSeconds) =>
        string.Create(CultureInfo.InvariantCulture, $"{Format(seconds)}, over the {Format(maxSeconds)} allowed");

    /// <summary>How long a wait is said to be: seconds up to a minute, then minutes, then hours.</summary>
    private static string Format(double seconds) =>
        seconds < 60 ? string.Create(CultureInfo.InvariantCulture, $"{seconds:0.#}s")
        : seconds < 3600 ? string.Create(CultureInfo.InvariantCulture, $"{seconds / 60:0.#} minutes")
        : string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600:0.#} hours");

    /// <summary>The seconds a part waits on purpose (null when it does not): the first command in it, and what that runs.</summary>
    private static double? Waits(string part, int depth)
    {
        var tokens = Tokens(RuleSet.Undecorated(part));
        if (tokens.Count == 0) return null;
        var first = tokens[0];
        if (IsSleep(first)) return SleepSeconds(tokens, 1);
        if (IsPowerShellSleep(first)) return PowerShellSeconds(tokens);
        if (first.Equals("ping", StringComparison.OrdinalIgnoreCase) && PingSeconds(tokens) is { } pings) return pings;
        // timeout D sleep N: the timeout ends the wait first, so the shorter of the two is what the agent sits through.
        if (first.Equals("timeout", StringComparison.OrdinalIgnoreCase) && TimeoutSeconds(tokens) is { } bounded) return bounded;
        if (depth < MaxDepth && Payload(tokens) is { } script && Waits(script, depth + 1) is { } nested) return nested;
        return null;
    }

    /// <summary><c>sleep 300</c>, <c>sleep 1h30m</c>, <c>sleep 500ms</c>, <c>sleep 1 2 3</c> (the arguments add up).</summary>
    private static double? SleepSeconds(IReadOnlyList<string> tokens, int from)
    {
        double total = 0;
        var read = false;
        for (var i = from; i < tokens.Count; i++)
        {
            if (!TryDuration(tokens[i], out var seconds)) break;   // not a number: the rest is an option or another word
            total += seconds;
            read = true;
        }
        return read ? total : null;
    }

    /// <summary><c>Start-Sleep 300</c>, <c>-Seconds 300</c> / <c>-s</c>, <c>-Milliseconds 300000</c> / <c>-ms</c>,
    /// <c>-Minutes</c> / <c>-Hours</c>, and a time span as <c>-Timeout 00:05:00</c> / <c>-TimeSpan</c>.</summary>
    private static double? PowerShellSeconds(IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 2) return null;
        var positional = 1;
        if (tokens[positional].StartsWith('-'))
        {
            if (positional + 1 >= tokens.Count) return null;
            var value = tokens[++positional];
            return Option(tokens[positional - 1]) switch
            {
                "ms" or "milliseconds" => Duration(value, 0.001),
                "t" or "ts" or "timespan" or "timeout" => TimeSpanSeconds(value),
                _ => Duration(value, 1),
            };
        }
        return SleepSeconds(tokens, positional);
    }

    /// <summary>An option as a name: the dash off, lower case, so <c>-Seconds</c> and <c>-s</c> read the same.</summary>
    private static string Option(string token) =>
        token.Length > 1 && token[0] == '-' ? token[1..].ToLowerInvariant() : token.ToLowerInvariant();

    /// <summary><c>ping -n 300</c> (Windows) and <c>ping -c 300</c> (GNU): a packet a second, so the count is the wait.</summary>
    private static double? PingSeconds(IReadOnlyList<string> tokens)
    {
        for (var i = 1; i < tokens.Count - 1; i++)
        {
            if (Option(tokens[i]) is not ("n" or "c")) continue;
            return Duration(tokens[i + 1], 1);
        }
        return null;
    }

    /// <summary><c>timeout D sleep N</c>: the sleep bounded by the timeout in front of it.</summary>
    private static double? TimeoutSeconds(IReadOnlyList<string> tokens)
    {
        for (var i = 1; i < tokens.Count; i++)
        {
            if (!IsSleep(tokens[i])) continue;
            if (SleepSeconds(tokens, i + 1) is not { } sleep) return null;
            return i >= 2 && TryDuration(tokens[i - 1], out var bound) ? Math.Min(sleep, bound) : sleep;
        }
        return null;
    }

    /// <summary>The script a shell runs for itself: the word after its <c>-c</c> / <c>-Command</c>, quotes and all.</summary>
    private static string? Payload(IReadOnlyList<string> tokens)
    {
        if (!IsShell(tokens[0]) && !IsPowerShell(tokens[0])) return null;
        for (var i = 1; i < tokens.Count - 1; i++)
            if (Option(tokens[i]) is ("c" or "command")) return tokens[i + 1];
        return null;
    }

    /// <summary>The shells whose <c>-c</c> takes a script (an <c>ssh</c> call is remote: it arrives as a part already).</summary>
    private static bool IsShell(string token) =>
        Path.GetFileNameWithoutExtension(token).ToLowerInvariant() is "sh" or "bash" or "zsh" or "dash" or "ksh" or "mksh" or "ash";

    private static bool IsPowerShell(string token) =>
        Path.GetFileNameWithoutExtension(token).ToLowerInvariant() is "pwsh" or "powershell";

    private static bool IsSleep(string token) => token.Equals("sleep", StringComparison.OrdinalIgnoreCase);

    private static bool IsPowerShellSleep(string token) =>
        token.Equals("Start-Sleep", StringComparison.OrdinalIgnoreCase) || token.Equals("Invoke-Sleep", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A GNU duration as <c>sleep</c> and <c>timeout</c> take it: <c>300</c>, <c>1.5</c>, <c>500ms</c>, <c>1h30m</c>, the
    /// longest unit last (<c>1m30s</c>, not <c>1s30m</c>). <paramref name="unit"/> scales what a bare number means.
    /// </summary>
    private static bool TryDuration(string token, out double seconds)
    {
        seconds = Duration(token, 1) ?? 0;
        return seconds > 0;
    }

    private static double? Duration(string token, double unit)
    {
        double total = 0;
        var read = false;
        var i = 0;
        while (i < token.Length)
        {
            var start = i;
            while (i < token.Length && (char.IsAsciiDigit(token[i]) || (token[i] == '.' && i == start))) i++;
            if (i == start) break;
            if (!double.TryParse(token[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;
            var letters = i;
            while (i < token.Length && char.IsAsciiLetter(token[i])) i++;
            if (Unit(token[letters..i]) is not { } scale) return null;
            total += value * scale * unit;
            read = true;
            if (letters == i) break;   // no unit: the number is all, what follows is another argument
        }
        return read ? total : null;
    }

    /// <summary>What one GNU unit is in seconds; null for a unit <c>sleep</c> would refuse.</summary>
    private static double? Unit(string unit) => unit.ToLowerInvariant() switch
    {
        "" or "s" or "sec" or "secs" or "second" or "seconds" => 1,
        "ms" or "milli" or "millis" or "millisecond" or "milliseconds" => 0.001,
        "us" or "micro" or "micros" or "microsecond" or "microseconds" => 0.000_001,
        "m" or "min" or "mins" or "minute" or "minutes" => 60,
        "h" or "hr" or "hrs" or "hour" or "hours" => 3600,
        "d" or "day" or "days" => 86_400,
        _ => null,
    };

    /// <summary>A PowerShell time span in seconds: <c>hh:mm:ss</c> or <c>mm:ss</c>, fractions allowed.</summary>
    private static double? TimeSpanSeconds(string value)
    {
        var parts = value.Split(':');
        if (parts.Length is < 2 or > 3) return null;
        double seconds = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return null;
            seconds = seconds * 60 + number;
        }
        return seconds;
    }

    /// <summary>
    /// The words of a command as a shell hands them over: whitespace separates, a quoted stretch is one word (quotes
    /// removed), so <c>bash -c "sleep 300"</c> keeps its script in one piece and <c>echo "sleep 300"</c> is two words
    /// whose first is not a command.
    /// </summary>
    private static List<string> Tokens(string command)
    {
        var tokens = new List<string>();
        var i = 0;
        while (true)
        {
            while (i < command.Length && char.IsWhiteSpace(command[i])) i++;
            if (i >= command.Length) break;
            if (command[i] is '"' or '\'')
            {
                var quote = command[i];
                var content = ++i;
                while (i < command.Length && command[i] != quote) i++;
                tokens.Add(command[content..i]);
                i = Math.Min(i + 1, command.Length);    // past the closing quote, or the end of an unclosed one
            }
            else
            {
                var start = i;
                while (i < command.Length && !char.IsWhiteSpace(command[i])) i++;
                tokens.Add(command[start..i]);
            }
        }
        return tokens;
    }
}