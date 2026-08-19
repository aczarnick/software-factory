using Factory.Core;

namespace Factory.TestPlugin;

/// <summary>Declares the plugin ABI major immediately below the host's — a plugin compiled against
/// the previous contract. Present so the catalog's version gate is proved to refuse an <em>older</em>
/// plugin and not only a newer one: that is the direction a contract bump exists to protect, since it
/// is the older plugin that would otherwise be handed a type it was never compiled against. It must
/// never reach the registry.</summary>
[FactoryProvider("past", Contract = FactoryVersion.ContractVersion - 1)]
public sealed class PastContractSink : IRunHistorySink
{
    public void Emit(FactoryEvent evt) => throw new NotSupportedException();
    public void Flush() => throw new NotSupportedException();
}
