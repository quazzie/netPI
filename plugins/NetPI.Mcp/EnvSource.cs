using System.Runtime.InteropServices;
using System.Text;

namespace NetPI.Mcp;

/// <summary>
/// Where the value of a named environment variable comes from. The registry is the user's current intent: a value
/// set in Windows after the process started (or changed since) wins over the process's own environment, which is
/// a snapshot from start and goes stale. The process environment is the fallback for a variable the launcher put
/// there that is not in the registry — so a running NetPI picks up a newly set variable at the next connect,
/// request or reconnect, without a restart.
/// </summary>
internal static class EnvSource
{
    public static string? Resolve(string name)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // User first: that is where the user's own variables (and the ones a script set with scope "User") live.
            var live = FromRegistry(HKeyCurrentUser, "Environment", name)
                ?? FromRegistry(HKeyLocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", name);
            if (live is not null) return live;
        }
        return Environment.GetEnvironmentVariable(name);
    }

    private static string? FromRegistry(IntPtr root, string subKey, string name)
    {
        if (RegOpenKeyExW(root, subKey, 0, KeyRead, out var hKey) != ErrorSuccess) return null;
        try
        {
            var size = 0;
            // First call: the size the value needs; a zero-length value comes back as success with size 0.
            if (RegQueryValueExW(hKey, name, IntPtr.Zero, out _, null, ref size) is not (ErrorSuccess or ErrorMoreData)) return null;
            var buffer = new byte[size];
            if (RegQueryValueExW(hKey, name, IntPtr.Zero, out var type, buffer, ref size) != ErrorSuccess) return null;
            var value = Encoding.Unicode.GetString(buffer, 0, size);
            // The registry reports the size with the null terminator; it is not part of the value.
            var end = value.IndexOf('\0');
            if (end >= 0) value = value[..end];
            // The registry stores user variables as REG_EXPAND_SZ; the expansion is best effort against the
            // process environment (an unresolvable %REF% stays as written, like a shell would leave it).
            return type == RegExpandSz ? Environment.ExpandEnvironmentVariables(value) : value;
        }
        finally { RegCloseKey(hKey); }
    }

    private const uint KeyRead = 0x0109; // KEY_READ (QUERY_VALUE | ENUM_SUB_KEYS | NOTIFY; the 64-bit WOW64 bits are included)
    private const int ErrorSuccess = 0, ErrorMoreData = 234;
    private const int RegExpandSz = 2;
    private static readonly IntPtr HKeyCurrentUser = unchecked((IntPtr)0x80000001);
    private static readonly IntPtr HKeyLocalMachine = unchecked((IntPtr)0x80000002);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegOpenKeyExW(IntPtr hKey, string subKey, uint options, uint samDesired, out IntPtr hResult);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueExW(IntPtr hKey, string? valueName, IntPtr lpReserved, out uint type, byte[]? buffer, ref int size);

    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(IntPtr hKey);
}
