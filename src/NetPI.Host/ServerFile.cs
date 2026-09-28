using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NetPI.Host.Web;

namespace NetPI.Host;

/// <summary>
/// <c>&lt;home&gt;/server.json</c>: how to reach the running app from outside (its URL and this run's token), written when
/// the server is ready and removed when it stops. The port can change (a busy one falls back to a random port) and the
/// token is new every run, so tools that inspect the app (<c>scripts/netpi.mjs</c>, a debugging agent with curl) read
/// them here. It lives next to settings.json, which holds API keys: the same user can read both.
/// <para><c>appDir</c> says where this app is installed, which is what a build needs to install into the running one
/// (it is not necessarily the repository it was built from: a worktree builds into its own artifacts).</para>
/// </summary>
internal static class ServerFile
{
    public const string Name = "server.json";

    public static string PathIn(NetPiPaths paths) => Path.Combine(paths.Home, Name);

    public static void Write(NetPiPaths paths, string url, string token, bool desktop, ILogger log)
    {
        var doc = new JsonObject
        {
            ["url"] = url,
            ["token"] = token,
            ["pid"] = Environment.ProcessId,
            ["version"] = HostInfo.Version,
            ["startedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["home"] = paths.Home,
            ["appDir"] = paths.AppDir,
            ["logs"] = paths.LogsDir,
            ["desktop"] = desktop,
            ["rpc"] = $"POST {url}/api/rpc/<method> with the header {WebServer.TokenHeader}: <token> and the params as a JSON body",
            ["inspect"] = "diag.overview first (then diag.problems, diag.calls, diag.run, …); rpc.list lists every method. See docs/DEBUGGING.md",
        };
        var path = PathIn(paths);
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, doc.ToJsonString(NetPiJson.Indented));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) { log.LogWarning(ex, "Writing {File} failed: tools can't find this instance", path); }
    }

    /// <summary>Remove the file if it is ours (it always is: one NetPI per home, see <see cref="HomeLock"/>).</summary>
    public static void Remove(NetPiPaths paths, ILogger log)
    {
        var path = PathIn(paths);
        try
        {
            if (!File.Exists(path)) return;
            var pid = JsonNode.Parse(File.ReadAllText(path))?["pid"]?.GetValue<int>();
            if (pid == Environment.ProcessId) File.Delete(path);
        }
        catch (Exception ex) { log.LogDebug(ex, "Removing {File} failed", path); }
    }
}
