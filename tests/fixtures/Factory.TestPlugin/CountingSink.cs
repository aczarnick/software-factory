using Factory.Core;

namespace Factory.TestPlugin;

[FactoryProvider("counting", Contract = 1)]
public sealed class CountingSink : IRunHistorySink
{
    public static int Emitted;
    public void Emit(FactoryEvent evt) => Emitted++;
    public void Flush() { }
}
