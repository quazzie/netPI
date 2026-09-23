using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace NetPI.Host.Plugins;

/// <summary>
/// Collectible load context for one plugin load. Assemblies the default context has or can load (NetPI.Abstractions,
/// the host, framework assemblies from the TPA list) resolve to the default context so contract types are shared;
/// everything else comes from the plugin's (shadow-copied) folder via its .deps.json.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly string _directory;
    private readonly AssemblyDependencyResolver? _resolver;

    public PluginLoadContext(string mainAssemblyPath, string name) : base(name, isCollectible: true)
    {
        _directory = Path.GetDirectoryName(mainAssemblyPath)!;
        try
        {
            _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        }
        catch (InvalidOperationException)
        {
            // Single-file hosts have no hostpolicy: fall back to probing the plugin folder by name.
            _resolver = null;
        }
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (SharedAssemblies.IsShared(assemblyName)) return null;
        var path = _resolver?.ResolveAssemblyToPath(assemblyName) ?? Probe(assemblyName.Name + ".dll");
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName) ?? ProbeNative(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }

    private string? Probe(string fileName)
    {
        var p = Path.Combine(_directory, fileName);
        return File.Exists(p) ? p : null;
    }

    private string? ProbeNative(string name)
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        var candidates = OperatingSystem.IsWindows() ? new[] { name, name + ".dll" }
            : OperatingSystem.IsMacOS() ? new[] { name, "lib" + name + ".dylib", name + ".dylib" }
            : new[] { name, "lib" + name + ".so", name + ".so" };
        foreach (var c in candidates)
        {
            if (Probe(c) is { } local) return local;
            var native = Path.Combine(_directory, "runtimes", rid, "native", c);
            if (File.Exists(native)) return native;
        }
        return null;
    }
}

internal static class SharedAssemblies
{
    private static readonly Lazy<HashSet<string>> Tpa = new(() =>
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string list)
            foreach (var path in list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                set.Add(Path.GetFileNameWithoutExtension(path));
        return set;
    });

    public static bool IsShared(AssemblyName name)
    {
        var simple = name.Name;
        if (string.IsNullOrEmpty(simple)) return false;
        if (simple == "NetPI.Abstractions" || Tpa.Value.Contains(simple)) return true;
        foreach (var a in AssemblyLoadContext.Default.Assemblies)
            if (string.Equals(a.GetName().Name, simple, StringComparison.OrdinalIgnoreCase)) return true;
        if (Tpa.Value.Count == 0)
        {
            // Single-file host: bundled framework assemblies are not listed in the TPA but the default context can load them.
            try
            {
                AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(simple));
                return true;
            }
            catch
            {
                return false;
            }
        }
        return false;
    }
}
