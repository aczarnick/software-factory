using Factory.Core;

namespace Factory.TestPlugin;

/// <summary>Implements both the read and write halves of run history — the natural shape for a
/// storage backend. Registration is the point; the members are not meant to be called.</summary>
[FactoryProvider("dual-port", Contract = 1)]
public sealed class DualPortStore : IRunHistory, IRunHistorySink
{
    public void Append(FactoryEvent evt) => throw new NotSupportedException();
    public IEnumerable<FactoryEvent> ReadFrom(long afterSeq) => throw new NotSupportedException();
    public IReadOnlyList<RunRecord> RunsForItem(string itemId) => throw new NotSupportedException();
    public IReadOnlyList<RunRecord> RunsForStation(string stationId) => throw new NotSupportedException();
    public SpendTotals Totals() => throw new NotSupportedException();
    public BudgetRestoreView ForBudget() => throw new NotSupportedException();
    public IReadOnlyDictionary<string, string> Champions() => throw new NotSupportedException();

    public void Emit(FactoryEvent evt) => throw new NotSupportedException();
    public void Flush() => throw new NotSupportedException();

    public void Dispose() { }
}
