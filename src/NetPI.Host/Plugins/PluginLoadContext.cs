using System.Reflection;
using System.Runtime.Loader;
using NetPI.Abstractions;

namespace NetPI.Host.Plugins;

/// <summary>
/// Collectible <see cref="AssemblyLoadContext"/> for one plugin generation
/// (PLAN §5). Uses an <see cref="AssemblyDependencyResolver"/> for the plugin's
/// private dependencies, but always resolves <c>netPI.Abstractions</c> from the
/// default ALC — otherwise interface type identity breaks across the boundary.
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    public const string AbstractionsAssemblyName = "NetPI.Abstractions";

    private static readonly string s_abstractionsAssemblyName =
        typeof(INetPiPlugin).Assembly.GetName().Name!;

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string name, string pluginDirectory)
        : base(name, isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginDirectory);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(assemblyName.Name, s_abstractionsAssemblyName, StringComparison.OrdinalIgnoreCase))
        {
            // Type identity must come from the host (default) ALC.
            return AssemblyLoadContext.Default
                .Assemblies
                .FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase))
                ?? throw new FileNotFoundException(
                    $"netPI.Abstractions is expected to be loaded in the default ALC, but was not found. " +
                    $"This is a host bug: the plugin context cannot resolve the shared ABI for '{assemblyName.Name}'.");
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string libPath)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(libPath);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
