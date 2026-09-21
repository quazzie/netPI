using System;
using System.IO;

namespace NetPI.Abstractions;

/// <summary>
/// The single effective runtime home (astra-1 P0.5). Both the host
/// (config, logs, caches) and the storage plugin (default database) derive
/// every path from this one place: an explicit <c>NETPI_HOME</c> env var
/// wins, otherwise the user profile's .netpi directory.
/// No database migration: an existing netpi.db in place is simply used.
/// </summary>
public static class RuntimeHome
{
    public static string Dir =>
        Environment.GetEnvironmentVariable("NETPI_HOME") is { Length: > 0 } h
            ? Path.GetFullPath(h)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".netpi");
}
