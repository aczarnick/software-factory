using Factory.Core;

namespace Factory.Runtime;

/// <summary>Writes durably, then offers the event to every sink. Reads never touch a sink,
/// so an unreachable tracing backend cannot block a report.</summary>
public sealed class FanOutRunHistory(IRunHistory writer, IReadOnlyList<IRunHistorySink> sinks) : IRunHistory
{
    public void Append(FactoryEvent evt)
    {
        writer.Append(evt);
        foreach (var sink in sinks) sink.Emit(evt);
    }

    public IEnumerable<FactoryEvent> ReadFrom(long afterSeq) => writer.ReadFrom(afterSeq);
    public IReadOnlyList<RunRecord> RunsForItem(string itemId) => writer.RunsForItem(itemId);
    public IReadOnlyList<RunRecord> RunsForStation(string stationId) => writer.RunsForStation(stationId);
    public SpendTotals Totals() => writer.Totals();
    public BudgetRestoreView ForBudget() => writer.ForBudget();
    public IReadOnlyDictionary<string, string> Champions() => writer.Champions();

    public void Dispose()
    {
        foreach (var sink in sinks) sink.Flush();
        writer.Dispose();
    }
}
