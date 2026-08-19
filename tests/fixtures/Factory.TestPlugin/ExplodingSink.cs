using Factory.Core;

namespace Factory.TestPlugin;

/// <summary>Fails while being constructed, the way a sink that opens its connection eagerly
/// does when the backend is unreachable. Construction is the point; the members never run.</summary>
[FactoryProvider("exploding", Contract = FactoryVersion.ContractVersion)]
public sealed class ExplodingSink : IRunHistorySink
{
    public ExplodingSink() => throw new InvalidOperationException("cannot reach the tracing backend");

    public void Emit(FactoryEvent evt) => throw new NotSupportedException();
    public void Flush() => throw new NotSupportedException();
}
