using Factory.Core;

namespace Factory.TestPlugin;

/// <summary>Declares a plugin ABI major the host does not implement. Present so the catalog's
/// version gate has something to refuse; it must never reach the registry.</summary>
[FactoryProvider("future", Contract = 2)]
public sealed class FutureContractSink : IRunHistorySink
{
    public void Emit(FactoryEvent evt) => throw new NotSupportedException();
    public void Flush() => throw new NotSupportedException();
}
