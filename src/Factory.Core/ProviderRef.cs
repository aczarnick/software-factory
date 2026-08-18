using System.Text.Json.Serialization;

namespace Factory.Core;

/// <summary>Selects a provider by name, with provider-specific options. Part of the plugin ABI:
/// options are read-only so a provider cannot mutate the host's configuration, and narrowing
/// them later would be a breaking change requiring a contract-version bump.</summary>
[method: JsonConstructor]
public sealed record ProviderRef(string Provider, IReadOnlyDictionary<string, string> Options)
{
    public ProviderRef(string provider) : this(provider, new Dictionary<string, string>()) { }

    /// <summary>Never null. A config entry that names a provider and nothing else — the common
    /// case, and what the spec's own example writes — deserialises with no options at all, and a
    /// provider reading its own settings must not have to guard against that.</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } =
        Options ?? new Dictionary<string, string>();
}
