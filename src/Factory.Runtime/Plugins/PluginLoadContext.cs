using System.Reflection;
using System.Runtime.Loader;

namespace Factory.Runtime;

/// <summary>
/// Isolates a plugin's own dependencies while forcing contract types to come from the host.
/// Without that second rule a plugin loads its own <c>Factory.Core</c>, and every contract type
/// it touches — the ports it implements, the attribute that marks it — is a different type than
/// the host's. The provider then simply never appears: no load error, no cast failure, not even a
/// skipped-type line, because the marker attribute stops matching before anything is logged.
/// </summary>
internal sealed class PluginLoadContext(string pluginPath)
    : AssemblyLoadContext(name: Path.GetFileNameWithoutExtension(pluginPath), isCollectible: false)
{
    private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Contract assemblies always come from the default context so types unify.
        if (assemblyName.Name is "Factory.Core") return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
