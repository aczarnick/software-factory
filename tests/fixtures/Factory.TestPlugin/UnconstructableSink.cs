using Factory.Core;

namespace Factory.TestPlugin;

/// <summary>Its only constructor takes neither a <see cref="ProviderRef"/> nor nothing at all,
/// so the catalog cannot build it. Present to prove one malformed provider does not stop the scan.</summary>
[FactoryProvider("unconstructable", Contract = 1)]
public sealed class UnconstructableSink(string unsatisfiable) : IRunHistorySink
{
    public void Emit(FactoryEvent evt) => throw new NotSupportedException(unsatisfiable);
    public void Flush() { }
}
