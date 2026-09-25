using System.Text.Json.Nodes;

namespace NetPI.Host;

/// <summary>
/// One NetPI per home: <c>&lt;home&gt;/netpi.lock</c> held open without sharing for the app's lifetime (the OS lets go of it
/// when the process ends, also after a crash). Two apps on one home would share its database, settings and server.json;
/// the second one refuses to start instead. Another setup runs with its own home (<c>--home</c>, <c>NETPI_HOME</c>).
/// </summary>
internal sealed class HomeLock : IDisposable
{
    public const string Name = "netpi.lock";

    private readonly FileStream _stream;

    private HomeLock(FileStream stream) => _stream = stream;

    public static HomeLock Acquire(string home)
    {
        var path = Path.Combine(home, Name);
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
            using (var writer = new StreamWriter(stream, leaveOpen: true)) writer.Write(Environment.ProcessId);
            stream.Flush();
            return new HomeLock(stream);
        }
        catch (IOException)
        {
            throw new InvalidOperationException(
                $"NetPI is already running with the home {home}{Describe(home)}. Close it first, or give this one its own home (--home <dir> or NETPI_HOME).");
        }
    }

    /// <summary>" (pid 123, http://127.0.0.1:7431)" from the running one's server.json, when it can be read.</summary>
    private static string Describe(string home)
    {
        try
        {
            var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(home, ServerFile.Name)));
            return $" (pid {doc?["pid"]}, {doc?["url"]})";
        }
        catch { return ""; }
    }

    public void Dispose()
    {
        try { _stream.Dispose(); } catch { }
    }
}
