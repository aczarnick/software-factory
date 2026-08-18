using Factory.Core;

namespace Factory.TestPlugin;

[FactoryProvider("counting", Contract = 1)]
public sealed class CountingSink : IRunHistorySink
{
    public void Emit(FactoryEvent evt) { }
    public void Flush() { }
}
