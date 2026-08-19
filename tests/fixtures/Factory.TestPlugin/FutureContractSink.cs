using Factory.Core;

namespace Factory.TestPlugin;

/// <summary>Declares the plugin ABI major immediately above the host's. Present so the catalog's
/// version gate has something newer to refuse; it must never reach the registry.
///
/// Expressed relative to <see cref="FactoryVersion.ContractVersion"/> rather than as a literal, so a
/// contract bump cannot leave this fixture accidentally matching the host and quietly stop testing the
/// gate at all — the same reason the current-contract fixtures track the constant.</summary>
[FactoryProvider("future", Contract = FactoryVersion.ContractVersion + 1)]
public sealed class FutureContractSink : IRunHistorySink
{
    public void Emit(FactoryEvent evt) => throw new NotSupportedException();
    public void Flush() => throw new NotSupportedException();
}
