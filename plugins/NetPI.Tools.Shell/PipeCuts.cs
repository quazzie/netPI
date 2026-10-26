using System.Text.RegularExpressions;

namespace NetPI.Tools.Shell;

/// <summary>
/// Two things a pipe does to a test/build run: a trailing <c>tail</c>/<c>head</c> (bash) or
/// <c>Select-Object -Last/-First</c> (pwsh) makes the call report the cutter's exit code (0) and throws away
/// the lines that would explain a failure, and a pipeline's exit code is its last segment's, so an earlier
/// segment that failed is invisible. <see cref="Detect"/> finds the first shape so the command can be run
/// without the cut; <see cref="WithProbe"/>/<see cref="ExtractProbe"/>/<see cref="HiddenFailureNote"/> report the
/// second. All of it is pure: the command text in, the decision out.
/// </summary>
public static class PipeCuts
{
    /// <summary>
    /// A trailing cut found in a command: <see cref="Command"/> is what to run instead (the command without its
    /// last segment), <see cref="Segment"/> the cut as written (for the note), <see cref="Lines"/> the N of the
    /// cut, <see cref="Head"/> whether it kept the beginning (<c>head</c>/<c>-First</c>) rather than the end.
    /// </summary>
    public sealed record Cut(string Command, string Segment, int Lines, bool Head);

    /// <summary>The marker a probed command prints around the last pipeline's exit code and segment statuses.</summary>
    public const string ProbeMarker = "__NETPI_PIPES__";

    /// <summary>
    /// A trailing <c>tail</c>/<c>head</c> (bash) or <c>Select-Object -Last/-First</c> (pwsh) that cuts a test/build
    /// run in the same pipeline: the command without that last segment, or null when the command is not that shape.
    /// Only a trailing cut is recognized; a <c>grep</c> filter, a cut in the middle of a command, a
    /// <c>tail -5 file.txt</c> that reads a file, and anything the parser cannot follow (unbalanced quotes, a
    /// heredoc's newline) are left alone.
    /// </summary>
    public static Cut? Detect(string? command, string shell)
    {
        if (command is null || command.Contains('\n') || command.Contains('\r')) return null;
        var s = Structure(command);
        if (s is null || s.Value.Pipes.Length == 0) return null;
        var st = s.Value;

        var lastFrom = st.Pipes[^1] + 1;
        var segment = command[lastFrom..].Trim();
        bool head;
        int lines;
        if (shell == "pwsh")
        {
            if (!ParsePwshCut(segment, out head, out lines)) return null;
        }
        else
        {
            if (!ParseBashCut(segment, out lines, out head)) return null;
        }
        return IsTestCommand(shell, st) ? new Cut(command[..st.Pipes[^1]].TrimEnd(), segment, lines, head) : null;
    }

    /// <summary>True when the command's last pipeline carries a top-level pipe (a probe is worth it).</summary>
    public static bool HasTopLevelPipe(string? command) =>
        command is not null && !command.Contains('\n') && !command.Contains('\r') && Structure(command) is { } s && s.Pipes.Length > 0;

    /// <summary>The text of the command's last top-level pipeline (for the hidden-failure note), or null when it has no pipe.</summary>
    public static string? LastPipelineText(string? command)
    {
        if (command is null || command.Contains('\n') || command.Contains('\r')) return null;
        if (Structure(command) is { } s && s.Pipes.Length > 0) return command[s.Start..].Trim();
        return null;
    }

    /// <summary>
    /// The statements appended to a single-line bash command that carries a top-level pipe: they print the last
    /// pipeline's exit code and its segments' <c>PIPESTATUS</c> on a <see cref="ProbeMarker"/> line and exit with
    /// the pipeline's own code, so the exit code is unchanged and the service can turn the line into a note.
    /// A <c>DEBUG</c> trap sees the same PIPESTATUS, but it runs before every simple command of the script
    /// (measured: a 100 000-iteration no-op loop took 0.30 s without the trap and 2.79 s with it, ~830 %), so the
    /// probe checks the last pipeline only and costs three statements. Null (the command is left alone) when it
    /// is not a single line, has no top-level pipe, or ends in <c>&</c> (a background job: <c>$?</c> is not the
    /// pipeline's then).
    /// </summary>
    public static string? WithProbe(string command)
    {
        if (!HasTopLevelPipe(command)) return null;
        if (command.TrimEnd().EndsWith('&')) return null;
        return command +
            "\n__netpi_probe=\"$? ${PIPESTATUS[*]}\"" +
            $"\necho \"{ProbeMarker} $__netpi_probe {ProbeMarker}\"" +
            "\nexit \"${__netpi_probe%% *}\"";
    }

    /// <summary>
    /// Strips the probe's marker line from captured output and returns the last pipeline's segment statuses
    /// (null: no marker — the command did not run to the probe, e.g. it ended in <c>exit N</c>, so nothing is
    /// reported). A malformed marker is left in the output as is.
    /// </summary>
    public static (string Text, int[]? Statuses) ExtractProbe(string text)
    {
        // The probe line is the output's last line (the probe is appended last): <marker> <rc> <s1 … sn> <marker>.
        // The last marker occurrence is its closing one; back up to the line's start and check the shape.
        var close = text.LastIndexOf(ProbeMarker, StringComparison.Ordinal);
        if (close < 0) return (text, null);
        var lineStart = close;
        while (lineStart > 0 && text[lineStart - 1] != '\n') lineStart--;
        var closeInLine = close - lineStart + ProbeMarker.Length;
        if (!text.AsSpan(lineStart).StartsWith(ProbeMarker, StringComparison.Ordinal)) return (text, null); // the last marker was not the line's opening
        if (closeInLine < text.Length - lineStart && text[closeInLine + lineStart] != '\n') return (text, null); // something after the closing marker: not the probe line
        var fields = text[lineStart..(closeInLine + lineStart)].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // [marker, rc, s1 … sn, marker]; rc is the pipeline's own code (sn), the statuses are its segments'
        if (fields.Length < 4 || fields[0] != ProbeMarker || fields[^1] != ProbeMarker) return (text, null);
        var statuses = new int[fields.Length - 3];
        for (var k = 0; k < statuses.Length; k++)
            if (!int.TryParse(fields[k + 2], out statuses[k])) return (text, null);
        return (text[..lineStart], statuses);
    }

    /// <summary>
    /// The note for a failure a pipe hid: an earlier segment of the last pipeline exited non-zero while its last
    /// segment — the pipeline's exit code — is 0. Exit 141 (SIGPIPE, a reader that closed early, <c>grep | head</c>)
    /// is not a failure. One line per call (the last pipeline is the one whose exit code is reported); the
    /// pipeline is clipped to 80 characters.
    /// </summary>
    public static string? HiddenFailureNote(int[]? statuses, string? pipelineText)
    {
        if (statuses is not { Length: >= 2 } || statuses[^1] != 0) return null;
        for (var i = 0; i < statuses.Length - 1; i++)
        {
            var code = statuses[i];
            if (code != 0 && code != 141)
            {
                var pipeline = pipelineText ?? "";
                if (pipeline.Length > 80) pipeline = pipeline[..77] + "…";
                return $"[a pipe hid a failure: a command before the last in \"{pipeline}\" exited {code}]";
            }
        }
        return null;
    }

    // ------------------------------------------------ the test/build commands a cut may hide

    /// <summary>
    /// A test or build run, by the names agents actually pipe: the segment must <i>start</i> with the run (after
    /// leading <c>VAR=value</c> assignments, and <c>&</c> in pwsh), so a name that is only an argument
    /// (<c>echo dotnet test</c>) does not count. The segment text is the masked one (quoted strings, $( )/` `
    /// substitutions and comments blanked). <c>npm run</c> needs its <c>&lt;x&gt;</c>: a bare <c>npm run</c>
    /// lists the run scripts, it runs nothing.
    /// </summary>
    private const string TestCommandPattern =
        @"(?:dotnet\s+(?:test|build|run)\b|pytest\b|ctest\b|cargo\s+(?:test|build)\b|go\s+(?:test|build)\b" +
        @"|mvn\b|gradle\b|npm\s+(?:test\b|run\s+\S)|make\b|test\.ps1\b|e2e\.ps1\b|build\.ps1\b)";

    private static readonly Regex TestCommand = new(
        @"^(?:\s*[A-Za-z_]\w*=\S*\s+)*" + @"(?:[\w./\\-]+/)?" + TestCommandPattern,
        RegexOptions.Compiled);

    private static readonly Regex TestCommandPwsh = new(
        @"^\s*(?:&\s+)?" + @"(?:[\w./\\-]+[\\/])?" + TestCommandPattern,
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool IsTestCommand(string shell, (int Start, int[] Pipes, char[] Mask) s)
    {
        var re = shell == "pwsh" ? TestCommandPwsh : TestCommand;
        for (var i = 0; i < s.Pipes.Length; i++)
        {
            var from = i == 0 ? s.Start : s.Pipes[i - 1] + 1;
            var segment = new string(s.Mask, from, s.Pipes[i] - from).Trim();
            if (segment.Length > 0 && re.IsMatch(segment)) return true;
        }
        return false;
    }

    // ------------------------------------------------ the last segment: what a cut looks like

    private static bool ParseBashCut(string segment, out int lines, out bool head)
    {
        lines = 0; head = false;
        var tokens = Tokenize(segment);
        if (tokens is null) return false;
        var i = 0;
        if (i < tokens.Length && tokens[i].Text == "2>&1") i++;
        if (i >= tokens.Length || tokens[i].Quoted || (tokens[i].Text != "tail" && tokens[i].Text != "head")) return false;
        head = tokens[i].Text == "head";
        i++;
        var end = tokens.Length;
        if (end > i && tokens[end - 1].Text == "2>&1") end--;
        // exactly one count, and nothing else: a second argument is a file (tail -5 file.txt) and is not a cut
        if (end - i == 1)
        {
            var t = tokens[i].Text;
            if (t.Length > 1 && t[0] == '-' && int.TryParse(t[1..], out lines)) return lines > 0; // -5
            if (t.StartsWith("--lines=", StringComparison.Ordinal) && int.TryParse(t["--lines=".Length..], out lines)) return lines > 0;
            if (t.Length > 0 && t[0] != '-' && int.TryParse(t, out lines)) return lines > 0; // 5 (a bare count)
        }
        else if (end - i == 2 && (tokens[i].Text == "-n" || tokens[i].Text == "--lines") && int.TryParse(tokens[i + 1].Text, out lines))
            return lines > 0;
        return false;
    }

    private static bool ParsePwshCut(string segment, out bool head, out int lines)
    {
        head = false; lines = 0;
        var tokens = Tokenize(segment);
        if (tokens is null || tokens.Length != 3) return false;
        if (!string.Equals(tokens[0].Text, "Select-Object", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(tokens[0].Text, "select", StringComparison.OrdinalIgnoreCase)) return false;
        var p = tokens[1].Text;
        var last = string.Equals(p, "-Last", StringComparison.OrdinalIgnoreCase) || string.Equals(p, "-l", StringComparison.OrdinalIgnoreCase);
        var first = string.Equals(p, "-First", StringComparison.OrdinalIgnoreCase) || string.Equals(p, "-f", StringComparison.OrdinalIgnoreCase);
        if (!last && !first || !int.TryParse(tokens[2].Text, out lines) || lines <= 0) return false;
        head = first;
        return true;
    }

    /// <summary>Split a segment into (text, quoted) tokens on unquoted whitespace; null when a quote is unbalanced.</summary>
    private static (string Text, bool Quoted)[]? Tokenize(string segment)
    {
        var tokens = new List<(string, bool)>();
        var i = 0;
        while (i < segment.Length)
        {
            while (i < segment.Length && char.IsWhiteSpace(segment[i])) i++;
            if (i >= segment.Length) break;
            var start = i;
            var quoted = false;
            if (segment[i] is '\'' or '"')
            {
                var q = segment[i];
                i++;
                while (i < segment.Length && segment[i] != q) i++;
                if (i >= segment.Length) return null;
                i++;
                quoted = true;
            }
            else
            {
                while (i < segment.Length && !char.IsWhiteSpace(segment[i]))
                {
                    if (segment[i] == '\\' && i + 1 < segment.Length) i++; // the escaped character is part of the token
                    i++;
                }
            }
            tokens.Add((segment[start..i], quoted));
        }
        return tokens.ToArray();
    }

    // ------------------------------------------------ the top-level structure of a single-line command

    // The scanner's contexts: what a position of the command is inside of.
    private enum Ctx { Norm, SQuote, DQuote, Dollar, Backtick }

    /// <summary>
    /// Where the command's last pipeline starts, the indexes of the top-level <c>|</c> inside it, and a masked
    /// copy of the command (quoted strings, $( ) and ` ` substitutions, and comments blanked). Pipes and
    /// separators inside quotes, substitutions, $( ) and subshells are not top-level. Anything the scanner
    /// cannot follow (an unmatched quote, paren or backtick) gives null: the command is left alone.
    /// </summary>
    private static (int Start, int[] Pipes, char[] Mask)? Structure(string command)
    {
        var n = command.Length;
        var mask = command.ToCharArray();
        var pipes = new List<int>();
        var start = 0;

        var ctx = new Stack<Ctx>();
        var paren = new Stack<int>();
        ctx.Push(Ctx.Norm);
        paren.Push(0);

        for (var i = 0; i < n; i++)
        {
            var c = command[i];
            switch (ctx.Peek())
            {
                case Ctx.SQuote:
                    if (c == '\'') ctx.Pop();
                    else mask[i] = ' ';
                    break;

                case Ctx.DQuote:
                    if (c == '\\' && i + 1 < n) { mask[i] = mask[++i] = ' '; }
                    else if (c == '"') ctx.Pop();
                    else if (c == '$' && i + 1 < n && command[i + 1] == '(') { ctx.Push(Ctx.Dollar); paren.Push(0); mask[i] = mask[++i] = ' '; }
                    else if (c == '`') { ctx.Push(Ctx.Backtick); paren.Push(0); mask[i] = ' '; }
                    else mask[i] = ' ';
                    break;

                case Ctx.Dollar:
                case Ctx.Backtick:
                    // inside a substitution: its own quotes and parens, and nothing in it is top-level
                    if (c == '(') { var p = paren.Pop(); paren.Push(p + 1); }
                    else if (c == ')')
                    {
                        if (paren.Peek() == 0) { ctx.Pop(); paren.Pop(); }
                        else { var p = paren.Pop(); paren.Push(p - 1); }
                    }
                    else if (c == '`' && ctx.Peek() == Ctx.Backtick && (i == 0 || command[i - 1] != '\\')) { ctx.Pop(); paren.Pop(); }
                    else if (c == '\'') { ctx.Push(Ctx.SQuote); mask[i] = ' '; }
                    else if (c == '"') { ctx.Push(Ctx.DQuote); mask[i] = ' '; }
                    else if (c == '$' && i + 1 < n && command[i + 1] == '(') { ctx.Push(Ctx.Dollar); paren.Push(0); mask[i] = mask[++i] = ' '; }
                    break;

                case Ctx.Norm:
                    switch (c)
                    {
                        case '\'': ctx.Push(Ctx.SQuote); mask[i] = ' '; break;
                        case '"': ctx.Push(Ctx.DQuote); mask[i] = ' '; break;
                        case '`': ctx.Push(Ctx.Backtick); paren.Push(0); mask[i] = ' '; break;
                        case '$' when i + 1 < n && command[i + 1] == '(':
                            ctx.Push(Ctx.Dollar); paren.Push(0); mask[i] = mask[++i] = ' '; break;
                        case '(': { var p = paren.Pop(); paren.Push(p + 1); break; }
                        case ')':
                            if (paren.Peek() == 0) return null; // unbalanced: do not guess
                            { var p = paren.Pop(); paren.Push(p - 1); break; }
                        case '#':
                            for (var k = i; k < n; k++) mask[k] = ' '; // a comment runs to the end of the line
                            i = n; break;
                        case '|' when paren.Peek() == 0:
                            pipes.Add(i);
                            break;
                        case ';' when paren.Peek() == 0:
                            start = i + 1; pipes.Clear(); break;
                        case '&' when paren.Peek() == 0:
                            if (i > 0 && command[i - 1] == '|') { }                // |& : the pipe was recorded
                            else if (i + 1 < n && command[i + 1] == '&') { start = i + 2; pipes.Clear(); }
                            else if (i > 0 && command[i - 1] == '>' || i + 1 < n && command[i + 1] == '>') { } // >& / &> redirect
                            else { start = i + 1; pipes.Clear(); }
                            break;
                    }
                    break;
            }
        }
        if (ctx.Count != 1 || paren.Peek() != 0) return null;
        return (start, pipes.ToArray(), mask);
    }
}
