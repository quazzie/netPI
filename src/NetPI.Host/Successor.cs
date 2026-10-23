using System.Diagnostics;
using System.Globalization;

namespace NetPI.Host;

/// <summary>
/// The shells' half of a restart (<see cref="NetPiServerOptions.Restart"/>): the running process starts its successor with
/// its own arguments plus <c>--after &lt;pid&gt;</c> and its token, then stops; the successor waits for it to exit (the
/// home's lock, the port and the single-instance mutex are free then), installs what the publish left
/// (<see cref="PendingBuild"/>) and serves with the same token, so a browser tab, the phone page or a script keeps working.
/// </summary>
public static class Successor
{
    public const string AfterOption = "--after";
    /// <summary>The variable the token travels in; netpi-server reads it anyway, the desktop shell only as a successor.</summary>
    public const string TokenVariable = "NETPI_TOKEN";
    private static readonly TimeSpan PredecessorWait = TimeSpan.FromSeconds(60);

    /// <summary>Start this program again: <paramref name="args"/> (without an earlier <c>--after</c>) and <c>--after</c> this process.</summary>
    public static void Start(IEnumerable<string> args, string token)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("The path of this program is unknown");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
        // run as "dotnet netpi-server.dll": the program is dotnet, the app its first argument
        if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        foreach (var a in Without(args.ToArray(), out var _)) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(AfterOption);
        psi.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        psi.Environment[TokenVariable] = token;
        using var started = Process.Start(psi) ?? throw new InvalidOperationException($"{exe} did not start");
    }

    /// <summary>
    /// When <paramref name="args"/> name a predecessor (<c>--after PID</c>), wait for it to exit - at most a minute; the
    /// start then fails on the home's lock with its usual message - and return the arguments without that option.
    /// </summary>
    public static string[] WaitForPredecessor(string[] args, out bool isSuccessor)
    {
        var rest = Without(args, out var pid);
        isSuccessor = pid is not null;
        if (pid is { } id)
        {
            try
            {
                using var p = Process.GetProcessById(id);
                p.WaitForExit(PredecessorWait);
            }
            catch (ArgumentException) { /* gone already */ }
            catch (InvalidOperationException) { }
        }
        return rest;
    }

    /// <summary>The arguments without <c>--after PID</c>; <paramref name="pid"/> is the predecessor it named.</summary>
    internal static string[] Without(string[] args, out int? pid)
    {
        pid = null;
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], AfterOption, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var id)) pid = id;
                i++;
                continue;
            }
            rest.Add(args[i]);
        }
        return [.. rest];
    }
}
