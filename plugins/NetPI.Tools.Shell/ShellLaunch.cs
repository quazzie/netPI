using System.Text;

namespace NetPI.Tools.Shell;

/// <summary>Builds the command line and environment for bash and PowerShell invocations.</summary>
public static class ShellLaunch
{
    /// <summary>Commands longer than this are passed through a temp script file instead of the command line/environment.</summary>
    public const int MaxInlineCommand = 30_000;

    /// <summary>
    /// Prefixed to every bash command (same line, so line numbers in error messages are unchanged): stderr goes into the
    /// stdout pipe so both streams keep their exact relative order.
    /// </summary>
    public const string BashMergeStderr = "exec 2>&1; ";

    /// <summary>Environment applied to every shell: non-interactive, colorless, pager-less, UTF-8.</summary>
    public static Dictionary<string, string?> BaseEnvironment()
    {
        var utf8Locale = OperatingSystem.IsMacOS() ? "en_US.UTF-8" : "C.UTF-8";
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["TERM"] = "dumb",
            ["NO_COLOR"] = "1",
            ["GIT_PAGER"] = "cat",
            ["PAGER"] = "cat",
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["NETPI"] = "1",
        };
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LANG"))) env["LANG"] = utf8Locale;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LC_ALL"))) env["LC_ALL"] = Environment.GetEnvironmentVariable("LANG") is { Length: > 0 } lang && lang.Contains("UTF-8", StringComparison.OrdinalIgnoreCase) ? lang : utf8Locale;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PYTHONIOENCODING"))) env["PYTHONIOENCODING"] = "utf-8";
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PYTHONUNBUFFERED"))) env["PYTHONUNBUFFERED"] = "1";
        return env;
    }

    /// <summary>
    /// bash invocation. On Unix: <c>bash -c &lt;command&gt;</c> (argv is passed verbatim).
    /// On Windows (<paramref name="windowsStyle"/>) the command travels in the NETPI_COMMAND environment variable and is
    /// eval'ed, because the MSYS runtime re-parses the Windows command line (collapsing backslashes, globbing).
    /// Very long commands go through a temp script file.
    /// </summary>
    public static LaunchSpec Bash(string bashPath, string command, string tempDir, bool? windowsStyle = null)
    {
        var win = windowsStyle ?? OperatingSystem.IsWindows();
        var env = BaseEnvironment();
        env["CHERE_INVOKING"] = "1";
        if (command.Length > MaxInlineCommand)
        {
            Directory.CreateDirectory(tempDir);
            var script = Path.Combine(tempDir, $"cmd-{Guid.NewGuid():N}.sh");
            File.WriteAllText(script, BashMergeStderr + command, new UTF8Encoding(false));
            return new LaunchSpec { Shell = "bash", Executable = bashPath, Arguments = [script.Replace('\\', '/')], Environment = env, TempScript = script };
        }
        if (win)
        {
            env["NETPI_COMMAND"] = command;
            return new LaunchSpec
            {
                Shell = "bash", Executable = bashPath, Environment = env,
                Arguments = ["-c", BashMergeStderr + "__netpi_cmd=$NETPI_COMMAND; unset NETPI_COMMAND; eval \"$__netpi_cmd\""],
            };
        }
        return new LaunchSpec { Shell = "bash", Executable = bashPath, Arguments = ["-c", BashMergeStderr + command], Environment = env };
    }

    /// <summary>Script prefix: no progress bars, UTF-8 in and out, plain-text rendering.</summary>
    public const string PwshPrefix =
        "$ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; $OutputEncoding=[Text.Encoding]::UTF8; " +
        "if ($PSStyle) { $PSStyle.OutputRendering = 'PlainText' }";

    /// <summary>Script suffix: propagate a native command's exit code when the last statement failed.</summary>
    public const string PwshSuffix =
        "$__netpi_ok = $?; $__netpi_code = $LASTEXITCODE; if (-not $__netpi_ok) { if ($__netpi_code) { exit $__netpi_code } else { exit 1 } }";

    public static string PwshScript(string command) => PwshPrefix + "\n" + command + "\n" + PwshSuffix + "\n";

    /// <summary>Base64 of the UTF-16LE script, as expected by -EncodedCommand.</summary>
    public static string EncodePwsh(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    public static LaunchSpec Pwsh(string pwshPath, string command, string tempDir)
    {
        var env = BaseEnvironment();
        env["POWERSHELL_TELEMETRY_OPTOUT"] = "1";
        env["POWERSHELL_UPDATECHECK"] = "Off";
        var script = PwshScript(command);
        var args = new List<string> { "-NoLogo", "-NoProfile", "-NonInteractive" };
        if (OperatingSystem.IsWindows()) args.AddRange(["-ExecutionPolicy", "Bypass"]);
        var encoded = EncodePwsh(script);
        string? temp = null;
        if (encoded.Length > MaxInlineCommand * 0.8)
        {
            Directory.CreateDirectory(tempDir);
            temp = Path.Combine(tempDir, $"cmd-{Guid.NewGuid():N}.ps1");
            File.WriteAllText(temp, script, new UTF8Encoding(true)); // BOM: Windows PowerShell 5.1 needs it for UTF-8
            args.AddRange(["-File", temp]);
        }
        else
        {
            args.AddRange(["-EncodedCommand", encoded]);
        }
        return new LaunchSpec { Shell = "pwsh", Executable = pwshPath, Arguments = args, Environment = env, TempScript = temp };
    }
}
