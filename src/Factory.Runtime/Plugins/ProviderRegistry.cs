using Factory.Core;

namespace Factory.Runtime;

/// <summary>Names to provider factories. Built-ins are registered directly and plugins are
/// added on top, so selecting either is the same config change.</summary>
public sealed class ProviderRegistry
{
    private readonly Dictionary<(Type Port, string Name), Func<ProviderRef, object>> _factories = [];

    public void Register<T>(string name, Func<ProviderRef, T> factory) where T : class =>
        _factories[(typeof(T), name)] = reference => factory(reference);

    public T Resolve<T>(ProviderRef reference) where T : class
    {
        if (_factories.TryGetValue((typeof(T), reference.Provider), out var factory))
            return (T)factory(reference);

        var available = _factories.Keys
            .Where(k => k.Port == typeof(T))
            .Select(k => k.Name)
            .OrderBy(n => n);

        throw new InvalidOperationException(
            $"No {typeof(T).Name} provider named '{reference.Provider}'. " +
            $"Available: {string.Join(", ", available)}. " +
            $"Third-party providers load from the plugins directory.");
    }

    public bool Has<T>(string name) where T : class => _factories.ContainsKey((typeof(T), name));
}
