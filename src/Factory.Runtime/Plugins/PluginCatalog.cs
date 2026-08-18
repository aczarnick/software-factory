using System.Reflection;
using Factory.Core;

namespace Factory.Runtime;

/// <summary>Discovers providers in <c>.factory/plugins/*.dll</c> and registers them by name.
/// A plugin that cannot be loaded is reported and skipped: one broken assembly must not stop
/// a factory whose configured providers are all built in.</summary>
public static class PluginCatalog
{
    public static void LoadInto(ProviderRegistry registry, string pluginsDir, Action<string> log)
    {
        if (!Directory.Exists(pluginsDir)) return;

        foreach (var dll in Directory.EnumerateFiles(pluginsDir, "*.dll").OrderBy(p => p))
        {
            try
            {
                RegisterAssembly(registry, dll, log);
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or ReflectionTypeLoadException)
            {
                log($"plugin '{Path.GetFileName(dll)}' could not be loaded: {ex.Message}");
            }
        }
    }

    private static void RegisterAssembly(ProviderRegistry registry, string dll, Action<string> log)
    {
        var context = new PluginLoadContext(dll);
        var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(dll));

        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.GetCustomAttribute<FactoryProviderAttribute>() is not { } marker) continue;

            if (marker.Contract != FactoryVersion.ContractVersion)
            {
                log($"plugin provider '{marker.Name}' targets contract v{marker.Contract}, " +
                    $"this factory implements v{FactoryVersion.ContractVersion} — skipped");
                continue;
            }

            if (Register<IRunHistorySink>(registry, type, marker.Name, log)) continue;
            if (Register<IWorkItemStore>(registry, type, marker.Name, log)) continue;
            if (Register<IRunHistory>(registry, type, marker.Name, log)) continue;

            log($"plugin type '{type.FullName}' is marked as a provider but implements no storage port — skipped");
        }
    }

    private static bool Register<T>(ProviderRegistry registry, Type type, string name, Action<string> log)
        where T : class
    {
        if (!typeof(T).IsAssignableFrom(type)) return false;

        if (registry.Has<T>(name))
        {
            log($"plugin provider '{name}' is shadowed by a built-in — skipped");
            return true;
        }

        var withOptions = type.GetConstructor([typeof(ProviderRef)]);
        if (withOptions is null && type.GetConstructor(Type.EmptyTypes) is null)
            throw new InvalidOperationException(
                $"Provider '{name}' ({type.FullName}) needs a constructor taking a {nameof(ProviderRef)}, " +
                "or a parameterless one.");

        registry.Register<T>(name, reference => (T)(withOptions is not null
            ? withOptions.Invoke([reference])
            : Activator.CreateInstance(type)!));

        return true;
    }
}
