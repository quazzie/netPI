using System.Runtime.InteropServices;
using System.Text;

namespace NetPI.Aux.Tests;

/// <summary>The test's own registry access: it writes what <c>EnvSource</c> must then see — a variable set in
/// HKCU\Environment after the process started, which the process's environment snapshot cannot show.</summary>
internal static class RegistryValue
{
    private const uint KeySetValue = 0x0002; // KEY_SET_VALUE
    private static readonly IntPtr HKeyCurrentUser = unchecked((IntPtr)0x80000001);

    public static void Set(string name, string value)
    {
        if (RegOpenKeyExW(HKeyCurrentUser, "Environment", 0, KeySetValue, out var hKey) != 0)
            throw new InvalidOperationException("HKCU\\Environment is not writable.");
        try
        {
            var bytes = Encoding.Unicode.GetBytes(value);
            if (RegSetValueExW(hKey, name, 0, 1 /* REG_SZ */, bytes, (uint)bytes.Length) != 0)
                throw new InvalidOperationException("Writing the test value failed.");
        }
        finally { RegCloseKey(hKey); }
    }

    public static void Delete(string name)
    {
        if (RegOpenKeyExW(HKeyCurrentUser, "Environment", 0, KeySetValue, out var hKey) != 0) return;
        try { RegDeleteValueW(hKey, name); }
        finally { RegCloseKey(hKey); }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegOpenKeyExW(IntPtr hKey, string subKey, uint options, uint samDesired, out IntPtr hResult);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegSetValueExW(IntPtr hKey, string valueName, int reserved, uint type, byte[] data, uint dataLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegDeleteValueW(IntPtr hKey, string valueName);

    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(IntPtr hKey);
}
