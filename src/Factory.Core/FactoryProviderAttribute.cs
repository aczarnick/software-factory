namespace Factory.Core;

/// <summary>Marks a type as a storage provider discoverable by name. Applied by plugin
/// authors; the factory's own built-ins are registered directly.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class FactoryProviderAttribute(string name) : Attribute
{
    /// <summary>Name used to select this provider in <c>factory.json</c>.</summary>
    public string Name { get; } = name;

    /// <summary>Plugin ABI major this provider was built against. A mismatch with
    /// <see cref="FactoryVersion.ContractVersion"/> is refused at load with a named error.</summary>
    public int Contract { get; init; } = 1;
}
