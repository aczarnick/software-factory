using System.Text.Json.Serialization;

namespace Factory.Core;

/// <summary>Selects a provider by name, with provider-specific options.</summary>
[method: JsonConstructor]
public sealed record ProviderRef(string Provider, Dictionary<string, string> Options)
{
    public ProviderRef(string provider) : this(provider, []) { }
}
