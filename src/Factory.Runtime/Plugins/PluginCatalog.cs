using System.Collections.Concurrent;
using System.Reflection;
using Factory.Core;

namespace Factory.Runtime;

/// <summary>Discovers providers in <c>.factory/plugins/*.dll</c> and registers them by name.
/// A plugin that cannot be loaded is reported and skipped: one broken assembly must not stop
/// a factory whose configured providers are all built in.</summary>
public static class PluginCatalog
{
    // One context per assembly for the life of the process. FactoryHost.Open runs once per
    // delegated work item, and PluginLoadContext is not collectible, so a context per open
    // would pin another copy of every plugin assembly for as long as the factory runs.
    private static readonly ConcurrentDictionary<string, PluginLoadContext> Contexts = new();

    public static void LoadInto(ProviderRegistry registry, string pluginsDir, Action<string> log)
    {
        if (!Directory.Exists(pluginsDir)) return;

        foreach (var dll in Directory.EnumerateFiles(pluginsDir, "*.dll").OrderBy(p => p))
        {
            try
            {
                RegisterAssembly(registry, dll, log);
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException
                                       or FileNotFoundException or TypeLoadException
                                       or ReflectionTypeLoadException)
            {
                log($"plugin '{Path.GetFileName(dll)}' could not be loaded: {ex.Message}");
            }
        }
    }

    private static void RegisterAssembly(ProviderRegistry registry, string dll, Action<string> log)
    {
        var path = Path.GetFullPath(dll);
        var context = Contexts.GetOrAdd(path, static resolved => new PluginLoadContext(resolved));
        var assembly = context.LoadFromAssemblyPath(path);
        var yieldedProvider = false;

        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.GetCustomAttribute<FactoryProviderAttribute>() is not { } marker) continue;

            if (marker.Contract != FactoryVersion.ContractVersion)
            {
                log($"plugin provider '{marker.Name}' targets contract v{marker.Contract}, " +
                    $"this factory implements v{FactoryVersion.ContractVersion} — skipped");
                continue;
            }

            // Every port the type satisfies, not just the first: a backend that both reads and
            // writes run history must be selectable as either.
            var claimed = Register<IRunHistorySink>(registry, type, marker.Name, log);
            claimed |= Register<IWorkItemStore>(registry, type, marker.Name, log);
            claimed |= Register<IRunHistory>(registry, type, marker.Name, log);

            if (!claimed)
            {
                log($"plugin type '{type.FullName}' is marked as a provider but implements no storage port — skipped");
                continue;
            }

            yieldedProvider = true;
        }

        // Silence here means the assembly's contract types did not unify with the host's, which
        // leaves no other trace: the attribute itself stops matching and every type is passed over.
        if (!yieldedProvider) log($"plugin '{Path.GetFileName(dll)}' registered no providers");
    }

    private static bool Register<T>(ProviderRegistry registry, Type type, string name, Action<string> log)
        where T : class
    {
        if (!typeof(T).IsAssignableFrom(type)) return false;

        var withOptions = type.GetConstructor([typeof(ProviderRef)]);
        var parameterless = type.GetConstructor(Type.EmptyTypes);
        if (withOptions is null && parameterless is null)
        {
            log($"plugin provider '{name}' ({type.FullName}) needs a constructor taking a " +
                $"{nameof(ProviderRef)}, or a parameterless one — skipped");
            return true;
        }

        // Both paths refuse the reflection wrapper: a provider that fails while constructing
        // must reach the host's boundary as its own exception, not as a TargetInvocationException
        // whose message says only that an invocation target threw.
        registry.RegisterPlugin<T>(name, reference => (T)(withOptions is not null
            ? withOptions.Invoke(BindingFlags.DoNotWrapExceptions, binder: null, [reference], culture: null)
            : parameterless!.Invoke(BindingFlags.DoNotWrapExceptions, binder: null, [], culture: null)));

        return true;
    }
}
