using Factory.Core;

namespace Factory.Runtime;

/// <summary>Names to provider factories. Built-ins are registered directly and plugins are
/// added on top, so selecting either is the same config change. A built-in always wins a name
/// collision, whichever side registered first: the two methods differ in origin, not in
/// ordering, so a built-in added after a scan is as safe as one added before it.</summary>
public sealed class ProviderRegistry(Action<string>? log = null)
{
    private readonly Dictionary<(Type Port, string Name), Registration> _providers = [];
    private readonly Action<string> _log = log ?? (_ => { });

    /// <summary>Registers a provider the factory ships, displacing a plugin that claimed the
    /// same name earlier in the scan.</summary>
    public void Register<T>(string name, Func<ProviderRef, T> factory) where T : class
    {
        var key = (typeof(T), name);

        if (_providers.TryGetValue(key, out var occupant) && occupant.FromPlugin)
            _log($"plugin provider '{name}' ({typeof(T).Name}) is displaced by the built-in of the same name");

        _providers[key] = new Registration(reference => factory(reference), FromPlugin: false);
    }

    /// <summary>Registers a provider discovered in a plugin. Refused, and named in the log,
    /// when the port already has a provider under that name.</summary>
    public void RegisterPlugin<T>(string name, Func<ProviderRef, T> factory) where T : class
    {
        var key = (typeof(T), name);

        if (_providers.TryGetValue(key, out var occupant))
        {
            _log(occupant.FromPlugin
                ? $"plugin provider '{name}' ({typeof(T).Name}) is already registered by another plugin — skipped"
                : $"plugin provider '{name}' ({typeof(T).Name}) is shadowed by a built-in — skipped");
            return;
        }

        _providers[key] = new Registration(reference => factory(reference), FromPlugin: true);
    }

    public T Resolve<T>(ProviderRef reference) where T : class
    {
        if (_providers.TryGetValue((typeof(T), reference.Provider), out var registration))
            return (T)registration.Create(reference);

        var available = _providers.Keys
            .Where(k => k.Port == typeof(T))
            .Select(k => k.Name)
            .OrderBy(n => n);

        throw new InvalidOperationException(
            $"No {typeof(T).Name} provider named '{reference.Provider}'. " +
            $"Available: {string.Join(", ", available)}. " +
            $"Third-party providers load from the plugins directory.");
    }

    private readonly record struct Registration(Func<ProviderRef, object> Create, bool FromPlugin);
}
