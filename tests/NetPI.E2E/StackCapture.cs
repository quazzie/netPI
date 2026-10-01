using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace NetPI.E2E;

/// <summary>
/// The managed stack of every thread of a server that has stopped answering: the one piece of evidence that names what a hung
/// process is blocked on. A process that answers nothing cannot be asked from inside, so this reads it from outside with
/// <c>dotnet-stack</c> (Microsoft's diagnostics tool, <c>dotnet tool install -g dotnet-stack</c>). It is not a dependency of anything:
/// when the tool is missing the failure says how to get it, and <c>NETPI_E2E_STACK_TOOL</c> names another executable that takes
/// <c>report -p &lt;pid&gt;</c>.
/// </summary>
public static class StackCapture
{
    public const string ToolVariable = "NETPI_E2E_STACK_TOOL";
    public const int MaxChars = 120_000;

    public static async Task<string> CaptureAsync(int pid, string? tool = null, TimeSpan? timeout = null)
    {
        tool ??= Environment.GetEnvironmentVariable(ToolVariable) is { Length: > 0 } t ? t : "dotnet-stack";
        var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "report", "-p", pid.ToString() }) psi.ArgumentList.Add(a);
        Process process;
        try { process = Process.Start(psi)!; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return $"(no stacks: '{tool}' is not installed. `dotnet tool install -g dotnet-stack` lets the next wedged server name the threads it is blocked in.)";
        }
        using (process)
        using (var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(25)))
        {
            var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = process.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                var text = (await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false)).Trim();
                if (text.Length == 0) text = $"('{tool} report -p {pid}' printed nothing, exit code {process.ExitCode})";
                return text.Length > MaxChars ? text[..MaxChars] + "\n(cut)" : text;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return $"('{tool} report -p {pid}' did not finish in time)";
            }
        }
    }
}
